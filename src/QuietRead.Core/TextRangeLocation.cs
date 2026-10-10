using System.Globalization;
using System.Text;

namespace QuietRead.Core;

public sealed record AnnotationSpan(int Block, int Start, int End, string Color);

public static class TextRangeLocation
{
    public static TextRangeLocator Capture(ParsedChapter chapter, int startBlock, int startOffset, int endBlock, int endOffset)
    {
        var start = GraphemeBoundary(chapter, startBlock, startOffset, forward: false);
        var end = GraphemeBoundary(chapter, endBlock, endOffset, forward: true);
        string text = Extract(chapter, start, end);
        if (text.Length is 0 or > 4096 || string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("请选择 1–4096 个文字字符。");
        return new TextRangeLocator(TextLocation.Capture(chapter, start.Block, start.Offset), TextLocation.Capture(chapter, end.Block, end.Offset), text);
    }

    public static IReadOnlyList<AnnotationSpan> Resolve(ParsedChapter chapter, IEnumerable<BookAnnotation> annotations, int chapterIndex, string resource, CancellationToken token = default)
    {
        var result = new List<AnnotationSpan>();
        foreach (BookAnnotation a in annotations.Take(BookDataRules.MaxAnnotations))
        {
            token.ThrowIfCancellationRequested();
            if (a.ChapterIndex != chapterIndex || a.ResourcePath != resource) continue;
            var start = TextLocation.Resolve(chapter, a.Range.Start, token);
            var end = TextLocation.Resolve(chapter, a.Range.End, token);
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

    private static (int Block, int Offset) GraphemeBoundary(ParsedChapter chapter, int block, int offset, bool forward)
    {
        if (block < 0 || block >= chapter.Blocks.Count) throw new InvalidDataException("选区不在正文中。");
        int start = block;
        while (start > 0 && chapter.Blocks[start].ContinuesPrevious) start--;
        var logical = new StringBuilder(); int requested = offset, end = start;
        while (end < chapter.Blocks.Count && (end == start || chapter.Blocks[end].ContinuesPrevious))
        {
            string text = chapter.Blocks[end].PlainText;
            if (end < block) requested += text.Length;
            logical.Append(text); end++;
        }
        string value = logical.ToString(); requested = Math.Clamp(requested, 0, value.Length);
        int position = 0;
        while (position < requested)
        {
            int next = position + StringInfo.GetNextTextElementLength(value.AsSpan(position));
            if (next > requested) { requested = forward ? next : position; break; }
            position = next;
        }
        for (int i = start; i < end; i++)
        {
            int length = chapter.Blocks[i].Inlines.Sum(x => x.Text.Length);
            if (requested < length || i == end - 1) return (i, Math.Min(requested, length));
            requested -= length;
        }
        return (block, offset);
    }
}
