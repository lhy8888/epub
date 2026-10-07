using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using QuietRead.Core;

string root = Path.Combine(Path.GetTempPath(), "QuietRead-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var outcomes = new List<object>();
int passed = 0, failed = 0;
var tests = new List<(string Name, Action Run)>();
int fixtureIndex = 0;
string WriteFixture(Dictionary<string, byte[]> entries)
{
    string path = Path.Combine(root, "fixture-" + fixtureIndex++ + ".epub");
    File.WriteAllBytes(path, Fixtures.Zip(entries)); return path;
}
void Test(string name, Action run) => tests.Add((name, run));

Test("EPUB3 metadata, nested TOC, anchors, Unicode and emphasis", () =>
{
    var entries = Fixtures.Book(["<h1 id='one'>第一章</h1><p>你好 <strong>世界</strong>，Hello <em>reader</em>.</p><h2 id='two'>第二节</h2>"]);
    entries["OEBPS/nav.xhtml"] = Fixtures.Utf8("<html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'><body><nav epub:type='toc'><ol><li><a href='c0.xhtml#one'>第一章</a><ol><li><a href='c0.xhtml#two'>第二节</a></li></ol></li></ol></nav></body></html>");
    using var book = EpubBook.Open(WriteFixture(entries));
    Check.Equal("QuietRead 测试书", book.Title); Check.Equal(2, book.TableOfContents.Count);
    Check.Equal(1, book.TableOfContents[1].Depth);
    ParsedChapter chapter = book.ReadChapter(0);
    Check.Equal(0, chapter.Anchors["one"]); Check.Equal(2, chapter.Anchors["two"]);
    Check.True(chapter.Blocks[1].PlainText.Contains("你好 世界，Hello reader."));
    Check.True(chapter.Blocks[1].Inlines.Any(x => x.Text == "世界" && x.Style.HasFlag(TextStyle.Bold)));
});

Test("EPUB2 NCX, harmless legacy DOCTYPE and HTML entities", () =>
{
    var entries = Fixtures.Book(["<p>A&nbsp;B &copy; &mdash; &amp; &lt;tag&gt;</p>"], epub2: true);
    entries["OEBPS/c0.xhtml"] = Fixtures.Utf8("<!DOCTYPE html PUBLIC '-//W3C//DTD XHTML 1.1//EN' 'http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd'>" + Fixtures.Html("<p>A&nbsp;B &copy; &mdash; &amp; &lt;tag&gt;</p>"));
    using var book = EpubBook.Open(WriteFixture(entries));
    Check.Equal("Chapter 1", book.TableOfContents[0].Title);
    Check.Equal("A\u00a0B © — & <tag>", book.ReadChapter(0).Blocks[0].PlainText);
});

Test("Scripts, forms, remote images and external hyperlinks cannot become active content", () =>
{
    var entries = Fixtures.Book(["<script>DO_NOT_SHOW</script><iframe src='file:///etc/passwd'>hidden</iframe><form><p>hidden form</p></form><p onclick='evil()'><a href='javascript:evil()'>safe text</a> <a href='https://evil.invalid'>remote</a> <a href='c0.xhtml#note'>local</a></p><p id='note'>footnote</p><img src='https://evil.invalid/track.png' alt='tracker'/><style>url(file:///etc/passwd)</style>"]);
    using var book = EpubBook.Open(WriteFixture(entries));
    ParsedChapter parsed = book.ReadChapter(0);
    string text = string.Join(' ', parsed.Blocks.Select(x => x.PlainText));
    Check.False(text.Contains("DO_NOT_SHOW") || text.Contains("hidden"));
    Check.Equal(1, parsed.Blocks.SelectMany(x => x.Inlines).Count(x => x.Link != null));
    Check.True(parsed.Blocks.Any(x => x.Kind == BlockKind.Image && x.ImagePath == null));
});

Test("External entity referencing a local secret is never expanded", () =>
{
    string secretPath = Path.Combine(root, "private.txt"); File.WriteAllText(secretPath, "SECRET_SENTINEL_88222");
    var entries = Fixtures.Book(["<p>&secret;</p>"]);
    entries["OEBPS/c0.xhtml"] = Fixtures.Utf8("<!DOCTYPE html [<!ENTITY secret SYSTEM '" + new Uri(secretPath).AbsoluteUri + "'>]>" + Fixtures.Html("<p>&secret;</p>"));
    using var book = EpubBook.Open(WriteFixture(entries));
    Check.Throws<EpubException>(() => book.ReadChapter(0));
    Check.Equal("SECRET_SENTINEL_88222", File.ReadAllText(secretPath));
});

Test("External DTD performs no loopback network request", () =>
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    try
    {
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var entries = Fixtures.Book(["<p>offline content</p>"]);
        entries["OEBPS/c0.xhtml"] = Fixtures.Utf8("<!DOCTYPE html SYSTEM 'http://127.0.0.1:" + port + "/test.dtd'>" + Fixtures.Html("<p>offline content</p>"));
        using var book = EpubBook.Open(WriteFixture(entries));
        Check.Equal("offline content", book.ReadChapter(0).Blocks[0].PlainText);
        Check.False(listener.Pending());
    }
    finally { listener.Stop(); }
});

