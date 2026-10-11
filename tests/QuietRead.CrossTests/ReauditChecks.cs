using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using QuietRead.Core;
using QuietRead.Cross;
using SkiaSharp;

namespace QuietRead.CrossTests;

internal static partial class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static Task Invoke(ReaderView view, string method, params object[] args) =>
        (Task)typeof(ReaderView).GetMethods(Private).Single(candidate => candidate.Name == method && candidate.GetParameters().Length == args.Length).Invoke(view, args)!;
    private static async Task Settled(ReaderView view)
    {
        var time = Stopwatch.StartNew();
        while (view.Busy || Field<bool>(view, "_restoring") || Field<bool>(view, "_needsRender"))
        { True(time.Elapsed < TimeSpan.FromSeconds(5), "Reader did not settle."); await Task.Delay(10); }
    }
    private static Window Show(ReaderView view) { var window = new Window { Content = view, Width = 1000, Height = 800 }; window.Show(); return window; }
    private static void Provider(Window window, IStorageFile file) => typeof(TopLevel).GetField("_storageProvider", Private)!.SetValue(window, TestStorage.Provider(file));
    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height); bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap); using var bytes = image.Encode(SKEncodedImageFormat.Png, 100); return bytes.ToArray();
    }
    private static string AuditBook(string name, string[] chapters, Dictionary<string, byte[]>? images = null)
    {
        string path = Path.Combine(_root, name + ".epub");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entries = new Dictionary<string, byte[]>
        {
            ["mimetype"] = Encoding.UTF8.GetBytes("application/epub+zip"),
            ["META-INF/container.xml"] = Encoding.UTF8.GetBytes("<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='OEBPS/book.opf'/></rootfiles></container>"),
            ["OEBPS/book.opf"] = Encoding.UTF8.GetBytes("<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>" + name + "</dc:identifier><dc:title>" + name + "</dc:title></metadata><manifest>" +
                string.Concat(Enumerable.Range(0, chapters.Length).Select(index => $"<item id='c{index}' href='c{index}.xhtml' media-type='application/xhtml+xml'/>")) +
                string.Concat((images ?? []).Keys.Select((key, index) => $"<item id='i{index}' href='{key}' media-type='image/png'/>")) +
                "</manifest><spine>" + string.Concat(Enumerable.Range(0, chapters.Length).Select(index => $"<itemref idref='c{index}'/>")) + "</spine></package>")
        };
        for (int index = 0; index < chapters.Length; index++) entries[$"OEBPS/c{index}.xhtml"] = Encoding.UTF8.GetBytes("<html xmlns='http://www.w3.org/1999/xhtml'><body>" + chapters[index] + "</body></html>");
        foreach (var image in images ?? []) entries["OEBPS/" + image.Key] = image.Value;
        foreach (var entry in entries) { using Stream output = archive.CreateEntry(entry.Key).Open(); output.Write(entry.Value); }
        return path;
    }
    private static async Task ReauditSuite()
    {
        byte[] png = Png(1000, 1000);
        string pictures = AuditBook("repeated-pictures", [string.Concat(Enumerable.Repeat("<img src='one.png'/>", 8))], new() { ["one.png"] = png });
        await Test("Repeated image references share one real decoded bitmap and detachment releases it", async () =>
        {
            using var session = new ReaderSession(DirectoryFor("image-sharing")); var view = new ReaderView(session); var window = Show(view);
            try
            {
                True(await view.OpenPathAsync(pictures)); await Settled(view);
                var images = Field<List<Bitmap>>(view, "_images"); Equal(1, images.Count);
                var controls = Field<StackPanel>(view, "_document").Children.OfType<Image>().ToArray(); Equal(8, controls.Length);
                True(controls.All(image => ReferenceEquals(image.Source, images[0])));
                Equal(1000, images[0].PixelSize.Width); Equal(1000, images[0].PixelSize.Height);
                Measurements.Add(new { Name = "Repeated image decoding", References = 8, UniqueBitmaps = images.Count, DecodedPixels = (long)images[0].PixelSize.Width * images[0].PixelSize.Height });
                window.Content = null; Equal(0, images.Count); window.Content = view; await Settled(view); Equal(1, images.Count);
            }
            finally { window.Close(); }
        });
        await Test("Decoded image budget counts actual unique pixels and skips oversized resources without dropping later images", async () =>
        {
            var files = new Dictionary<string, byte[]> { ["large.png"] = Png(2500, 2000) };
            for (int index = 0; index < 5; index++) files[$"{index}.png"] = png;
            string path = AuditBook("mixed-pictures", [string.Concat(files.Keys.Select(key => $"<img src='{key}'/>"))], files);
            using var session = new ReaderSession(DirectoryFor("image-budget")); var view = new ReaderView(session); var window = Show(view);
            try
            {
                True(await view.OpenPathAsync(path)); await Settled(view); var images = Field<List<Bitmap>>(view, "_images");
                Equal(4, images.Count); Equal(ReaderLimits.ImageViewPixels, images.Sum(bitmap => (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height));
                Equal(4, Field<StackPanel>(view, "_document").Children.OfType<Image>().Count());
            }
            finally { window.Close(); }
        });
        await Test("Export rejects EPUB names and protected application or book paths before opening a write stream", async () =>
        {
            string directory = DirectoryFor("export-protection"); Directory.CreateDirectory(directory);
            var epub = new TestStorage { Name = "original.epub", Bytes = png }; await Throws<InvalidDataException>(() => ExportFiles.WriteAsync(epub.File, "{}"u8.ToArray(), "json", directory, []));
            Equal(0, epub.Writes); True(epub.Bytes.SequenceEqual(png));
            string state = Path.Combine(directory, "state.json"); File.WriteAllText(state, "original settings");
            var protectedFile = TestStorage.Local(state); await Throws<InvalidDataException>(() => ExportFiles.WriteAsync(protectedFile.File, "{}"u8.ToArray(), "json", directory, []));
            Equal("original settings", File.ReadAllText(state)); Equal(0, protectedFile.Writes);
            string bookPath = Path.Combine(_root, "book-with-json-name.json"); File.Copy(pictures, bookPath);
            var book = TestStorage.Local(bookPath); await Throws<InvalidDataException>(() => ExportFiles.WriteAsync(book.File, "{}"u8.ToArray(), "json", directory, [bookPath]));
            True(File.ReadAllBytes(bookPath).SequenceEqual(File.ReadAllBytes(pictures)));
            await Throws<InvalidDataException>(() => ExportFiles.WriteAsync(book.File, "{}"u8.ToArray(), "json", Path.GetPathRoot(_root)!, []));
            True(File.ReadAllBytes(bookPath).SequenceEqual(File.ReadAllBytes(pictures)));
        });
        if (!OperatingSystem.IsWindows()) await Test("Export cannot enter private storage through a directory symlink", async () =>
        {
            string directory = DirectoryFor("export-private"), alias = DirectoryFor("export-alias"); Directory.CreateDirectory(directory);
            string state = Path.Combine(directory, "state.json"); File.WriteAllText(state, "keep settings"); Directory.CreateSymbolicLink(alias, directory);
            var target = TestStorage.Local(Path.Combine(alias, "state.json"));
            await Throws<InvalidDataException>(() => ExportFiles.WriteAsync(target.File, "{}"u8.ToArray(), "json", directory, [])); Equal("keep settings", File.ReadAllText(state));
            string parentAlias = DirectoryFor("export-parent-alias"), indirectAlias = DirectoryFor("export-indirect-alias");
            Directory.CreateSymbolicLink(parentAlias, _root);
            Directory.CreateSymbolicLink(indirectAlias, Path.Combine(parentAlias, Path.GetFileName(directory)));
            var indirect = TestStorage.Local(Path.Combine(indirectAlias, "state.json"));
            await Throws<InvalidDataException>(() => ExportFiles.WriteAsync(indirect.File, "{}"u8.ToArray(), "json", directory, [])); Equal("keep settings", File.ReadAllText(state));
        });
        await Test("Local export replaces an existing backup atomically and cancellation preserves it without temporary files", async () =>
        {
            string directory = DirectoryFor("atomic-export"); Directory.CreateDirectory(directory); string path = Path.Combine(directory, "backup.json");
            File.WriteAllText(path, "old backup"); var target = TestStorage.Local(path); byte[] bytes = "new complete backup"u8.ToArray();
            await ExportFiles.WriteAsync(target.File, bytes, "json", DirectoryFor("private-atomic"), []); True(File.ReadAllBytes(path).SequenceEqual(bytes)); Equal(0, target.Writes);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Throws<OperationCanceledException>(() => ExportFiles.WriteAsync(target.File, "partial"u8.ToArray(), "json", DirectoryFor("private-atomic"), [], cancel.Token));
            True(File.ReadAllBytes(path).SequenceEqual(bytes)); Equal(1, Directory.GetFiles(directory).Length);
        });
        if (!OperatingSystem.IsWindows()) await Test("Local export write failure preserves the old backup", async () =>
        {
            if (OperatingSystem.IsWindows()) return;
            string directory = DirectoryFor("readonly-export"); Directory.CreateDirectory(directory); string path = Path.Combine(directory, "backup.json"); File.WriteAllText(path, "old backup");
            UnixFileMode permissions = File.GetUnixFileMode(directory); File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try { await Throws<UnauthorizedAccessException>(() => ExportFiles.WriteAsync(TestStorage.Local(path).File, "new"u8.ToArray(), "json", DirectoryFor("private-readonly"), [])); Equal("old backup", File.ReadAllText(path)); }
            finally { File.SetUnixFileMode(directory, permissions); }
        });
        await Test("Document providers keep an existing backup and reject unreadable destinations before writing", async () =>
        {
            var target = new TestStorage { Bytes = "previous valid backup"u8.ToArray() };
            await Throws<InvalidDataException>(() => ExportFiles.WriteAsync(target.File, "new"u8.ToArray(), "json", DirectoryFor("private-provider"), []));
            Equal("previous valid backup", Encoding.UTF8.GetString(target.Bytes)); Equal(0, target.Writes); Equal(0, target.Deletes);
            target.ReadError = true; await Throws<IOException>(() => ExportFiles.WriteAsync(target.File, "new"u8.ToArray(), "json", DirectoryFor("private-provider"), []));
            Equal(0, target.Writes); Equal(0, target.Deletes);
        });
        await Test("New provider exports clean up partial documents after write, flush or cancellation failures", async () =>
        {
            foreach (string failure in new[] { "write", "flush", "cancel" })
            {
                using var cancel = new CancellationTokenSource(); var target = new TestStorage { Failure = failure, Cancel = cancel };
                if (failure == "cancel") await Throws<OperationCanceledException>(() => ExportFiles.WriteAsync(target.File, "complete bytes"u8.ToArray(), "json", DirectoryFor("private-new-provider"), [], cancel.Token));
                else await Throws<IOException>(() => ExportFiles.WriteAsync(target.File, "complete bytes"u8.ToArray(), "json", DirectoryFor("private-new-provider"), []));
                Equal(1, target.Writes); Equal(1, target.Deletes); Equal(0, target.Bytes.Length);
            }
        });
        await Test("New document export succeeds and cleanup failures are reported without touching existing backups", async () =>
        {
            var target = new TestStorage(); byte[] bytes = "complete backup"u8.ToArray();
            await ExportFiles.WriteAsync(target.File, bytes, "json", DirectoryFor("private-new-success"), []); True(target.Bytes.SequenceEqual(bytes)); Equal(0, target.Deletes);
            var failure = new TestStorage { Failure = "write", DeleteError = true };
            await Throws<IOException>(() => ExportFiles.WriteAsync(failure.File, bytes, "json", DirectoryFor("private-new-failure"), [])); Equal(1, failure.Deletes);
        });
        await Test("Reader export validates the picked destination and writes an importable backup", async () =>
        {
            using var session = new ReaderSession(DirectoryFor("export-view")); var view = new ReaderView(session); var window = Show(view);
            try
            {
                True(await view.OpenPathAsync(pictures)); await Settled(view); string original = Path.Combine(_root, "export-original.epub"); File.Copy(pictures, original);
                Provider(window, TestStorage.Local(original).File); await Invoke(view, "ExportAsync", "json", false); True(File.ReadAllBytes(original).SequenceEqual(File.ReadAllBytes(pictures))); True(Field<string?>(view, "_statusError") != null);
                string destination = Path.Combine(_root, "view-backup.json"); Provider(window, TestStorage.Local(destination).File); await Invoke(view, "ExportAsync", "json", false);
                Equal(null, Field<string?>(view, "_statusError")); using var restored = new ReaderSession(DirectoryFor("view-import")); restored.ImportBackup(File.ReadAllBytes(destination), true); Equal(1, restored.Library.Entries().Count);
            }
            finally { window.Close(); }
        });
        string paragraphs = string.Concat(Enumerable.Range(0, 400).Select(index => $"<p>Paragraph {index}.</p>"));
        string chapters = AuditBook("reverse-pages", [paragraphs, paragraphs]);
        await Test("Previous page enters the last screen of the previous chapter and segment", async () =>
        {
            using var session = new ReaderSession(DirectoryFor("reverse-ui")); var view = new ReaderView(session); var window = Show(view);
            try
            {
                True(await view.OpenPathAsync(chapters)); await Settled(view);
                True(await view.RunAsync(async () => { await Task.Run(() => session.Navigate(1)); await view.RenderAsync(); })); await Settled(view);
                Field<ScrollViewer>(view, "_scroll").Offset = default; await Invoke(view, "PageAsync", -1); await Settled(view);
                Equal(0, session.ChapterIndex); Equal(360, session.SegmentStart); True(Field<ScrollViewer>(view, "_scroll").Offset.Y > 0); True(view.RenderedText.Contains("Paragraph 399."));
                await Invoke(view, "PageAsync", 1); await Settled(view); Equal(1, session.ChapterIndex); Equal(0, session.SegmentStart);
                True(await view.RunAsync(async () => { await Task.Run(() => session.Navigate(0, 180)); await view.RenderAsync(); })); await Settled(view);
                Field<ScrollViewer>(view, "_scroll").Offset = default; await Invoke(view, "PageAsync", -1); await Settled(view);
                Equal(0, session.SegmentStart); True(view.RenderedText.Contains("Paragraph 179.")); True(Field<ScrollViewer>(view, "_scroll").Offset.Y > 0);
            }
            finally { window.Close(); }
        });
        await Test("Dense text obeys all three view budgets with no missing blocks and navigation uses the same boundaries", async () =>
        {
            string paragraph = "<p>" + string.Concat(Enumerable.Range(0, 96).Select(index => index % 2 == 0 ? "<b>abcdefghij中文mnopqrstuvwx01234567</b>" : "<i>abcdefghij中文mnopqrstuvwx01234567</i>")) + "</p>";
            string dense = AuditBook("dense-view", [string.Concat(Enumerable.Repeat(paragraph, 180))]);
            using var session = new ReaderSession(DirectoryFor("dense-view")); var view = new ReaderView(session);
            var time = Stopwatch.StartNew(); True(await view.OpenPathAsync(dense)); double initial = time.Elapsed.TotalMilliseconds; await Settled(view);
            var seen = new List<int>(); var rendered = new StringBuilder(); var timings = new List<double>(); int maxCharacters = 0, maxInlines = 0;
            int[] starts = session.SegmentStarts.ToArray(); True(starts.Length > 1);
            foreach (int start in starts)
            {
                time.Restart(); True(await view.RunAsync(async () => { await Task.Run(() => session.Navigate(0, start)); await view.RenderAsync(); })); timings.Add(time.Elapsed.TotalMilliseconds); await Settled(view);
                var controls = Field<List<(int Index, SelectableTextBlock Text)>>(view, "_paragraphs");
                int characters = controls.Sum(item => item.Text.Inlines!.Text?.Length ?? 0), inlines = controls.Sum(item => item.Text.Inlines!.Count);
                True(controls.Count <= ReaderLimits.BlocksPerView && characters <= ReaderLimits.CharactersPerView && inlines <= ReaderLimits.InlinesPerView);
                maxCharacters = Math.Max(maxCharacters, characters); maxInlines = Math.Max(maxInlines, inlines);
                seen.AddRange(controls.Select(item => item.Index)); foreach (var item in controls) rendered.Append(item.Text.Inlines!.Text);
            }
            True(seen.SequenceEqual(Enumerable.Range(0, session.Chapter!.Blocks.Count))); Equal(string.Concat(session.Chapter.Blocks.Select(block => block.PlainText)), rendered.ToString());
            session.Navigate(0, starts[1]); True(session.NavigateSegment(-1)); Equal(starts[1] - 1, session.TargetBlock); True(session.NavigateSegment(1)); Equal(starts[1], session.TargetBlock);
            Measurements.Add(new { Name = "Dense view construction excluding layout", ChapterCharacters = session.Chapter.CharacterCount, Segments = starts.Length, MaxViewCharacters = maxCharacters, MaxViewInlines = maxInlines, InitialMs = initial, RenderMs = timings });
        });
        await Test("Background cancellation retains the old page, resets rendering state and queued foreground activation finishes redraw", async () =>
        {
            using var session = new ReaderSession(DirectoryFor("cancel-view")); var view = new ReaderView(session); var window = Show(view);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            try
            {
                True(await view.OpenPathAsync(pictures)); await Settled(view); int count = Field<StackPanel>(view, "_document").Children.Count;
                object gate = Field<object>(session, "_gate"); Task held = Task.Run(() => { lock (gate) { entered.Set(); release.Wait(); } }); True(entered.Wait(TimeSpan.FromSeconds(2)));
                Task<bool> render = view.RunAsync(view.RenderAsync); Task unlock = Task.Run(async () => { await Task.Delay(120); release.Set(); });
                view.PersistForBackground(); Task resume = view.ResumeFromBackgroundAsync(); True(!await render); await held; await unlock; await resume;
                Equal(count, Field<StackPanel>(view, "_document").Children.Count); True(!Field<bool>(view, "_restoring"));
                await Settled(view); Equal(count, Field<StackPanel>(view, "_document").Children.Count); Equal(1, Field<List<Bitmap>>(view, "_images").Count); True(!view.Busy);
            }
            finally { release.Set(); window.Close(); }
        });
        await Test("Foreground cancellation redraws the current book without clearing the visible page", async () =>
        {
            using var session = new ReaderSession(DirectoryFor("cancel-foreground")); var view = new ReaderView(session); var window = Show(view);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            try
            {
                True(await view.OpenPathAsync(pictures)); await Settled(view); int count = Field<StackPanel>(view, "_document").Children.Count;
                Task held = Task.Run(() => { lock (Field<object>(session, "_gate")) { entered.Set(); release.Wait(); } }); True(entered.Wait(TimeSpan.FromSeconds(2)));
                Task<bool> render = view.RunAsync(view.RenderAsync); Field<CancellationTokenSource>(view, "_operation").Cancel();
                Equal(count, Field<StackPanel>(view, "_document").Children.Count); release.Set(); True(!await render); await held; await Settled(view);
                Equal(count, Field<StackPanel>(view, "_document").Children.Count); Equal(1, Field<List<Bitmap>>(view, "_images").Count);
            }
            finally { release.Set(); window.Close(); }
        });
        await Test("Activity detachment retires pending renders and a replacement view restores the shared session", async () =>
        {
            using var session = new ReaderSession(DirectoryFor("cancel-detachment")); var view = new ReaderView(session); var window = Show(view);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            try
            {
                True(await view.OpenPathAsync(pictures)); await Settled(view);
                Task held = Task.Run(() => { lock (Field<object>(session, "_gate")) { entered.Set(); release.Wait(); } }); True(entered.Wait(TimeSpan.FromSeconds(2)));
                Task<bool> render = view.RunAsync(view.RenderAsync); window.Content = null; Equal(0, Field<List<Bitmap>>(view, "_images").Count);
                release.Set(); True(!await render); await held; True(!view.Busy); Equal(0, Field<StackPanel>(view, "_document").Children.Count);
                var replacement = new ReaderView(session); window.Content = replacement; await Settled(replacement); Equal(1, Field<List<Bitmap>>(replacement, "_images").Count);
            }
            finally { release.Set(); window.Close(); }
        });
        await Test("Cancelled chapter redraw does not save old visible paragraphs into the new chapter", async () =>
        {
            string path = AuditBook("cancelled-navigation", ["<p>Old page</p><img src='one.png'/>", "<p>New page</p><img src='one.png'/>"], new() { ["one.png"] = png });
            using var session = new ReaderSession(DirectoryFor("cancel-navigation")); var view = new ReaderView(session); var window = Show(view);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            try
            {
                True(await view.OpenPathAsync(path)); await Settled(view); True(await view.RunAsync(() => Task.Run(() => session.Navigate(1))));
                Task held = Task.Run(() => { lock (Field<object>(session, "_gate")) { entered.Set(); release.Wait(); } }); True(entered.Wait(TimeSpan.FromSeconds(2)));
                Task<bool> render = view.RunAsync(view.RenderAsync); Task unlock = Task.Run(async () => { await Task.Delay(120); release.Set(); }); view.PersistForBackground(); True(!await render); await held; await unlock;
                Equal(1, session.Data!.History.ChapterIndex); True(view.RenderedText.Contains("Old page")); True(!Field<bool>(view, "_restoring"));
                await view.ResumeFromBackgroundAsync(); await Settled(view); True(view.RenderedText.Contains("New page")); Equal(1, session.Data.History.ChapterIndex);
            }
            finally { release.Set(); window.Close(); }
        });
        await Test("Search results belong to their book and query; failed opens preserve valid results and removing a book clears them", async () =>
        {
            string first = AuditBook("search-source", ["<p>different phrase</p>", "<p>ONLY_IN_A unique phrase</p>"]);
            string second = AuditBook("search-destination", ["<p>ONLY_IN_B second phrase</p>", "<p>another phrase</p>"]);
            using var session = new ReaderSession(DirectoryFor("search-ownership")); var view = new ReaderView(session); var window = Show(view);
            try
            {
                True(await view.OpenPathAsync(first)); await Settled(view); Field<TextBox>(view, "_query").Text = "ONLY_IN_A"; await Invoke(view, "SearchAsync");
                var list = Field<ListBox>(view, "_searchList"); Equal(1, list.ItemCount); object old = list.Items[0]!;
                True(await view.OpenPathAsync(second)); await Settled(view); Equal(0, list.ItemCount); await Invoke(view, "JumpSearchAsync", old); Equal(0, session.ChapterIndex);
                Field<TextBox>(view, "_query").Text = "ONLY_IN_B"; await Invoke(view, "SearchAsync"); Equal(1, list.ItemCount); object valid = list.Items[0]!;
                string damaged = Path.Combine(_root, "damaged-open.epub"); File.WriteAllText(damaged, "invalid EPUB"); True(!await view.OpenPathAsync(damaged)); Equal(1, list.ItemCount);
                Field<TextBox>(view, "_query").Text = "another"; await Invoke(view, "SearchAsync"); Equal(1, list.ItemCount); await Invoke(view, "JumpSearchAsync", valid); Equal(0, session.ChapterIndex);
                await Invoke(view, "JumpSearchAsync", list.Items[0]!); await Settled(view); Equal(1, session.ChapterIndex);
                string id = session.Book!.BookKey; True(await view.RunAsync(async () => { await Task.Run(() => session.RemoveBook(id)); await view.RenderAsync(); })); Equal(0, list.ItemCount);
            }
            finally { window.Close(); }
        });
    }
}

