using System.Globalization;
using System.Text;

namespace QuietRead.Core;

public sealed record AnnotationSpan(int Block, int Start, int End, string Color);

public static class TextRangeLocation
{
    public static TextRangeLocator Capture(ParsedChapter chapter, int startBlock, int startOffset, int endBlock, int endOffset, CancellationToken token = default)
    {
        var start = GraphemeBoundary(chapter, startBlock, startOffset, forward: false, token);
        var end = GraphemeBoundary(chapter, endBlock, endOffset, forward: true, token);
        string text = Extract(chapter, start, end);
        if (text.Length is 0 or > 4096 || string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("请选择 1–4096 个文字字符。");
        return new TextRangeLocator(TextLocation.Capture(chapter, start.Block, start.Offset), TextLocation.Capture(chapter, end.Block, end.Offset), text);
    }

    public static IReadOnlyList<AnnotationSpan> Resolve(ParsedChapter chapter, IEnumerable<BookAnnotation> annotations, int chapterIndex, string resource, CancellationToken token = default)
    {
        var result = new List<AnnotationSpan>();
        var budget = new LocatorWorkBudget();
        var locations = new Dictionary<ReadingLocator, (int Block, int Offset)?>();
        (int Block, int Offset)? Location(ReadingLocator locator)
        {
            if (!locations.TryGetValue(locator, out var position))
            { position = TextLocation.Resolve(chapter, locator, budget, token); locations.Add(locator, position); }
            return position;
        }
        foreach (BookAnnotation a in annotations.Take(BookDataRules.MaxAnnotations))
        {
            token.ThrowIfCancellationRequested();
            if (a.ChapterIndex != chapterIndex || a.ResourcePath != resource) continue;
            var start = Location(a.Range.Start);
            if (start == null) continue;
            var end = Location(a.Range.End);
            if (start == null || end == null || start.Value.Block > end.Value.Block ||
                start.Value.Block == end.Value.Block && start.Value.Offset >= end.Value.Offset) continue;
            // A stale or edited range must never highlight an unrelated passage.
            try { if (Extract(chapter, start.Value, end.Value) != a.Range.Text) continue; }
            catch (InvalidDataException) { continue; }
            for (int i = start.Value.Block; i <= end.Value.Block; i++)
            {
                int begin = i == start.Value.Block ? start.Value.Offset : 0;
                int finish = i == end.Value.Block ? end.Value.Offset : chapter.Blocks[i].Inlines.Sum(x => x.Text.Length);
                if (finish > begin) result.Add(new AnnotationSpan(i, begin, finish, a.Color));
            }
        }
        return result;
    }

    public static string Extract(ParsedChapter chapter, (int Block, int Offset) start, (int Block, int Offset) end)
    {
        var text = new StringBuilder();
        for (int i = start.Block; i <= end.Block && i < chapter.Blocks.Count; i++)
        {
            string block = chapter.Blocks[i].PlainText;
            int begin = i == start.Block ? Math.Clamp(start.Offset, 0, block.Length) : 0;
            int finish = i == end.Block ? Math.Clamp(end.Offset, 0, block.Length) : block.Length;
            if (finish < begin) throw new InvalidDataException("选区顺序无效。");
            if (i > start.Block && !chapter.Blocks[i].ContinuesPrevious) text.Append('\n');
            if (text.Length + finish - begin > 4096) throw new InvalidDataException("选区超过 4096 字符。");
            text.Append(block.AsSpan(begin, finish - begin));
        }
        return text.ToString();
    }

    private static (int Block, int Offset) GraphemeBoundary(ParsedChapter chapter, int block, int offset, bool forward, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (block < 0 || block >= chapter.Blocks.Count) throw new InvalidDataException("选区不在正文中。");
        int start = block;
        while (start > 0 && chapter.Blocks[start].ContinuesPrevious) start--;
        int requested = Math.Clamp(offset, 0, Length(chapter.Blocks[block])), end = start;
        while (end < chapter.Blocks.Count && (end == start || chapter.Blocks[end].ContinuesPrevious))
        {
            if (end < block) requested += Length(chapter.Blocks[end]);
            end++;
        }

        // Stream from a real paragraph boundary. Keep the final grapheme until
        // the next chunk arrives: surrogate pairs, RI pairs and ZWJ sequences
        // can extend across both inline and artificial block boundaries.
        Span<char> buffer = stackalloc char[8192];
        int count = 0, position = 0, readBlock = start, readInline = 0, readOffset = 0;
        while (position < requested)
        {
            token.ThrowIfCancellationRequested();
            while (count < buffer.Length && readBlock < end)
            {
                var inlines = chapter.Blocks[readBlock].Inlines;
                if (readInline >= inlines.Count) { readBlock++; readInline = 0; continue; }
                ReadOnlySpan<char> text = inlines[readInline].Text.AsSpan(readOffset);
                int copied = Math.Min(text.Length, buffer.Length - count);
                text[..copied].CopyTo(buffer[count..]); count += copied; readOffset += copied;
                if (readOffset == inlines[readInline].Text.Length) { readInline++; readOffset = 0; }
            }
            int consumed = 0;
            while (consumed < count)
            {
                ReadOnlySpan<char> remaining = buffer.Slice(consumed, count - consumed);
                int run = remaining[0] is >= ' ' and <= '~' ? remaining.IndexOfAnyExceptInRange(' ', '~') :
                    remaining[0] is >= '\u4E00' and <= '\u9FFF' ? remaining.IndexOfAnyExceptInRange('\u4E00', '\u9FFF') : 0;
                if (run < 0) run = remaining.Length;
                // These characters are individual graphemes. Retain the last
                // one because a following combining mark can still attach to it.
                if (run > 1)
                {
                    if (position + consumed + run - 1 >= requested) return ToBlock(chapter, start, end, requested);
                    consumed += run - 1; continue;
                }
                int length = StringInfo.GetNextTextElementLength(buffer.Slice(consumed, count - consumed));
                if (readBlock < end && consumed + length == count) break;
                if (length > 4096) throw new InvalidDataException("字素组合超过 4096 字符，无法创建批注。");
                int next = position + consumed + length;
                if (next >= requested)
                {
                    if (next > requested) requested = forward ? next : position + consumed;
                    return ToBlock(chapter, start, end, requested);
                }
                consumed += length;
            }
            if (consumed == 0 && count == buffer.Length) throw new InvalidDataException("字素组合过长，无法创建批注。");
            if (readBlock == end && consumed == count) return ToBlock(chapter, start, end, position + count);
            buffer.Slice(consumed, count - consumed).CopyTo(buffer);
            count -= consumed; position += consumed;
        }
        return ToBlock(chapter, start, end, requested);
    }

    private static int Length(BookBlock block)
    { int length = 0; for (int i = 0; i < block.Inlines.Count; i++) length += block.Inlines[i].Text.Length; return length; }
    private static (int Block, int Offset) ToBlock(ParsedChapter chapter, int start, int end, int requested)
    {
        for (int i = start; i < end; i++)
        {
            int length = Length(chapter.Blocks[i]);
            if (requested < length || i == end - 1) return (i, Math.Min(requested, length));
            requested -= length;
        }
        return (start, 0);
    }
}