Test("Internal entity expansion bomb is rejected", () =>
{
    var entries = Fixtures.Book(["<p>&large;</p>"]);
    entries["OEBPS/c0.xhtml"] = Fixtures.Utf8("<!DOCTYPE html [<!ENTITY small '1234567890'><!ENTITY large '&small;&small;&small;&small;'>]>" + Fixtures.Html("<p>&large;</p>"));
    using var book = EpubBook.Open(WriteFixture(entries)); Check.Throws<EpubException>(() => book.ReadChapter(0));
});

foreach (string path in new[] { "../escape", "/absolute", "C:/absolute", "OEBPS/../../escape", "OEBPS\\escape", "OEBPS/./escape", "OEBPS//escape" })
{
    string unsafePath = path;
    Test("Reject ZIP path: " + unsafePath, () =>
    {
        var entries = Fixtures.Book(["<p>normal</p>"]); entries[unsafePath] = Fixtures.Utf8("malicious");
        Check.Throws<EpubException>(() => EpubBook.Open(WriteFixture(entries)));
        Check.False(File.Exists(Path.Combine(root, "escape")));
    });
}

Test("Reference normalization, percent encoding and root confinement", () =>
{
    Check.Equal("Images/封面.png", ArchivePath.Resolve("Text/a.xhtml", "../Images/%E5%B0%81%E9%9D%A2.png")!.Path);
    Check.Equal("section 1", ArchivePath.Resolve("a.xhtml", "#section%201")!.Fragment);
    foreach (string href in new[] { "../../outside", "file:///secret", "https://example.org/a", "//example.org/a", "C%3A%5Csecret", "%2e%2e/%2e%2e/a", "%00a", "%ZZ", "%5csecret" })
        Check.True(ArchivePath.Resolve("Text/a.xhtml", href) == null, href);
});

Test("Duplicate ZIP resource names are rejected", () =>
{
    byte[] zip = Fixtures.Zip(Fixtures.Book(["<p>one</p>"]), duplicate: "OEBPS/c0.xhtml");
    string path = Path.Combine(root, "duplicate.epub"); File.WriteAllBytes(path, zip);
    Check.Throws<EpubException>(() => EpubBook.Open(path));
});

Test("Encrypted ZIP flag is rejected before resource parsing", () =>
{
    byte[] zip = Fixtures.Zip(Fixtures.Book(["<p>one</p>"])); Fixtures.ChangeCentral(zip, "OEBPS/c0.xhtml", (bytes, at) => bytes[at + 8] |= 1);
    string path = Path.Combine(root, "encrypted-zip.epub"); File.WriteAllBytes(path, zip); Check.Throws<EpubException>(() => EpubBook.Open(path));
});

Test("ZIP symbolic links cannot enter the resource table", () =>
{
    byte[] zip = Fixtures.Zip(Fixtures.Book(["<p>one</p>"]));
    Fixtures.ChangeCentral(zip, "OEBPS/c0.xhtml", (bytes, at) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 38), 0xA1FF0000));
    string path = Path.Combine(root, "symlink.epub"); File.WriteAllBytes(path, zip); Check.Throws<EpubException>(() => EpubBook.Open(path));
});