public class StorageTestProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
}
internal sealed class TestStorage
{
    public string Name = "backup.json";
    public Uri Uri = new("content://quietread-test/" + Guid.NewGuid().ToString("N"));
    public byte[] Bytes = [];
    public int Writes, Deletes;
    public bool ReadError, DeleteError;
    public string? Failure;
    public CancellationTokenSource? Cancel;
    public IStorageFile File => Proxy<IStorageFile>((method, _) => method.Name switch
    {
        "get_Name" => Name,
        "get_Path" => Uri,
        "get_CanBookmark" => false,
        "Dispose" => null,
        "OpenReadAsync" => ReadError ? Task.FromException<Stream>(new IOException("Injected read failure")) : Task.FromResult<Stream>(new MemoryStream(Bytes, writable: false)),
        "OpenWriteAsync" => Write(),
        "DeleteAsync" => Delete(),
        _ => throw new NotSupportedException(method.Name)
    });
    public static TestStorage Local(string path) => new() { Name = Path.GetFileName(path), Uri = new Uri(path) };
    public static IStorageProvider Provider(IStorageFile target) => Proxy<IStorageProvider>((method, _) => method.Name switch
    {
        "get_CanSave" => true,
        "get_CanOpen" or "get_CanPickFolder" => false,
        "SaveFilePickerAsync" => Task.FromResult<IStorageFile?>(target),
        _ => throw new NotSupportedException(method.Name)
    });
    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    { var result = DispatchProxy.Create<T, StorageTestProxy>(); ((StorageTestProxy)(object)result).Handler = handler; return result; }
    private Task<Stream> Write() { Writes++; return Task.FromResult<Stream>(new ProviderWriteStream(bytes => Bytes = bytes, Failure, Cancel)); }
    private Task Delete() { Deletes++; if (DeleteError) return Task.FromException(new IOException("Injected cleanup failure")); Bytes = []; return Task.CompletedTask; }
    private sealed class ProviderWriteStream(Action<byte[]> publish, string? failure, CancellationTokenSource? cancel) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            base.Write(buffer.Span[..(failure is "write" or "cancel" ? Math.Min(3, buffer.Length) : buffer.Length)]);
            if (failure == "write") return ValueTask.FromException(new IOException("Injected write failure"));
            if (failure == "cancel") { cancel!.Cancel(); return ValueTask.FromCanceled(token); }
            return ValueTask.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken token) => failure == "flush" ? Task.FromException(new IOException("Injected flush failure")) : Task.CompletedTask;
        protected override void Dispose(bool disposing) { if (disposing) publish(ToArray()); base.Dispose(disposing); }
    }
}
