using System.Windows;
using System.Windows.Documents;
using QuietRead.Core;

namespace QuietRead;

internal static class DocumentPosition
{
    public static TextPointer? Pointer(Block block, int offset)
    {
        if (block is not Paragraph paragraph) return block.ContentStart;
        foreach (Run run in paragraph.Inlines.OfType<Span>().SelectMany(s => s.Inlines.OfType<Run>()))
        {
            if (offset <= run.Text.Length) return run.ContentStart.GetPositionAtOffset(offset, LogicalDirection.Forward);
            offset -= run.Text.Length;
        }
        return paragraph.ContentEnd;
    }

    public static (int Block, int Offset) Capture(FlowDocument document, IReadOnlyDictionary<int, Block> blocks, double verticalOffset)
    {
        Rect origin = document.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        double top = origin.IsEmpty ? verticalOffset : origin.Top + verticalOffset;
        foreach ((int index, Block block) in blocks)
        {
            Rect end = block.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
            if (!end.IsEmpty && end.Bottom < top) continue;
            if (block is not Paragraph paragraph) return (index, 0);
            int length = paragraph.Inlines.OfType<Span>().SelectMany(s => s.Inlines.OfType<Run>()).Sum(r => r.Text.Length);
            int low = 0, high = length;
            // Layout is monotonic along the reading axis, including RTL text.
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                Rect rect = Pointer(block, middle)!.GetCharacterRect(LogicalDirection.Forward);
                if (!rect.IsEmpty && rect.Bottom < top) low = middle + 1; else high = middle;
            }
            return (index, low);
        }
        return (blocks.Keys.LastOrDefault(), 0);
    }

    public static double? Offset(FlowDocument document, Block block, int character)
    {
        TextPointer? pointer = Pointer(block, character);
        if (pointer == null || !pointer.HasValidLayout) return null;
        Rect target = pointer.GetCharacterRect(LogicalDirection.Forward);
        Rect origin = document.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        return target.IsEmpty || origin.IsEmpty ? null : Math.Max(0, target.Top - origin.Top);
    }
}