Test("Unsupported compression and central directory entry overflow are rejected", () =>
{
    byte[] zip = Fixtures.Zip(Fixtures.Book(["<p>one</p>"])); Fixtures.ChangeCentral(zip, "OEBPS/c0.xhtml", (bytes, at) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 10), 99));
    string path = Path.Combine(root, "method.epub"); File.WriteAllBytes(path, zip); Check.Throws<EpubException>(() => EpubBook.Open(path));
    zip = Fixtures.Zip(Fixtures.Book(["<p>one</p>"])); int end = Fixtures.End(zip);
    BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(end + 8), 12_001); BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(end + 10), 12_001);
    File.WriteAllBytes(path, zip); Check.Throws<EpubException>(() => EpubBook.Open(path));
});

Test("Oversized and suspiciously compressed declared resources are rejected", () =>
{
    foreach (uint size in new[] { (uint)ReaderLimits.EntryBytes + 1, 50_000_000U })
    {
        byte[] zip = Fixtures.Zip(Fixtures.Book(["<p>one</p>"]));
        Fixtures.ChangeCentral(zip, "OEBPS/c0.xhtml", (bytes, at) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 24), size));
        string path = Path.Combine(root, "oversized-entry.epub"); File.WriteAllBytes(path, zip);
        Check.Throws<EpubException>(() => EpubBook.Open(path));
    }
});

Test("Archive size cap works without reading a huge file", () =>
{
    string path = Path.Combine(root, "sparse.epub");
    using (var file = new FileStream(path, FileMode.Create, FileAccess.Write)) file.SetLength(ReaderLimits.ArchiveBytes + 1);
    Check.Throws<EpubException>(() => EpubBook.Open(path));
});

Test("Truncated and non-EPUB input fail cleanly", () =>
{
    byte[] zip = Fixtures.Zip(Fixtures.Book(["<p>one</p>"]));
    string path = Path.Combine(root, "truncated.epub"); File.WriteAllBytes(path, zip[..^12]);
    Check.Throws<EpubException>(() => EpubBook.Open(path));
    var entries = Fixtures.Book(["<p>one</p>"]); entries["mimetype"] = Fixtures.Utf8("application/not-epub");
    Check.Throws<EpubException>(() => EpubBook.Open(WriteFixture(entries)));
});

Test("ZIP64 directory is supported within the same resource limits", () =>
{
    byte[] zip = Fixtures.Zip64(Fixtures.Zip(Fixtures.Book(["<p>zip64 content</p>"])));
    string path = Path.Combine(root, "zip64.epub"); File.WriteAllBytes(path, zip);
    using var book = EpubBook.Open(path); Check.Equal("zip64 content", book.ReadChapter(0).Blocks[0].PlainText);
});

Test("XML nesting and node-count limits reject resource exhaustion", () =>
{
    foreach (string content in new[] { string.Concat(Enumerable.Repeat("<div>", 70)) + "deep" + string.Concat(Enumerable.Repeat("</div>", 70)), string.Concat(Enumerable.Repeat("<br/>", 76_000)) })
    {
        using var book = EpubBook.Open(WriteFixture(Fixtures.Book([content]))); Check.Throws<EpubException>(() => book.ReadChapter(0));
    }
});

Test("XML byte and paragraph limits reject extreme chapters", () =>
{
    foreach (string content in new[] { "<p>" + new string('x', ReaderLimits.XmlBytes + 1) + "</p>", string.Concat(Enumerable.Repeat("<p>text</p>", ReaderLimits.ChapterBlocks + 1)) })
    {
        using var book = EpubBook.Open(WriteFixture(Fixtures.Book([content]))); Check.Throws<EpubException>(() => book.ReadChapter(0));
    }
});

Test("Large paragraph is split without losing text or surrogate pairs", () =>
{
    string text = string.Concat(Enumerable.Repeat("汉字😀英语 words ", 3000));
    using var book = EpubBook.Open(WriteFixture(Fixtures.Book(["<p>" + text + "</p>"])));
    ParsedChapter chapter = book.ReadChapter(0);
    Check.True(chapter.Blocks.Count > 1);
    Check.True(chapter.Blocks.All(x => x.PlainText.Length <= 4099));
    string merged = string.Concat(chapter.Blocks.Select(x => x.PlainText));
    Check.Equal(text.Replace(" ", ""), merged.Replace(" ", ""));
    Check.False(merged.Contains('\uFFFD'));
});

