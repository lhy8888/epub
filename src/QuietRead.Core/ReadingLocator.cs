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
        int quoteEnd = Boundary(text, Math.Min(text.Length, at + 48));
        int before = Math.Max(0, at - 16);
        if (before < text.Length && char.IsLowSurrogate(text[before])) before++;
        int after = Boundary(text, Math.Min(text.Length, quoteEnd + 16));
        return new ReadingLocator(paragraph, logicalOffset, text[at..quoteEnd], text[before..at], text[quoteEnd..after]);
    }

    public static (int Block, int Offset)? Resolve(ParsedChapter chapter, ReadingLocator? locator, CancellationToken token = default)
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
        for (int start = 0; start < chapter.Blocks.Count;)
        {
            token.ThrowIfCancellationRequested();
            string text = ParagraphText(chapter, start, out int end);
            int at = 0;
            while (at <= text.Length - value.Quote.Length)
            {
                token.ThrowIfCancellationRequested();
                int found = text.IndexOf(value.Quote, at, StringComparison.Ordinal);
                if (found < 0) break;
                if (Matches(text, found, value))
                {
                    if (candidate != null) return null;
                    candidate = ToBlock(chapter, start, end, found);
                }
                at = found + 1;
            }
            start = end;
        }
        return candidate;
    }

    private static bool Matches(string text, int offset, ReadingLocator value) =>
        offset >= value.Before.Length && offset + value.Quote.Length + value.After.Length <= text.Length &&
        text.AsSpan(offset - value.Before.Length, value.Before.Length).SequenceEqual(value.Before) &&
        text.AsSpan(offset, value.Quote.Length).SequenceEqual(value.Quote) &&
        text.AsSpan(offset + value.Quote.Length, value.After.Length).SequenceEqual(value.After);

    private static string ParagraphText(ParsedChapter chapter, int start, out int end)
    {
        end = start + 1;
        while (end < chapter.Blocks.Count && chapter.Blocks[end].ContinuesPrevious) end++;
        if (end == start + 1) return chapter.Blocks[start].PlainText;
        var text = new System.Text.StringBuilder();
        for (int i = start; i < end; i++) foreach (BookInline inline in chapter.Blocks[i].Inlines) text.Append(inline.Text);
        return text.ToString();
    }

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

    private static int Length(BookBlock block) => block.Inlines.Sum(inline => inline.Text.Length);

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
