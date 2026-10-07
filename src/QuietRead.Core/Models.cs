namespace QuietRead.Core;

public static class ReaderLimits
{
    public const long ArchiveBytes = 512L * 1024 * 1024;
    public const long ExpandedBytes = 1024L * 1024 * 1024;
    public const long EntryBytes = 64L * 1024 * 1024;
    public const int XmlBytes = 8 * 1024 * 1024;
    public const int ImageBytes = 12 * 1024 * 1024;
    public const int Entries = 12_000;
    public const int SpineItems = 4_096;
    public const int XmlDepth = 64;
    public const int XmlNodes = 75_000;
    public const int ChapterBlocks = 6_000;
    public const int ChapterInlines = 30_000;
    public const int ChapterCharacters = 2_000_000;
    public const int CacheChapters = 2;
    public const int CacheCharacters = 2_000_000;
    public const long CacheBytes = 8L * 1024 * 1024;
    public const int BlockCharacters = 4096;
    public const int BlockInlines = 512;
    public const int BlocksPerView = 180;
    public const int SearchResults = 200;
    public const long ImageViewPixels = 4_000_000;
}

public sealed class EpubException(string message, Exception? inner = null) : Exception(message, inner);

public enum BlockKind { Paragraph, Heading, Quote, Code, ListItem, Image, Rule, TableRow }

[Flags]
public enum TextStyle { None = 0, Bold = 1, Italic = 2, Code = 4, Superscript = 8, Subscript = 16 }

public sealed record LocalLink(string Path, string Fragment);
public sealed record BookInline(string Text, TextStyle Style = TextStyle.None, LocalLink? Link = null);
public sealed record BookBlock(BlockKind Kind, IReadOnlyList<BookInline> Inlines,
    int Level = 0, string? ImagePath = null, string? Alt = null)
{
    public string PlainText => string.Concat(Inlines.Select(x => x.Text));
}

public sealed record BookChapter(string Path, string Title);
public sealed record TocItem(string Title, int ChapterIndex, string Fragment, int Depth);
public sealed record ParsedChapter(IReadOnlyList<BookBlock> Blocks,
    IReadOnlyDictionary<string, int> Anchors, bool RightToLeft, int CharacterCount)
{
    // A conservative model estimate, not a measurement of total process memory.
    public long EstimatedMemoryBytes => 256L + Blocks.Sum(block => 128L +
        2L * ((block.Alt?.Length ?? 0) + (block.ImagePath?.Length ?? 0)) +
        block.Inlines.Sum(inline => 96L + 2L * inline.Text.Length + (inline.Link == null ? 0 :
            64L + 2L * (inline.Link.Path.Length + inline.Link.Fragment.Length)))) +
        Anchors.Sum(anchor => 96L + 2L * anchor.Key.Length);
}
public sealed record SearchHit(int ChapterIndex, int BlockIndex, string ChapterTitle, string Snippet);
public sealed record SearchOutcome(IReadOnlyList<SearchHit> Hits, int FailedChapters, bool LimitReached);
public sealed record RasterData(byte[] Bytes, RasterInfo Info);
public readonly record struct RasterInfo(int Width, int Height, string Format);