Test("Nested quotes, lists, line breaks, code and basic table rows survive conversion", () =>
{
    using var book = EpubBook.Open(WriteFixture(Fixtures.Book(["<blockquote><p>quote</p></blockquote><ul><li><p>item one</p></li><li>item two</li></ul><pre>a\n  b</pre><p>line<br/>break</p><table><tr><th>Name</th><td>Value</td></tr></table>"])));
    ParsedChapter chapter = book.ReadChapter(0);
    Check.True(chapter.Blocks.Any(x => x.Kind == BlockKind.Quote && x.PlainText == "quote"));
    Check.Equal(2, chapter.Blocks.Count(x => x.Kind == BlockKind.ListItem));
    Check.True(chapter.Blocks.Any(x => x.Kind == BlockKind.Code && x.PlainText == "a\n  b"));
    Check.True(chapter.Blocks.Any(x => x.PlainText == "line\nbreak"));
    Check.True(chapter.Blocks.Any(x => x.Kind == BlockKind.TableRow && x.PlainText.Contains("Name  |  Value")));
});

Test("Raster dimensions and type are checked before the Windows decoder", () =>
{
    byte[] png = Fixtures.Png(640, 480); Check.Equal(640, RasterGuard.Inspect(png).Width);
    byte[] huge = Fixtures.Png(65_000, 65_000); Check.Throws<EpubException>(() => RasterGuard.Inspect(huge));
    Check.Throws<EpubException>(() => RasterGuard.Inspect("<svg onload='evil()'/>"u8.ToArray()));
    var entries = Fixtures.Book(["<img src='image.png' alt='picture'/>"]); Fixtures.AddManifest(entries, "<item id='image' href='image.png' media-type='image/png'/>"); entries["OEBPS/image.png"] = png;
    using var book = EpubBook.Open(WriteFixture(entries)); Check.Equal(480, book.ReadImage("OEBPS/image.png").Info.Height);
    Check.Throws<EpubException>(() => book.ReadImage("OEBPS/c0.xhtml"));
});

Test("Nested SVG fallback text cannot multiply retained output", () =>
{
    string svg = "<svg xmlns='http://www.w3.org/2000/svg'>" + string.Concat(Enumerable.Repeat("<text>", 55)) +
        new string('x', 16_000) + string.Concat(Enumerable.Repeat("</text>", 55)) + "</svg>";
    using var book = EpubBook.Open(WriteFixture(Fixtures.Book([svg])));
    ParsedChapter chapter = book.ReadChapter(0);
    Check.Equal(1, chapter.Blocks.Count); Check.Equal(BlockKind.Image, chapter.Blocks[0].Kind);
    Check.True(chapter.Blocks[0].Alt!.Length < 1000); Check.Equal(0, chapter.CharacterCount);
});

Test("Oversized raster and mismatched MIME are rejected", () =>
{
    var entries = Fixtures.Book(["<p>image test</p>"]);
    Fixtures.AddManifest(entries, "<item id='image' href='image.png' media-type='image/png'/>");
    byte[] large = new byte[ReaderLimits.ImageBytes + 1]; Fixtures.Png(10, 10).CopyTo(large, 0); entries["OEBPS/image.png"] = large;
    using (var book = EpubBook.Open(WriteFixture(entries))) Check.Throws<EpubException>(() => book.ReadImage("OEBPS/image.png"));
    entries["OEBPS/image.png"] = "GIF89a\u0001\u0000\u0001\u0000\u0000\u0000\u0000"u8.ToArray();
    using (var book = EpubBook.Open(WriteFixture(entries))) Check.Throws<EpubException>(() => book.ReadImage("OEBPS/image.png"));
});

