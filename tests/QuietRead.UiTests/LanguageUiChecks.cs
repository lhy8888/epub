using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using QuietRead;
using QuietRead.Core;
using static QuietRead.UiTests.Program;

namespace QuietRead.UiTests;

internal static class LanguageUiChecks
{
    internal static async Task Run(string temporary, string screenshot)
    {
        string directory = Path.Combine(temporary, "language"), file = Path.Combine(temporary, "language.epub");
        WriteFixture(file, ["<p>中文原文 打开 书库 English passage stays unchanged.</p>" +
            "<p><a xmlns:epub='http://www.idpf.org/2007/ops' epub:type='noteref' href='#note'>注</a></p>" +
            "<aside role='doc-footnote' id='note'><p>脚注原文 Footnote content.</p></aside>"]);
        var window = new MainWindow(new StateStore(directory)); window.Show(); await Idle(window);
        try
        {
            await Check("Chinese and English menus switch immediately and fit the minimum window", async () =>
            {
                Require((string)window.LibraryButton.Content == "书库", "Fresh settings did not start in Chinese.");
                window.AppearanceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(window);
                window.LanguageCombo.SelectedIndex = 1; await Idle(window);
                Require((string)window.LibraryButton.Content == "Library" && (string)window.HomeButton.Content == "Recent", "Toolbar did not switch to English.");
                Require(window.SidebarTabs.Items.Cast<TabItem>().Select(t => (string)t.Header).SequenceEqual(new[] { "Contents", "Bookmarks", "Search", "Notes" }), "Sidebar menus did not translate.");
                Require((string)window.FontCombo.Items.Cast<ComboBoxItem>().First().Content == "SimSun · Chinese serif", "Appearance popup did not translate.");
                Require(AutomationProperties.GetName(window.Reader) == "Book text" && (string)window.FullScreenButton.ToolTip == "Toggle full screen", "Automation or tooltip text stayed Chinese.");
                window.AppearancePopup.IsOpen = false;
                double width = window.Width; window.Width = window.MinWidth; await Idle(window);
                foreach (TabItem tab in Visuals<TabItem>(window.SidebarTabs))
                {
                    Point at = tab.TranslatePoint(new Point(), window.Sidebar);
                    Require(at.X >= -1 && at.X + tab.ActualWidth <= window.Sidebar.ActualWidth + 1, "English tab header is clipped.");
                }
                foreach (Button button in new[] { window.HomeButton, window.LibraryButton, window.FullScreenButton })
                {
                    Point at = button.TranslatePoint(new Point(), window);
                    Require(at.X >= 0 && at.X + button.ActualWidth <= window.ActualWidth, "English toolbar is clipped.");
                }
                window.Width = width; await Idle(window); Capture(window, screenshot);
            });
            await window.OpenBookAsync(file); await Idle(window);
            await Check("Language changes preserve book text, reading location, footnotes and note drafts", async () =>
            {
                window.BookAppearance.IsChecked = true; window.FontSizeSlider.Value = 30;
                await Task.Delay(180); await Idle(window);
                Paragraph p = window.Reader.Document.Blocks.OfType<Paragraph>().First();
                window.Reader.Selection.Select(DocumentPosition.Pointer(p, 0)!, DocumentPosition.Pointer(p, 4)!);
                await window.AddAnnotationAsync("用户笔记 user note");
                Require(window.AnnotationsList.Items.Count == 1, "Annotation setup failed.");
                window.AnnotationNoteBox.Text = "编辑中 draft";
                await window.FollowLinkAsync(new LocalLink("c0.xhtml", "note", true));
                Require(window.FootnoteOverlay.Visibility == Visibility.Visible, "Footnote setup failed.");
                FlowDocument bookDocument = window.Reader.Document, noteDocument = window.FootnoteReader.Document;
                string original = Text(window), footnote = new TextRange(noteDocument.ContentStart, noteDocument.ContentEnd).Text;
                ReadingLocator? position = window.CaptureReadingLocator();
                window.LanguageCombo.SelectedIndex = 0; await Idle(window);
                Require(ReferenceEquals(bookDocument, window.Reader.Document) && Text(window) == original, "Book text or document changed during translation.");
                Require(ReferenceEquals(noteDocument, window.FootnoteReader.Document) && new TextRange(noteDocument.ContentStart, noteDocument.ContentEnd).Text == footnote, "Footnote content was translated or replaced.");
                Require(window.AnnotationNoteBox.Text == "编辑中 draft" && window.FontSizeSlider.Value == 30, "Language change reset note draft or book appearance.");
                Require(window.CaptureReadingLocator() == position, "Language change moved the reading position.");
                Hyperlink link = bookDocument.Blocks.OfType<Paragraph>().SelectMany(b => b.Inlines).OfType<Hyperlink>().Single();
                Require(AutomationProperties.GetName(link).StartsWith("查看注释：", StringComparison.Ordinal), "Existing link automation name did not switch.");
                window.LanguageCombo.SelectedIndex = 1; await Idle(window);
                Require(AutomationProperties.GetName(link).StartsWith("Read footnote:", StringComparison.Ordinal), "Existing link automation did not return to English.");
                window.CloseFootnote();
                await Idle(window); Capture(window, screenshot);
            });
            await Check("Search results and validation errors follow language changes without modifying data", async () =>
            {
                window.SearchBox.Text = "English"; await window.SearchAsync();
                Require(window.SearchStatusText.Text.StartsWith("Found ", StringComparison.Ordinal), "Search completion stayed Chinese.");
                int hits = window.SearchList.Items.Count;
                window.LanguageCombo.SelectedIndex = 0; await Idle(window);
                Require(window.SearchStatusText.Text.StartsWith("找到", StringComparison.Ordinal) && window.SearchList.Items.Count == hits, "Switching language lost or failed to translate search results.");
                window.LanguageCombo.SelectedIndex = 1;
                Paragraph p = window.Reader.Document.Blocks.OfType<Paragraph>().First();
                window.Reader.Selection.Select(DocumentPosition.Pointer(p, 0)!, DocumentPosition.Pointer(p, 4)!);
                await window.AddAnnotationAsync(new string('x', 2001));
                Require(window.StatusText.Text == "Invalid annotation range, color or text.", "Core validation error did not translate.");
                window.LanguageCombo.SelectedIndex = 0; await Idle(window);
                Require(window.StatusText.Text.Contains("不符合限制", StringComparison.Ordinal) && window.AnnotationsList.Items.Count == 1, "Error did not switch back or invalid data was committed.");
            });
            await Check("Library menus and status translate while edited metadata stays intact", async () =>
            {
                window.ChangeInterfaceLanguage("en");
                var library = new LibraryWindow(window.Library) { Owner = window }; library.Show(); await Idle(window);
                try
                {
                    library.BooksList.SelectedIndex = 0;
                    library.TitleBox.Text = "未保存书名 draft";
                    Require(library.Title == "QuietRead · Library and reading data" && library.LibraryStatus.Text.StartsWith("Showing ", StringComparison.Ordinal), "Library window did not open in English.");
                    Require(Visuals<TextBlock>(library.BooksList).Any(t => t.Text == "Reading"), "Reading status did not translate.");
                    window.ChangeInterfaceLanguage("zh-CN"); await Idle(window);
                    Require(library.Title == "QuietRead · 书库与阅读数据" && library.TitleBox.Text == "未保存书名 draft", "Language switch overwrote edited metadata.");
                    Require(Visuals<TextBlock>(library.BooksList).Any(t => t.Text == "在读"), "Live library status did not switch back.");
                    Require((string)((ComboBoxItem)library.ReadingState.SelectedItem).Tag == "Reading", "Translation changed the stored status identifier.");
                }
                finally { library.Close(); }
            });
            await Check("Interface language saves on immediate close and remains separate from book appearance", async () =>
            {
                string id = window.Library.Entries().Single().BookId;
                window.LanguageCombo.SelectedIndex = 1; window.Close();
                Require(new StateStore(directory).Load().InterfaceLanguage == "en", "Close lost the language choice.");
                var reopened = new MainWindow(new StateStore(directory)); reopened.Show(); await Idle(reopened);
                try
                {
                    Require(reopened.LanguageCombo.SelectedIndex == 1 && (string)reopened.LibraryButton.Content == "Library", "Restart did not restore English.");
                    await reopened.OpenBookAsync(file);
                    Require(reopened.FontSizeSlider.Value == 30 && reopened.Library.LoadBook(id).Annotations.Single().Note == "用户笔记 user note", "Language persistence altered book appearance or note data.");
                    reopened.LanguageCombo.SelectedIndex = 0; reopened.Close();
                    Require(new StateStore(directory).Load().InterfaceLanguage == "zh-CN", "Immediate switch back did not persist.");
                }
                finally { reopened.Close(); }
            });
        }
        finally { window.Close(); UiText.Current.Language = "zh-CN"; }
    }
}
