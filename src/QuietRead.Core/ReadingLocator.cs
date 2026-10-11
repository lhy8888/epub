using System.Globalization;

namespace QuietRead.Core;

// Logical paragraphs, unlike rendering blocks or screen fractions, survive reflow
// and changes to the artificial block size. Outer records retain the spine index.
public sealed record ReadingLocator(int Paragraph, int Offset, string Quote, string Before, string After)
{
    public static ReadingLocator? Normalize(ReadingLocator? value) => value is null ||
        value.Paragraph < 0 || value.Paragraph >= ReaderLimits.ChapterBlocks || value.Offset < 0 ||
        value.Offset > ReaderLimits.ChapterCharacters + 3 || value.Quote is null || value.Before is null || value.After is null ||
        value.Quote.Length > 48 || value.Before.Length > 16 || value.After.Length > 16 ? null : value;
}

public static class TextLocation
{
    public static ReadingLocator Capture(ParsedChapter chapter, int block, int offset)
    {
        block = Math.Clamp(block, 0, chapter.Blocks.Count - 1);
        int start = block;
        while (start > 0 && chapter.Blocks[start].ContinuesPrevious) start--;
        int paragraph = 0;
        for (int i = 1; i <= start; i++) if (!chapter.Blocks[i].ContinuesPrevious) paragraph++;
        string current = chapter.Blocks[block].PlainText;
        int logicalOffset = Boundary(current, Math.Clamp(offset, 0, current.Length));
        for (int i = start; i < block; i++) logicalOffset += Length(chapter.Blocks[i]);
        int windowStart = Math.Max(0, logicalOffset - 18);
        string text = Slice(chapter, start, windowStart, 84);
        int at = logicalOffset - windowStart;
        int quoteEnd = Math.Max(at, Boundary(text, Math.Min(text.Length, at + 48)));
        int before = Math.Max(0, at - 16);
        if (before < text.Length && char.IsLowSurrogate(text[before])) before++;
        int after = Math.Max(quoteEnd, Boundary(text, Math.Min(text.Length, quoteEnd + 16)));
        return new ReadingLocator(paragraph, logicalOffset, text[at..quoteEnd], text[before..at], text[quoteEnd..after]);
    }

    public static (int Block, int Offset)? Resolve(ParsedChapter chapter, ReadingLocator? locator, CancellationToken token = default)
        => Resolve(chapter, locator, new LocatorWorkBudget(), token);

    internal static (int Block, int Offset)? Resolve(ParsedChapter chapter, ReadingLocator? locator, LocatorWorkBudget budget, CancellationToken token)
    {
        if (ReadingLocator.Normalize(locator) is not { } value) return null;
        int paragraph = 0;
        // First use the precise logical address and verify the saved text.
        for (int start = 0; start < chapter.Blocks.Count;)
        {
            token.ThrowIfCancellationRequested();
            int end = start + 1;
            while (end < chapter.Blocks.Count && chapter.Blocks[end].ContinuesPrevious) end++;
            if (paragraph == value.Paragraph)
            {
                int length = 0;
                for (int i = start; i < end; i++) length += Length(chapter.Blocks[i]);
                if (value.Offset <= length)
                {
                    var location = ToBlock(chapter, start, end, value.Offset);
                    if (Capture(chapter, location.Block, location.Offset) == value) return location;
                }
                break;
            }
            paragraph++; start = end;
        }
        if (value.Quote.Length == 0) return null;
        (int Block, int Offset)? candidate = null;
        // A parser migration may move a logical paragraph. Only a unique quote
        // with its context is safe to recover; repeated sentences are ambiguous.
        Span<char> buffer = stackalloc char[ChapterText.BufferCharacters];
        for (int block = 0; block < chapter.Blocks.Count; block++)
        {
            token.ThrowIfCancellationRequested();
            if (!budget.Spend(Length(chapter.Blocks[block]) + 160)) return null;
            ReadOnlySpan<char> text = ChapterText.CopyContext(chapter, block, buffer, 80, out int prefix, out int length);
            int at = prefix;
            while (at <= text.Length - value.Quote.Length)
            {
                token.ThrowIfCancellationRequested();
                int next = text[at..].IndexOf(value.Quote, StringComparison.Ordinal);
                if (next < 0) break;
                int found = at + next;
                if (found >= prefix + length) break;
                if (!budget.Spend(value.Before.Length + value.Quote.Length + value.After.Length + 1)) return null;
                if (Matches(text, found, value))
                {
                    if (candidate != null) return null;
                    candidate = (block, found - prefix);
                }
                at = found + 1;
            }
        }
        return candidate;
    }

    private static bool Matches(ReadOnlySpan<char> text, int offset, ReadingLocator value) =>
        offset >= value.Before.Length && offset + value.Quote.Length + value.After.Length <= text.Length &&
        text.Slice(offset - value.Before.Length, value.Before.Length).SequenceEqual(value.Before) &&
        text.Slice(offset, value.Quote.Length).SequenceEqual(value.Quote) &&
        text.Slice(offset + value.Quote.Length, value.After.Length).SequenceEqual(value.After);

    private static (int Block, int Offset) ToBlock(ParsedChapter chapter, int start, int end, int offset)
    {
        for (int i = start; i < end; i++)
        {
            int length = Length(chapter.Blocks[i]);
            if (offset < length || i == end - 1) return (i, Math.Min(offset, length));
            offset -= length;
        }
        return (start, 0);
    }

    private static int Boundary(string text, int offset)
    {
        if (offset == 0 || offset == text.Length) return offset;
        if (char.IsLowSurrogate(text[offset])) offset--;
        while (offset > 0 && CharUnicodeInfo.GetUnicodeCategory(text, offset) is UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
        { offset--; if (char.IsLowSurrogate(text[offset])) offset--; }
        return offset;
    }

    private static int Length(BookBlock block)
    { int length = 0; for (int i = 0; i < block.Inlines.Count; i++) length += block.Inlines[i].Text.Length; return length; }

    private static string Slice(ParsedChapter chapter, int start, int offset, int count)
    {
        var text = new System.Text.StringBuilder(count);
        for (int i = start; i < chapter.Blocks.Count && (i == start || chapter.Blocks[i].ContinuesPrevious) && text.Length < count; i++)
            foreach (BookInline inline in chapter.Blocks[i].Inlines)
            {
                if (offset >= inline.Text.Length) { offset -= inline.Text.Length; continue; }
                int length = Math.Min(count - text.Length, inline.Text.Length - offset);
                text.Append(inline.Text.AsSpan(offset, length)); offset = 0;
                if (text.Length == count) break;
            }
        // The extra four units give context boundaries room to move before a
        // surrogate pair/combining mark without retaining an entire paragraph.
        return text.ToString();
    }
}

// Shared across all annotation endpoints in one view preparation. It bounds
// failed scans and candidate verification, while precise addresses remain usable.
internal sealed class LocatorWorkBudget
{
    private int _remaining = ReaderLimits.ChapterCharacters * 8;
    internal bool Spend(int units)
    { if (units > _remaining) return false; _remaining -= units; return true; }
}
