namespace QuietRead.Core;

// Both renderers use the same boundaries so navigation and restoration agree
// with the actual amount of text and formatting in a view.
public static class ChapterSegments
{
    public static int[] BuildStarts(ParsedChapter chapter)
    {
        var starts = new List<int> { 0 };
        int characters = 0, blocks = 0, inlines = 0;
        for (int index = 0; index < chapter.Blocks.Count; index++)
        {
            BookBlock block = chapter.Blocks[index];
            int length = block.Inlines.Sum(inline => inline.Text.Length);
            if (blocks > 0 && (blocks >= ReaderLimits.BlocksPerView ||
                characters + length > ReaderLimits.CharactersPerView ||
                inlines + block.Inlines.Count > ReaderLimits.InlinesPerView))
            { starts.Add(index); characters = 0; blocks = 0; inlines = 0; }
            characters += length; blocks++; inlines += block.Inlines.Count;
        }
        return starts.ToArray();
    }

    public static int FindIndex(int[] starts, int block)
    {
        int found = Array.BinarySearch(starts, block);
        return found >= 0 ? found : Math.Max(0, ~found - 1);
    }
}
