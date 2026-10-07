using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using QuietRead.Core;

namespace QuietRead;

public partial class MainWindow : Window
{
    private readonly StateStore _store;
    private readonly AppState _state;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _appearanceTimer;
    private CancellationTokenSource? _openToken, _readToken, _searchToken;
    private EpubBook? _book;
    private BookHistory? _history;
    private ParsedChapter? _chapter;
    private int[] _segments = [0];
    private Dictionary<int, Block> _renderedBlocks = [];
    private ScrollViewer? _scroll;
    private int _chapterIndex, _segmentIndex, _openGeneration, _readGeneration, _busyGeneration;
    private long _saveRevision;
    private bool _ready, _restoring, _selectingToc, _closed, _busy, _fullScreen;
    private string _highlight = "";
    private WindowStyle _savedStyle;
    private ResizeMode _savedResize;
    private WindowState _savedWindowState;

    public MainWindow() : this(new StateStore()) { }

    internal MainWindow(StateStore store)
    {
        _store = store;
        _state = _store.Load();
        InitializeComponent();
        Rect workArea = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, workArea.Width - 32));
        Height = Math.Max(MinHeight, Math.Min(Height, workArea.Height - 32));
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += SaveTimer_Tick;
        _appearanceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _appearanceTimer.Tick += (_, _) => { _appearanceTimer.Stop(); ApplyTypography(); ScheduleSave(); };
        ReaderPreferences preferences = _state.Preferences;
        FontSizeSlider.Value = preferences.FontSize; LineSlider.Value = preferences.LineSpacing; WidthSlider.Value = preferences.TextWidth;
        FontCombo.SelectedIndex = preferences.Font switch { "Sans" => 1, "Georgia" => 2, _ => 0 };
        SetSidebar(preferences.SidebarVisible);
        ApplyTheme(preferences.Theme);
        RefreshRecent();
        _ready = true;
        ApplyTypography();
        if (_store.LoadFailed) StatusText.Text = "原阅读记录无法读取，已使用默认设置。";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyTitleBar();
        if (((App)Application.Current).LaunchPath is string path) await OpenBookAsync(path);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "打开 EPUB 书籍", Filter = "EPUB 电子书 (*.epub)|*.epub",
            CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) == true) await OpenBookAsync(picker.FileName);
    }

    internal async Task OpenBookAsync(string path)
    {
        try { path = LocalFile(path); }
        catch (Exception exception) when (exception is EpubException or ArgumentException or IOException or UnauthorizedAccessException)
        { ShowError(exception.Message); return; }
        Cancel(ref _openToken); Cancel(ref _readToken); Cancel(ref _searchToken);
        _openToken = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken token = _openToken.Token;
        int generation = ++_openGeneration;
        int busy = BeginBusy("正在打开书籍…");
        EpubBook? opened = null;
        try
        {
            opened = await Task.Run(() => EpubBook.Open(path, token), token);
            if (_closed || token.IsCancellationRequested || generation != _openGeneration) return;
            CapturePosition();
            EpubBook? previous = _book;
            _book = opened; opened = null;
            if (previous != null) _ = Task.Run(previous.Dispose);
            _history = _state.Books.FirstOrDefault(x => x.BookKey == _book.BookKey);
            if (_history == null)
            {
                _history = new BookHistory { BookKey = _book.BookKey, FilePath = path, Title = _book.Title, Author = _book.Author };
                _state.Books.Insert(0, _history);
            }
            _history.LastReadUtc = DateTime.UtcNow;
            _history.Title = _book.Title; _history.Author = _book.Author;
            _chapter = null; _scroll = null; _highlight = "";
            SearchList.ItemsSource = null; SearchStatusText.Text = "输入文字，按 Enter 搜索全书。";
            BookTitleText.Text = _book.Title; BookAuthorText.Text = _book.Author;
            Title = _book.Title + " · QuietRead";
            ChapterCountText.Text = $"{_book.Chapters.Count} 个章节";
            _selectingToc = true;
            TocList.ItemsSource = _book.TableOfContents.Select(x => new TocRow(x)).ToArray();
            _selectingToc = false;
            RefreshBookmarks();
            WelcomePane.Visibility = Visibility.Collapsed; ReaderPane.Visibility = Visibility.Visible;
            SidebarButton.IsEnabled = true;
            await NavigateAsync(Math.Clamp(_history.ChapterIndex, 0, _book.Chapters.Count - 1), _history.BlockIndex, _history.Fraction, saveCurrent: false);
            ScheduleSave();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is EpubException or IOException or UnauthorizedAccessException)
        { if (!_closed && !token.IsCancellationRequested) { ShowError(exception.Message); StatusText.Text = "无法打开这本书。"; } }
        finally { if (opened != null) _ = Task.Run(opened.Dispose); EndBusy(busy); }
    }

    private static string LocalFile(string path)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(path), ".epub", StringComparison.OrdinalIgnoreCase))
            throw new EpubException("请选择 .epub 文件。");
        string root = Path.GetPathRoot(path) ?? "";
        if (root.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType == DriveType.Network)
            throw new EpubException("请先把书籍复制到本地磁盘，再用 QuietRead 打开。");
        if (!File.Exists(path)) throw new EpubException("找不到这本书，请重新选择文件。");
        return path;
    }

    internal async Task NavigateAsync(int index, int blockIndex = 0, double fraction = 0, string? fragment = null, bool saveCurrent = true)
    {
        EpubBook? book = _book;
        if (book == null || index < 0 || index >= book.Chapters.Count || _closed) return;
        if (saveCurrent) CapturePosition();
        Cancel(ref _readToken);
        _readToken = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken token = _readToken.Token;
        int generation = ++_readGeneration;
        int busy = BeginBusy("正在载入章节…");
        try
        {
            var slice = await Task.Run(() =>
            {
                ParsedChapter chapter = book.ReadChapter(index, token);
                int[] segments = DocumentRenderer.BuildSegments(chapter);
                int target = Math.Clamp(blockIndex, 0, chapter.Blocks.Count - 1);
                if (!string.IsNullOrEmpty(fragment) && chapter.Anchors.TryGetValue(fragment, out int anchor)) target = anchor;
                int part = Array.BinarySearch(segments, target);
                if (part < 0) part = ~part - 1;
                int start = segments[part], end = part + 1 < segments.Length ? segments[part + 1] : chapter.Blocks.Count;
                PreparedImages images = DocumentRenderer.PrepareImages(book, chapter, start, end, token);
                return (chapter, segments, part, start, end, target, images);
            }, token);
            if (_closed || token.IsCancellationRequested || generation != _readGeneration || book != _book) return;
            _restoring = true;
            _chapter = slice.chapter; _chapterIndex = index; _segments = slice.segments; _segmentIndex = slice.part;
            Reader.Document = DocumentRenderer.Create(_chapter, slice.start, slice.end, slice.images,
                _state.Preferences, _highlight, FollowLink, out _renderedBlocks);
            ApplyTypography();
            ChapterTitleText.Text = book.Chapters[index].Title;
            SelectCurrentToc();
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closed || generation != _readGeneration || book != _book) return;
                Reader.UpdateLayout();
                _scroll = FindVisual<ScrollViewer>(Reader);
                if ((fragment != null || slice.target != slice.start) && _renderedBlocks.TryGetValue(slice.target, out Block? target))
                    target.BringIntoView();
                else _scroll?.ScrollToVerticalOffset(Math.Clamp(fraction, 0, 1) * (_scroll?.ScrollableHeight ?? 0));
                _restoring = false;
                UpdateProgress(); CapturePosition();
                Reader.Focus();
            }, DispatcherPriority.Loaded, token);
            StatusText.Text = slice.images.Skipped > 0 ? $"本段有 {slice.images.Skipped} 张图片未显示。" :
                book.ExternalResourcesSkipped > 0 ? "已跳过书内外部资源。" : "阅读位置自动保存";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is EpubException or IOException or UnauthorizedAccessException)
        { if (!_closed && !token.IsCancellationRequested) { ShowError(exception.Message); StatusText.Text = "此章节暂时无法显示，可从目录选择其他章节。"; } }
        finally { if (generation == _readGeneration) _restoring = false; EndBusy(busy); }
    }

    private void SelectCurrentToc()
    {
        if (TocList.ItemsSource is not TocRow[] rows) return;
        TocRow? row = rows.FirstOrDefault(x => x.Item.ChapterIndex == _chapterIndex);
        _selectingToc = true;
        TocList.SelectedItem = row;
        if (row != null) TocList.ScrollIntoView(row);
        _selectingToc = false;
    }

    private async void Toc_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectingToc || !_ready || _busy) return;
        if (TocList.SelectedItem is TocRow row) await NavigateAsync(row.Item.ChapterIndex, fragment: row.Item.Fragment);
    }

    private async void FollowLink(LocalLink target)
    {
        if (_book == null || _busy) return;
        int chapter = _book.FindChapter(target.Path);
        if (chapter >= 0) await NavigateAsync(chapter, fragment: target.Fragment);
    }

    private void Reader_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_ready || _restoring || _chapter == null) return;
        _scroll ??= FindVisual<ScrollViewer>(Reader);
        UpdateProgress();
        if (Math.Abs(e.VerticalChange) > 0.1) { CapturePosition(); ScheduleSave(); }
    }

    private double ScrollFraction => _scroll == null || _scroll.ScrollableHeight <= 0 ? 0 :
        Math.Clamp(_scroll.VerticalOffset / _scroll.ScrollableHeight, 0, 1);

    private void CapturePosition()
    {
        if (_history == null || _chapter == null || _restoring || _closed) return;
        _history.ChapterIndex = _chapterIndex;
        _history.BlockIndex = _segments[_segmentIndex];
        _history.Fraction = ScrollFraction;
        _history.LastReadUtc = DateTime.UtcNow;
    }

    private void UpdateProgress()
    {
        if (_book == null || _chapter == null) { ProgressText.Text = "—"; return; }
        int end = _segmentIndex + 1 < _segments.Length ? _segments[_segmentIndex + 1] : _chapter.Blocks.Count;
        double viewed = _scroll != null && _scroll.ScrollableHeight < 1 ? 1 : ScrollFraction;
        double chapterPart = (_segments[_segmentIndex] + (end - _segments[_segmentIndex]) * viewed) / _chapter.Blocks.Count;
        double percent = 100 * (_chapterIndex + chapterPart) / _book.Chapters.Count;
        ProgressText.Text = $"{_chapterIndex + 1} / {_book.Chapters.Count}  ·  ≈{percent:0}%";
        PartText.Text = _segments.Length > 1 ? $"第 {_segmentIndex + 1}/{_segments.Length} 部分" : "Space 翻页";
        UpdateButtons();
    }

    private async Task StepReadingAsync(int direction)
    {
        if (_book == null || _chapter == null || _busy) return;
        _scroll ??= FindVisual<ScrollViewer>(Reader);
        if (_scroll != null)
        {
            double offset = _scroll.VerticalOffset, maximum = _scroll.ScrollableHeight;
            if (direction > 0 && offset < maximum - 2 || direction < 0 && offset > 2)
            {
                _scroll.ScrollToVerticalOffset(Math.Clamp(offset + direction * Math.Max(100, _scroll.ViewportHeight * 0.85), 0, maximum));
                return;
            }
        }
        int part = _segmentIndex + direction;
        if (part >= 0 && part < _segments.Length)
            await NavigateAsync(_chapterIndex, _segments[part], direction > 0 ? 0 : 1);
        else if (_chapterIndex + direction >= 0 && _chapterIndex + direction < _book.Chapters.Count)
            await NavigateAsync(_chapterIndex + direction, direction > 0 ? 0 : int.MaxValue, direction > 0 ? 0 : 1);
        else StatusText.Text = direction > 0 ? "已到书末。" : "已到书首。";
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) => await StepReadingAsync(-1);
    private async void Next_Click(object sender, RoutedEventArgs e) => await StepReadingAsync(1);

    private void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_history == null || _book == null || _chapter == null || _busy) return;
        CapturePosition();
        if (_history.Bookmarks.Count >= 64) { StatusText.Text = "每本书最多保存 64 个书签，请先删除部分书签。"; return; }
        if (_history.Bookmarks.Any(x => x.ChapterIndex == _chapterIndex && x.BlockIndex == _history.BlockIndex && Math.Abs(x.Fraction - ScrollFraction) < 0.03))
        { StatusText.Text = "这个位置已有书签。"; return; }
        _history.Bookmarks.Add(new Bookmark { ChapterIndex = _chapterIndex, BlockIndex = _history.BlockIndex,
            Fraction = ScrollFraction, Label = _book.Chapters[_chapterIndex].Title + (_segments.Length > 1 ? $" · 第 {_segmentIndex + 1} 部分" : "") });
        RefreshBookmarks(); ScheduleSave(); StatusText.Text = "已添加书签。";
    }

    private void RefreshBookmarks()
    {
        BookmarksList.ItemsSource = _history?.Bookmarks.OrderBy(x => x.ChapterIndex).ThenBy(x => x.BlockIndex).ThenBy(x => x.Fraction).Select(x => new BookmarkRow(x)).ToArray();
        BookmarksHint.Text = _history?.Bookmarks.Count > 0 ? "点击书签跳转；× 删除。" : "阅读时点击「＋书签」保存位置。";
    }

    private async void Bookmarks_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _busy) return;
        if (BookmarksList.SelectedItem is BookmarkRow row)
        { await NavigateAsync(row.Item.ChapterIndex, row.Item.BlockIndex, row.Item.Fraction); BookmarksList.SelectedItem = null; }
    }

    private void DeleteBookmark_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BookmarkRow row && _history != null)
        { _history.Bookmarks.Remove(row.Item); RefreshBookmarks(); ScheduleSave(); e.Handled = true; }
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();
    private async void Search_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); } }

    internal async Task SearchAsync()
    {
        EpubBook? book = _book;
        string query = SearchBox.Text.Trim();
        if (book == null || query.Length == 0 || _busy) return;
        Cancel(ref _searchToken);
        _searchToken = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken token = _searchToken.Token;
        SearchStatusText.Text = "正在搜索全书…"; SearchList.ItemsSource = null;
        try
        {
            SearchOutcome outcome = await Task.Run(() => book.Search(query, token), token);
            if (_closed || token.IsCancellationRequested || book != _book) return;
            _highlight = query;
            SearchList.ItemsSource = outcome.Hits;
            SearchStatusText.Text = outcome.LimitReached ? "显示前 200 个匹配段落，可缩小关键词范围。" : $"找到 {outcome.Hits.Count} 个匹配段落。";
            if (outcome.FailedChapters > 0) SearchStatusText.Text += $" {outcome.FailedChapters} 个章节未能解析。";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is EpubException or IOException or ArgumentException)
        { if (!_closed && !token.IsCancellationRequested) SearchStatusText.Text = "搜索无法完成。"; }
    }

    private async void Search_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _busy) return;
        if (SearchList.SelectedItem is SearchHit hit)
        { await NavigateAsync(hit.ChapterIndex, hit.BlockIndex); SearchList.SelectedItem = null; }
    }

    private void Sidebar_Click(object sender, RoutedEventArgs e)
    { SetSidebar(!_state.Preferences.SidebarVisible); ScheduleSave(); }

    private void SetSidebar(bool visible)
    {
        _state.Preferences.SidebarVisible = visible;
        Sidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(visible ? 268 : 0);
    }

    private void Appearance_Click(object sender, RoutedEventArgs e) => AppearancePopup.IsOpen = !AppearancePopup.IsOpen;

    private void Appearance_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _state.Preferences.FontSize = FontSizeSlider.Value;
        _state.Preferences.LineSpacing = LineSlider.Value;
        _state.Preferences.TextWidth = WidthSlider.Value;
        FontSizeLabel.Text = FontSizeSlider.Value.ToString("0", CultureInfo.InvariantCulture);
        _appearanceTimer.Stop(); _appearanceTimer.Start();
    }

    private void Font_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || FontCombo.SelectedItem is not ComboBoxItem item) return;
        _state.Preferences.Font = (string)item.Tag;
        ApplyTypography(); ScheduleSave();
    }

    private void ApplyTypography()
    {
        ReaderPreferences p = _state.Preferences;
        FontSizeLabel.Text = p.FontSize.ToString("0", CultureInfo.InvariantCulture);
        Reader.MaxWidth = p.TextWidth + 64;
        if (Reader.Document == null || _chapter == null) return;
        double fraction = ScrollFraction;
        Reader.Document.FontFamily = DocumentRenderer.Font(p.Font);
        Reader.Document.FontSize = p.FontSize;
        Reader.Document.LineHeight = p.FontSize * p.LineSpacing;
        foreach ((int index, Block block) in _renderedBlocks)
        {
            if (block is Paragraph paragraph)
            {
                DocumentRenderer.ApplyParagraph(paragraph, _chapter.Blocks[index], p);
                foreach (Span span in paragraph.Inlines.OfType<Span>())
                    if (span.FontFamily.Source.StartsWith("Consolas", StringComparison.Ordinal)) span.FontSize = p.FontSize * 0.88;
            }
            else if (block is BlockUIContainer container && container.Child is Image image) image.MaxWidth = p.TextWidth - 64;
        }
        if (!_restoring)
        {
            _restoring = true;
            Reader.UpdateLayout();
            _scroll?.ScrollToVerticalOffset(fraction * (_scroll?.ScrollableHeight ?? 0));
            _restoring = false;
            UpdateProgress(); CapturePosition();
        }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    { if ((sender as Button)?.Tag is string theme) { _state.Preferences.Theme = theme; ApplyTheme(theme); ScheduleSave(); } }

    private void ApplyTheme(string theme)
    {
        string[] names = ["AppBrush", "PanelBrush", "ReaderBrush", "TextBrush", "SecondaryBrush", "BorderBrush", "AccentBrush", "AccentSoftBrush", "HighlightBrush", "CodeBrush", "PrimaryTextBrush"];
        string[] colors = theme switch
        {
            "Dark" => ["#181E1A", "#202823", "#1C231F", "#DBE5DA", "#98AA9A", "#344136", "#8FCDA8", "#2C4233", "#5C542B", "#29362C", "#183625"],
            "Sepia" => ["#EEE8DA", "#F7F1E5", "#F5EFDF", "#403A2E", "#817763", "#DFD5C1", "#726337", "#EAE1C8", "#EBD796", "#EDE4D1", "#FFFFFF"],
            _ => ["#F4F6F3", "#FFFFFF", "#FDFCF9", "#25352D", "#68766D", "#E0E6DF", "#28664B", "#E8F0E8", "#F8E69F", "#EDF1EC", "#FFFFFF"]
        };
        for (int i = 0; i < names.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            brush.Freeze(); Application.Current.Resources[names[i]] = brush;
        }
        ApplyTitleBar();
    }

    [DllImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    private void ApplyTitleBar()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = _state.Preferences.Theme == "Dark" ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool editing = Keyboard.FocusedElement is TextBox or ComboBox;
        if (control)
        {
            switch (e.Key)
            {
                case Key.O: e.Handled = true; Open_Click(this, new RoutedEventArgs()); return;
                case Key.B: e.Handled = true; Sidebar_Click(this, new RoutedEventArgs()); return;
                case Key.D: e.Handled = true; Bookmark_Click(this, new RoutedEventArgs()); return;
                case Key.F: e.Handled = true; SetSidebar(true); SidebarTabs.SelectedIndex = 2; SearchBox.Focus(); return;
                case Key.W: e.Handled = true; Home_Click(this, new RoutedEventArgs()); return;
                case Key.Right when !editing: e.Handled = true; await NavigateAsync(_chapterIndex + 1); return;
                case Key.Left when !editing: e.Handled = true; await NavigateAsync(_chapterIndex - 1); return;
                case Key.Home when !editing: e.Handled = true; await NavigateAsync(0); return;
                case Key.End when !editing: e.Handled = true; if (_book != null) await NavigateAsync(_book.Chapters.Count - 1, int.MaxValue, 1); return;
                case Key.OemPlus: case Key.Add: e.Handled = true; FontSizeSlider.Value = Math.Min(36, FontSizeSlider.Value + 1); return;
                case Key.OemMinus: case Key.Subtract: e.Handled = true; FontSizeSlider.Value = Math.Max(14, FontSizeSlider.Value - 1); return;
            }
        }
        if (e.Key == Key.F11) { e.Handled = true; ToggleFullScreen(); return; }
        if (e.Key == Key.Escape)
        {
            if (AppearancePopup.IsOpen) { AppearancePopup.IsOpen = false; e.Handled = true; }
            else if (_fullScreen) { ToggleFullScreen(); e.Handled = true; }
            return;
        }
        if (editing || AppearancePopup.IsOpen || control || (shift && e.Key is Key.Left or Key.Right)) return;
        if (e.Key == Key.Space && Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase) return;
        if (e.Key is Key.Space or Key.PageDown or Key.Right)
        { e.Handled = true; await StepReadingAsync(e.Key == Key.Space && shift ? -1 : 1); }
        else if (e.Key is Key.PageUp or Key.Left) { e.Handled = true; await StepReadingAsync(-1); }
    }

    private void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _savedStyle = WindowStyle; _savedResize = ResizeMode; _savedWindowState = WindowState;
            WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized;
        }
        else { WindowState = WindowState.Normal; WindowStyle = _savedStyle; ResizeMode = _savedResize; WindowState = _savedWindowState; }
        _fullScreen = !_fullScreen;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths &&
            paths.Length == 1 && string.Equals(Path.GetExtension(paths[0]), ".epub", StringComparison.OrdinalIgnoreCase) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length == 1) await OpenBookAsync(paths[0]);
    }

    private async void Recent_Click(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.DataContext is BookHistory history) await OpenBookAsync(history.FilePath); }

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        CapturePosition(); Cancel(ref _openToken); Cancel(ref _readToken); Cancel(ref _searchToken);
        _openGeneration++; _readGeneration++; _busyGeneration++;
        EpubBook? book = _book;
        _book = null; _history = null; _chapter = null; _scroll = null; _renderedBlocks.Clear();
        Reader.Document = null; TocList.ItemsSource = null; SearchList.ItemsSource = null; RefreshBookmarks();
        ReaderPane.Visibility = Visibility.Collapsed; WelcomePane.Visibility = Visibility.Visible;
        BookTitleText.Text = "QuietRead"; BookAuthorText.Text = "留一点时间，给阅读。"; Title = "QuietRead · 静读";
        ChapterCountText.Text = "书籍导航"; PartText.Text = ""; ProgressText.Text = "—";
        _busy = false; BusyBar.Visibility = Visibility.Hidden; Reader.IsEnabled = true; Cursor = null;
        RefreshRecent(); UpdateButtons(); ScheduleSave(); StatusText.Text = "就绪";
        if (book != null) _ = Task.Run(book.Dispose);
    }

    private void RefreshRecent()
    {
        RecentList.ItemsSource = _state.Books.OrderByDescending(x => x.LastReadUtc).Take(6).ToArray();
        RecentHint.Visibility = _state.Books.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.Visibility = _state.Books.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "清除所有阅读位置、最近记录和书签？\nEPUB 文件会保留。", "清除阅读记录", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _state.Books.Clear(); RefreshRecent(); _saveTimer.Stop();
        try
        {
            byte[] bytes = StateStore.Snapshot(_state); long revision = ++_saveRevision;
            await Task.Run(() => _store.Save(bytes, revision));
            if (!_closed && _state.Books.Count == 0) StatusText.Text = "阅读记录已清除。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { if (!_closed) StatusText.Text = "无法清除已保存的阅读记录，请检查文件夹写入权限。"; }
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        AppearancePopup.IsOpen = false;
        MessageBox.Show(this, "QuietRead · 静读 0.1.0\n\n" +
            "Ctrl+O  打开 EPUB\nCtrl+B  显示/隐藏目录\nCtrl+F  全书搜索\nCtrl+D  添加书签\nCtrl+W  返回最近阅读\n" +
            "Space / → / Page Down  下一页\nShift+Space / ← / Page Up  上一页\nCtrl+← / →  上一章 / 下一章\nCtrl+Home / End  书首 / 书末\n" +
            "Ctrl+＋ / －  调整字号\nF11  全屏；Esc  退出全屏\n\n" +
            "打开本地可重排 EPUB 2/3。支持基本文字排版、表格行和 JPEG/PNG/GIF/BMP 图片。长章节分段显示，翻页可继续下一段。\n\n" +
            "书内脚本、外部资源、嵌入字体不执行/不加载。原书 CSS、复杂 SVG、音视频、固定版式和 DRM 暂不支持。\n" +
            "阅读记录仅保存在本机。进度百分比和恢复位置为近似值，文件移动或修改后需重新建立记录。",
            "使用说明", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private int BeginBusy(string message)
    {
        int id = ++_busyGeneration; _busy = true; BusyBar.Visibility = Visibility.Visible;
        Reader.IsEnabled = false; Cursor = Cursors.Wait; StatusText.Text = message; UpdateButtons();
        return id;
    }

    private void EndBusy(int id)
    {
        if (_closed || id != _busyGeneration) return;
        _busy = false; BusyBar.Visibility = Visibility.Hidden; Reader.IsEnabled = true; Cursor = null; UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool active = _book != null && _chapter != null && !_busy;
        BookmarkButton.IsEnabled = active; SearchButton.IsEnabled = _book != null && !_busy;
        PreviousButton.IsEnabled = active && (_chapterIndex > 0 || _segmentIndex > 0 || (_scroll?.VerticalOffset ?? 0) > 2);
        NextButton.IsEnabled = active && (_chapterIndex + 1 < _book!.Chapters.Count || _segmentIndex + 1 < _segments.Length ||
            (_scroll != null && _scroll.VerticalOffset < _scroll.ScrollableHeight - 2));
    }

    private void ScheduleSave()
    { if (!_ready || _closed) return; _saveTimer.Stop(); _saveTimer.Start(); }

    private async void SaveTimer_Tick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        if (_closed) return;
        CapturePosition();
        try { byte[] bytes = StateStore.Snapshot(_state); long revision = ++_saveRevision; await Task.Run(() => _store.Save(bytes, revision)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { if (!_closed) StatusText.Text = "阅读位置无法保存，请检查当前用户文件夹的写入权限。"; }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        CapturePosition(); _closed = true; _saveTimer.Stop(); _appearanceTimer.Stop();
        _lifetime.Cancel(); Cancel(ref _openToken); Cancel(ref _readToken); Cancel(ref _searchToken);
        try { _store.Save(StateStore.Snapshot(_state), ++_saveRevision); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        EpubBook? book = _book; _book = null;
        if (book != null) _ = Task.Run(book.Dispose);
    }

    private void ShowError(string message)
    { if (!_closed) MessageBox.Show(this, message, "无法读取书籍", MessageBoxButton.OK, MessageBoxImage.Information); }

    private static void Cancel(ref CancellationTokenSource? source)
    { source?.Cancel(); source?.Dispose(); source = null; }

    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            T? result = FindVisual<T>(child); if (result != null) return result;
        }
        return null;
    }

    private sealed class TocRow(TocItem item)
    {
        public TocItem Item { get; } = item;
        public string Title => Item.Title;
        public Thickness Indent => new(Math.Min(Item.Depth, 6) * 12, 0, 0, 0);
    }

    private sealed class BookmarkRow(Bookmark item)
    {
        public Bookmark Item { get; } = item;
        public string Label => Item.Label;
        public string DateLabel => Item.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
