using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuietRead;
using QuietRead.Core;

namespace QuietRead.UiTests;

internal static class Program
{
    private static readonly List<object> Outcomes = [];
    private static readonly List<object> MemorySamples = [];
    private static int _passed, _failed;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: QuietRead.UiTests <sample.epub> <report.json> <screenshot.png>");
            return 2;
        }

        string sample = Path.GetFullPath(args[0]);
        string report = Path.GetFullPath(args[1]);
        string screenshot = Path.GetFullPath(args[2]);
        string temporary = Path.Combine(Path.GetTempPath(), "QuietRead-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var app = new App(createMainWindow: false) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            Record("UI watchdog", new TimeoutException("WPF smoke tests exceeded two minutes."));
            WriteReport(report);
            app.Shutdown(1);
        };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            MainWindow? window = null;
            try
            {
                var store = new StateStore(temporary);
                window = new MainWindow(store);
                window.Show();
                await Idle(window);
                await Check("Real WPF window starts with empty history", () =>
                {
                    Require(window.IsLoaded && window.IsVisible, "Window was not shown.");
                    Require(window.WelcomePane.Visibility == Visibility.Visible, "Welcome pane missing.");
                    Require(window.RecentList.Items.Count == 0, "Test state is not isolated.");
                    return Task.CompletedTask;
                });
                await Check("EPUB opens and Windows decodes its cover", async () =>
                {
                    await window.OpenBookAsync(sample);
                    await Idle(window);
                    Require(window.ReaderPane.Visibility == Visibility.Visible, "Reader is hidden.");
                    Require(window.BookTitleText.Text == "静读 · 阅读指南", "Book did not load.");
                    Require(window.TocList.Items.Count == 4, "TOC was not loaded.");
                    Image[] images = window.Reader.Document.Blocks.OfType<BlockUIContainer>()
                        .Select(b => b.Child).OfType<Image>().ToArray();
                    Require(images.Length > 0, "Windows failed to decode the cover.");
                    Require(images.All(i => i.Source.IsFrozen && i.Source is BitmapSource), "Images are not detached/frozen.");
                    Require(Text(window).Contains("QuietRead", StringComparison.Ordinal), "Text was not rendered.");
                    Capture(window, screenshot);
                });
                await Check("Long chapter is segmented and navigation changes content", async () =>
                {
                    await window.NavigateAsync(3);
                    Require(window.PartText.Text.Contains("/", StringComparison.Ordinal), "Long chapter was not segmented.");
                    string before = Text(window);
                    await window.NavigateAsync(3, int.MaxValue, 1);
                    Require(Text(window) != before, "Last segment did not replace the first.");
                    Require(window.Reader.Document.Blocks.Count <= ReaderLimits.BlocksPerView, "View exceeds block limit.");
                });
                await Check("Full screen enters and exits through its toolbar button", async () =>
                {
                    WindowStyle style = window.WindowStyle;
                    WindowState state = window.WindowState;
                    window.FullScreenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Idle(window);
                    Require(window.WindowStyle == WindowStyle.None && window.WindowState == WindowState.Maximized, "Full screen did not open.");
                    Require((string)window.FullScreenButton.Content == "退出全屏", "Exit control is missing.");
                    window.FullScreenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Idle(window);
                    Require(window.WindowStyle == style && window.WindowState == state, "Window state did not restore.");
                });
                await Check("Local file policy rejects UNC paths before opening them", () =>
                {
                    Require(MainWindow.LocalFile(sample) == sample, "Local sample was rejected.");
                    bool rejected = false;
                    try { MainWindow.LocalFile(@"\\unreachable.invalid\books\test.epub"); }
                    catch (EpubException) { rejected = true; }
                    Require(rejected, "UNC path was accepted.");
                    return Task.CompletedTask;
                });
                await Check("Native image decoding honors the total pixel budget and supports GIF", async () =>
                {
                    string fixture = Path.Combine(temporary, "image-budget.epub");
                    WriteImageFixture(fixture);
                    using var book = EpubBook.Open(fixture);
                    ParsedChapter chapter = book.ReadChapter(0);
                    PreparedImages prepared = await Task.Run(() => DocumentRenderer.PrepareImages(book, chapter, 0, chapter.Blocks.Count, CancellationToken.None));
                    long pixels = prepared.Images.Values.OfType<BitmapSource>().Sum(b => (long)b.PixelWidth * b.PixelHeight);
                    Require(pixels > 0 && pixels <= ReaderLimits.ImageViewPixels, "Decoded images exceeded their pixel budget.");
                    Require(prepared.Skipped > 0, "Excess images were not skipped.");
                    Require(prepared.Images.ContainsKey("small.gif"), "A valid first GIF frame failed to decode.");
                });
                await Check("Invalid images cannot bypass the cumulative input allocation budget", async () =>
                {
                    string fixture = Path.Combine(temporary, "invalid-images.epub");
                    byte[] invalid = new byte[ReaderLimits.ImageBytes];
                    WriteFixture(fixture, [string.Concat(Enumerable.Range(0, 24).Select(i => $"<img src='i{i}.png'/>"))],
                        Enumerable.Range(0, 24).ToDictionary(i => $"i{i}.png", _ => invalid));
                    using var book = EpubBook.Open(fixture);
                    ParsedChapter chapter = book.ReadChapter(0);
                    var result = await Task.Run(() =>
                    {
                        long before = GC.GetAllocatedBytesForCurrentThread();
                        PreparedImages images = DocumentRenderer.PrepareImages(book, chapter, 0, chapter.Blocks.Count, CancellationToken.None);
                        return (images, allocated: GC.GetAllocatedBytesForCurrentThread() - before);
                    });
                    Require(result.images.Images.Count == 0 && result.images.Skipped == 24, "Invalid images were not skipped.");
                    Require(result.allocated < 32L * 1024 * 1024, $"Invalid image input allocated {result.allocated} bytes.");
                });
                await Check("Full-text search renders results and local hyperlinks have no external URI", async () =>
                {
                    window.SearchBox.Text = "阅读";
                    await window.SearchAsync();
                    Require(window.SearchList.Items.Count > 0, "Search returned no results.");
                    await window.NavigateAsync(0);
                    foreach (Hyperlink link in window.Reader.Document.Blocks.OfType<Paragraph>()
                        .SelectMany(p => p.Inlines.OfType<Hyperlink>()))
                        Require(link.NavigateUri == null, "Hyperlink could navigate outside the book.");
                });
                await Check("Search highlights a phrase crossing a bold text boundary", async () =>
                {
                    window.SearchBox.Text = "的 Aa";
                    await window.SearchAsync();
                    Require(window.SearchList.Items.Count > 0, "Cross-format search returned no results.");
                    await window.NavigateAsync(0);
                    string highlighted = string.Concat(window.Reader.Document.Blocks.OfType<Paragraph>()
                        .SelectMany(p => p.Inlines.OfType<Span>()).SelectMany(s => s.Inlines.OfType<Run>())
                        .Where(r => r.ReadLocalValue(TextElement.BackgroundProperty) != DependencyProperty.UnsetValue).Select(r => r.Text));
                    Require(highlighted.Contains("的 Aa", StringComparison.Ordinal), "Highlight stopped at the bold boundary.");
                });
                await Check("Search highlights both sides of an artificial paragraph split", () =>
                {
                    string fixture = Path.Combine(temporary, "split-search.epub");
                    WriteFixture(fixture, ["<p>" + new string('x', 4093) + "<b>alpha</b> beta</p>"]);
                    using var book = EpubBook.Open(fixture);
                    ParsedChapter chapter = book.ReadChapter(0);
                    Require(book.Search("alpha beta").Hits.Count == 1, "Split phrase was not found.");
                    FlowDocument document = DocumentRenderer.Create(chapter, 0, chapter.Blocks.Count,
                        new PreparedImages(new Dictionary<string, ImageSource>(), 0), new ReaderPreferences(), "alpha beta", _ => { }, out _);
                    string highlighted = string.Concat(document.Blocks.OfType<Paragraph>()
                        .SelectMany(p => p.Inlines.OfType<Span>()).SelectMany(s => s.Inlines.OfType<Run>())
                        .Where(r => r.ReadLocalValue(TextElement.BackgroundProperty) != DependencyProperty.UnsetValue).Select(r => r.Text));
                    Require(highlighted == "alpha beta", "Split phrase highlight was incomplete.");
                    Require(document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Span>())
                        .Where(s => s.FontWeight == FontWeights.SemiBold).SelectMany(s => s.Inlines.OfType<Run>()).Any(r => r.Text == "alp"),
                        "Highlight removed bold styling.");
                    return Task.CompletedTask;
                });
                await Check("Previous chapter navigation reaches the bottom of a tall final paragraph", async () =>
                {
                    string fixture = Path.Combine(temporary, "previous-bottom.epub");
                    WriteFixture(fixture, ["<p>start</p><p>" + string.Concat(Enumerable.Repeat("last paragraph words ", 190)) + "</p>", "<p>next chapter</p>"]);
                    await window.OpenBookAsync(fixture);
                    await window.NavigateAsync(1);
                    await window.StepReadingAsync(-1);
                    await Idle(window);
                    ScrollViewer scroll = Visuals<ScrollViewer>(window.Reader).First();
                    Require(window.ProgressText.Text.StartsWith("1 / 2", StringComparison.Ordinal), "Previous chapter was not selected.");
                    Require(scroll.ScrollableHeight > 100, "Fixture does not exercise scrolling.");
                    Require(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 2, "Previous page did not reach the chapter bottom.");
                    await window.OpenBookAsync(sample);
                });
                await Check("Bookmark button stores one bookmark and rejects duplicates", () =>
                {
                    window.BookmarkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(window.BookmarksList.Items.Count == 1, "Bookmark was not added.");
                    window.BookmarkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(window.BookmarksList.Items.Count == 1, "Duplicate bookmark was stored.");
                    return Task.CompletedTask;
                });
                await Check("Three themes and typography update the document", async () =>
                {
                    window.AppearanceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Idle(window);
                    Require(window.AppearancePopup.IsOpen, "Appearance popup failed.");
                    Button[] themes = Visuals<Button>(window.AppearancePopup.Child).Where(b => b.Tag is string s &&
                        s is "Paper" or "Sepia" or "Dark").ToArray();
                    Require(themes.Length == 3, "Theme controls missing.");
                    foreach (Button theme in themes)
                    {
                        theme.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Color expected = (string)theme.Tag == "Dark" ? Color.FromRgb(0x18, 0x1E, 0x1A) :
                            (string)theme.Tag == "Sepia" ? Color.FromRgb(0xEE, 0xE8, 0xDA) : Color.FromRgb(0xF4, 0xF6, 0xF3);
                        Require(((SolidColorBrush)app.Resources["AppBrush"]).Color == expected, "Theme color did not update.");
                    }
                    window.FontSizeSlider.Value = 28;
                    await Task.Delay(250);
                    await Idle(window);
                    Require(Math.Abs(window.Reader.Document.FontSize - 28) < 0.01, "Typography did not update.");
                    window.AppearancePopup.IsOpen = false;
                });
                await Check("Rapid book replacement cancels retired operations", async () =>
                {
                    Task[] replacements = Enumerable.Range(0, 12).Select(_ => window.OpenBookAsync(sample)).ToArray();
                    await Task.WhenAll(replacements);
                    Require(window.BookTitleText.Text == "静读 · 阅读指南" && window.Reader.IsEnabled, "Book replacement did not settle.");
                });
                await Check("Returning home during navigation clears document and stale callbacks", async () =>
                {
                    Task pending = window.NavigateAsync(3);
                    window.HomeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await pending;
                    await Idle(window);
                    Require(window.Reader.Document == null && window.TocList.Items.Count == 0, "Retired content was retained.");
                    Require(window.WelcomePane.Visibility == Visibility.Visible && window.StatusText.Text == "就绪", "A stale callback changed the home page.");
                    await window.OpenBookAsync(sample);
                });
                await Check("Closing saves isolated history, preferences and bookmarks", async () =>
                {
                    await window.NavigateAsync(2);
                    Task pending = window.NavigateAsync(3);
                    window.Close();
                    await pending;
                    await window.OpenBookAsync(sample); // closed windows ignore further requests
                    AppState state = store.Load();
                    Require(!store.LoadFailed && state.Books.Count == 2, "History did not persist.");
                    BookHistory sampleHistory = state.Books.Single(b => b.FilePath == sample);
                    Require(sampleHistory.ChapterIndex == 2 && sampleHistory.Bookmarks.Count == 1, "Reading position/bookmark missing.");
                    Require(Math.Abs(state.Preferences.FontSize - 28) < 0.01, "Preferences did not persist.");
                    window = new MainWindow(new StateStore(temporary));
                    window.Show();
                    await Idle(window);
                    await window.OpenBookAsync(sample);
                    Require(window.ProgressText.Text.StartsWith("3 / 4", StringComparison.Ordinal), "Resume did not restore the chapter.");
                    Require(window.BookmarksList.Items.Count == 1, "Resume lost bookmarks.");
                });
            }
            catch (Exception exception) { Record("WPF smoke harness", exception); }
            finally
            {
                window?.Close();
                timeout.Stop();
                WriteReport(report);
                app.Shutdown(_failed == 0 ? 0 : 1);
            }
        };
        int exitCode = app.Run();
        try { Directory.Delete(temporary, true); }
        catch (IOException) { }
        return exitCode;
    }

    private static async Task Check(string name, Func<Task> action)
    {
        try { await action(); Record(name, null); }
        catch (Exception exception) { Record(name, exception); }
    }

    private static void Record(string name, Exception? exception)
    {
        if (exception == null) _passed++; else _failed++;
        Outcomes.Add(new { name, passed = exception == null, error = exception?.ToString() });
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        MemorySamples.Add(new
        {
            after = name,
            workingSetBytes = process.WorkingSet64,
            privateBytes = process.PrivateMemorySize64,
            peakWorkingSetBytes = process.PeakWorkingSet64,
            managedBytes = GC.GetTotalMemory(false)
        });
        Console.WriteLine($"{(exception == null ? "PASS" : "FAIL")} {name}{(exception == null ? "" : ": " + exception.Message)}");
    }

    private static void WriteReport(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            utc = DateTime.UtcNow,
            os = RuntimeInformation.OSDescription,
            dotnet = RuntimeInformation.FrameworkDescription,
            windowsUiExecuted = true,
            passed = _passed,
            failed = _failed,
            tests = Outcomes,
            memorySamples = MemorySamples,
            memoryNote = "Hosted Windows runner; includes synthetic image stress and test harness. No forced GC; not a Windows 11 idle-memory benchmark."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static string Text(MainWindow window) => new TextRange(window.Reader.Document.ContentStart, window.Reader.Document.ContentEnd).Text;

    private static async Task Idle(MainWindow window)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    }

    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (T child in Visuals<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }

    private static void Capture(MainWindow window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void WriteImageFixture(string path)
    {
        var bitmap = BitmapSource.Create(1600, 1200, 96, 96, PixelFormats.Bgra32, null, new byte[1600 * 1200 * 4], 1600 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream(); encoder.Save(png);
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        void Add(string name, byte[] data)
        { using Stream entry = zip.CreateEntry(name).Open(); entry.Write(data); }
        void Xml(string name, string text) => Add(name, System.Text.Encoding.UTF8.GetBytes(text));
        Xml("mimetype", "application/epub+zip");
        Xml("META-INF/container.xml", "<container><rootfiles><rootfile full-path='book.opf'/></rootfiles></container>");
        string manifest = string.Concat(Enumerable.Range(0, 9).Select(i => $"<item id='i{i}' href='i{i}.png' media-type='image/png'/>"));
        Xml("book.opf", "<package><metadata><title>Image budget test</title></metadata><manifest><item id='c' href='c.xhtml' media-type='application/xhtml+xml'/>" + manifest +
            "<item id='gif' href='small.gif' media-type='image/gif'/></manifest><spine><itemref idref='c'/></spine></package>");
        Xml("c.xhtml", "<html><body>" + string.Concat(Enumerable.Range(0, 9).Select(i => $"<img src='i{i}.png'/>")) + "<img src='small.gif'/></body></html>");
        for (int i = 0; i < 9; i++) Add($"i{i}.png", png.ToArray());
        Add("small.gif", Convert.FromHexString("47494638396101000100800000000000FFFFFF2C00000000010001000002024401003B"));
    }

    private static void WriteFixture(string path, string[] bodies, Dictionary<string, byte[]>? images = null)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        void Add(string name, byte[] data) { using Stream stream = zip.CreateEntry(name).Open(); stream.Write(data); }
        void Xml(string name, string text) => Add(name, System.Text.Encoding.UTF8.GetBytes(text));
        Xml("mimetype", "application/epub+zip");
        Xml("META-INF/container.xml", "<container><rootfiles><rootfile full-path='book.opf'/></rootfiles></container>");
        string manifest = string.Concat(Enumerable.Range(0, bodies.Length).Select(i => $"<item id='c{i}' href='c{i}.xhtml' media-type='application/xhtml+xml'/>"));
        if (images != null)
            manifest += string.Concat(images.Keys.Select((name, i) => $"<item id='i{i}' href='{name}' media-type='image/png'/>"));
        Xml("book.opf", "<package><metadata><title>UI audit fixture</title></metadata><manifest>" + manifest + "</manifest><spine>" +
            string.Concat(Enumerable.Range(0, bodies.Length).Select(i => $"<itemref idref='c{i}'/>")) + "</spine></package>");
        for (int i = 0; i < bodies.Length; i++) Xml($"c{i}.xhtml", "<html><body>" + bodies[i] + "</body></html>");
        if (images != null) foreach ((string name, byte[] data) in images) Add(name, data);
    }
}