Test("DRM and fixed layout report an explicit unsupported-format error", () =>
{
    var entries = Fixtures.Book(["<p>one</p>"]);
    entries["META-INF/encryption.xml"] = Fixtures.Utf8("<encryption><EncryptedData><EncryptionMethod Algorithm='http://www.w3.org/2001/04/xmlenc#aes256-cbc'/></EncryptedData></encryption>");
    Check.Throws<EpubException>(() => EpubBook.Open(WriteFixture(entries)));
    entries = Fixtures.Book(["<p>one</p>"]);
    entries["OEBPS/content.opf"] = Fixtures.Utf8(Encoding.UTF8.GetString(entries["OEBPS/content.opf"]).Replace("</metadata>", "<meta property='rendition:layout'>pre-paginated</meta></metadata>"));
    Check.Throws<EpubException>(() => EpubBook.Open(WriteFixture(entries)));
});

Test("Font obfuscation can be ignored without loading embedded fonts", () =>
{
    var entries = Fixtures.Book(["<p>normal text</p>"]); Fixtures.AddManifest(entries, "<item id='font' href='font.otf' media-type='font/otf'/>");
    entries["META-INF/encryption.xml"] = Fixtures.Utf8("<encryption><EncryptedData><EncryptionMethod Algorithm='http://www.idpf.org/2008/embedding'/><CipherReference URI='OEBPS/font.otf'/></EncryptedData></encryption>");
    entries["OEBPS/font.otf"] = [1, 2, 3];
    using var book = EpubBook.Open(WriteFixture(entries)); Check.Equal("normal text", book.ReadChapter(0).Blocks[0].PlainText);
});

Test("Fallback chains and repeated spine references preserve reading order", () =>
{
    var entries = Fixtures.Book(["<p>fallback</p>"]);
    Fixtures.AddManifest(entries, "<item id='unsupported' href='thing.bin' media-type='application/unknown' fallback='c0'/>"); entries["OEBPS/thing.bin"] = [1];
    entries["OEBPS/content.opf"] = Fixtures.Utf8(Encoding.UTF8.GetString(entries["OEBPS/content.opf"]).Replace("<itemref idref='c0'/>", "<itemref idref='unsupported'/><itemref idref='c0'/>"));
    using var book = EpubBook.Open(WriteFixture(entries)); Check.Equal(2, book.Chapters.Count);
    Check.Equal("fallback", book.ReadChapter(1).Blocks[0].PlainText); Check.Equal(0, book.FindChapter("OEBPS/c0.xhtml"));
});

Test("Chapter loading is lazy and its cache stays bounded", () =>
{
    var entries = Fixtures.Book(Enumerable.Range(0, 8).Select(x => "<p>Chapter " + x + "</p>").ToArray());
    entries["OEBPS/c7.xhtml"] = Fixtures.Utf8("<not well formed");
    using var book = EpubBook.Open(WriteFixture(entries)); Check.Equal(0, book.CachedChapterCount);
    for (int i = 0; i < 7; i++) { book.ReadChapter(i); Check.True(book.CachedChapterCount <= 3); }
    Check.Equal(3, book.CachedChapterCount); Check.Throws<EpubException>(() => book.ReadChapter(7));
});

Test("Search handles Unicode, skips broken chapters and caps results", () =>
{
    var entries = Fixtures.Book(["<p>第一段 hello 世界</p><p>Another HELLO</p>", "<p>broken</p>"]);
    entries["OEBPS/c1.xhtml"] = Fixtures.Utf8("<broken");
    using (var book = EpubBook.Open(WriteFixture(entries)))
    {
        SearchOutcome result = book.Search("hello"); Check.Equal(2, result.Hits.Count); Check.Equal(1, result.FailedChapters);
        Check.Equal(1, book.Search("世界").Hits.Count); Check.Equal(0, book.CachedChapterCount);
    }
    using (var book = EpubBook.Open(WriteFixture(Fixtures.Book([string.Concat(Enumerable.Repeat("<p>match word</p>", 250))]))))
    { SearchOutcome result = book.Search("match"); Check.Equal(200, result.Hits.Count); Check.True(result.LimitReached); }
});

Test("Cancellation stops open, read and search; disposed books cannot be reused", () =>
{
    string path = WriteFixture(Fixtures.Book(["<p>one</p>"]));
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    Check.Throws<OperationCanceledException>(() => EpubBook.Open(path, cancellation.Token));
    using var book = EpubBook.Open(path);
    Check.Throws<OperationCanceledException>(() => book.ReadChapter(0, cancellation.Token));
    Check.Throws<OperationCanceledException>(() => book.Search("one", cancellation.Token));
    book.Dispose(); Check.Throws<ObjectDisposedException>(() => book.ReadChapter(0));
});

