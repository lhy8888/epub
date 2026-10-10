namespace QuietRead.Core;

/// <summary>Bounded context across artificial splits, never across real paragraph boundaries.</summary>
public static class ChapterText
{
    public const int BufferCharacters = ReaderLimits.BlockCharacters + 3 + 2 * 127;

    public static ReadOnlySpan<char> CopyContext(ParsedChapter chapter, int index, Span<char> buffer,
        int contextCharacters, out int prefixLength, out int blockLength)
    {
        if (contextCharacters is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(contextCharacters));
        prefixLength = 0;
        int cursor = index;
        while (cursor > 0 && chapter.Blocks[cursor].ContinuesPrevious && prefixLength < contextCharacters)
        {
            cursor--;
            prefixLength += CopyTail(chapter.Blocks[cursor], buffer[..(contextCharacters - prefixLength)]);
        }
        blockLength = 0;
        IReadOnlyList<BookInline> inlines = chapter.Blocks[index].Inlines;
        for (int i = 0; i < inlines.Count; i++)
        {
            inlines[i].Text.AsSpan().CopyTo(buffer[(contextCharacters + blockLength)..]);
            blockLength += inlines[i].Text.Length;
        }
        int suffixLength = 0;
        cursor = index;
        while (cursor + 1 < chapter.Blocks.Count && chapter.Blocks[cursor + 1].ContinuesPrevious && suffixLength < contextCharacters)
        {
            cursor++;
            suffixLength += CopyHead(chapter.Blocks[cursor], buffer.Slice(contextCharacters + blockLength + suffixLength,
                contextCharacters - suffixLength));
        }
        return buffer.Slice(contextCharacters - prefixLength, prefixLength + blockLength + suffixLength);
    }

    private static int CopyTail(BookBlock block, Span<char> destination)
    {
        int written = 0;
        for (int i = block.Inlines.Count - 1; i >= 0 && written < destination.Length; i--)
        {
            ReadOnlySpan<char> text = block.Inlines[i].Text;
            int length = Math.Min(text.Length, destination.Length - written);
            text[^length..].CopyTo(destination.Slice(destination.Length - written - length, length));
            written += length;
        }
        return written;
    }

    private static int CopyHead(BookBlock block, Span<char> destination)
    {
        int written = 0;
        for (int i = 0; i < block.Inlines.Count && written < destination.Length; i++)
        {
            ReadOnlySpan<char> text = block.Inlines[i].Text;
            int length = Math.Min(text.Length, destination.Length - written);
            text[..length].CopyTo(destination[written..]);
            written += length;
        }
        return written;
    }
}
