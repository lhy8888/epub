using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using QuietRead;
using QuietRead.Core;
using static QuietRead.UiTests.Program;

namespace QuietRead.UiTests;

internal static class P1UiChecks
{
    internal static async Task Run(string temporary)
    {
        string directory = Path.Combine(temporary, "p1"), file = Path.Combine(temporary, "p1.epub");
        string filler = string.Concat(Enumerable.Range(0, 50).Select(i => $"<p>Filler paragraph {i:00} for directory scrolling.</p>"));
        WriteFixture(file, ["<h1 id='first'>First section</h1><p>alpha <strong>bold</strong> é 😀 👩‍💻 tail</p>" + filler + "<h2 id='second'>Second section</h2><p>second passage</p>" + filler,
            "<p>needle needles needle_x 中文测试。</p><p>NEEDLE</p><p style='text-align:center;text-indent:2em'>source alignment</p><pre>  first\n    second</pre><blockquote>quoted passage</blockquote>",
            "<p>needle final chapter</p>"]);
        AddNavigation(file);
        byte[] original = SHA256.HashData(File.ReadAllBytes(file));
        var window = new MainWindow(new StateStore(directory)); window.Show(); await Idle(window);
        try
        {
            await window.OpenBookAsync(file); await Idle(window);
            string id = window.Library.Entries().Single().BookId;
            await Check("P1 selection across inline styles creates stable, accessible annotations", async () =>
            {
                Paragraph paragraph = window.Reader.Document.Blocks.OfType<Paragraph>().Single(p => ParagraphText(p).StartsWith("alpha", StringComparison.Ordinal));
                window.Reader.Selection.Select(DocumentPosition.Pointer(paragraph, 0)!, DocumentPosition.Pointer(paragraph, 10)!);
                await window.AddAnnotationAsync("first note"); await Idle(window);
                Require(window.AnnotationsList.Items.Count == 1, "Selection was not annotated.");
                var a = (BookAnnotation)window.AnnotationsList.Items[0]; Require(a.Range.Text == "alpha bold", "Cross-style selection has wrong offsets.");
                paragraph = window.Reader.Document.Blocks.OfType<Paragraph>().Single(p => ParagraphText(p).StartsWith("alpha", StringComparison.Ordinal));
                Require(paragraph.Inlines.OfType<Span>().Any(s => s.FontWeight == FontWeights.SemiBold), "Annotation removed emphasis.");
                Require(Runs(paragraph).Any(r => r.Background != null), "Annotation background is missing.");
                window.AnnotationNoteBox.Text = "edited note 中文"; window.AnnotationColorCombo.SelectedIndex = 2;
                window.AnnotationSaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntil(() => window.Reader.IsEnabled); await Idle(window);
                Require(((BookAnnotation)window.AnnotationsList.Items[0]).Note == "edited note 中文", "Note edit did not apply.");
                Require(((BookAnnotation)window.AnnotationsList.Items[0]).Color == "Blue", "Color edit did not apply.");
                Require(await window.SaveReadingDataAsync(), "Annotation save failed.");
                Require(window.Library.LoadBook(id).Annotations[0].Range.Text == "alpha bold", "Saved range changed.");
            });
            await Check("P1 annotation survives font reflow and renamed-file reopening without EPUB changes", async () =>
            {
                window.FontSizeSlider.Value = 30; await Task.Delay(250); await Idle(window);
                Require(window.AnnotationsList.Items.Count == 1 && Text(window).Contains("alpha bold", StringComparison.Ordinal), "Reflow lost annotation text.");
                string renamed = Path.Combine(temporary, "p1-renamed.epub"); File.Copy(file, renamed);
                await window.OpenBookAsync(renamed); await Idle(window);
                Require(window.Library.Entries().Count == 1 && window.AnnotationsList.Items.Count == 1, "Content identity did not restore notes.");
                Require(window.Library.Entries()[0].FilePath == renamed, "Library retained the missing path.");
                Require(original.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(file))), "Reading changed the EPUB.");
            });
            await Check("P1 per-book appearance is isolated from global defaults and can be reset", async () =>
            {
                window.FontSizeSlider.Value = 22; window.BookAppearance.IsChecked = true; window.FontSizeSlider.Value = 32;
                await Task.Delay(250); Require(await window.SaveReadingDataAsync(), "Book appearance save failed.");
                string other = Path.Combine(temporary, "p1-other.epub"); WriteFixture(other, ["<p>Another book</p>"]);
                await window.OpenBookAsync(other); Require(Math.Abs(window.Reader.Document.FontSize - 22) < 0.01, "Book override leaked into defaults.");
                string otherId = window.Library.Entries().Single(e => e.FilePath == other).BookId;
                window.Library.UpdateMetadata(otherId, "Another book", "", [], "", "Reading");
                await window.OpenBookAsync(file); Require(Math.Abs(window.Reader.Document.FontSize - 32) < 0.01 && window.BookAppearance.IsChecked == true, "Book override did not restore.");
                window.BookAppearance.IsChecked = false; await Task.Delay(250); await Idle(window);
                Require(Math.Abs(window.Reader.Document.FontSize - 22) < 0.01, "Clearing override did not restore defaults.");
            });
            await Check("P1 bounded source layout and reading presets preserve preformatted code", async () =>
            {
                await window.NavigateAsync(1); await Idle(window);
                Paragraph source = window.Reader.Document.Blocks.OfType<Paragraph>().Single(p => ParagraphText(p).Contains("source alignment", StringComparison.Ordinal));
                Require(source.TextAlignment == TextAlignment.Center && Math.Abs(source.TextIndent - 44) < 0.01, "Whitelisted source layout was not applied.");
                Paragraph code = window.Reader.Document.Blocks.OfType<Paragraph>().Single(p => ParagraphText(p).Contains("first", StringComparison.Ordinal));
                Require(ParagraphText(code).Contains("  first\n    second", StringComparison.Ordinal), "Code indentation was collapsed.");
                window.AppearanceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(window);
                Button preset = Visuals<Button>(window.AppearancePopup.Child).Single(b => b.Tag is string tag && tag == "Novel");
                preset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(250); await Idle(window);
                Require(window.AlignmentCombo.SelectedIndex == 4 && window.IndentSlider.Value == 2, "Novel preset is incomplete.");
                code = window.Reader.Document.Blocks.OfType<Paragraph>().Single(p => ParagraphText(p).Contains("first", StringComparison.Ordinal));
                Require(code.TextAlignment == TextAlignment.Left && ParagraphText(code).Contains("    second", StringComparison.Ordinal), "Preset damaged code formatting.");
                window.AppearancePopup.IsOpen = false;
            });
            await Check("P1 directory folding, ancestor filtering and current section track separate anchors", async () =>
            {
                await window.NavigateAsync(0, fragment: "first"); await Idle(window);
                int count = window.TocList.Items.Count;
                Button toggle = Visuals<Button>(window.TocList).First(b => b.Tag is int index && index == 0);
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Require(window.TocList.Items.Count == count - 1, "Directory did not fold.");
                window.TocFilter.Text = "Second section"; Require(window.TocList.Items.Count == 2, "Filtering lost the ancestor or child.");
                await window.NavigateAsync(0, fragment: "second"); await Idle(window);
                object current = window.TocList.Items.Cast<object>().Single(r => (string)r.GetType().GetProperty("CurrentMark")!.GetValue(r)! == "●");
                TocItem item = (TocItem)current.GetType().GetProperty("Item")!.GetValue(current)!;
                Require(item.Fragment == "second", "Current marker selected another section in the same chapter.");
                window.TocFilter.Text = "";
            });
            await Check("P1 chapter, case and whole-word search options match the rendered highlights", async () =>
            {
                await window.NavigateAsync(1); window.SearchBox.Text = "needle";
                window.SearchChapter.IsChecked = true; window.SearchCase.IsChecked = true; window.SearchWholeWord.IsChecked = true;
                await window.SearchAsync(); Require(window.SearchList.Items.Count == 1, "Scoped exact-word search returned partial or case-insensitive matches.");
                await window.MoveSearchAsync(1); await Idle(window);
                Brush search = (Brush)Application.Current.Resources["HighlightBrush"];
                string highlighted = string.Concat(window.Reader.Document.Blocks.OfType<Paragraph>().SelectMany(Runs).Where(r => ReferenceEquals(r.Background, search)).Select(r => r.Text));
                Require(highlighted == "needle", "Highlight matching differs from search matching.");
                window.SearchCase.IsChecked = false; await window.SearchAsync(); Require(window.SearchList.Items.Count == 2, "Case-insensitive scope did not match both words.");
                window.SearchChapter.IsChecked = false; await window.SearchAsync(); Require(window.SearchList.Items.Count == 3, "Whole-book search omitted the final chapter.");
                window.StopSearch();
            });
            await Check("P1 library metadata, filtering and recent-list clearing retain long-term data", async () =>
            {
                Require(await window.SaveReadingDataAsync(), "Before-library save failed.");
                var library = new LibraryWindow(window.Library) { Owner = window }; library.Show();
                try
                {
                    library.FilterBox.Text = "UI audit fixture"; Require(library.BooksList.Items.Count == 1, "Library title filter failed.");
                    library.BooksList.SelectedIndex = 0; library.TitleBox.Text = "Edited library title"; library.AuthorBox.Text = "Editor";
                    library.TagsBox.Text = "fiction, 中文"; library.SeriesBox.Text = "Series A"; library.ReadingState.SelectedIndex = 2;
                    Button save = Visuals<Button>(library).Single(b => b.Content is string text && text == "保存元数据");
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitUntil(() => library.IsEnabled);
                    library.FilterBox.Text = "中文"; library.StatusFilter.SelectedIndex = 3; library.SortCombo.SelectedIndex = 1;
                    Require(library.BooksList.Items.Count == 1, "Tags/state filtering failed.");
                    LibraryEntry entry = window.Library.Entries().Single(e => e.BookId == id); Require(entry.Title == "Edited library title" && entry.Series == "Series A", "Metadata did not persist.");
                }
                finally { library.Close(); }
                await window.ClearRecentAsync(); Require(window.RecentList.Items.Count == 0, "Recent list did not clear.");
                Require(window.Library.LoadBook(id).Annotations.Count == 1 && window.Library.Entries().Count == 2, "Clearing recents deleted long-term data.");
                Require((await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => window.SaveReadingDataAsync()))).All(saved => saved), "Concurrent save requests did not settle.");
                await window.NavigateAsync(2); Require(window.TryFlushReadingData(), "Clean-to-dirty navigation did not flush.");
                Require(window.Library.LoadBook(id).History.ChapterIndex == 2, "Final flush omitted navigation after a completed save.");
            });
            await Check("P1 backup exports and repeated imports preserve notes, metadata and source bytes", () =>
            {
                string backup = Path.Combine(temporary, "p1-backup.json"), markdown = Path.Combine(temporary, "p1-notes.md");
                LibraryWindow.WriteExport(LocalFilePath.Check(backup, ".json", false), window.Library.ExportBackup());
                LibraryWindow.WriteExport(LocalFilePath.Check(markdown, ".md", false), Encoding.UTF8.GetBytes(window.Library.ExportMarkdown(id)));
                var restored = new LibraryStore(Path.Combine(temporary, "p1-restored")); byte[] bytes = LibraryStore.ReadBounded(backup, BookDataRules.MaxBackupBytes);
                Require(restored.PreviewImport(bytes).NewBooks == 2, "Import preview has the wrong book count."); restored.Import(bytes, false); restored.Import(bytes, false);
                Require(restored.LoadBook(id).Annotations.Count == 1 && restored.Entries().Count == 2, "Repeated import duplicated data.");
                Require(restored.Entries().All(e => e.FilePath == ""), "Backup exposed local paths.");
                Require(File.ReadAllText(markdown).Contains("edited note 中文", StringComparison.Ordinal), "Markdown omitted notes.");
                Require(original.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(file))), "Export changed the EPUB."); return Task.CompletedTask;
            });
            await Check("P1 failed autosave is visible, preserves committed bytes and succeeds after retry", async () =>
            {
                Require(await window.SaveReadingDataAsync(), "Initial save failed.");
                byte[] index = File.ReadAllBytes(Path.Combine(directory, "library.json"));
                string books = Path.Combine(directory, "books"), moved = books + ".saved";
                Directory.Move(books, moved); File.WriteAllText(books, "block writes");
                try
                {
                    window.FontSizeSlider.Value = 26;
                    window.BookmarkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(!await window.SaveReadingDataAsync(), "Blocked save was reported successful.");
                    Require(window.RetrySaveButton.Visibility == Visibility.Visible && window.SaveStatusText.Text.Contains("保存失败", StringComparison.Ordinal), "Save failure is hidden.");
                    Require(index.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "library.json"))), "Failed save altered committed data.");
                }
                finally { File.Delete(books); Directory.Move(moved, books); }
                Require(await window.SaveReadingDataAsync(force: true), "Retry did not recover.");
                Require(window.RetrySaveButton.Visibility == Visibility.Collapsed, "Recovered save still reports failure.");
                string[] generations = Directory.GetFiles(books); Require(window.TryFlushReadingData(), "Final flush failed.");
                Require(generations.Order().SequenceEqual(Directory.GetFiles(books).Order()), "Unchanged final flush created another generation.");
            });
            await Check("P1 note deletion updates both rendered text and persistent book data", async () =>
            {
                await window.NavigateAsync(0); window.AnnotationsList.SelectedIndex = 0;
                window.AnnotationDeleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntil(() => window.Reader.IsEnabled); Require(await window.SaveReadingDataAsync(), "Deletion save failed.");
                Require(window.AnnotationsList.Items.Count == 0 && window.Library.LoadBook(id).Annotations.Count == 0, "Deleted annotation reappeared.");
            });
        }
        finally { window.Close(); }
    }

    private static string ParagraphText(Paragraph paragraph) => new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
    private static IEnumerable<Run> Runs(Paragraph paragraph) => paragraph.Inlines.OfType<Span>().SelectMany(s => s.Inlines.OfType<Run>());
    private static void AddNavigation(string path)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite); using var zip = new ZipArchive(file, ZipArchiveMode.Update);
        ZipArchiveEntry package = zip.GetEntry("book.opf")!; string xml;
        using (var reader = new StreamReader(package.Open())) xml = reader.ReadToEnd(); package.Delete();
        using (var writer = new StreamWriter(zip.CreateEntry("book.opf").Open())) writer.Write(xml.Replace("</manifest>", "<item id='nav' href='nav.xhtml' media-type='application/xhtml+xml' properties='nav'/></manifest>", StringComparison.Ordinal));
        using var navigation = new StreamWriter(zip.CreateEntry("nav.xhtml").Open());
        navigation.Write("<html xmlns:epub='http://www.idpf.org/2007/ops'><body><nav epub:type='toc'><ol><li><a href='c0.xhtml#first'>First section</a><ol><li><a href='c0.xhtml#second'>Second section</a></li></ol></li><li><a href='c1.xhtml'>Chapter two</a></li><li><a href='c2.xhtml'>Chapter three</a></li></ol></nav></body></html>");
    }
}