Test("A cancelled operation on a retired book returns cancellation safely", () =>
{
    using var book = EpubBook.Open(WriteFixture(Fixtures.Book(["<p>one</p>"])));
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); book.Dispose();
    Check.Throws<OperationCanceledException>(() => book.ReadChapter(0, cancellation.Token));
    Check.Throws<OperationCanceledException>(() => book.ReadImage("missing.png", cancellation.Token));
    Check.Throws<OperationCanceledException>(() => book.Search("one", cancellation.Token));
});

Test("State round-trip, bounds, bad data recovery and atomic write order", () =>
{
    string directory = Path.Combine(root, "settings"); var store = new StateStore(directory);
    var state = new AppState
    {
        Preferences = new ReaderPreferences { FontSize = double.NaN, Theme = "script", TextWidth = 10_000 },
        Books = [new BookHistory { BookKey = new string('a', 64), FilePath = "test.epub", ChapterIndex = 999_999,
            Bookmarks = [new Bookmark { Label = "bookmark", Fraction = -4 }] }]
    };
    store.Save(StateStore.Snapshot(state), 2);
    AppState loaded = store.Load(); Check.Equal(22.0, loaded.Preferences.FontSize); Check.Equal("Paper", loaded.Preferences.Theme);
    Check.Equal(1100.0, loaded.Preferences.TextWidth); Check.Equal(4095, loaded.Books[0].ChapterIndex); Check.Equal(0.0, loaded.Books[0].Bookmarks[0].Fraction);
    store.Save(StateStore.Snapshot(new AppState { Preferences = new ReaderPreferences { Theme = "Dark" } }), 1);
    Check.Equal("Paper", store.Load().Preferences.Theme);
    Check.Equal(0, Directory.GetFiles(directory, "*.tmp").Length);
    File.WriteAllText(Path.Combine(directory, "state.json"), "{not valid JSON");
    Check.Equal(0, store.Load().Books.Count); Check.True(store.LoadFailed);
    File.WriteAllText(Path.Combine(directory, "state.json"), "{\"Schema\":1,\"Preferences\":null,\"Books\":[null]}");
    Check.Equal(0, new StateStore(directory).Load().Books.Count);
});

Test("State size and unknown schema are safely ignored", () =>
{
    string directory = Path.Combine(root, "bad-settings"); Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "state.json"), new string('x', StateStore.MaxStateBytes + 1));
    var store = new StateStore(directory); Check.Equal(0, store.Load().Books.Count); Check.True(store.LoadFailed);
    File.WriteAllText(Path.Combine(directory, "state.json"), "{\"Schema\":888}");
    store = new StateStore(directory); Check.Equal(1, store.Load().Schema); Check.True(store.LoadFailed);
});

