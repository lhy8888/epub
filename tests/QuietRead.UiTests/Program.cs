using System.IO;
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
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        app.StartupUri = null;
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
                    Require(window.Reader.Document.FontSize == 28, "Typography did not update.");
                    window.AppearancePopup.IsOpen = false;
                });
                await Check("Rapid book replacement cancels retired operations", async () =>
                {
                    Task first = window.OpenBookAsync(sample);
                    Task second = window.OpenBookAsync(sample);
                    await Task.WhenAll(first, second);
                    Require(window.BookTitleText.Text == "静读 · 阅读指南" && window.Reader.IsEnabled, "Book replacement did not settle.");
                });
                await Check("Closing saves isolated history, preferences and bookmarks", async () =>
                {
                    await window.NavigateAsync(2);
                    window.Close();
                    AppState state = store.Load();
                    Require(!store.LoadFailed && state.Books.Count == 1, "History did not persist.");
                    Require(state.Books[0].ChapterIndex == 2 && state.Books[0].Bookmarks.Count == 1, "Reading position/bookmark missing.");
                    Require(state.Preferences.FontSize == 28, "Preferences did not persist.");
                    window = new MainWindow(store);
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
        Console.WriteLine($"{(exception == null ? "PASS" : "FAIL")} {name}{(exception == null ? "" : ": " + exception.Message)}");
    }

    private static void WriteReport(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            utc = DateTime.UtcNow, os = RuntimeInformation.OSDescription,
            dotnet = RuntimeInformation.FrameworkDescription, windowsUiExecuted = true,
            passed = _passed, failed = _failed, tests = Outcomes
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
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
