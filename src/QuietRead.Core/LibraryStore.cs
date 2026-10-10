using System.Text;
using System.Text.Json;

namespace QuietRead.Core;

// The catalog is the commit point. Each book is an immutable generation file;
// a failed multi-book import cannot expose a partially updated catalog.
public sealed class LibraryStore
{
    private readonly string _directory, _catalogPath, _booksPath;
    private readonly object _gate = new();
    private LibraryCatalog? _catalog;
    private readonly Dictionary<string, long> _revisions = new(StringComparer.Ordinal);
    private (string Id, byte[] Bytes)? _lastSnapshot;
    private long _changeVersion;
    public long ChangeVersion { get { lock (_gate) return _changeVersion; } }
    public LibraryStore(string directory)
    {
        _directory = directory; _catalogPath = Path.Combine(directory, "library.json");
        _booksPath = Path.Combine(directory, "books");
    }

    private LibraryCatalog Catalog
    {
        get
        {
            if (_catalog != null) return _catalog;
            if (!File.Exists(_catalogPath)) return _catalog = new LibraryCatalog();
            byte[] bytes = ReadBounded(_catalogPath, BookDataRules.MaxCatalogBytes);
            DataJsonBudget.Check(bytes);
            LibraryCatalog c = JsonSerializer.Deserialize<LibraryCatalog>(bytes, BookDataRules.Json) ?? throw new InvalidDataException("书库为空。");
            if (c.Schema != 1 || c.Books == null || c.Books.Count > BookDataRules.MaxBooks)
                throw new InvalidDataException("书库版本或数量不符合限制。");
            foreach (LibraryEntry entry in c.Books) BookDataRules.ValidateEntry(entry);
            if (c.Books.Select(b => b.BookId).Distinct().Count() != c.Books.Count) throw new InvalidDataException("书库身份重复。");
            return _catalog = c;
        }
    }

    public IReadOnlyList<LibraryEntry> Entries()
    { lock (_gate) return Catalog.Books.Select(e => e with { Tags = e.Tags.ToArray() }).ToArray(); }

    public void MigrateLegacy(IEnumerable<BookHistory> histories)
    {
        lock (_gate)
        {
            var entries = Catalog.Books.ToList(); var staged = new List<string>();
            try
            {
                foreach (BookHistory h in histories)
                {
                    h.BookKey = h.BookKey.ToUpperInvariant();
                    if (entries.Any(e => e.BookId == h.BookKey || e.FilePath == h.FilePath)) continue;
                    var data = new BookData { BookId = h.BookKey, History = h };
                    byte[] bytes = BookDataRules.Snapshot(data);
                    string generation = Stage(data.BookId, bytes, staged);
                    entries.Add(new LibraryEntry(data.BookId, generation, h.Title, h.Author, h.FilePath, [], "", "Reading", h.LastReadUtc, true));
                }
                if (staged.Count > 0) Commit(entries);
            }
            catch { DeleteStaged(staged); throw; }
        }
    }

    public BookData LoadBook(string id)
    {
        lock (_gate)
        {
            LibraryEntry entry = Catalog.Books.Single(e => e.BookId == id);
            BookData data = BookDataRules.Parse(ReadBounded(DataPath(entry), BookDataRules.MaxBookBytes));
            if (data.BookId != entry.BookId) throw new InvalidDataException("逐书数据身份不匹配。");
            return data;
        }
    }

