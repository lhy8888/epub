using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace QuietRead.Core;

public sealed class EpubBook : IDisposable
{
    private readonly FileStream _file;
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _mediaTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _chapterByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ParsedChapter> _cache = [];
    private readonly LinkedList<int> _recency = [];
    private readonly object _gate = new();
    private bool _disposed;
    private int _cacheCharacters;
    private long _cacheBytes;

    public string FilePath { get; }
    public string BookKey { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Author { get; private set; } = "";
    public string Language { get; private set; } = "";
    public IReadOnlyList<BookChapter> Chapters { get; private set; } = [];
    public IReadOnlyList<TocItem> TableOfContents { get; private set; } = [];
    public int ExternalResourcesSkipped { get; private set; }
    public int CachedChapterCount { get { lock (_gate) return _cache.Count; } }
    public long EstimatedCacheBytes { get { lock (_gate) return _cacheBytes; } }

    private EpubBook(string path, FileStream file, ZipArchive archive)
    { FilePath = path; _file = file; _archive = archive; }

    public static EpubBook Open(string path, CancellationToken token = default)
    {
        FileStream? file = null;
        ZipArchive? archive = null;
        EpubBook? book = null;
        try
        {
            path = Path.GetFullPath(path);
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
            ZipGuard.Validate(file, token);
            archive = new ZipArchive(file, ZipArchiveMode.Read, true, Encoding.UTF8);
            book = new EpubBook(path, file, archive);
            book.Initialize(token);
            return book;
        }
        catch (Exception exception)
        {
            if (book != null) book.Dispose();
            else { archive?.Dispose(); file?.Dispose(); }
            if (exception is EpubException or OperationCanceledException or UnauthorizedAccessException or FileNotFoundException)
                throw;
            if (exception is InvalidDataException or IOException or ArgumentException or OverflowException)
                throw new EpubException("EPUB 文件损坏、资源路径无效或不受支持。", exception);
            throw;
        }
    }

    private void Initialize(CancellationToken token)
    {
        long expanded = 0;
        foreach (ZipArchiveEntry entry in _archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            ArchivePath.ValidateEntry(entry.FullName);
            if (!_entries.TryAdd(entry.FullName, entry)) throw new EpubException("书籍包含重复的 ZIP 资源名称。");
            int fileType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (fileType == 0xa000) throw new EpubException("书籍包含不受支持的符号链接。");
            if (entry.Length < 0 || entry.Length > ReaderLimits.EntryBytes ||
                entry.CompressedLength < 0 || entry.CompressedLength > ReaderLimits.EntryBytes ||
                (entry.Length > 1024 * 1024 && entry.Length / Math.Max(1, entry.CompressedLength) > 2000))
                throw new EpubException("书籍资源过大或压缩比异常，已停止加载。");
            expanded = checked(expanded + entry.Length);
            if (expanded > ReaderLimits.ExpandedBytes) throw new EpubException("书籍的解压后总大小超过 1 GB 限制。");
        }
        byte[] mime = ReadBytes("mimetype", 64, token);
        if (!mime.AsSpan().SequenceEqual("application/epub+zip"u8)) throw new EpubException("此文件不是有效的 EPUB。");
        XDocument container = ReadXml("META-INF/container.xml", token);
        XElement? rootFile = container.Descendants().FirstOrDefault(x => x.Name.LocalName == "rootfile" &&
            ((string?)x.Attribute("media-type") is null or "application/oebps-package+xml"));
        string packagePath = (string?)rootFile?.Attribute("full-path") ?? throw new EpubException("找不到 EPUB 包文档。");
        ArchivePath.ValidateEntry(packagePath);
        XDocument package = ReadXml(packagePath, token);
        XElement metadata = package.Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "metadata")
            ?? throw new EpubException("EPUB 缺少元数据。");
        Title = ReadLabel(metadata.Elements().FirstOrDefault(x => x.Name.LocalName == "title"), 256, Path.GetFileNameWithoutExtension(FilePath), token);
        Author = Clip(string.Join("、", metadata.Elements().Where(x => x.Name.LocalName == "creator").Take(8)
            .Select(x => ReadLabel(x, 256, "", token))), 256, "作者未注明");
        Language = ReadLabel(metadata.Elements().FirstOrDefault(x => x.Name.LocalName == "language"), 32, "", token);
        if (metadata.Elements().Any(x => (string?)x.Attribute("property") == "rendition:layout" && ReadLabel(x, 32, "", token) == "pre-paginated"))
            throw new EpubException("首版支持可重排 EPUB；此书采用固定版式，暂不支持。");

        var items = new Dictionary<string, ManifestItem>(StringComparer.Ordinal);
        XElement manifest = package.Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "manifest")
            ?? throw new EpubException("EPUB 缺少资源清单。");
        foreach (XElement item in manifest.Elements().Where(x => x.Name.LocalName == "item"))
        {
            token.ThrowIfCancellationRequested();
            string id = (string?)item.Attribute("id") ?? "";
            string type = (string?)item.Attribute("media-type") ?? "";
            string properties = (string?)item.Attribute("properties") ?? "";
            string fallback = (string?)item.Attribute("fallback") ?? "";
            LocalLink? link = ArchivePath.Resolve(packagePath, (string?)item.Attribute("href"));
            if (id.Length == 0 || id.Length > 1024 || items.ContainsKey(id)) throw new EpubException("EPUB 资源清单包含无效或重复 ID。");
            if (type.Length > 128 || properties.Length > 1024 || fallback.Length > 1024)
                throw new EpubException("EPUB 资源属性过长。");
            if (link == null) { ExternalResourcesSkipped++; items.Add(id, new ManifestItem("", type, "", "")); continue; }
            items.Add(id, new ManifestItem(link.Path, type, properties, fallback));
            if (_mediaTypes.TryGetValue(link.Path, out string? prior) && prior != type)
                throw new EpubException("同一个书籍资源具有冲突的格式声明。");
            _mediaTypes[link.Path] = type;
        }
        XElement spine = package.Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "spine")
            ?? throw new EpubException("EPUB 缺少阅读顺序。");
        var chapters = new List<BookChapter>();
        var resolved = new Dictionary<string, ManifestItem?>(StringComparer.Ordinal);
        foreach (XElement reference in spine.Elements().Where(x => x.Name.LocalName == "itemref"))
        {
            token.ThrowIfCancellationRequested();
            if (chapters.Count >= ReaderLimits.SpineItems) throw new EpubException("书籍章节过多。");
            string id = (string?)reference.Attribute("idref") ?? "";
            var visited = new HashSet<string>(StringComparer.Ordinal);
            ManifestItem? item = null;
            while (items.TryGetValue(id, out ManifestItem? next) && visited.Add(id))
            {
                token.ThrowIfCancellationRequested();
                if (resolved.TryGetValue(id, out item)) break;
                if (next.MediaType is "application/xhtml+xml" or "text/html" or "image/svg+xml") { item = next; break; }
                id = next.Fallback;
            }
            foreach (string visitedId in visited) resolved[visitedId] = item;
            if (item == null || item.Path.Length == 0 || !_entries.ContainsKey(item.Path))
                throw new EpubException("EPUB 中有缺失或不受支持的章节。");
            _chapterByPath.TryAdd(item.Path, chapters.Count);
            chapters.Add(new BookChapter(item.Path, $"第 {chapters.Count + 1} 章"));
        }
        if (chapters.Count == 0) throw new EpubException("这本 EPUB 没有可阅读的章节。");
        Chapters = chapters.AsReadOnly();
        CheckEncryption(token);

        var toc = new List<TocItem>();
        ManifestItem? navigation = items.Values.FirstOrDefault(x => HasToken(x.Properties, "nav"));
        if (navigation != null && navigation.Path.Length > 0)
        {
            XDocument nav = ReadXml(navigation.Path, token);
            XElement? navRoot = nav.Descendants().FirstOrDefault(x => x.Name.LocalName == "nav" &&
                x.Attributes().Any(a => a.Name.LocalName == "type" && HasToken(a.Value, "toc")));
            navRoot ??= nav.Descendants().FirstOrDefault(x => x.Name.LocalName == "nav");
            if (navRoot != null)
                foreach (XElement a in navRoot.Descendants().Where(x => x.Name.LocalName == "a"))
                    AddToc(toc, navigation.Path, (string?)a.Attribute("href"), ReadLabel(a, 160, "", token),
                        Math.Clamp(a.Ancestors().Count(x => x.Name.LocalName == "ol") - 1, 0, 16));
        }
        if (toc.Count == 0)
        {
            string ncxId = (string?)spine.Attribute("toc") ?? "";
            ManifestItem? ncx = items.GetValueOrDefault(ncxId) ?? items.Values.FirstOrDefault(x => x.MediaType == "application/x-dtbncx+xml");
            if (ncx != null && ncx.Path.Length > 0)
                foreach (XElement point in ReadXml(ncx.Path, token).Descendants().Where(x => x.Name.LocalName == "navPoint"))
                {
                    XElement? label = point.Elements().FirstOrDefault(x => x.Name.LocalName == "navLabel");
                    XElement? content = point.Elements().FirstOrDefault(x => x.Name.LocalName == "content");
                    AddToc(toc, ncx.Path, (string?)content?.Attribute("src"), ReadLabel(label, 160, "", token),
                        Math.Clamp(point.Ancestors().Count(x => x.Name.LocalName == "navPoint"), 0, 16));
                }
        }
        var titledChapters = new HashSet<int>();
        foreach (TocItem item in toc)
            if (titledChapters.Add(item.ChapterIndex))
                chapters[item.ChapterIndex] = chapters[item.ChapterIndex] with { Title = item.Title };
        if (toc.Count == 0)
            toc.AddRange(chapters.Select((x, i) => new TocItem(x.Title, i, "", 0)));
        TableOfContents = toc.AsReadOnly();
        // Local file identity: fast and stable for an unchanged file at the same path.
        string identity = FilePath + "\n" + _file.Length + "\n" + File.GetLastWriteTimeUtc(FilePath).Ticks;
        BookKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private void AddToc(List<TocItem> toc, string source, string? href, string? title, int depth)
    {
        if (toc.Count >= ReaderLimits.SpineItems) throw new EpubException("书籍目录条目过多。");
        LocalLink? target = ArchivePath.Resolve(source, href);
        if (target != null && _chapterByPath.TryGetValue(target.Path, out int index))
            toc.Add(new TocItem(Clip(title, 160, Chapters[index].Title), index, target.Fragment, depth));
    }

    private void CheckEncryption(CancellationToken token)
    {
        if (!_entries.ContainsKey("META-INF/encryption.xml")) return;
        XDocument encryption = ReadXml("META-INF/encryption.xml", token);
        foreach (XElement encrypted in encryption.Descendants().Where(x => x.Name.LocalName == "EncryptedData"))
        {
            string algorithm = (string?)encrypted.Descendants().FirstOrDefault(x => x.Name.LocalName == "EncryptionMethod")?.Attribute("Algorithm") ?? "";
            if (algorithm is not ("http://www.idpf.org/2008/embedding" or "http://ns.adobe.com/pdf/enc#RC"))
                throw new EpubException("此书带有 DRM/加密内容；QuietRead 不支持解密。");
            string uri = (string?)encrypted.Descendants().FirstOrDefault(x => x.Name.LocalName == "CipherReference")?.Attribute("URI") ?? "";
            LocalLink? target = ArchivePath.Resolve("root.opf", uri);
            string type = target == null ? "" : _mediaTypes.GetValueOrDefault(target.Path, "");
            if (!(type.StartsWith("font/", StringComparison.Ordinal) || type is "application/vnd.ms-opentype" or "application/font-sfnt" or "application/font-woff"))
                throw new EpubException("书籍包含加密的非字体资源。");
        }
    }

    public int FindChapter(string path) => _chapterByPath.GetValueOrDefault(path, -1);

    public ParsedChapter ReadChapter(int index, CancellationToken token = default, bool cache = true)
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (index < 0 || index >= Chapters.Count) throw new ArgumentOutOfRangeException(nameof(index));
            token.ThrowIfCancellationRequested();
            if (_cache.TryGetValue(index, out ParsedChapter? result))
            { _recency.Remove(index); _recency.AddLast(index); return result; }
            string path = Chapters[index].Path;
            result = new ContentParser(path, _chapterByPath.ContainsKey, token).Parse(ReadXml(path, token));
            long bytes = result.EstimatedMemoryBytes;
            if (cache && bytes <= ReaderLimits.CacheBytes && result.CharacterCount <= ReaderLimits.CacheCharacters)
            {
                while (_cache.Count > 0 && (_cache.Count >= ReaderLimits.CacheChapters ||
                    _cacheCharacters + result.CharacterCount > ReaderLimits.CacheCharacters || _cacheBytes + bytes > ReaderLimits.CacheBytes))
                {
                    int oldest = _recency.First!.Value;
                    _cacheCharacters -= _cache[oldest].CharacterCount;
                    _cacheBytes -= _cache[oldest].EstimatedMemoryBytes;
                    _cache.Remove(oldest); _recency.RemoveFirst();
                }
                _cache[index] = result; _cacheCharacters += result.CharacterCount; _cacheBytes += bytes; _recency.AddLast(index);
            }
            return result;
        }
    }

    public RasterData ReadImage(string path, CancellationToken token = default, int maximumBytes = ReaderLimits.ImageBytes,
        ResourceReadBudget? budget = null)
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_mediaTypes.TryGetValue(path, out string? type) || type is not ("image/png" or "image/jpeg" or "image/gif" or "image/bmp"))
                throw new EpubException("此图片格式暂不支持，或图片不在书籍资源清单中。");
            if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            byte[] bytes = ReadBytes(path, Math.Min(ReaderLimits.ImageBytes, maximumBytes), token, budget);
            RasterInfo info = RasterGuard.Inspect(bytes);
            string expected = type[6..];
            if (info.Format != expected) throw new EpubException("图片的实际格式与声明不一致。");
            return new RasterData(bytes, info);
        }
    }

    public SearchOutcome Search(string query, CancellationToken token = default)
    {
        query = query.Trim();
        if (query.Length is 0 or > 128) throw new ArgumentException("搜索文字应为 1–128 个字符。", nameof(query));
        var hits = new List<SearchHit>();
        var scanned = new Dictionary<string, IReadOnlyList<BlockSearchHit>?>(StringComparer.Ordinal);
        int failed = 0;
        for (int chapterIndex = 0; chapterIndex < Chapters.Count; chapterIndex++)
        {
            token.ThrowIfCancellationRequested();
            string path = Chapters[chapterIndex].Path;
            if (!scanned.TryGetValue(path, out IReadOnlyList<BlockSearchHit>? matches))
            {
                try { matches = ScanChapter(ReadChapter(chapterIndex, token, false), query, token); }
                catch (EpubException) { matches = null; }
                scanned.Add(path, matches);
            }
            if (matches == null) { failed++; continue; }
            foreach (BlockSearchHit match in matches)
            {
                token.ThrowIfCancellationRequested();
                hits.Add(new SearchHit(chapterIndex, match.BlockIndex, Chapters[chapterIndex].Title, match.Snippet));
                if (hits.Count >= ReaderLimits.SearchResults) return new SearchOutcome(hits.AsReadOnly(), failed, true);
            }
        }
        return new SearchOutcome(hits.AsReadOnly(), failed, false);
    }

    private static IReadOnlyList<BlockSearchHit> ScanChapter(ParsedChapter chapter, string query, CancellationToken token)
    {
        var matches = new List<BlockSearchHit>();
        Span<char> buffer = stackalloc char[ChapterText.BufferCharacters];
        for (int index = 0; index < chapter.Blocks.Count; index++)
        {
            if ((index & 63) == 0) token.ThrowIfCancellationRequested();
            ReadOnlySpan<char> text = ChapterText.CopyContext(chapter, index, buffer, query.Length - 1, out int prefix, out int length);
            int searchStart = prefix;
            int found = text[searchStart..].IndexOf(query.AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (found < 0 || found >= length) continue;
            found += searchStart;
            int start = Math.Max(0, found - 28), count = Math.Min(100, text.Length - start);
            if (start > 0 && char.IsLowSurrogate(text[start])) { start--; count = Math.Min(100, text.Length - start); }
            if (count > 0 && char.IsHighSurrogate(text[start + count - 1])) count--;
            string snippet = (start > 0 ? "…" : "") + new string(text.Slice(start, count)).Replace('\n', ' ') +
                (start + count < text.Length ? "…" : "");
            matches.Add(new BlockSearchHit(index, snippet));
            if (matches.Count >= ReaderLimits.SearchResults) break;
        }
        return matches;
    }

    private sealed record BlockSearchHit(int BlockIndex, string Snippet);

    private XDocument ReadXml(string path, CancellationToken token) => SafeXml.Parse(ReadBytes(path, ReaderLimits.XmlBytes, token), token);

    private byte[] ReadBytes(string path, int limit, CancellationToken token, ResourceReadBudget? budget = null)
    {
        token.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(path, out ZipArchiveEntry? entry)) throw new EpubException("书籍缺少资源：" + Clip(path, 120, "未知"));
        if (entry.Length > limit || entry.CompressedLength > Math.Max(4096L, limit + 64L * 1024))
            throw new EpubException("书籍资源超过允许的大小限制。");
        budget?.Reserve((int)entry.Length);
        try
        {
            using Stream content = entry.Open();
            byte[] data = new byte[(int)entry.Length];
            int at = 0;
            uint crc = uint.MaxValue;
            while (at < data.Length)
            {
                token.ThrowIfCancellationRequested();
                int read = content.Read(data, at, Math.Min(64 * 1024, data.Length - at));
                if (read == 0) throw new EpubException("书籍资源被截断。");
                crc = ZipCrc.Update(crc, data.AsSpan(at, read));
                at += read;
            }
            if (content.ReadByte() != -1) throw new EpubException("书籍资源的实际大小与 ZIP 声明不符。");
            if (~crc != entry.Crc32) throw new EpubException("书籍资源的 CRC 校验失败，文件可能已损坏。");
            return data;
        }
        catch (InvalidDataException exception) { throw new EpubException("书籍资源压缩数据损坏。", exception); }
    }

    private static string Clip(string? value, int max, string fallback)
    {
        ReadOnlySpan<char> text = value.AsSpan().Trim();
        if (text.IsEmpty) return fallback;
        int length = Math.Min(max, text.Length);
        if (length < text.Length && char.IsHighSurrogate(text[length - 1])) length--;
        string bounded = text[..length].ToString();
        return string.Create(length, bounded, (destination, source) =>
        { for (int i = 0; i < source.Length; i++) destination[i] = char.IsControl(source[i]) ? ' ' : source[i]; });
    }

    private static string ReadLabel(XElement? element, int max, string fallback, CancellationToken token)
    {
        if (element == null) return fallback;
        var text = new StringBuilder(max);
        int scanned = 0, nodes = 0;
        foreach (XNode node in element.DescendantNodes())
        {
            token.ThrowIfCancellationRequested();
            if (++nodes > 1024) break;
            if (node is not XText value) continue;
            foreach (char c in value.Value)
            {
                if ((++scanned & 4095) == 0) token.ThrowIfCancellationRequested();
                if (scanned > 4096) return Clip(text.ToString(), max, fallback);
                if (text.Length == 0 && char.IsWhiteSpace(c)) continue;
                text.Append(c);
                if (text.Length >= max)
                {
                    if (char.IsHighSurrogate(text[^1])) text.Length--;
                    return Clip(text.ToString(), max, fallback);
                }
            }
        }
        return Clip(text.ToString(), max, fallback);
    }

    private static bool HasToken(string value, string expected)
    {
        ReadOnlySpan<char> text = value.AsSpan();
        while (!text.IsEmpty)
        {
            text = text.TrimStart();
            int length = 0;
            while (length < text.Length && !char.IsWhiteSpace(text[length])) length++;
            if (text[..length].SequenceEqual(expected)) return true;
            text = text[length..];
        }
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _cache.Clear(); _recency.Clear(); _cacheCharacters = 0; _cacheBytes = 0;
            _archive.Dispose(); _file.Dispose();
        }
    }

    private sealed record ManifestItem(string Path, string MediaType, string Properties, string Fallback);
}
