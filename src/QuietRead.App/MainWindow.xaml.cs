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
using System.Text.Json;
using Microsoft.Win32;
using QuietRead.Core;

namespace QuietRead;

public partial class MainWindow : Window
{
    private readonly StateStore _store;
    private readonly AppState _state;
    private readonly LibraryStore _library;
    private readonly Task _libraryReady;
    private BookData? _bookData;
    private long _libraryVersion;
    private ReaderPreferences Preferences => _bookData?.Appearance ?? _state.Preferences;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _readingWork = new(1, 1);
    private readonly SemaphoreSlim _searchWork = new(1, 1);
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
    private readonly bool _ready;
    private bool _restoring, _selectingToc, _closed, _busy, _fullScreen;
    private string _highlight = "";
    private WindowStyle _savedStyle;
    private ResizeMode _savedResize;
    private WindowState _savedWindowState;

    public MainWindow() : this(new StateStore()) { }

    internal MainWindow(StateStore store)
    {
        _store = store;
        _state = _store.Load();
        _library = new LibraryStore(store.DirectoryPath);
        byte[] legacyState = StateStore.Snapshot(_state);
        _libraryReady = Task.Run(() => _library.MigrateLegacy(JsonSerializer.Deserialize<AppState>(legacyState)!.Books));
        InitializeComponent();
        Rect workArea = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, workArea.Width - 32));
        Height = Math.Max(MinHeight, Math.Min(Height, workArea.Height - 32));
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += SaveTimer_Tick;
        _appearanceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _appearanceTimer.Tick += (_, _) => { _appearanceTimer.Stop(); ApplyTypography(); ScheduleSave(); };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            if (_closed || _restoring) return;
            _restoring = true; Reader.UpdateLayout(); RestoreTextPosition(_layoutLocator);
            Reader.UpdateLayout();
            _layoutLocator = null; _restoring = false; CapturePosition(); ScheduleSave();
        };
        SystemParameters.StaticPropertyChanged += SystemSettingsChanged;
        ReaderPreferences preferences = _state.Preferences;
        FontSizeSlider.Value = preferences.FontSize; LineSlider.Value = preferences.LineSpacing; WidthSlider.Value = preferences.TextWidth;
        FontCombo.SelectedIndex = preferences.Font switch { "Sans" => 1, "Georgia" => 2, _ => 0 };
        SetSidebar(preferences.SidebarVisible);
        ApplyTheme(preferences.Theme);
        RefreshRecent();
        _ready = true;
        LoadAppearanceControls(); SystemEvents.PowerModeChanged += PowerModeChanged;
        ApplyTypography();
        if (_store.LoadFailed) StatusText.Text = "原阅读记录无法读取，已使用默认设置。";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyTitleBar();
        try { await _libraryReady; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { SaveStatusText.Text = "书库无法读取，现有数据已保留。"; }
        if (((App)Application.Current).LaunchPath is string path) await OpenBookAsync(path);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "打开 EPUB 书籍",
            Filter = "EPUB 电子书 (*.epub)|*.epub",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) == true) await OpenBookAsync(picker.FileName);
    }

    internal async Task OpenBookAsync(string path, string? associate = null)
    {
        if (_closed) return;
        if (_bookData != null && !await SaveReadingDataAsync()) return;
        try { path = LocalFile(path); }
        catch (Exception exception) when (exception is EpubException or ArgumentException or IOException or UnauthorizedAccessException)
        { ShowError(exception.Message); return; }
        Cancel(ref _openToken); Cancel(ref _readToken); Cancel(ref _searchToken);
        CloseFootnote(); _resizeTimer.Stop(); _layoutLocator = null; _searchGeneration++; _searching = false;
        _openToken = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken token = _openToken.Token;
        int generation = ++_openGeneration;
        int busy = BeginBusy("正在打开书籍…");
        EpubBook? opened = null;
        try
        {
            await _libraryReady;
            token.ThrowIfCancellationRequested();
            opened = await RunBoundedWorkAsync(_readingWork, () => EpubBook.Open(path, token), token);
            if (_closed || token.IsCancellationRequested || generation != _openGeneration) return;
            EpubBook candidate = opened;
            BookData data = await RunBoundedWorkAsync(_readingWork, () => _library.OpenBook(candidate.BookKey,
                candidate.LegacyBookKey, path, candidate.Title, candidate.Author, associate), token);
            if (_closed || token.IsCancellationRequested || generation != _openGeneration) return;
            CapturePosition();
            EpubBook? previous = _book;
            _book = opened; opened = null;
            if (previous != null) _ = Task.Run(previous.Dispose);
            _bookData = data; _history = data.History;
            _libraryVersion = _library.ChangeVersion;
            _state.Books.RemoveAll(x => x.BookKey.Equals(_book.BookKey, StringComparison.OrdinalIgnoreCase) ||
                x.BookKey.Equals(_book.LegacyBookKey, StringComparison.OrdinalIgnoreCase));
            _state.Books.Insert(0, _history);
            _history.LastReadUtc = DateTime.UtcNow;
            _history.Title = _book.Title; _history.Author = _book.Author;
            _chapter = null; _scroll = null; _highlight = "";
            _back.Clear(); _forward.Clear(); _searchHits = Array.Empty<SearchHit>(); _searchIndex = -1; _searchOrigin = null;
            SearchProgressBar.Value = 0; SearchPositionText.Text = "—";
            Reader.Document = null; _renderedBlocks.Clear(); _segments = [0]; _segmentIndex = 0;
            PartText.Text = ""; ProgressText.Text = "—";
            SearchList.ItemsSource = null; SearchStatusText.Text = "输入文字，点击「查找」搜索全书。";
            BookTitleText.Text = _book.Title; BookAuthorText.Text = _book.Author;
            Title = _book.Title + " · QuietRead";
            ChapterCountText.Text = $"{_book.Chapters.Count} 个章节";
            _selectingToc = true;
            _toc = new TocNavigator(_book.TableOfContents); _tocCollapsed.Clear(); _currentToc = -1; TocFilter.Text = ""; RefreshToc();
            _selectingToc = false;
            RefreshBookmarks();
            RefreshAnnotations(); LoadAppearanceControls(); ApplyTheme(Preferences.Theme);
            WelcomePane.Visibility = Visibility.Collapsed; ReaderPane.Visibility = Visibility.Visible;
            SidebarButton.IsEnabled = true;
            await NavigateAsync(Math.Clamp(_history.ChapterIndex, 0, _book.Chapters.Count - 1), _history.BlockIndex, _history.Fraction,
                saveCurrent: false, locator: _history.Locator, addHistory: false);
            ScheduleSave();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is EpubException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { if (!_closed && !token.IsCancellationRequested) { ShowError(exception.Message); StatusText.Text = "无法打开这本书。"; } }
        finally
        {
            if (opened != null) { _ = Task.Run(opened.Dispose); }
            EndBusy(busy);
        }
    }

    internal static string LocalFile(string path)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(path), ".epub", StringComparison.OrdinalIgnoreCase))
            throw new EpubException("请选择 .epub 文件。");
        string root = Path.GetPathRoot(path) ?? "";
        if (root.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType == DriveType.Network)
            throw new EpubException("请先把书籍复制到本地磁盘，再用 QuietRead 打开。");
        // Inspect from the root down. Opening a child of a junction first could
        // already contact an SMB server or hydrate a cloud placeholder.
        string component = root;
        foreach (string part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            component = Path.Combine(component, part);
            if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                throw new EpubException("请打开本地实际文件，暂不支持符号链接、目录联接或云占位文件。");
        }
        if (!File.Exists(path)) throw new EpubException("找不到这本书，请重新选择文件。");
        return path;
    }

    internal async Task NavigateAsync(int index, int blockIndex = 0, double fraction = 0, string? fragment = null, bool saveCurrent = true,
        ReadingLocator? locator = null, bool addHistory = true, int characterOffset = 0)
    {
        EpubBook? book = _book;
        if (book == null || index < 0 || index >= book.Chapters.Count || _closed) return;
        NavigationPosition? origin = saveCurrent ? CurrentPosition() : null;
        CloseFootnote(); _resizeTimer.Stop(); _layoutLocator = null;
        Cancel(ref _readToken);
        _readToken = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken token = _readToken.Token;
        int generation = ++_readGeneration;
        int busy = BeginBusy("正在载入章节…");
        ParsedChapter? currentChapter = index == _chapterIndex ? _chapter : null;
        int[]? currentSegments = currentChapter != null ? _segments : null;
        BookAnnotation[] annotationSnapshot = _bookData?.Annotations.ToArray() ?? [];
        try
        {
            var slice = await RunBoundedWorkAsync(_readingWork, () =>
            {
                token.ThrowIfCancellationRequested();
                ParsedChapter chapter = currentChapter ?? book.ReadChapter(index, token);
                int[] segments = currentSegments ?? DocumentRenderer.BuildSegments(chapter);
                int target = Math.Clamp(blockIndex, 0, chapter.Blocks.Count - 1);
                var location = TextLocation.Resolve(chapter, locator, token);
                int offset = characterOffset;
                if (location != null) { target = location.Value.Block; offset = location.Value.Offset; }
                if (!string.IsNullOrEmpty(fragment) && chapter.Anchors.TryGetValue(fragment, out int anchor)) target = anchor;
                int part = Array.BinarySearch(segments, target);
                if (part < 0) part = ~part - 1;
                int start = segments[part], end = part + 1 < segments.Length ? segments[part + 1] : chapter.Blocks.Count;
                PreparedImages images = DocumentRenderer.PrepareImages(book, chapter, start, end, token);
                IReadOnlyList<AnnotationSpan> annotations = TextRangeLocation.Resolve(chapter, annotationSnapshot, index, book.Chapters[index].Path, token);
                return (chapter, segments, part, start, end, target, offset, precise: location != null || characterOffset > 0, images, annotations);
            }, token);
            if (_closed || token.IsCancellationRequested || generation != _readGeneration || book != _book) return;
            _restoring = true;
            _chapter = slice.chapter; _chapterIndex = index; _segments = slice.segments; _segmentIndex = slice.part;
            Reader.Document = DocumentRenderer.Create(_chapter, slice.start, slice.end, slice.images,
                Preferences, _highlight, FollowLink, out _renderedBlocks, slice.annotations, _highlightOptions);
            ApplyTypography();
            ChapterTitleText.Text = book.Chapters[index].Title;
            SelectCurrentToc();
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closed || generation != _readGeneration || book != _book) return;
                Reader.UpdateLayout();
                _scroll = FindVisual<ScrollViewer>(Reader);
                if (slice.precise && _renderedBlocks.TryGetValue(slice.target, out Block? preciseBlock) &&
                    DocumentPosition.Offset(Reader.Document, preciseBlock, slice.offset) is double textOffset)
                    _scroll?.ScrollToVerticalOffset(textOffset);
                else if ((fragment != null || fraction == 0 && slice.target != slice.start) && _renderedBlocks.TryGetValue(slice.target, out Block? target))
                {
                    if (DocumentPosition.Offset(Reader.Document, target, 0) is double anchorOffset) _scroll?.ScrollToVerticalOffset(anchorOffset);
                    else target.BringIntoView();
                }
                else _scroll?.ScrollToVerticalOffset(Math.Clamp(fraction, 0, 1) * (_scroll?.ScrollableHeight ?? 0));
                Reader.UpdateLayout();
                _restoring = false;
                RefreshDocumentAutomation(Reader);
                UpdateProgress(); CapturePosition();
                Reader.Focus();
            }, DispatcherPriority.Loaded, token);
            if (_closed || token.IsCancellationRequested || generation != _readGeneration || book != _book) return;
            ScheduleSave();
            if (addHistory) PushHistory(origin);
            UpdateButtons(); Announce(ChapterTitleText);
            StatusText.Text = slice.images.Skipped > 0 ? $"本段有 {slice.images.Skipped} 张图片未显示。" :
                book.ExternalResourcesSkipped > 0 ? "已跳过书内外部资源。" : "阅读位置自动保存";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is EpubException or IOException or UnauthorizedAccessException)
        { if (!_closed && !token.IsCancellationRequested) { ShowError(exception.Message); StatusText.Text = "此章节暂时无法显示，可从目录选择其他章节。"; } }
        finally
        {
            if (generation == _readGeneration) { _restoring = false; }
            EndBusy(busy);
        }
    }

    private void SelectCurrentToc()
    {
        UpdateCurrentToc();
        if (TocList.ItemsSource is not TocRow[] rows) return;
        TocRow? row = rows.FirstOrDefault(x => x.Index == _currentToc);
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

    private async void FollowLink(LocalLink target) => await FollowLinkAsync(target);

    private void Reader_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_ready || _restoring || _chapter == null) return;
        _scroll ??= FindVisual<ScrollViewer>(Reader);
        UpdateProgress();
        if (Math.Abs(e.VerticalChange) > 0.1 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0 && !_resizeTimer.IsEnabled)
        { CapturePosition(); ScheduleSave(); }
    }

    private double ScrollFraction => _scroll == null || _scroll.ScrollableHeight <= 0 ? 0 :
        Math.Clamp(_scroll.VerticalOffset / _scroll.ScrollableHeight, 0, 1);

    private void CapturePosition()
    {
        if (_history == null || _chapter == null || _restoring || _closed) return;
        _history.ChapterIndex = _chapterIndex;
        _history.BlockIndex = _segments[_segmentIndex];
        _history.Fraction = ScrollFraction;
        if (Reader.Document != null && _renderedBlocks.Count > 0)
        {
            var position = DocumentPosition.Capture(Reader.Document, _renderedBlocks, _scroll?.VerticalOffset ?? 0);
            _history.Locator = TextLocation.Capture(_chapter, position.Block, position.Offset);
            UpdateCurrentToc(position.Block);
        }
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
        PartText.Text = _segments.Length > 1 ? $"第 {_segmentIndex + 1}/{_segments.Length} 部分" : "滚动或点击翻页";
        UpdateButtons();
    }

    internal async Task StepReadingAsync(int direction)
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
            await NavigateAsync(_chapterIndex, _segments[part], direction > 0 ? 0 : 1, addHistory: false);
        else if (_chapterIndex + direction >= 0 && _chapterIndex + direction < _book.Chapters.Count)
            await NavigateAsync(_chapterIndex + direction, direction > 0 ? 0 : int.MaxValue, direction > 0 ? 0 : 1, addHistory: false);
        else StatusText.Text = direction > 0 ? "已到书末。" : "已到书首。";
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) => await StepReadingAsync(-1);
    private async void Next_Click(object sender, RoutedEventArgs e) => await StepReadingAsync(1);

    private void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_history == null || _book == null || _chapter == null || _busy) return;
        CapturePosition();
        if (_history.Bookmarks.Count >= 64) { StatusText.Text = "每本书最多保存 64 个书签，请先删除部分书签。"; return; }
        if (_history.Bookmarks.Any(x => x.ChapterIndex == _chapterIndex &&
            (x.Locator is { } saved && _history.Locator is { } current ? saved.Paragraph == current.Paragraph && saved.Offset == current.Offset :
                x.BlockIndex == _history.BlockIndex && Math.Abs(x.Fraction - ScrollFraction) < 0.03)))
        { StatusText.Text = "这个位置已有书签。"; return; }
        _history.Bookmarks.Add(new Bookmark
        {
            ChapterIndex = _chapterIndex,
            BlockIndex = _history.BlockIndex,
            Fraction = ScrollFraction,
            Locator = _history.Locator,
            Label = _book.Chapters[_chapterIndex].Title + (_segments.Length > 1 ? $" · 第 {_segmentIndex + 1} 部分" : "")
        });
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
        { await NavigateAsync(row.Item.ChapterIndex, row.Item.BlockIndex, row.Item.Fraction, locator: row.Item.Locator); BookmarksList.SelectedItem = null; }
    }

    private void DeleteBookmark_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BookmarkRow row && _history != null)
        { _history.Bookmarks.Remove(row.Item); RefreshBookmarks(); ScheduleSave(); e.Handled = true; }
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    internal async Task SearchAsync()
    {
        EpubBook? book = _book;
        string query = SearchBox.Text.Trim();
        if (book == null || query.Length == 0 || _busy) return;
        Cancel(ref _searchToken);
        _searchToken = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken token = _searchToken.Token;
        int generation = ++_searchGeneration;
        var options = new SearchOptions(SearchCase.IsChecked == true, SearchWholeWord.IsChecked == true, SearchChapter.IsChecked == true ? _chapterIndex : null);
        _searchOrigin = CurrentPosition(); _searching = true;
        _searchHits = Array.Empty<SearchHit>(); _searchIndex = -1; SearchPositionText.Text = "—";
        SearchProgressBar.Value = 0; UpdateButtons();
        var progress = new Progress<SearchProgress>(value =>
        {
            if (_closed || token.IsCancellationRequested || generation != _searchGeneration || book != _book || !_searching) return;
            SearchProgressBar.Value = 100d * value.Completed / Math.Max(1, value.Total);
            SearchStatusText.Text = $"已搜索 {value.Completed}/{value.Total} 个正文资源…";
        });
        SearchStatusText.Text = "正在搜索全书…"; SearchList.ItemsSource = null;
        try
        {
            SearchOutcome outcome = await RunBoundedWorkAsync(_searchWork, () => book.Search(query, token, progress, options), token);
            if (_closed || token.IsCancellationRequested || book != _book) return;
            _highlight = query;
            _highlightOptions = options;
            SearchList.ItemsSource = outcome.Hits;
            _searchHits = outcome.Hits; SearchProgressBar.Value = 100;
            SearchStatusText.Text = outcome.LimitReached ? "显示前 200 个匹配段落，可缩小关键词范围。" : $"找到 {outcome.Hits.Count} 个匹配段落。";
            if (outcome.FailedChapters > 0) SearchStatusText.Text += $" {outcome.FailedChapters} 个章节未能解析。";
            Announce(SearchStatusText);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is EpubException or IOException or ArgumentException)
        { if (!_closed && !token.IsCancellationRequested) SearchStatusText.Text = "搜索无法完成。"; }
        finally { if (generation == _searchGeneration) { _searching = false; UpdateButtons(); } }
    }

    private async void Search_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _busy) return;
        if (SearchList.SelectedItem is SearchHit hit)
        { await GoToSearchHitAsync(hit, _searchHits.ToList().IndexOf(hit)); SearchList.SelectedItem = null; }
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
        if (!_ready || _settingAppearance) return;
        Preferences.FontSize = FontSizeSlider.Value;
        Preferences.LineSpacing = LineSlider.Value;
        Preferences.TextWidth = WidthSlider.Value;
        Preferences.ParagraphSpacing = ParagraphSpacingSlider.Value;
        Preferences.FirstLineIndent = IndentSlider.Value;
        IndentLabel.Text = Preferences.FirstLineIndent < 0 ? "遵循来源" : Preferences.FirstLineIndent.ToString("0.#", CultureInfo.InvariantCulture) + " em";
        FontSizeLabel.Text = FontSizeSlider.Value.ToString("0", CultureInfo.InvariantCulture);
        _appearanceTimer.Stop(); _appearanceTimer.Start();
    }

    private void Font_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _settingAppearance || FontCombo.SelectedItem is not ComboBoxItem item) return;
        Preferences.Font = (string)item.Tag;
        ApplyTypography(); ScheduleSave();
    }

    private void ApplyTypography()
    {
        ReaderPreferences p = Preferences;
        ReadingLocator? locator = !_restoring ? CurrentPosition()?.Locator : null;
        FontSizeLabel.Text = p.FontSize.ToString("0", CultureInfo.InvariantCulture);
        Reader.MaxWidth = p.TextWidth + 64;
        if (Reader.Document == null || _chapter == null) return;
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
            RestoreTextPosition(locator);
            Reader.UpdateLayout();
            _restoring = false;
            UpdateProgress(); CapturePosition();
        }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    { if ((sender as Button)?.Tag is string theme) { Preferences.Theme = theme; ApplyTheme(theme); ScheduleSave(); } }

    private void ApplyTheme(string theme)
    {
        string[] names = ["AppBrush", "PanelBrush", "ReaderBrush", "TextBrush", "SecondaryBrush", "BorderBrush", "AccentBrush", "AccentSoftBrush", "HighlightBrush", "CodeBrush", "PrimaryTextBrush", "HighlightTextBrush"];
        string[] colors = theme switch
        {
            "Dark" => ["#181E1A", "#202823", "#1C231F", "#DBE5DA", "#98AA9A", "#344136", "#8FCDA8", "#2C4233", "#5C542B", "#29362C", "#183625", "#DBE5DA"],
            "Sepia" => ["#EEE8DA", "#F7F1E5", "#F5EFDF", "#403A2E", "#817763", "#DFD5C1", "#726337", "#EAE1C8", "#EBD796", "#EDE4D1", "#FFFFFF", "#403A2E"],
            _ => ["#F4F6F3", "#FFFFFF", "#FDFCF9", "#25352D", "#68766D", "#E0E6DF", "#28664B", "#E8F0E8", "#F8E69F", "#EDF1EC", "#FFFFFF", "#25352D"]
        };
        if (SystemParameters.HighContrast)
        {
            Color[] systemColors = [SystemColors.WindowColor, SystemColors.WindowColor, SystemColors.WindowColor,
                SystemColors.WindowTextColor, SystemColors.WindowTextColor, SystemColors.WindowTextColor,
                SystemColors.HotTrackColor, SystemColors.WindowColor, SystemColors.HighlightColor,
                SystemColors.WindowColor, SystemColors.HighlightTextColor, SystemColors.HighlightTextColor];
            colors = systemColors.Select(c => c.ToString()).ToArray();
        }
        for (int i = 0; i < names.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            brush.Freeze(); Application.Current.Resources[names[i]] = brush;
        }
        string[] annotationColors = theme == "Dark" ? ["#5C542B", "#274B35", "#2A425C", "#542C42"] : ["#F8E69F", "#BFE6C8", "#C8DDF4", "#F1CDDC"];
        string[] annotationNames = ["Yellow", "Green", "Blue", "Rose"];
        for (int i = 0; i < annotationNames.Length; i++)
        {
            var brush = new SolidColorBrush(SystemParameters.HighContrast ? SystemColors.HighlightColor : (Color)ColorConverter.ConvertFromString(annotationColors[i]));
            brush.Freeze(); Application.Current.Resources["Annotation" + annotationNames[i] + "Brush"] = brush;
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
        int dark = Preferences.Theme == "Dark" ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _savedStyle = WindowStyle; _savedResize = ResizeMode; _savedWindowState = WindowState;
            WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized;
        }
        else { WindowState = WindowState.Normal; WindowStyle = _savedStyle; ResizeMode = _savedResize; WindowState = _savedWindowState; }
        _fullScreen = !_fullScreen;
        FullScreenButton.Content = _fullScreen ? "退出全屏" : "全屏";
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

    private async void Home_Click(object sender, RoutedEventArgs e)
    {
        CapturePosition(); Cancel(ref _openToken); Cancel(ref _readToken); Cancel(ref _searchToken);
        _openGeneration++; _readGeneration++; _busyGeneration++; _restoring = false;
        _busy = false; Reader.IsEnabled = true; UpdateButtons();
        if (!await SaveReadingDataAsync()) return;
        CapturePosition(); Cancel(ref _openToken); Cancel(ref _readToken); Cancel(ref _searchToken);
        CloseFootnote(); _resizeTimer.Stop(); _layoutLocator = null;
        _back.Clear(); _forward.Clear(); _searchHits = Array.Empty<SearchHit>(); _searchIndex = -1; _searchOrigin = null;
        _searchGeneration++; _searching = false; SearchProgressBar.Value = 0; SearchPositionText.Text = "—";
        _openGeneration++; _readGeneration++; _busyGeneration++;
        _restoring = false;
        EpubBook? book = _book;
        _book = null; _history = null; _bookData = null; _chapter = null; _scroll = null; _renderedBlocks.Clear();
        _toc = null; _currentToc = -1; AnnotationsList.ItemsSource = null;
        LoadAppearanceControls(); ApplyTheme(Preferences.Theme);
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
        if (MessageBox.Show(this, "清除最近阅读列表？\n书库、续读、书签、批注及 EPUB 文件会保留。", "清除最近记录", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await ClearRecentAsync();
    }

    internal async Task ClearRecentAsync()
    {
        try { await _libraryReady; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { ShowSaveFailure(); return; }
        _state.Books.Clear(); RefreshRecent(); _saveTimer.Stop();
        ScheduleSave();
        if (await SaveReadingDataAsync() && !_closed) StatusText.Text = "阅读记录已清除。";
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        AppearancePopup.IsOpen = false;
        MessageBox.Show(this, "QuietRead · 静读\n\n" +
            "点击「打开」或拖入本地 EPUB。用目录跳转，用底部按钮翻页，在「查找」中搜索全书。\n\n" +
            "「Aa」调整排版、选择预设或单书外观；「全屏」放大阅读区域；「＋书签」保存位置。\n\n" +
            "选中文字后在「批注」添加高亮和笔记；「书库」管理标签、阅读状态、备份与导入。搜索支持当前章、大小写和整词。保存失败可点击「重试保存」。\n\n" +
            "阅读记录只保存在本机。软件不会执行书内脚本或加载外部资源。适合小说和一般文字书，暂不支持 DRM 和固定版式。",
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
        TocList.IsEnabled = !_busy; BookmarksList.IsEnabled = !_busy; SearchList.IsEnabled = !_busy;
        BookmarkButton.IsEnabled = active; SearchButton.IsEnabled = _book != null && !_busy;
        BackButton.IsEnabled = active && _back.Count > 0; ForwardButton.IsEnabled = active && _forward.Count > 0;
        SearchCancelButton.IsEnabled = _searching;
        SearchPreviousButton.IsEnabled = active && _searchIndex > 0;
        SearchNextButton.IsEnabled = active && _searchIndex + 1 < _searchHits.Count;
        SearchReturnButton.IsEnabled = active && _searchOrigin != null;
        AnnotationAddButton.IsEnabled = active; AnnotationsList.IsEnabled = active;
        bool note = active && AnnotationsList.SelectedItem is BookAnnotation;
        AnnotationSaveButton.IsEnabled = note; AnnotationJumpButton.IsEnabled = note; AnnotationDeleteButton.IsEnabled = note;
        PreviousButton.IsEnabled = active && (_chapterIndex > 0 || _segmentIndex > 0 || (_scroll?.VerticalOffset ?? 0) > 2);
        NextButton.IsEnabled = active && (_chapterIndex + 1 < _book!.Chapters.Count || _segmentIndex + 1 < _segments.Length ||
            (_scroll != null && _scroll.VerticalOffset < _scroll.ScrollableHeight - 2));
    }

    private void ScheduleSave()
    { if (!_ready || _closed) return; _dirty = true; _editRevision++; _saveTimer.Stop(); _saveTimer.Start(); SaveStatusText.Text = "待保存"; }

    private async void SaveTimer_Tick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        if (_closed) return;
        await SaveReadingDataAsync();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        if (!TryFlushReadingData() && MessageBox.Show(this, "阅读数据保存失败。仍要退出吗？\n选择「否」保留窗口，修复权限或磁盘问题后点击「重试保存」。",
            "尚未保存", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) { e.Cancel = true; return; }
        CapturePosition(); _closed = true; _saveTimer.Stop(); _appearanceTimer.Stop();
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        CloseFootnote(); _resizeTimer.Stop(); SystemParameters.StaticPropertyChanged -= SystemSettingsChanged;
        _lifetime.Cancel(); Cancel(ref _openToken); Cancel(ref _readToken); Cancel(ref _searchToken);
        EpubBook? book = _book; _book = null; _chapter = null; _history = null; _scroll = null;
        _bookData = null; _toc = null;
        Reader.Document = null; _renderedBlocks.Clear();
        AnnotationsList.ItemsSource = null;
        TocList.ItemsSource = null; SearchList.ItemsSource = null; BookmarksList.ItemsSource = null; RecentList.ItemsSource = null;
        _lifetime.Dispose();
        if (book != null) _ = Task.Run(book.Dispose);
    }

    private void ShowError(string message)
    { if (!_closed) MessageBox.Show(this, message, "无法读取书籍", MessageBoxButton.OK, MessageBoxImage.Information); }

    private static void Cancel(ref CancellationTokenSource? source)
    { source?.Cancel(); source?.Dispose(); source = null; }

    private static async Task<T> RunBoundedWorkAsync<T>(SemaphoreSlim gate, Func<T> work, CancellationToken token)
    {
        // Cancel queued requests without occupying worker threads. A codec cannot
        // be interrupted internally, so let its retired invocation finish before
        // starting another image/open operation. Search has a separate queue.
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(work, token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

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

    private sealed class TocRow(TocItem item, int index, bool children, bool expanded, bool current)
    {
        public TocItem Item { get; } = item;
        public string Title => Item.Title;
        public int Index => index;
        public string Toggle => expanded ? "▾" : "▸";
        public Visibility ToggleVisibility => children ? Visibility.Visible : Visibility.Hidden;
        public string CurrentMark => current ? "●" : "";
        public Thickness Indent => new(Math.Clamp(Item.Depth, 0, 6) * 12d, 0, 0, 0);
    }

    private sealed class BookmarkRow(Bookmark item)
    {
        public Bookmark Item { get; } = item;
        public string Label => Item.Label;
        public string DateLabel => Item.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
