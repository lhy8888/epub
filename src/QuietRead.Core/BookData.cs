using System.Text.Json;

namespace QuietRead.Core;

public sealed record TextRangeLocator(ReadingLocator Start, ReadingLocator End, string Text);
public sealed record BookAnnotation(Guid Id, int ChapterIndex, string ResourcePath, TextRangeLocator Range,
    string Color, string Note, DateTime CreatedUtc, DateTime UpdatedUtc);

public sealed class BookData
{
    public int Schema { get; set; } = 1;
    public string BookId { get; set; } = "";
    public BookHistory History { get; set; } = new();
    public List<BookAnnotation> Annotations { get; set; } = [];
    public ReaderPreferences? Appearance { get; set; }
}

public sealed record LibraryEntry(string BookId, string DataRevision, string Title, string Author, string FilePath,
    string[] Tags, string Series, string ReadingStatus, DateTime LastReadUtc, bool Legacy = false);
public sealed class LibraryCatalog
{
    public int Schema { get; set; } = 1;
    public List<LibraryEntry> Books { get; set; } = [];
}
public sealed record BackupBook(LibraryEntry Entry, BookData Data);
public sealed class LibraryBackup
{
    public int Schema { get; set; } = 1;
    public List<BackupBook> Books { get; set; } = [];
}
public sealed record ImportConflict(string BookId, string Title, int AnnotationConflicts, bool ProgressDiffers, bool AppearanceDiffers, bool MetadataDiffers = false);
public sealed record ImportPreview(int NewBooks, int ExistingBooks, IReadOnlyList<ImportConflict> Conflicts);

public static class BookDataRules
{
    public const int MaxAnnotations = 128;
    public const int MaxBookBytes = 2 * 1024 * 1024;
    public const int MaxCatalogBytes = 4 * 1024 * 1024;
    public const int MaxBackupBytes = 16 * 1024 * 1024;
    public const int MaxBooks = 1000;
    internal static readonly JsonSerializerOptions Json = new() { MaxDepth = 20 };
    public static bool IsId(string? id) => id?.Length == 64 && id.All(Uri.IsHexDigit) && id == id.ToUpperInvariant();
    internal static bool Bounded(string? value, int maximum) => value != null && value.Length <= maximum;

    public static void Validate(BookData data)
    {
        if (data == null || data.Schema != 1 || !IsId(data.BookId) || data.History == null || data.History.BookKey != data.BookId ||
            data.Annotations == null || data.Annotations.Count > MaxAnnotations)
            throw new InvalidDataException("逐书数据格式或数量不符合限制。");
        BookHistory h = data.History;
        if (!Bounded(h.FilePath, 2048) || !Bounded(h.Title, 256) || !Bounded(h.Author, 256) ||
            h.ChapterIndex is < 0 or >= ReaderLimits.SpineItems || h.BlockIndex is < 0 or >= ReaderLimits.ChapterBlocks ||
            !double.IsFinite(h.Fraction) || h.Fraction is < 0 or > 1 ||
            h.Locator != null && ReadingLocator.Normalize(h.Locator) == null || h.Bookmarks == null || h.Bookmarks.Count > 64)
            throw new InvalidDataException("逐书阅读位置不符合限制。");
        foreach (Bookmark b in h.Bookmarks)
            if (b == null || b.ChapterIndex is < 0 or >= ReaderLimits.SpineItems || b.BlockIndex is < 0 or >= ReaderLimits.ChapterBlocks ||
                !double.IsFinite(b.Fraction) || b.Fraction is < 0 or > 1 || !Bounded(b.Label, 100) ||
                b.Locator != null && ReadingLocator.Normalize(b.Locator) == null)
                throw new InvalidDataException("书签不符合限制。");
        var ids = new HashSet<Guid>();
        foreach (BookAnnotation a in data.Annotations)
            if (a == null || a.Id == Guid.Empty || !ids.Add(a.Id) || a.ChapterIndex is < 0 or >= ReaderLimits.SpineItems ||
                !Bounded(a.ResourcePath, 1024) || a.ResourcePath.Length == 0 ||
                a.Range == null || ReadingLocator.Normalize(a.Range.Start) == null || ReadingLocator.Normalize(a.Range.End) == null ||
                !Bounded(a.Range.Text, 4096) || a.Range.Text.Length == 0 || !Bounded(a.Note, 2000) ||
                a.Color is not ("Yellow" or "Green" or "Blue" or "Rose") || a.UpdatedUtc < a.CreatedUtc ||
                a.Range.Start.Paragraph > a.Range.End.Paragraph ||
                a.Range.Start.Paragraph == a.Range.End.Paragraph && a.Range.Start.Offset >= a.Range.End.Offset)
                throw new InvalidDataException("批注范围、颜色或文字不符合限制。");
        foreach (BookAnnotation a in data.Annotations)
            try { ArchivePath.ValidateEntry(a.ResourcePath); }
            catch (EpubException error) { throw new InvalidDataException("批注资源路径不安全。", error); }
        if (data.Appearance != null) ReaderPreferences.Validate(data.Appearance);
    }

    public static byte[] Snapshot(BookData data)
    {
        Validate(data);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, Json);
        if (bytes.Length > MaxBookBytes) throw new InvalidDataException("单书数据超过 2 MiB。");
        DataJsonBudget.Check(bytes);
        return bytes;
    }

    public static BookData Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBookBytes) throw new InvalidDataException("单书数据超过 2 MiB。");
        DataJsonBudget.Check(bytes);
        BookData data = JsonSerializer.Deserialize<BookData>(bytes, Json) ?? throw new InvalidDataException("逐书数据为空。");
        Validate(data); return data;
    }

    internal static void ValidateEntry(LibraryEntry e, bool backup = false)
    {
        if (e == null || !IsId(e.BookId) || !Bounded(e.Title, 256) || !Bounded(e.Author, 256) ||
            !Bounded(e.FilePath, 2048) || !Bounded(e.Series, 100) || e.Tags == null || e.Tags.Length > 16 ||
            e.Tags.Any(t => !Bounded(t, 40)) || e.ReadingStatus is not ("Unread" or "Reading" or "Finished") ||
            e.Title.Any(char.IsControl) || e.Author.Any(char.IsControl) || e.Series.Any(char.IsControl) || e.Tags.Any(t => t.Any(char.IsControl)) ||
            !backup && !Guid.TryParseExact(e.DataRevision, "N", out _))
            throw new InvalidDataException("书库元数据不符合限制。");
    }
}