var benchmarkResults = new List<object>();
var compatibilityResults = new List<object>();
try
{
    foreach ((string name, Action run) in tests)
    {
        var timer = Stopwatch.StartNew();
        try { run(); passed++; outcomes.Add(new { name, passed = true, milliseconds = timer.Elapsed.TotalMilliseconds }); Console.WriteLine("PASS " + name); }
        catch (Exception exception)
        { failed++; outcomes.Add(new { name, passed = false, error = exception.ToString(), milliseconds = timer.Elapsed.TotalMilliseconds }); Console.WriteLine("FAIL " + name + ": " + exception.Message); }
    }
    for (int argument = 0; argument < args.Length - 1; argument++)
    {
        if (args[argument] != "--validate") continue;
        string path = args[++argument];
        var timer = Stopwatch.StartNew();
        try
        {
            using var book = EpubBook.Open(path);
            int blocks = 0, characters = 0, images = 0, unsupportedImages = 0;
            var seenImages = new HashSet<string>();
            for (int i = 0; i < book.Chapters.Count; i++)
            {
                ParsedChapter chapter = book.ReadChapter(i);
                blocks += chapter.Blocks.Count; characters += chapter.CharacterCount;
                foreach (BookBlock block in chapter.Blocks)
                    if (block.ImagePath != null && seenImages.Add(block.ImagePath))
                    {
                        try { book.ReadImage(block.ImagePath); images++; }
                        catch (EpubException) { unsupportedImages++; }
                    }
            }
            compatibilityResults.Add(new
            {
                file = Path.GetFileName(path),
                title = book.Title,
                passed = true,
                chapters = book.Chapters.Count,
                tocItems = book.TableOfContents.Count,
                blocks,
                characters,
                readableRasters = images,
                unsupportedImages,
                coreValidationMs = timer.Elapsed.TotalMilliseconds
            });
            Console.WriteLine($"COMPAT PASS {Path.GetFileName(path)}: {book.Chapters.Count} chapters, {characters} chars, {images} raster images");
        }
        catch (Exception exception)
        { failed++; compatibilityResults.Add(new { file = Path.GetFileName(path), passed = false, error = exception.Message }); Console.WriteLine("COMPAT FAIL " + path + ": " + exception.Message); }
    }
    if (args.Contains("--benchmark"))
    {
        Console.WriteLine("Benchmark: native core only; not Windows UI/startup/painting.");
        string paragraph = "<p>这是用于性能测量的可重复文本。A quiet native reader keeps the interface responsive. No remote resources are needed.</p>";
        string content = string.Concat(Enumerable.Repeat(paragraph, 80));
        string path = WriteFixture(Fixtures.Book(Enumerable.Repeat(content, 1000).ToArray()));
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var opens = new List<double>(); var reads = new List<double>();
        for (int iteration = 0; iteration < 7; iteration++)
        {
            var timer = Stopwatch.StartNew(); using var book = EpubBook.Open(path); opens.Add(timer.Elapsed.TotalMilliseconds);
            timer.Restart(); book.ReadChapter(500); reads.Add(timer.Elapsed.TotalMilliseconds);
        }
        using (var book = EpubBook.Open(path))
        {
            var timer = Stopwatch.StartNew(); SearchOutcome result = book.Search("not-present-keyword");
            benchmarkResults.Add(new
            {
                name = "1000-chapter full-text scan (no matches)",
                milliseconds = timer.Elapsed.TotalMilliseconds,
                chapters = book.Chapters.Count,
                cachedChapters = book.CachedChapterCount,
                failedChapters = result.FailedChapters
            });
        }
        benchmarkResults.Add(new { name = "Open 1000-chapter EPUB, warmed OS/runtime", medianMs = opens.Order().ElementAt(3), maxMs = opens.Max(), iterations = 7 });
        benchmarkResults.Add(new { name = "Parse one 80-paragraph chapter, cache miss", medianMs = reads.Order().ElementAt(3), maxMs = reads.Max(), iterations = 7 });
        Console.WriteLine(JsonSerializer.Serialize(benchmarkResults, new JsonSerializerOptions { WriteIndented = true }));
    }
    string? report = null;
    for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--report") report = args[i + 1];
    if (report != null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            version = "0.1.0",
            utc = DateTime.UtcNow,
            os = RuntimeInformation.OSDescription,
            dotnet = Environment.Version.ToString(),
            windowsUiExecuted = false,
            passed,
            failed,
            tests = outcomes,
            compatibility = compatibilityResults,
            benchmarks = benchmarkResults
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    Console.WriteLine($"{passed} passed, {failed} failed.");
}
finally { Directory.Delete(root, true); }
return failed == 0 ? 0 : 1;

static class Check
{
    public static void True(bool value, string? message = null) { if (!value) throw new InvalidOperationException(message ?? "Assertion failed."); }
    public static void False(bool value) => True(!value);
    public static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected exception " + typeof(T).Name);
    }
}

