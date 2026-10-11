using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuietRead.Core;
using QuietRead.Cross;

namespace QuietRead.CrossTests;

internal static class Program
{
    private static readonly List<object> Results = [];
    private static int _failed;
    private static string _root = "";
    [STAThread]
    private static int Main(string[] args)
    {
        _root = Path.Combine(Path.GetTempPath(), "QuietRead-cross-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root);
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Dispatcher.UIThread.Post(async () =>
        {
            try { await Suite(); }
            catch (Exception e) { _failed++; Console.Error.WriteLine(e); }
            finally { stop.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        if (args.Length > 0) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!); File.WriteAllText(args[0], JsonSerializer.Serialize(new { Passed = Results.Count - _failed, Failed = _failed, Tests = Results }, new JsonSerializerOptions { WriteIndented = true })); }
        Console.WriteLine($"{Results.Count - _failed} passed, {_failed} failed");
        try { Directory.Delete(_root, true); } catch (IOException) { }
        return _failed == 0 ? 0 : 1;
    }
    private static async Task Test(string name, Func<Task> test)
    {
        try { await test(); Results.Add(new { Name = name, Passed = true }); Console.WriteLine("PASS: " + name); }
        catch (Exception error) { _failed++; Results.Add(new { Name = name, Passed = false, Error = error.ToString() }); Console.WriteLine($"::error title={name}::{error}"); }
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; got {actual}"); }
    private static void True(bool value, string message = "Assertion failed") { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static string DirectoryFor(string name) => Path.Combine(_root, name);
    private static string Book(string name, int paragraphs = 3)
    {
        string path = Path.Combine(_root, name + ".epub");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entries = new Dictionary<string, string>
        {
            ["mimetype"] = "application/epub+zip",
            ["META-INF/container.xml"] = "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='OEBPS/book.opf' media-type='application/oebps-package+xml'/></rootfiles></container>",
            ["OEBPS/book.opf"] = "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>cross-platform</dc:identifier><dc:title>中英文原书</dc:title><dc:creator>原作者</dc:creator></metadata><manifest><item id='c' href='c.xhtml' media-type='application/xhtml+xml'/><item id='n' href='nav.xhtml' media-type='application/xhtml+xml' properties='nav'/></manifest><spine><itemref idref='c'/></spine></package>",
            ["OEBPS/nav.xhtml"] = "<html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'><body><nav epub:type='toc'><ol><li><a href='c.xhtml'>原章节</a></li></ol></nav></body></html>",
            ["OEBPS/c.xhtml"] = "<html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'><body><h1 id='start'>原文 heading</h1>" + string.Concat(Enumerable.Range(0, paragraphs).Select(i => $"<p>中文原文 书库 English passage {i} <strong>bold</strong> <em>italic</em></p>")) + "<p><a epub:type='noteref' href='#note'>注</a></p><aside role='doc-footnote' id='note'><p>脚注原文 Footnote content.</p></aside></body></html>"
        };
        foreach (var entry in entries) { using var writer = new StreamWriter(archive.CreateEntry(entry.Key).Open(), new UTF8Encoding(false)); writer.Write(entry.Value); }
        return path;
    }
    private static async Task Suite()
    {
        string sample = Book("sample");
        await Test("Private storage imports nonseekable streams, validates and deduplicates EPUB", async () =>
        {
            var files = new ManagedBookFiles(DirectoryFor("import"));
            await using var stream = File.OpenRead(sample); string path = await files.ImportAsync(new NonSeeking(stream));
            using EpubBook book = EpubBook.Open(path); True(Path.GetFileName(path) == book.BookKey + ".epub");
            Equal("中英文原书", book.Title);
            await using var again = File.OpenRead(sample); Equal(path, await files.ImportAsync(again));
            Equal(1, Directory.GetFiles(files.DirectoryPath).Length);
        });
        await Test("Damaged and cancelled imports never publish a file or leave partial data", async () =>
        {
            var files = new ManagedBookFiles(DirectoryFor("bad-import"));
            using var bad = new MemoryStream("bad EPUB"u8.ToArray()); await Throws<EpubException>(() => files.ImportAsync(bad));
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); await using var source = File.OpenRead(sample);
            await Throws<OperationCanceledException>(() => files.ImportAsync(source, cancel.Token)); Equal(0, Directory.GetFiles(files.DirectoryPath).Length);
        });
        await Test("Provider size lies cannot bypass bounded copy and caller streams stay open", async () =>
        {
            using var source = new MemoryStream(new byte[65]); using var output = new MemoryStream();
            await Throws<InvalidDataException>(() => ManagedBookFiles.CopyBoundedAsync(new NonSeeking(source), output, 64));
            True(source.CanRead && output.CanWrite); Equal(0L, output.Length);
            source.Position = 0; await ManagedBookFiles.CopyBoundedAsync(source, output, 65); Equal(65L, output.Length);
        });
        await Test("Reading, language, per-book appearance, bookmarks and annotations survive restart", async () =>
        {
            string directory = DirectoryFor("restart");
            using (var session = new ReaderSession(directory))
            {
                session.Open(sample); session.Navigate(0, 2); session.SetLanguage("en");
                var appearance = session.Preferences.Copy(); appearance.FontSize = 30; session.SetAppearance(appearance, true);
                session.AddBookmark(); session.AddAnnotation(1, 0, 4, "原笔记 English note", "Green");
                var position = session.Data!.History.Locator;
                session.RememberPosition("NOT_THE_BOOK", 0, 0); Equal(position, session.Data.History.Locator);
            }
            using var restored = new ReaderSession(directory); restored.Open(sample);
            Equal("en", restored.State.InterfaceLanguage); Equal(30d, restored.Preferences.FontSize);
            Equal(2, restored.Data!.History.BlockIndex); Equal(1, restored.Data.History.Bookmarks.Count);
            Equal("原笔记 English note", restored.Data.Annotations.Single().Note); await Task.CompletedTask;
        });
        await Test("Invalid annotation changes preserve data; search cancellation keeps session usable", async () =>
        {
            using var session = new ReaderSession(DirectoryFor("invalid")); session.Open(sample);
            var note = session.AddAnnotation(1, 0, 4, "valid", "Yellow")!;
            await Throws<InvalidDataException>(() => Task.Run(() => session.EditAnnotation(note.Id, new string('x', 2001), "Yellow")));
            Equal("valid", session.Data!.Annotations.Single().Note);
            using var stop = new CancellationTokenSource(); stop.Cancel();
            await Throws<OperationCanceledException>(() => Task.Run(() => session.Search("English", new SearchOptions(), stop.Token)));
            True(session.Search("English", new SearchOptions(), default).Hits.Count > 0);
        });
        await Test("Backup imports merge data across private directories without paths or UI language", async () =>
        {
            byte[] backup;
            using (var source = new ReaderSession(DirectoryFor("backup-source")))
            { source.Open(sample); source.SetLanguage("en"); source.AddBookmark(); source.AddAnnotation(1, 0, 4, "迁移笔记", "Rose"); backup = source.Library.ExportBackup(); }
            using var target = new ReaderSession(DirectoryFor("backup-target")); target.ImportBackup(backup, true);
            Equal("zh-CN", target.State.InterfaceLanguage); Equal("", target.Library.Entries().Single().FilePath);
            target.Open(sample); Equal(1, target.Data!.History.Bookmarks.Count); Equal("迁移笔记", target.Data.Annotations.Single().Note);
            True(target.Library.ExportMarkdown(target.Book!.BookKey, "en").Contains("迁移笔记")); await Task.CompletedTask;
        });
        await Test("A second desktop instance cannot mutate the same store", async () =>
        {
            using var first = new ReaderSession(DirectoryFor("lease"));
            await Throws<IOException>(() => Task.Run(() => { using var second = new ReaderSession(DirectoryFor("lease")); }));
        });
        await Test("Shared reader renders formatted EPUB and bilingual menus without translating book text", async () =>
        {
            using var session = new ReaderSession(DirectoryFor("ui")); var view = new ReaderView(session);
            var window = new Window { Content = view, Width = 1100, Height = 800 }; window.Show();
            try
            {
                True(await view.OpenPathAsync(sample)); await Task.Delay(40);
                True(view.GetVisualDescendants().OfType<SelectableTextBlock>().Any(p => p.Inlines?.OfType<Run>().Any(r => r.FontWeight == Avalonia.Media.FontWeight.Bold) == true));
                string body = string.Join('\n', view.GetVisualDescendants().OfType<SelectableTextBlock>().Select(p => p.Inlines?.Text ?? p.Text));
                True(body.Contains("中文原文")); await view.SetLanguageAsync("en"); await Task.Delay(40);
                True(view.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "Open")));
                Equal(body, string.Join('\n', view.GetVisualDescendants().OfType<SelectableTextBlock>().Select(p => p.Inlines?.Text ?? p.Text)));
                Equal("en", session.State.InterfaceLanguage); await view.SetLanguageAsync("zh-CN"); await Task.Delay(40);
                True(view.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "打开")));
            }
            finally { window.Close(); }
        });
        await Test("Phone and desktop layouts fit their viewport and bound long chapters", async () =>
        {
            using var session = new ReaderSession(DirectoryFor("layout")); var view = new ReaderView(session);
            var window = new Window { Content = view, Width = 390, Height = 844 }; window.Show();
            try
            {
                True(await view.OpenPathAsync(Book("long", 500))); await Task.Delay(40);
                True(view.GetVisualDescendants().OfType<SelectableTextBlock>().Count() <= ReaderLimits.BlocksPerView);
                await view.SetLanguageAsync("en"); await Task.Delay(40);
                foreach (var button in view.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible && b.Bounds.Width > 0))
                { var point = button.TranslatePoint(default, view); if (point is { } p) True(p.X >= -1 && p.X + button.Bounds.Width <= view.Bounds.Width + 1, "Phone button clipped: " + button.Content); }
                window.Width = 1100; await Task.Delay(40); True(view.Bounds.Width > 800);
                view.PersistForBackground();
            }
            finally { window.Close(); }
        });
    }
    private sealed class TestApp : Application { public override void Initialize() => Styles.Add(new FluentTheme()); }
    private sealed class NonSeeking(Stream inner) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
