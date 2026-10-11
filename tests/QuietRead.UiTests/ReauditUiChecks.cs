using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using QuietRead;
using QuietRead.Core;
using static QuietRead.UiTests.Program;

namespace QuietRead.UiTests;

internal static class ReauditUiChecks
{
    internal static async Task Run(string temporary)
    {
        string file = Path.Combine(temporary, "reaudit.epub"), other = Path.Combine(temporary, "reaudit-other.epub");
        WriteFixture(file, ["<p>" + new string('中', 4096) + "</p>"]);
        WriteFixture(other, ["<p>Another target book</p>"]);
        var window = new MainWindow(new StateStore(Path.Combine(temporary, "reaudit"))); window.Show(); await Idle(window);
        try
        {
            await window.OpenBookAsync(file); await Idle(window);
            string id = window.Library.Entries().Single().BookId;
            await Check("Audit annotation candidates validate before mutation and retain full notes", async () =>
            {
                Paragraph p = window.Reader.Document.Blocks.OfType<Paragraph>().First();
                window.Reader.Selection.Select(DocumentPosition.Pointer(p, 0)!, DocumentPosition.Pointer(p, 4096)!);
                await window.AddAnnotationAsync(new string('中', 2001));
                Require(window.AnnotationsList.Items.Count == 0 && window.StatusText.Text.Contains("符合限制", StringComparison.Ordinal), "Invalid candidate mutated active data.");
                window.Reader.Selection.Select(DocumentPosition.Pointer(p, 0)!, DocumentPosition.Pointer(p, 4096)!);
                await window.AddAnnotationAsync(new string('中', 2000));
                Require(await window.SaveReadingDataAsync(), "Full-length note failed to save.");
                Require(window.Library.LoadBook(id).Annotations.Single().Note.Length == 2000, "Full note did not persist.");
            });
            await Check("Audit immediate appearance flush and close do not wait for debounce", async () =>
            {
                await Idle(window); Require(await window.SaveReadingDataAsync(), "Initial clean save failed.");
                window.BookAppearance.IsChecked = true; Require(await window.SaveReadingDataAsync(), "Scope save failed.");
                window.FontSizeSlider.Value = 30;
                Require(window.TryFlushReadingData(), "Immediate final flush failed.");
                Require(window.Library.LoadBook(id).Appearance?.FontSize == 30, "Final flush skipped pending slider edit.");
                window.FontSizeSlider.Value = 31;
                window.Close();
                var reopened = new MainWindow(new StateStore(Path.Combine(temporary, "reaudit"))); reopened.Show();
                try { await reopened.OpenBookAsync(file); Require(reopened.FontSizeSlider.Value == 31, "Close lost the last slider value."); }
                finally { reopened.Close(); }
            });
        }
        finally { window.Close(); }

        var theme = new MainWindow(new StateStore(Path.Combine(temporary, "reaudit-theme"))); theme.Show(); await Idle(theme);
        try
        {
            await theme.OpenBookAsync(file); await Idle(theme);
            await Check("Audit system-theme refresh uses the current book appearance", async () =>
            {
                theme.BookAppearance.IsChecked = true;
                theme.AppearanceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(theme);
                Button dark = Visuals<Button>(theme.AppearancePopup.Child).Single(b => b.Tag is string s && s == "Dark");
                dark.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); theme.RefreshSystemTheme();
                theme.AppearancePopup.IsOpen = false;
                var brush = (SolidColorBrush)Application.Current.Resources["ReaderBrush"];
                if (!SystemParameters.HighContrast) Require(brush.Color == (Color)ColorConverter.ConvertFromString("#1C231F"), "System refresh used the global Paper theme.");
                Require(await theme.SaveReadingDataAsync(), "Theme save failed.");
                Require(theme.Library.LoadBook(theme.Library.Entries().Single().BookId).Appearance?.Theme == "Dark", "Theme source changed.");
            });
            await Check("Audit slider edits during an in-flight save remain dirty for the next snapshot", async () =>
            {
                object gate = typeof(LibraryStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(theme.Library)!;
                using var release = new ManualResetEventSlim();
                var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task holder = Task.Run(() => { lock (gate) { held.SetResult(true); release.Wait(); } });
                try
                {
                    await held.Task;
                    theme.FontSizeSlider.Value = 26; Task<bool> first = theme.SaveReadingDataAsync();
                    Require(!first.IsCompleted, "Save was not held in flight.");
                    theme.FontSizeSlider.Value = 29; release.Set(); await holder;
                    Require(await first && await theme.SaveReadingDataAsync(), "Coalesced save failed.");
                    Require(theme.Library.LoadBook(theme.Library.Entries().Single().BookId).Appearance?.FontSize == 29, "Old snapshot marked the new slider edit clean.");
                }
                finally { release.Set(); await holder; }
            });
            await Check("Audit pending annotation capture is cancelled when Home retires its chapter", async () =>
            {
                Require(await theme.SaveReadingDataAsync(), "Pre-cancel save failed.");
                string id = theme.Library.Entries().Single().BookId;
                var gate = (SemaphoreSlim)typeof(MainWindow).GetField("_readingWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(theme)!;
                await gate.WaitAsync();
                try
                {
                    Paragraph p = theme.Reader.Document.Blocks.OfType<Paragraph>().First();
                    theme.Reader.Selection.Select(DocumentPosition.Pointer(p, 0)!, DocumentPosition.Pointer(p, 10)!);
                    Task adding = theme.AddAnnotationAsync("retired selection"); Require(!adding.IsCompleted, "Capture did not enter the bounded queue.");
                    Require(await theme.GoHomeAsync(), "Home did not complete."); await adding;
                    Require(theme.WelcomePane.Visibility == Visibility.Visible && theme.Library.LoadBook(id).Annotations.Count == 0, "Cancelled capture modified retired data.");
                }
                finally { gate.Release(); }
            });
        }
        finally { theme.Close(); }

        var closing = new MainWindow(new StateStore(Path.Combine(temporary, "reaudit-close"))); closing.Show(); await Idle(closing);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await closing.OpenBookAsync(file); await Idle(closing); Require(await closing.SaveReadingDataAsync(), "Pre-close save failed.");
            await Check("Audit an open waiting for save retires safely when the window closes", async () =>
            {
                SetPendingSave(closing, pending.Task);
                Task opening = closing.OpenBookAsync(other); Require(!opening.IsCompleted, "Open did not wait for pending save.");
                closing.Close(); pending.SetResult(true); await opening;
                Require(!closing.IsVisible, "Retired open revived its closed window.");
            });
        }
        finally { pending.TrySetResult(true); closing.Close(); }

        var libraryOwner = new MainWindow(new StateStore(Path.Combine(temporary, "reaudit-library"))); libraryOwner.Show(); await Idle(libraryOwner);
        try
        {
            await libraryOwner.OpenBookAsync(file); await Idle(libraryOwner); Require(await libraryOwner.SaveReadingDataAsync(), "Pre-library save failed.");
            await Check("Audit removing the active entry completes Home before opening the next book", async () =>
            {
                var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var edited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var start = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                start.Tick += (_, _) =>
                {
                    LibraryWindow? dialog = Application.Current.Windows.OfType<LibraryWindow>().FirstOrDefault();
                    if (dialog == null) return;
                    start.Stop();
                    string active = libraryOwner.Library.Entries().Single().BookId;
                    libraryOwner.Library.Remove(active); dialog.Removed.Add(active);
                    typeof(LibraryWindow).GetProperty("OpenPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(dialog, other);
                    SetPendingSave(libraryOwner, saved.Task); dialog.Close(); edited.SetResult(true);
                };
                try
                {
                    start.Start();
                    libraryOwner.LibraryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await edited.Task;
                    Require(libraryOwner.ReaderPane.Visibility == Visibility.Visible, "Removed-book transition did not wait for save.");
                    saved.SetResult(true);
                    await WaitUntil(() => libraryOwner.Reader.IsEnabled && libraryOwner.Reader.Document != null && Text(libraryOwner).Contains("Another target book", StringComparison.Ordinal));
                    await Task.Delay(200); await Idle(libraryOwner);
                    Require(libraryOwner.ReaderPane.Visibility == Visibility.Visible && Text(libraryOwner).Contains("Another target book", StringComparison.Ordinal), "Retired Home cleared the chosen book.");
                }
                finally { start.Stop(); saved.TrySetResult(true); }
            });
        }
        finally { libraryOwner.Close(); }
    }

    private static void SetPendingSave(MainWindow window, Task<bool> task) =>
        typeof(MainWindow).GetField("_saveTask", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, task);
}
