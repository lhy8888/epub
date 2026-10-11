using System.Globalization;
using System.Text;

namespace QuietRead.Core;

public sealed record SearchOptions(bool MatchCase = false, bool WholeWord = false, int? ChapterIndex = null);

public static class SearchText
{
    public static int Find(ReadOnlySpan<char> text, ReadOnlySpan<char> query, int start, SearchOptions? options = null)
    {
        if (query.IsEmpty) return -1;
        options ??= new SearchOptions();
        while (start <= text.Length - query.Length)
        {
            int found = text[start..].IndexOf(query, options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            if (found < 0) return -1;
            found += start;
            if (!options.WholeWord || (!WordBefore(text, found) && !WordAfter(text, found + query.Length))) return found;
            start = found + 1;
        }
        return -1;
    }

    private static bool WordBefore(ReadOnlySpan<char> text, int at)
    {
        if (at == 0) return false;
        Rune.DecodeLastFromUtf16(text[..at], out Rune left, out _);
        Rune.DecodeFromUtf16(text[at..], out Rune right, out _);
        return IsWord(left) && IsWord(right);
    }
    private static bool WordAfter(ReadOnlySpan<char> text, int at)
    {
        if (at == text.Length) return false;
        Rune.DecodeLastFromUtf16(text[..at], out Rune left, out _);
        Rune.DecodeFromUtf16(text[at..], out Rune right, out _);
        return IsWord(left) && IsWord(right);
    }
    private static bool IsWord(Rune rune)
    {
        // Han characters have individual boundaries; Latin letters, accents,
        // digits, marks and connector punctuation form continuous words.
        int value = rune.Value;
        if (value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF) return false;
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or
            UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber or
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.ConnectorPunctuation;
    }
}