    public BookData OpenBook(string id, string legacyId, string path, string title, string author, string? associate = null)
    {
        if (!BookDataRules.IsId(id)) throw new ArgumentException("Invalid content identity.", nameof(id));
        lock (_gate)
        {
            LibraryEntry? existing = Catalog.Books.FirstOrDefault(e => e.BookId == id);
            LibraryEntry? legacy = Catalog.Books.FirstOrDefault(e => e.Legacy && e.BookId == (associate ?? legacyId));
            if (associate != null && legacy == null && associate != id) throw new InvalidDataException("所选文件内容不同，不能关联到这本书。");
            BookData data = existing != null ? LoadBook(id) : legacy != null ? LoadBook(legacy.BookId) :
                new BookData { BookId = id, History = new BookHistory { BookKey = id, Title = title, Author = author } };
            if (existing != null && legacy != null)
            {
                BookData old = LoadBook(legacy.BookId);
                data.History.Bookmarks = data.History.Bookmarks.Concat(old.History.Bookmarks)
                    .DistinctBy(m => (m.ChapterIndex, m.Locator, m.Locator == null ? m.BlockIndex : 0, m.Locator == null ? m.Fraction : 0)).ToList();
                data.Annotations = data.Annotations.Concat(old.Annotations).DistinctBy(a => a.Id).ToList();
                // Snapshot validation refuses an over-limit merge before either generation is touched.
            }
            data.BookId = id; data.History.BookKey = id; data.History.FilePath = path;
            data.History.LastReadUtc = DateTime.UtcNow;
            var entries = Catalog.Books.Where(e => e.BookId != id && (legacy == null || e.BookId != legacy.BookId)).ToList();
            var basis = existing ?? legacy;
            var staged = new List<string>();
            try
            {
                string generation = Stage(id, BookDataRules.Snapshot(data), staged);
                entries.Add(new LibraryEntry(id, generation, basis?.Title ?? title, basis?.Author ?? author, path,
                    basis?.Tags ?? [], basis?.Series ?? "", basis?.ReadingStatus == "Finished" ? "Finished" : "Reading", data.History.LastReadUtc));
                Commit(entries);
            }
            catch { DeleteStaged(staged); throw; }
            return data;
        }
    }

    public void SaveBook(byte[] snapshot, long revision = 0, long? expectedChangeVersion = null)
    {
        BookData data = BookDataRules.Parse(snapshot);
        lock (_gate)
        {
            if (expectedChangeVersion != null && expectedChangeVersion != _changeVersion) throw new InvalidDataException("书库已导入或移除数据，请先重新加载当前书籍，避免覆盖更新。");
            if (revision > 0 && revision < _revisions.GetValueOrDefault(data.BookId)) return;
            LibraryEntry? entry = Catalog.Books.FirstOrDefault(e => e.BookId == data.BookId);
            // Removing a library entry must not be undone by a retired autosave.
            if (entry == null) return;
            if (_lastSnapshot is { } last && last.Id == data.BookId && last.Bytes.AsSpan().SequenceEqual(snapshot))
            { if (revision > 0) _revisions[data.BookId] = revision; return; }
            var staged = new List<string>();
            try
            {
                string generation = Stage(data.BookId, snapshot, staged);
                Commit(Catalog.Books.Select(e => e.BookId == data.BookId ? e with
                { DataRevision = generation, FilePath = data.History.FilePath, LastReadUtc = data.History.LastReadUtc } : e).ToList());
                _lastSnapshot = (data.BookId, snapshot.ToArray());
                if (revision > 0) _revisions[data.BookId] = revision;
            }
            catch { DeleteStaged(staged); throw; }
        }
    }

    public void UpdateMetadata(string id, string title, string author, string[] tags, string series, string status)
    {
        lock (_gate)
        {
            LibraryEntry e = Catalog.Books.Single(b => b.BookId == id) with
            { Title = title.Trim(), Author = author.Trim(), Tags = tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToArray(), Series = series.Trim(), ReadingStatus = status };
            BookDataRules.ValidateEntry(e);
            Commit(Catalog.Books.Select(b => b.BookId == id ? e : b).ToList());
        }
    }

    public void Remove(string id)
    { lock (_gate) { Commit(Catalog.Books.Where(e => e.BookId != id).ToList()); _changeVersion++; _revisions.Remove(id); _lastSnapshot = null; } }

    public byte[] ExportBackup(string? onlyBook = null)
    {
        lock (_gate)
        {
            var backup = new LibraryBackup(); int total = 0;
            foreach (LibraryEntry e in Catalog.Books.Where(e => onlyBook == null || e.BookId == onlyBook))
            {
                byte[] bytes = ReadBounded(DataPath(e), BookDataRules.MaxBookBytes);
                total = checked(total + bytes.Length + 4096);
                if (total > BookDataRules.MaxBackupBytes) throw new InvalidDataException("备份超过 16 MiB，请按单书导出。");
                BookData d = BookDataRules.Parse(bytes);
                d.History.FilePath = "";
                backup.Books.Add(new BackupBook(e with { DataRevision = "", FilePath = "" }, d));
            }
            byte[] result = JsonSerializer.SerializeToUtf8Bytes(backup, BookDataRules.Json);
            if (result.Length > BookDataRules.MaxBackupBytes) throw new InvalidDataException("备份超过 16 MiB。");
            return result;
        }
    }

