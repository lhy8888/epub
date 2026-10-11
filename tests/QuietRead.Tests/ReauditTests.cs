using System.Diagnostics;
using System.Text;
using System.Text.Json;
using QuietRead.Core;

internal static class ReauditTests
{
    internal static List<object> Measurements { get; } = [];
    internal static void Register(List<(string Name, Action Run)> tests, string root)
    {
        string Id(int n) => n.ToString("X64");
        LibraryStore Store() => new(Path.Combine(root, "reaudit-" + Guid.NewGuid().ToString("N")));
        BookData Open(LibraryStore store) => store.OpenBook(Id(1), Id(2), "first.epub", "Book", "Author");
        ParsedChapter Split(string text, int size = 4096) => new(Enumerable.Range(0, (text.Length + size - 1) / size).Select(i =>
            new BookBlock(BlockKind.Paragraph, [new BookInline(text.Substring(i * size, Math.Min(size, text.Length - i * size)))], ContinuesPrevious: i > 0)).ToArray(),
            new Dictionary<string, int>(), false, text.Length);
        BookAnnotation Note(TextRangeLocator range, string note = "") => new(Guid.NewGuid(), 0, "OEBPS/c0.xhtml", range, "Yellow", note, DateTime.UnixEpoch, DateTime.UnixEpoch);
        void Test(string name, Action action) => tests.Add((name, action));

        Test("All legal Unicode annotation and bookmark maxima can save, reload and back up", () =>
        {
            LibraryStore s = Store(); BookData d = Open(s);
            var start = new ReadingLocator(0, 0, new string('中', 48), new string('中', 16), new string('中', 16));
            var range = new TextRangeLocator(start, start with { Offset = 4096 }, new string('中', 4096));
            d.History.FilePath = new string('中', 2048); d.History.Title = d.History.Author = new string('中', 256); d.History.Locator = start;
            d.History.Bookmarks = Enumerable.Range(0, 64).Select(i => new Bookmark { ChapterIndex = i, Label = new string('中', 100), Locator = start }).ToList();
            d.Annotations = Enumerable.Range(0, 128).Select(_ => Note(range, new string('中', 2000)) with { ResourcePath = "O/" + new string('中', 1022) }).ToList();
            byte[] bytes = BookDataRules.Snapshot(d);
            Check.True(bytes.Length > 2 * 1024 * 1024 && bytes.Length < BookDataRules.MaxBookBytes);
            s.SaveBook(bytes); Check.Equal(128, s.LoadBook(d.BookId).Annotations.Count);
            var restored = Store(); restored.Import(s.ExportBackup(), false);
            Check.Equal(128, restored.LoadBook(d.BookId).Annotations.Count); Check.Equal(64, restored.LoadBook(d.BookId).History.Bookmarks.Count);
            Measurements.Add(new { name = "Full Unicode book data", serializedBytes = bytes.Length });
        });
        Test("Duplicate and escaped typed JSON properties are rejected before replaced arrays allocate", () =>
        {
            var d = Open(Store()); var range = TextRangeLocation.Capture(Split("hello world"), 0, 0, 0, 5);
            string row = JsonSerializer.Serialize(Note(range));
            string array = "[" + string.Join(',', Enumerable.Repeat(row, 128)) + "]";
            byte[] input = Encoding.UTF8.GetBytes("{\"BookId\":\"" + d.BookId + "\",\"History\":{\"BookKey\":\"" + d.BookId + "\"}," +
                string.Join(',', Enumerable.Repeat("\"Annotations\":" + array, 25)) + ",\"Annotations\":[]}");
            Check.Throws<InvalidDataException>(() => BookDataRules.Parse(input));
            long before = GC.GetAllocatedBytesForCurrentThread(); Check.Throws<InvalidDataException>(() => BookDataRules.Parse(input));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before; Check.True(allocated < 64 * 1024);
            Check.Throws<InvalidDataException>(() => LibraryStore.ParseBackup("{\"Books\":[],\"Boo\\u006Bs\":[]}"u8));
            string directory = Path.Combine(root, "duplicate-index"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "library.json"), "{\"Schema\":1,\"S\\u0063hema\":1,\"Books\":[]}");
            Check.Throws<InvalidDataException>(() => new LibraryStore(directory).Entries());
            Measurements.Add(new { name = "Reject duplicate arrays", inputBytes = input.Length, allocatedBytes = allocated });
        });
        Test("Reopening invalidates snapshot deduplication and hot or cold saves agree", () =>
        {
            var s = Store(); BookData d = Open(s); byte[] a = BookDataRules.Snapshot(d); s.SaveBook(a, 1);
            s.OpenBook(d.BookId, Id(2), "moved.epub", "Book", "Author"); s.SaveBook(a, 2);
            Check.Equal("first.epub", s.LoadBook(d.BookId).History.FilePath);
        });
        Test("Whole-word boundaries handle Han-Latin, supplementary Han and punctuation queries", () =>
        {
            var whole = new SearchOptions(WholeWord: true);
            Check.Equal(1, SearchText.Find("A中文B", "中文", 0, whole));
            Check.Equal(0, SearchText.Find("中文B", "中文", 0, whole));
            Check.Equal(1, SearchText.Find("A中", "中", 0, whole));
            Check.Equal(1, SearchText.Find("A\U00020000B", "\U00020000", 0, whole));
            Check.Equal(6, SearchText.Find("learn C++programming", "C++", 0, whole));
            Check.Equal(-1, SearchText.Find("xhello hello_2 café", "hello", 0, whole));
            Check.Equal(-1, SearchText.Find("café", "cafe", 0, whole));
        });
        Test("Tiny selections in maximum paragraphs allocate bounded memory and cancel", () =>
        {
            ParsedChapter chapter = Split(new string('x', ReaderLimits.ChapterCharacters));
            int last = chapter.Blocks.Count - 1, end = chapter.Blocks[last].Inlines[0].Text.Length;
            _ = TextRangeLocation.Capture(chapter, last, end - 10, last, end);
            long before = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
            TextRangeLocator range = TextRangeLocation.Capture(chapter, last, end - 10, last, end); watch.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check.Equal(new string('x', 10), range.Text); Check.True(allocated < 128 * 1024);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Check.Throws<OperationCanceledException>(() => TextRangeLocation.Capture(chapter, 0, 0, last, end, cancel.Token));
            Measurements.Add(new { name = "Maximum paragraph tiny selection", allocatedBytes = allocated, milliseconds = watch.Elapsed.TotalMilliseconds });
        });
        Test("Chunked graphemes preserve cross-block ZWJ, RI pairs and combining text", () =>
        {
            string text = new string('x', 8190) + "👩‍💻🇺🇸é tail";
            ParsedChapter chapter = Split(text, 777);
            int at = 8191, finish = 8198;
            TextRangeLocator range = TextRangeLocation.Capture(chapter, at / 777, at % 777, finish / 777, finish % 777);
            Check.Equal("👩‍💻🇺🇸", range.Text);
            ParsedChapter oversized = Split("a" + new string('\u0301', 9000));
            Check.Throws<InvalidDataException>(() => TextRangeLocation.Capture(oversized, 0, 0, 0, 10));
            Check.True(ReadingLocator.Normalize(TextLocation.Capture(oversized, 1, 0)) != null);
        });
        Test("Distinct stale ranges share a bounded scan budget while precise ranges still resolve", () =>
        {
            ParsedChapter chapter = Split(new string('x', ReaderLimits.ChapterCharacters));
            var notes = Enumerable.Range(0, 127).Select(i => Note(new TextRangeLocator(new ReadingLocator(0, 0, "absent-" + i, "", ""),
                new ReadingLocator(0, 10, "absent-end", "", ""), "absent"))).ToList();
            var good = TextRangeLocation.Capture(chapter, 0, 0, 0, 10); notes.Add(Note(good));
            long before = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
            var spans = TextRangeLocation.Resolve(chapter, notes, 0, "OEBPS/c0.xhtml"); watch.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check.Equal(1, spans.Count); Check.Equal(10, spans[0].End - spans[0].Start); Check.True(allocated < 1024 * 1024);
            Measurements.Add(new { name = "128 distinct stale/precise annotations", allocatedBytes = allocated, milliseconds = watch.Elapsed.TotalMilliseconds });
        });
        Test("Unique locator fallback crosses chunk boundaries and refuses repeated context", () =>
        {
            string text = new string('x', 4080) + " unique passage after a block edge " + new string('y', 100);
            ParsedChapter old = Split(text); ReadingLocator locator = TextLocation.Capture(old, 0, 4090);
            var moved = Split(text, 777);
            var shifted = moved with { Blocks = new[] { new BookBlock(BlockKind.Paragraph, [new BookInline("prefix")]) }.Concat(moved.Blocks).ToArray() };
            var resolved = TextLocation.Resolve(shifted, locator); Check.True(resolved != null);
            Check.Equal(4090, TextLocation.Capture(shifted, resolved!.Value.Block, resolved.Value.Offset).Offset);
            var duplicate = shifted with { Blocks = shifted.Blocks.Concat(moved.Blocks).ToArray() };
            Check.True(TextLocation.Resolve(duplicate, locator) == null);
        });
    }
}
