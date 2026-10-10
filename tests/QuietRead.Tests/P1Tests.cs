using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuietRead.Core;

internal static class P1Tests
{
    public static void Register(List<(string Name, Action Run)> tests, string root)
    {
        int fixture = 0;
        string Book(string body)
        {
            string path = Path.Combine(root, "p1-" + fixture++ + ".epub"); File.WriteAllBytes(path, Fixtures.Zip(Fixtures.Book([body]))); return path;
        }
        LibraryStore Store() => new(Path.Combine(root, "library-" + Guid.NewGuid().ToString("N")));
        string Id(int number) => number.ToString("X64");
        BookData Open(LibraryStore s, int number) => s.OpenBook(Id(number), Id(number + 5000), "book.epub", "Book " + number, "Author");
        using var source = EpubBook.Open(Book("<p>alpha <strong>bold</strong> é 😀 👩‍💻 tail</p>"));
        ParsedChapter chapter = source.ReadChapter(0);
        TextRangeLocator range = TextRangeLocation.Capture(chapter, 0, 0, 0, 10);
        BookAnnotation Annotation(Guid? id = null, string note = "note") => new(id ?? Guid.NewGuid(), 0, "OEBPS/c0.xhtml", range, "Yellow", note, DateTime.UnixEpoch, DateTime.UnixEpoch);
        void Test(string name, Action action) => tests.Add((name, action));

        Test("Content identity survives file moves and distinguishes same-title revisions", () =>
        {
            string path = Book("<p>version one</p>"), moved = Path.Combine(root, "renamed.epub");
            using var first = EpubBook.Open(path); File.Copy(path, moved);
            using var second = EpubBook.Open(moved); using var changed = EpubBook.Open(Book("<p>version two</p>"));
            Check.Equal(first.BookKey, second.BookKey); Check.False(first.BookKey == changed.BookKey);
            Check.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), first.BookKey);
            Check.False(first.LegacyBookKey == second.LegacyBookKey);
        });
        Test("Legacy bookmarks migrate to side files and survive opening the thirty-third book", () =>
        {
            var s = Store(); string legacy = Id(6000);
            s.MigrateLegacy([new BookHistory { BookKey = legacy.ToLowerInvariant(), FilePath = "old.epub", Bookmarks = [new Bookmark { Label = "keep" }] }]);
            for (int i = 0; i < 40; i++) Open(s, i);
            BookData promoted = s.OpenBook(Id(101), legacy, "old.epub", "Title", "Author");
            Check.Equal(1, promoted.History.Bookmarks.Count); Check.Equal("keep", promoted.History.Bookmarks[0].Label);
            Check.False(s.Entries().Any(e => e.BookId == legacy));
            Check.Equal(1, s.LoadBook(Id(101)).History.Bookmarks.Count);
        });
        Test("Legacy and content identities merge their bookmarks without discarding either record", () =>
        {
            var s = Store(); BookData d = Open(s, 1); d.History.Bookmarks.Add(new Bookmark { Label = "new", ChapterIndex = 1 }); s.SaveBook(BookDataRules.Snapshot(d));
            s.MigrateLegacy([new BookHistory { BookKey = Id(6000), FilePath = "book.epub", Bookmarks = [new Bookmark { Label = "old", ChapterIndex = 2 }] }]);
            Check.Equal(2, s.OpenBook(Id(1), Id(6000), "renamed.epub", "Book", "Author").History.Bookmarks.Count);
            Check.Equal(1, s.Entries().Count);
        });
        Test("Stale reader autosaves cannot overwrite an import or resurrect removed data", () =>
        {
            var s = Store(); BookData d = Open(s, 1); long version = s.ChangeVersion; byte[] stale = BookDataRules.Snapshot(d);
            var other = Store(); BookData incoming = Open(other, 1); incoming.History.ChapterIndex = 3; other.SaveBook(BookDataRules.Snapshot(incoming));
            s.Import(other.ExportBackup(), true);
            Check.Throws<InvalidDataException>(() => s.SaveBook(stale, 100, version)); Check.Equal(3, s.LoadBook(Id(1)).History.ChapterIndex);
            version = s.ChangeVersion; s.Remove(Id(1)); Check.Throws<InvalidDataException>(() => s.SaveBook(stale, 101, version)); Check.Equal(0, s.Entries().Count);
        });
        Test("JSON collection and string budgets reject malicious input before allocating object graphs", () =>
        {
            byte[] excessive = Encoding.UTF8.GetBytes("{\"Schema\":1,\"Books\":[" + string.Join(',', Enumerable.Repeat("{}", 50000)) + "]}");
            long before = GC.GetAllocatedBytesForCurrentThread();
            Check.Throws<InvalidDataException>(() => LibraryStore.ParseBackup(excessive));
            Check.True(GC.GetAllocatedBytesForCurrentThread() - before < 128 * 1024);
            excessive = Encoding.UTF8.GetBytes("{\"Annotations\":[" + string.Join(',', Enumerable.Repeat("{}", 20000)) + "]}");
            before = GC.GetAllocatedBytesForCurrentThread(); Check.Throws<InvalidDataException>(() => BookDataRules.Parse(excessive));
            Check.True(GC.GetAllocatedBytesForCurrentThread() - before < 128 * 1024);
            Check.Throws<InvalidDataException>(() => LibraryStore.ParseBackup(Encoding.UTF8.GetBytes("{\"Books\":[],\"Tags\":[\"" + new string('x', 10000) + "\"]}")));
        });
        Test("Reopening identical content restores long-term data and wrong reassociation is rejected", () =>
        {
            var s = Store(); BookData d = Open(s, 1); d.Annotations.Add(Annotation()); s.SaveBook(BookDataRules.Snapshot(d));
            BookData moved = s.OpenBook(Id(1), Id(4000), "moved.epub", "Same", "Author", Id(1));
            Check.Equal(1, moved.Annotations.Count); Check.Equal("moved.epub", s.Entries()[0].FilePath);
            Check.Throws<InvalidDataException>(() => s.OpenBook(Id(2), Id(4001), "wrong.epub", "Same", "Author", Id(1)));
            Check.Equal(1, s.Entries().Count);
        });
        Test("A corrupt book cannot damage another book or be silently rewritten", () =>
        {
            string dir = Path.Combine(root, "corrupt-book"); var s = new LibraryStore(dir); Open(s, 1); Open(s, 2);
            LibraryEntry e = s.Entries().Single(e => e.BookId == Id(1));
            string path = Path.Combine(dir, "books", e.BookId + "-" + e.DataRevision + ".json"); File.WriteAllText(path, "{broken");
            Check.Throws<JsonException>(() => s.LoadBook(Id(1))); Check.Equal(Id(2), s.LoadBook(Id(2)).BookId);
            Check.Throws<JsonException>(() => s.OpenBook(Id(1), Id(1000), "same.epub", "Title", "Author"));
            Check.Equal("{broken", File.ReadAllText(path));
        });
        Test("Library metadata is independent of recent history and removal never deletes the EPUB", () =>
        {
            var s = Store(); string path = Book("<p>original</p>");
            s.OpenBook(Id(1), Id(5001), path, "Title", "Author");
            s.UpdateMetadata(Id(1), "Edited", "Other", ["小说", "小说", "中文"], "Series", "Finished");
            LibraryEntry e = s.Entries().Single(); Check.Equal("Edited", e.Title); Check.Equal(2, e.Tags.Length); Check.Equal("Finished", e.ReadingStatus);
            new AppState().Books.Clear(); Check.Equal(1, s.Entries().Count);
            s.Remove(Id(1)); Check.True(File.Exists(path)); Check.Equal(0, s.Entries().Count);
        });
        Test("Autosave revisions reject stale writes even after an identical snapshot", () =>
        {
            var s = Store(); BookData d = Open(s, 1); d.History.ChapterIndex = 2; byte[] latest = BookDataRules.Snapshot(d);
            s.SaveBook(latest, 10); s.SaveBook(latest, 20); d.History.ChapterIndex = 1; s.SaveBook(BookDataRules.Snapshot(d), 15);
            Check.Equal(2, s.LoadBook(Id(1)).History.ChapterIndex);
            s.Remove(Id(1)); s.SaveBook(latest, 30); Check.Equal(0, s.Entries().Count);
        });
        Test("Backup round trip strips device paths, is idempotent and leaves EPUB bytes unchanged", () =>
        {
            var s = Store(); string path = Book("<p>unmodified</p>"); byte[] before = SHA256.HashData(File.ReadAllBytes(path));
            BookData d = s.OpenBook(Id(1), Id(5001), path, "Title", "Author"); d.Annotations.Add(Annotation()); d.History.Bookmarks.Add(new Bookmark { Label = "mark", Locator = range.Start });
            s.SaveBook(BookDataRules.Snapshot(d)); byte[] backup = s.ExportBackup(); Check.False(Encoding.UTF8.GetString(backup).Contains(root, StringComparison.Ordinal));
            var restored = Store(); restored.Import(backup, false); restored.Import(backup, false);
            BookData r = restored.LoadBook(Id(1)); Check.Equal(1, r.Annotations.Count); Check.Equal(1, r.History.Bookmarks.Count); Check.Equal("", restored.Entries()[0].FilePath);
            Check.True(before.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))));
        });
        Test("Import preview exposes progress and annotation conflicts and respects explicit choice", () =>
        {
            var s = Store(); BookData current = Open(s, 1); Guid id = Guid.NewGuid(); current.Annotations.Add(Annotation(id, "current")); current.History.ChapterIndex = 5; s.SaveBook(BookDataRules.Snapshot(current));
            var incoming = Store(); BookData other = Open(incoming, 1); other.Annotations.Add(Annotation(id, "imported")); other.History.ChapterIndex = 1; incoming.SaveBook(BookDataRules.Snapshot(other));
            byte[] backup = incoming.ExportBackup(); ImportPreview preview = s.PreviewImport(backup);
            Check.Equal(1, preview.Conflicts.Count); Check.True(preview.Conflicts[0].ProgressDiffers); Check.Equal(1, preview.Conflicts[0].AnnotationConflicts);
            s.Import(backup, false); Check.Equal(5, s.LoadBook(Id(1)).History.ChapterIndex); Check.Equal("current", s.LoadBook(Id(1)).Annotations[0].Note);
            s.Import(backup, true); Check.Equal(1, s.LoadBook(Id(1)).History.ChapterIndex); Check.Equal("imported", s.LoadBook(Id(1)).Annotations[0].Note);
        });
        Test("Malformed, oversized and over-limit merged imports leave committed data unchanged", () =>
        {
            var s = Store(); BookData d = Open(s, 1); d.Annotations.AddRange(Enumerable.Range(0, 64).Select(_ => Annotation())); s.SaveBook(BookDataRules.Snapshot(d));
            byte[] before = s.ExportBackup();
            Check.Throws<JsonException>(() => s.Import("{bad"u8.ToArray(), false));
            Check.Throws<InvalidDataException>(() => s.Import(new byte[BookDataRules.MaxBackupBytes + 1], false));
            Check.Throws<InvalidDataException>(() => s.Import("{\"Schema\":99,\"Books\":[]}"u8.ToArray(), false));
            var other = Store(); BookData imported = Open(other, 1); imported.Annotations.AddRange(Enumerable.Range(0, 65).Select(_ => Annotation())); other.SaveBook(BookDataRules.Snapshot(imported));
            Check.Throws<InvalidDataException>(() => s.Import(other.ExportBackup(), false)); Check.True(before.AsSpan().SequenceEqual(s.ExportBackup()));
        });
        Test("An index commit failure rolls back every staged import generation", () =>
        {
            string dir = Path.Combine(root, "failed-import"); var s = new LibraryStore(dir); Open(s, 1);
            var other = Store(); Open(other, 1).History.ChapterIndex = 3; Open(other, 2); byte[] backup = other.ExportBackup();
            string index = Path.Combine(dir, "library.json"), saved = index + ".saved"; byte[] previous = File.ReadAllBytes(index);
            File.Move(index, saved); Directory.CreateDirectory(index);
            int generations = Directory.GetFiles(Path.Combine(dir, "books")).Length;
            bool failed = false;
            try { s.Import(backup, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
            Check.True(failed); Check.Equal(1, s.Entries().Count); Check.Equal(0, s.LoadBook(Id(1)).History.ChapterIndex);
            Check.Equal(generations, Directory.GetFiles(Path.Combine(dir, "books")).Length);
            Directory.Delete(index); File.Move(saved, index); Check.True(previous.AsSpan().SequenceEqual(File.ReadAllBytes(index)));
        });
        Test("Markdown export escapes active markup in excerpts and notes", () =>
        {
            var s = Store(); BookData d = Open(s, 1); d.Annotations.Add(Annotation(note: "<script>bad</script> [remote](https://invalid) ![image](file:///secret)")); s.SaveBook(BookDataRules.Snapshot(d));
            string markdown = s.ExportMarkdown(Id(1)); Check.True(markdown.Contains("\\<script\\>")); Check.True(markdown.Contains("\\[remote\\]"));
        });
        Test("Annotation ranges preserve combining, surrogate and joined emoji graphemes", () =>
        {
            string text = chapter.Blocks[0].PlainText;
            int at = text.IndexOf("👩‍💻", StringComparison.Ordinal);
            TextRangeLocator emoji = TextRangeLocation.Capture(chapter, 0, at + 1, 0, at + 3);
            Check.Equal("👩‍💻", emoji.Text);
            at = text.IndexOf("é", StringComparison.Ordinal);
            Check.Equal("é", TextRangeLocation.Capture(chapter, 0, at + 1, 0, at + 2).Text);
        });
        Test("Annotations crossing artificial splits resolve after different split sizes", () =>
        {
            string text = new string('x', 4090) + "unique cross style selection " + new string('y', 300);
            ParsedChapter Split(int size) => new(Enumerable.Range(0, (text.Length + size - 1) / size).Select(i => new BookBlock(BlockKind.Paragraph,
                [new BookInline(text.Substring(i * size, Math.Min(size, text.Length - i * size)))], ContinuesPrevious: i > 0)).ToArray(), new Dictionary<string, int>(), false, text.Length);
            var first = Split(4096); TextRangeLocator selected = TextRangeLocation.Capture(first, 0, 4090, 1, 22);
            BookAnnotation a = Annotation() with { Range = selected };
            Check.Equal("unique cross style selection", selected.Text); var resolved = TextRangeLocation.Resolve(Split(777), [a], 0, "OEBPS/c0.xhtml");
            Check.True(resolved.Count > 0); Check.Equal(selected.Text.Length, resolved.Sum(s => s.End - s.Start));
            Check.Equal(0, TextRangeLocation.Resolve(Split(777), [a with { Range = selected with { Text = "unrelated" } }], 0, "OEBPS/c0.xhtml").Count);
        });
        Test("Annotation selection and persisted data obey character, version and quantity limits", () =>
        {
            using var longBook = EpubBook.Open(Book("<p>" + new string('x', 6000) + "</p>"));
            Check.Throws<InvalidDataException>(() => TextRangeLocation.Capture(longBook.ReadChapter(0), 0, 0, 1, 1000));
            BookData d = Open(Store(), 1); d.Annotations.AddRange(Enumerable.Range(0, 129).Select(_ => Annotation()));
            Check.Throws<InvalidDataException>(() => BookDataRules.Snapshot(d));
        });
        Test("Search case and whole-word options share exact Unicode and punctuation semantics", () =>
        {
            var whole = new SearchOptions(WholeWord: true);
            Check.Equal(-1, SearchText.Find("xHello hello_2", "hello", 0, whole));
            Check.Equal(7, SearchText.Find("xHello hello!", "hello", 0, whole));
            Check.Equal(-1, SearchText.Find("café", "caf", 0, whole));
            Check.Equal(2, SearchText.Find("你好中文内容", "中文", 0, whole));
            Check.Equal(-1, SearchText.Find("HELLO", "hello", 0, new SearchOptions(MatchCase: true)));
        });
        Test("Chapter-scoped search retains spine positions and reports one resource", () =>
        {
            string path = Path.Combine(root, "chapter-search.epub"); File.WriteAllBytes(path, Fixtures.Zip(Fixtures.Book(["<p>HELLO</p>", "<p>hello</p>"])));
            using var book = EpubBook.Open(path); var reports = new List<SearchProgress>();
            SearchOutcome outcome = book.Search("hello", progress: new InlineProgress<SearchProgress>(reports.Add), options: new SearchOptions(true, true, 1));
            Check.Equal(1, outcome.Hits.Count); Check.Equal(1, outcome.Hits[0].ChapterIndex); Check.Equal(new SearchProgress(1, 1), reports[^1]);
        });
        Test("Whole-word matching checks the neighbor across continuation blocks and styles", () =>
        {
            using var book = EpubBook.Open(Book("<p>" + new string(' ', 1) + new string('x', 4094) + "<b> hello</b>world hello!</p>"));
            SearchOutcome outcome = book.Search("hello", options: new SearchOptions(WholeWord: true));
            Check.Equal(1, outcome.Hits.Count); Check.True(outcome.Hits[0].Snippet.Contains("hello!"));
        });
        Test("Toc hierarchy filters include ancestors and current anchors distinguish sections", () =>
        {
            var items = Enumerable.Range(0, 1000).Select(i => new TocItem("Section " + i, 0, "a" + i, i % 2)).ToArray(); var toc = new TocNavigator(items);
            Check.Equal(500, toc.Visible("", Enumerable.Range(0, 500).Select(i => i * 2).ToHashSet()).Count);
            IReadOnlyList<int> filtered = toc.Visible("Section 999", new HashSet<int> { 998 }); Check.True(filtered.SequenceEqual(new[] { 998, 999 }));
            Check.Equal(501, toc.Current(0, 501, items.Select((e, i) => (e.Fragment, i)).ToDictionary(e => e.Fragment, e => e.i)));
        });
        Test("Only bounded inline paragraph properties survive source formatting", () =>
        {
            using var book = EpubBook.Open(Book("<p style='text-align:center;text-indent:999em;background:url(https://invalid);font-family:evil'>novel</p>" +
                "<p style='white-space:pre-wrap'>  poetry\n    next</p><pre>  code\n x</pre>"));
            ParsedChapter c = book.ReadChapter(0); Check.Equal("Center", c.Blocks[0].Layout!.Alignment); Check.Equal(4d, c.Blocks[0].Layout!.IndentEm);
            Check.Equal("  poetry\n    next", c.Blocks[1].PlainText); Check.Equal(BlockKind.Code, c.Blocks[2].Kind);
            ReaderPreferences.Validate(new ReaderPreferences()); Check.Throws<InvalidDataException>(() => ReaderPreferences.Validate(new ReaderPreferences { ParagraphSpacing = double.NaN }));
        });
    }
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