static class Fixtures
{
    public static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
    public static string Html(string body) => "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Test</title></head><body>" + body + "</body></html>";
    public static Dictionary<string, byte[]> Book(string[] bodies, bool epub2 = false)
    {
        var result = new Dictionary<string, byte[]>
        {
            ["mimetype"] = Utf8("application/epub+zip"),
            ["META-INF/container.xml"] = Utf8("<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='OEBPS/content.opf' media-type='application/oebps-package+xml'/></rootfiles></container>")
        };
        var manifest = new StringBuilder(); var spine = new StringBuilder(); var nav = new StringBuilder();
        for (int i = 0; i < bodies.Length; i++)
        {
            result[$"OEBPS/c{i}.xhtml"] = Utf8(Html(bodies[i]));
            manifest.Append($"<item id='c{i}' href='c{i}.xhtml' media-type='application/xhtml+xml'/>"); spine.Append($"<itemref idref='c{i}'/>");
            nav.Append(epub2 ? $"<navPoint id='n{i}'><navLabel><text>Chapter {i + 1}</text></navLabel><content src='c{i}.xhtml'/></navPoint>" : $"<li><a href='c{i}.xhtml'>Chapter {i + 1}</a></li>");
        }
        manifest.Append(epub2 ? "<item id='ncx' href='toc.ncx' media-type='application/x-dtbncx+xml'/>" : "<item id='nav' href='nav.xhtml' media-type='application/xhtml+xml' properties='nav'/>");
        result["OEBPS/content.opf"] = Utf8($"<package xmlns='http://www.idpf.org/2007/opf' version='{(epub2 ? "2.0" : "3.0")}' unique-identifier='uid'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title>QuietRead 测试书</dc:title><dc:creator>QuietRead</dc:creator><dc:language>zh-CN</dc:language><dc:identifier id='uid'>urn:test:quietread</dc:identifier></metadata><manifest>{manifest}</manifest><spine{(epub2 ? " toc='ncx'" : "")}>{spine}</spine></package>");
        if (epub2) result["OEBPS/toc.ncx"] = Utf8("<ncx xmlns='http://www.daisy.org/z3986/2005/ncx/' version='2005-1'><navMap>" + nav + "</navMap></ncx>");
        else result["OEBPS/nav.xhtml"] = Utf8("<html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'><body><nav epub:type='toc'><ol>" + nav + "</ol></nav></body></html>");
        return result;
    }
    public static void AddManifest(Dictionary<string, byte[]> entries, string item) =>
        entries["OEBPS/content.opf"] = Utf8(Encoding.UTF8.GetString(entries["OEBPS/content.opf"]).Replace("</manifest>", item + "</manifest>"));
    public static byte[] Zip(Dictionary<string, byte[]> entries, string? duplicate = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach ((string name, byte[] bytes) in entries)
            {
                using Stream entry = archive.CreateEntry(name, name == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Fastest).Open(); entry.Write(bytes);
            }
            if (duplicate != null) { using Stream entry = archive.CreateEntry(duplicate).Open(); entry.Write(Utf8("duplicate")); }
        }
        return stream.ToArray();
    }
    public static int End(byte[] bytes)
    {
        for (int i = bytes.Length - 22; i >= 0; i--) if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == 0x06054b50) return i;
        throw new InvalidOperationException();
    }
    public static void ChangeCentral(byte[] bytes, string name, Action<byte[], int> action)
    {
        int end = End(bytes); int at = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16));
        while (at < end && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)) == 0x02014b50)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 28));
            if (Encoding.UTF8.GetString(bytes, at + 46, length) == name) { action(bytes, at); return; }
            at += 46 + length + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 30)) + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 32));
        }
        throw new InvalidOperationException("Central resource missing.");
    }
    public static byte[] Zip64(byte[] input)
    {
        int end = End(input); ulong count = BinaryPrimitives.ReadUInt16LittleEndian(input.AsSpan(end + 10));
        ulong size = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(end + 12)), offset = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(end + 16));
        using var stream = new MemoryStream(); stream.Write(input.AsSpan(0, end));
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(0x06064b50U); writer.Write(44UL); writer.Write((ushort)45); writer.Write((ushort)45); writer.Write(0U); writer.Write(0U);
        writer.Write(count); writer.Write(count); writer.Write(size); writer.Write(offset);
        writer.Write(0x07064b50U); writer.Write(0U); writer.Write((ulong)end); writer.Write(1U);
        writer.Write(0x06054b50U); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(ushort.MaxValue); writer.Write(ushort.MaxValue);
        writer.Write(uint.MaxValue); writer.Write(uint.MaxValue); writer.Write((ushort)0);
        return stream.ToArray();
    }
    public static byte[] Png(uint width, uint height)
    {
        byte[] bytes = new byte[33]; new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13); "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height); bytes[24] = 8; bytes[25] = 2;
        return bytes;
    }
}