    public static LibraryBackup ParseBackup(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > BookDataRules.MaxBackupBytes) throw new InvalidDataException("导入超过 16 MiB。");
        DataJsonBudget.Check(bytes);
        LibraryBackup b = JsonSerializer.Deserialize<LibraryBackup>(bytes, BookDataRules.Json) ?? throw new InvalidDataException("备份为空。");
        if (b.Schema != 1 || b.Books == null || b.Books.Count > BookDataRules.MaxBooks) throw new InvalidDataException("备份版本或数量不符合限制。");
        var ids = new HashSet<string>();
        foreach (BackupBook book in b.Books)
        {
            if (book == null) throw new InvalidDataException("备份书籍为空。");
            BookDataRules.ValidateEntry(book.Entry, true); BookDataRules.Validate(book.Data);
            if (book.Entry.BookId != book.Data.BookId || !ids.Add(book.Entry.BookId)) throw new InvalidDataException("备份书籍身份重复或不一致。");
            if (BookDataRules.Snapshot(book.Data).Length > BookDataRules.MaxBookBytes) throw new InvalidDataException("导入的单书数据超限。");
            // Device paths are never accepted from a backup, even if hand-edited.
            book.Data.History.FilePath = "";
        }
        return b;
    }

    public ImportPreview PreviewImport(byte[] bytes)
    {
        LibraryBackup backup = ParseBackup(bytes);
        lock (_gate)
        {
            int added = 0, existing = 0; var conflicts = new List<ImportConflict>();
            foreach (BackupBook b in backup.Books)
            {
                if (Catalog.Books.All(e => e.BookId != b.Entry.BookId)) { added++; continue; }
                existing++; BookData current = LoadBook(b.Entry.BookId);
                int changed = b.Data.Annotations.Count(a => current.Annotations.Any(c => c.Id == a.Id && c != a));
                bool progress = current.History.Locator != b.Data.History.Locator || current.History.ChapterIndex != b.Data.History.ChapterIndex ||
                    current.History.BlockIndex != b.Data.History.BlockIndex || current.History.Fraction != b.Data.History.Fraction;
                bool appearance = JsonSerializer.Serialize(current.Appearance) != JsonSerializer.Serialize(b.Data.Appearance);
                LibraryEntry entry = Catalog.Books.Single(e => e.BookId == b.Entry.BookId);
                bool metadata = entry.Title != b.Entry.Title || entry.Author != b.Entry.Author || entry.Series != b.Entry.Series ||
                    entry.ReadingStatus != b.Entry.ReadingStatus || !entry.Tags.SequenceEqual(b.Entry.Tags);
                if (changed > 0 || progress || appearance || metadata) conflicts.Add(new ImportConflict(b.Entry.BookId, b.Entry.Title, changed, progress, appearance, metadata));
            }
            return new ImportPreview(added, existing, conflicts);
        }
    }

    public void Import(byte[] bytes, bool preferImported)
    {
        LibraryBackup backup = ParseBackup(bytes);
        lock (_gate)
        {
            var entries = Catalog.Books.ToList(); var pending = new List<(LibraryEntry Entry, byte[] Bytes)>();
            // Validate and merge every book before writing anything.
            foreach (BackupBook b in backup.Books)
            {
                LibraryEntry? existing = entries.FirstOrDefault(e => e.BookId == b.Entry.BookId);
                BookData merged = b.Data;
                if (existing != null)
                {
                    BookData current = LoadBook(existing.BookId);
                    var marks = (preferImported ? b.Data.History.Bookmarks.Concat(current.History.Bookmarks) : current.History.Bookmarks.Concat(b.Data.History.Bookmarks))
                        .DistinctBy(m => (m.ChapterIndex, m.Locator, m.Locator == null ? m.BlockIndex : 0, m.Locator == null ? m.Fraction : 0)).ToList();
                    var notes = (preferImported ? b.Data.Annotations.Concat(current.Annotations) : current.Annotations.Concat(b.Data.Annotations)).DistinctBy(a => a.Id).ToList();
                    merged = preferImported ? b.Data : current;
                    merged.History.Bookmarks = marks; merged.Annotations = notes;
                    merged.History.FilePath = existing.FilePath;
                }
                LibraryEntry chosen = existing != null && !preferImported ? existing : b.Entry with { FilePath = existing?.FilePath ?? "" };
                pending.Add((chosen, BookDataRules.Snapshot(merged)));
                entries.RemoveAll(e => e.BookId == b.Entry.BookId); entries.Add(chosen);
            }
            if (entries.Count > BookDataRules.MaxBooks) throw new InvalidDataException("书库最多保存 1000 本。");
            var staged = new List<string>();
            try
            {
                foreach ((LibraryEntry e, byte[] snapshot) in pending)
                {
                    string generation = Stage(e.BookId, snapshot, staged);
                    int at = entries.FindIndex(b => b.BookId == e.BookId); entries[at] = e with { DataRevision = generation };
                }
                Commit(entries); _changeVersion++; _lastSnapshot = null;
            }
            catch { DeleteStaged(staged); throw; }
        }
    }

    public string ExportMarkdown(string id)
    {
        lock (_gate)
        {
            LibraryEntry e = Catalog.Books.Single(b => b.BookId == id); BookData d = LoadBook(id);
            var text = new StringBuilder();
            text.Append("# ").AppendLine(Markdown(e.Title)).AppendLine().Append("作者：").AppendLine(Markdown(e.Author));
            foreach (BookAnnotation a in d.Annotations.OrderBy(a => a.ChapterIndex).ThenBy(a => a.Range.Start.Paragraph))
            {
                text.AppendLine().Append("## 第 ").Append(a.ChapterIndex + 1).Append(" 章 · ").AppendLine(a.Id.ToString());
                foreach (string line in a.Range.Text.Split('\n')) text.Append("> ").AppendLine(Markdown(line));
                if (a.Note.Length > 0) text.AppendLine().AppendLine(Markdown(a.Note));
            }
            foreach (Bookmark b in d.History.Bookmarks) text.AppendLine().Append("- 书签：").Append(Markdown(b.Label)).Append(" · 第 ").Append(b.ChapterIndex + 1).AppendLine(" 章");
            return text.ToString();
        }
    }

    private static string Markdown(string text)
    {
        var output = new StringBuilder(text.Length);
        foreach (char c in text)
        { if ("\\`*_{}[]<>()#+-.!|".Contains(c)) output.Append('\\'); output.Append(c); }
        return output.ToString();
    }

    public static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new InvalidDataException("数据文件超过大小限制。");
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("数据文件在读取时发生变化。");
        return bytes;
    }

    private string DataPath(LibraryEntry e) => Path.Combine(_booksPath, e.BookId + "-" + e.DataRevision + ".json");
    private string Stage(string id, byte[] bytes, List<string> staged)
    {
        string revision = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(_booksPath);
        string path = Path.Combine(_booksPath, id + "-" + revision + ".json"); staged.Add(path);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes); file.Flush(true); return revision;
    }

    private void Commit(List<LibraryEntry> entries)
    {
        if (entries.Count > BookDataRules.MaxBooks) throw new InvalidDataException("书库最多保存 1000 本。");
        foreach (LibraryEntry e in entries) BookDataRules.ValidateEntry(e);
        var next = new LibraryCatalog { Books = entries };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next, BookDataRules.Json);
        if (bytes.Length > BookDataRules.MaxCatalogBytes) throw new InvalidDataException("书库索引超过 4 MiB。");
        Directory.CreateDirectory(_directory);
        string temporary = Path.Combine(_directory, "library-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, _catalogPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        LibraryCatalog? previous = _catalog; _catalog = next;
        foreach (LibraryEntry old in previous?.Books ?? [])
            if (next.Books.All(e => e.BookId != old.BookId || e.DataRevision != old.DataRevision))
                try { File.Delete(DataPath(old)); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void DeleteStaged(List<string> paths)
    { foreach (string path in paths) try { File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
}
