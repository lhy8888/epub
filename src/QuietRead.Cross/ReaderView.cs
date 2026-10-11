using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using QuietRead.Core;

namespace QuietRead.Cross;

// One UI for macOS/Linux and touch devices. All book text is produced by the
// guarded core parser, never by a WebView. Each rendered segment is bounded.
public sealed class ReaderView : UserControl
{
    public ReaderSession Session { get; }
    public UiLanguage Language { get; }
    private readonly Grid _body = new();
    private readonly Border _sidebar = new();
    private readonly TabControl _tabs = new();
    private readonly StackPanel _document = new() { Spacing = 10, Margin = new Thickness(16, 12), MaxWidth = 1100 };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly TextBlock _title = new() { FontSize = 18, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _libraryList = new(), _tocList = new(), _searchList = new(), _marksList = new(), _notesList = new();
    private readonly TextBox _libraryFilter = new(), _tocFilter = new(), _query = new(), _note = new() { AcceptsReturn = true, Height = 88, MaxLength = 2000 };
    private readonly TextBox _metaTitle = new(), _metaAuthor = new(), _metaTags = new(), _metaSeries = new();
    private readonly ComboBox _metaStatus = new(), _languageChoice = new(), _theme = new(), _color = new();
    private readonly Slider _font = new() { Minimum = 14, Maximum = 36 }, _line = new() { Minimum = 1.2, Maximum = 2.4 }, _width = new() { Minimum = 520, Maximum = 1100 };
    private readonly CheckBox _perBook = new(), _matchCase = new(), _wholeWord = new(), _currentChapter = new();
    private readonly WrapPanel _actions = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _footnote = new() { Spacing = 8, Margin = new Thickness(8) };
    private readonly Border _footnotePanel = new() { IsVisible = false, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
    private readonly List<(int Index, SelectableTextBlock Text)> _paragraphs = [];
    private readonly List<Bitmap> _images = [];
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private CancellationTokenSource? _operation;
    private bool _ready, _busy, _restoring, _closed, _dialogOpen;
    private string _statusKey = "就绪";
    private object?[] _statusArguments = [];
    private string? _statusError;
    private int _renderGeneration;
    private readonly Stack<(int Chapter, int Block, ReadingLocator? Locator)> _back = new(), _forward = new();
    public bool Busy => _busy;
    public string RenderedText => string.Join('\n', _paragraphs.Select(p => p.Text.Inlines?.Text ?? p.Text.Text));
    public ReaderView(ReaderSession session)
    {
        Session = session; Language = new UiLanguage(session.State.InterfaceLanguage);
        Build(); _ready = true; RefreshLists(); ApplyAppearance();
        _scroll.ScrollChanged += (_, _) => { if (!_restoring && !_busy) { _saveTimer.Stop(); _saveTimer.Start(); } };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await RunAsync(() => Task.Run(PersistPosition), showBusy: false); };
        DetachedFromVisualTree += (_, _) => { _saveTimer.Stop(); _operation?.Cancel(); };
        AttachedToVisualTree += async (_, _) =>
        {
            if (ReaderApp.StartupFile is not { } path)
            {
                if (Session.Data != null && Session.Book != null)
                    await RunAsync(async () => { await Task.Run(() => Session.Navigate(Session.Data.History.ChapterIndex, Session.Data.History.BlockIndex, Session.Data.History.Locator)); await RenderAsync(); });
                return;
            }
            ReaderApp.StartupFile = null;
            bool success = await OpenPathAsync(path);
            if (ReaderApp.SmokeReport is { } report) await FinishSmokeAsync(report, success);
        };
    }

    private TextBlock Label(string key) { var label = new TextBlock { TextWrapping = TextWrapping.Wrap }; Language.Bind(label, TextBlock.TextProperty, key); return label; }
    private Button Button(string key, Func<Task> action)
    {
        var button = new Button { Padding = new Thickness(10, 8), MinHeight = 40 };
        Language.Bind(button, ContentControl.ContentProperty, key);
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception error) { _statusError = error.Message; RefreshStatus(); }
        }; return button;
    }
    private CheckBox Check(CheckBox box, string key) { Language.Bind(box, ContentControl.ContentProperty, key); return box; }
    private static StackPanel Panel(params Control[] children)
    { var panel = new StackPanel { Spacing = 8, Margin = new Thickness(8) }; foreach (Control child in children) panel.Children.Add(child); return panel; }
    private TabItem Tab(string key, Control content)
    { var tab = new TabItem { Content = content, Padding = new Thickness(6), FontSize = 12 }; Language.Bind(tab, HeaderedContentControl.HeaderProperty, key); return tab; }
    private ComboBox Options(ComboBox box, params string[] keys)
    { box.ItemsSource = keys.Select(key => (object)Label(key)).ToArray(); box.SelectedIndex = 0; return box; }
    private void Build()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(6) };
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        toolbar.Children.Add(Button("打开", PickBookAsync));
        toolbar.Children.Add(Button("书库", () => ShowTabAsync(0)));
        toolbar.Children.Add(Button("目录", () => ShowTabAsync(1)));
        toolbar.Children.Add(Button("查找", () => ShowTabAsync(2)));
        toolbar.Children.Add(Button("批注", () => ShowTabAsync(3)));
        toolbar.Children.Add(Button("＋书签", () => RunAsync(() => Task.Run(() => { PersistPosition(); Session.AddBookmark(); }), refresh: true)));
        var appearance = new Button { Content = "Aa", Padding = new Thickness(12, 8), MinHeight = 40 };
        Language.Bind(appearance, AutomationProperties.NameProperty, "阅读外观"); appearance.Click += async (_, _) => await ShowTabAsync(4); toolbar.Children.Add(appearance);
        toolbar.Children.Add(Button("返回上次跳转位置", BackAsync));
        toolbar.Children.Add(Button("前进到下一跳转位置", ForwardAsync));
        toolbar.Children.Add(Button("使用说明", () => { SetStatus("点击「打开」选择 EPUB；目录、查找和批注用于导航与笔记。「Aa」调整外观和界面语言，书库管理元数据与 JSON 备份。阅读数据保存在本机。"); return Task.CompletedTask; }));
        var top = Panel(_title, toolbar); top.Margin = new Thickness(0); root.Children.Add(top);
        _scroll.Content = _document; AutomationProperties.SetName(_scroll, Language.Get("书籍正文"));
        _body.Children.Add(_scroll); _sidebar.Child = _tabs; _sidebar.BorderThickness = new Thickness(0, 0, 1, 0); _sidebar.BorderBrush = Brushes.Gray;
        _body.Children.Add(_sidebar); Grid.SetRow(_body, 1); root.Children.Add(_body);
        _libraryFilter.PlaceholderText = Language.Get("筛选书名、作者、标签或系列"); _libraryFilter.TextChanged += (_, _) => RefreshLibraryRows();
        _libraryList.MinHeight = 100; _libraryList.MaxHeight = 240; _libraryList.SelectionChanged += (_, _) => LoadMetadata();
        _metaTitle.MaxLength = _metaAuthor.MaxLength = 256; _metaSeries.MaxLength = 100; _metaTags.MaxLength = 1100;
        Options(_metaStatus, "未读", "在读", "已读");
        var library = Panel(_libraryFilter, _libraryList, Button("打开选中书籍", OpenSelectedAsync), Label("本地元数据"), Label("书名"), _metaTitle,
            Label("作者"), _metaAuthor, Label("标签（用逗号分隔）"), _metaTags, Label("系列"), _metaSeries, Label("阅读状态"), _metaStatus,
            Button("保存元数据", SaveMetadataAsync), Button("导出本书 JSON", () => ExportAsync("json", true)),
            Button("导出本书 Markdown 摘录", () => ExportAsync("md", true)), Button("备份全部 JSON", () => ExportAsync("json", false)),
            Button("导入阅读数据", ImportBackupAsync), Button("移除书库条目", RemoveSelectedAsync));
        _tocFilter.PlaceholderText = Language.Get("筛选目录标题"); _tocFilter.TextChanged += (_, _) => RefreshToc();
        _tocList.MinHeight = 100; _tocList.MaxHeight = 280;
        _tocList.SelectionChanged += async (_, _) => { if (_tocList.SelectedItem is TocRow row) await NavigateAsync(row.Item.ChapterIndex, fragment: row.Item.Fragment); };
        _marksList.MaxHeight = 200; _marksList.SelectionChanged += async (_, _) =>
        { if (_marksList.SelectedItem is MarkRow row) await NavigateAsync(row.Mark.ChapterIndex, row.Mark.BlockIndex, row.Mark.Locator); };
        var toc = Panel(_tocFilter, _tocList, Label("书签"), _marksList, Button("删除书签", DeleteBookmarkAsync));
        _query.MaxLength = 128; _query.PlaceholderText = Language.Get("全文搜索关键词");
        _searchList.MaxHeight = 360; _searchList.SelectionChanged += async (_, _) => { if (_searchList.SelectedItem is HitRow row) await NavigateAsync(row.Hit.ChapterIndex, row.Hit.BlockIndex); };
        var stop = Button("停止", () => { _operation?.Cancel(); return Task.CompletedTask; });
        var search = Panel(_query, Check(_currentChapter, "当前章"), Check(_matchCase, "区分大小写"), Check(_wholeWord, "整词"), Button("查找", SearchAsync), stop, _searchList);
        _notesList.MaxHeight = 240; _notesList.SelectionChanged += (_, _) => { if (_notesList.SelectedItem is NoteRow row) { _note.Text = row.Note.Note; _color.SelectedIndex = Array.IndexOf(Colors, row.Note.Color); } };
        Options(_color, "黄色", "绿色", "蓝色", "粉色");
        var notes = Panel(Label("在正文选中文字，再点击高亮。"), _color, _note, Button("选区高亮", AddAnnotationAsync), _notesList,
            Button("保存笔记", SaveAnnotationAsync), Button("定位", JumpAnnotationAsync), Button("删除", DeleteAnnotationAsync));
        _languageChoice.ItemsSource = new[] { "简体中文", "English" }; _languageChoice.SelectedIndex = Language.Language == "en" ? 1 : 0;
        _languageChoice.SelectionChanged += async (_, _) =>
        { if (_ready) await SetLanguageAsync(_languageChoice.SelectedIndex == 1 ? "en" : "zh-CN"); };
        Options(_theme, "纸白", "暖色", "夜间");
        _font.Value = Session.Preferences.FontSize; _line.Value = Session.Preferences.LineSpacing; _width.Value = Session.Preferences.TextWidth;
        _theme.SelectedIndex = Array.IndexOf(Themes, Session.Preferences.Theme); _perBook.IsChecked = Session.Data?.Appearance != null;
        var settings = Panel(Label("界面语言"), _languageChoice, Check(_perBook, "本书独立外观（关闭后使用全局默认）"), Label("背景"), _theme,
            Label("字号"), _font, Label("行距"), _line, Label("阅读宽度"), _width, Button("应用阅读外观", SaveAppearanceAsync),
            Button("本书恢复默认", ResetAppearanceAsync));
        _tabs.ItemsSource = new[] { Tab("书库", Wrap(library)), Tab("目录", Wrap(toc)), Tab("查找", Wrap(search)), Tab("批注", Wrap(notes)), Tab("阅读外观", Wrap(settings)) };
        _actions.Children.Add(Button("← 上一页", () => PageAsync(-1))); _actions.Children.Add(Button("下一页 →", () => PageAsync(1)));
        _actions.Children.Add(Button("重试保存", () => RunAsync(() => Task.Run(() => { PersistPosition(); Session.Save(); }))));
        var closeTools = Button("关闭", () => { _sidebar.IsVisible = false; ResizeBody(); return Task.CompletedTask; }); _actions.Children.Add(closeTools);
        var bottom = Panel(_actions, _status); bottom.Margin = new Thickness(0); Grid.SetRow(bottom, 2); root.Children.Add(bottom);
        _footnotePanel.Child = new ScrollViewer { Content = _footnote, MaxHeight = 320 }; Grid.SetRow(_footnotePanel, 1); root.Children.Add(_footnotePanel);
        Content = root; _sidebar.IsVisible = false; SizeChanged += (_, _) => ResizeBody(); ResizeBody();
        _title.Text = "QuietRead"; _document.Children.Add(Label("打开一本书。")); _document.Children.Add(Label("拖入 EPUB，或选择本地文件。")); RefreshStatus();
    }
    private static readonly string[] Themes = ["Paper", "Sepia", "Dark"], Colors = ["Yellow", "Green", "Blue", "Rose"];
    private static ScrollViewer Wrap(Control child) => new() { Content = child, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private void ResizeBody()
    {
        bool side = Bounds.Width >= 800;
        _body.ColumnDefinitions = new ColumnDefinitions(side && _sidebar.IsVisible ? "300,*" : "*");
        _body.RowDefinitions = new RowDefinitions(side || !_sidebar.IsVisible ? "*" : "2*,3*");
        Grid.SetColumn(_sidebar, 0); Grid.SetColumn(_scroll, side && _sidebar.IsVisible ? 1 : 0);
        Grid.SetRow(_sidebar, 0); Grid.SetRow(_scroll, side || !_sidebar.IsVisible ? 0 : 1);
    }
    private Task ShowTabAsync(int index)
    { if (_dialogOpen) return Task.CompletedTask; _tabs.SelectedIndex = index; _sidebar.IsVisible = true; ResizeBody(); return Task.CompletedTask; }

    public async Task<bool> RunAsync(Func<Task> work, bool refresh = false, bool showBusy = true)
    {
        if (_busy || _closed || _dialogOpen) return false;
        CapturePosition(); _busy = true; _saveTimer.Stop();
        _operation = new CancellationTokenSource();
        try
        {
            if (showBusy) SetStatus("正在处理…");
            await work(); if (refresh) RefreshLists(); SetStatus("已保存"); return true;
        }
        catch (OperationCanceledException) { SetStatus("搜索已停止。"); return false; }
        catch (Exception error) { _statusError = error.Message; RefreshStatus(); return false; }
        finally { _operation.Dispose(); _operation = null; _busy = false; }
    }
    private void SetStatus(string key, params object?[] args)
    { _statusKey = key; _statusArguments = args; _statusError = null; RefreshStatus(); }
    private void RefreshStatus() => _status.Text = _statusError == null ? Language.Get(_statusKey, _statusArguments) : InterfaceText.Error(Language.Language, _statusError);
    public async Task SetLanguageAsync(string language)
    {
        string key = _statusKey; object?[] args = _statusArguments; string? error = _statusError;
        if (await RunAsync(() => Task.Run(() => Session.SetLanguage(language))))
        {
            Language.Set(Session.State.InterfaceLanguage); _statusKey = key; _statusArguments = args; _statusError = error; RefreshStatus();
            _libraryFilter.PlaceholderText = Language.Get("筛选书名、作者、标签或系列"); _tocFilter.PlaceholderText = Language.Get("筛选目录标题"); _query.PlaceholderText = Language.Get("全文搜索关键词");
            AutomationProperties.SetName(_scroll, Language.Get("书籍正文"));
        }
        _ready = false; _languageChoice.SelectedIndex = Session.State.InterfaceLanguage == "en" ? 1 : 0; _ready = true;
    }
    public async Task<bool> OpenPathAsync(string path)
    {
        bool result = await RunAsync(async () =>
        { await Task.Run(() => { PersistPosition(); Session.Open(path, _operation!.Token); }); _back.Clear(); _forward.Clear(); await RenderAsync(); RefreshLists(); });
        return result;
    }
    private async Task PickBookAsync()
    {
        if (_busy || _dialogOpen) return; var provider = TopLevel.GetTopLevel(this)?.StorageProvider; if (provider?.CanOpen != true) { SetStatus("此平台暂时无法使用文件选择器。"); return; }
        var selected = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Language.Get("打开 EPUB"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("EPUB") { Patterns = ["*.epub"], MimeTypes = ["application/epub+zip"], AppleUniformTypeIdentifiers = ["org.idpf.epub-container"] }]
        });
        if (selected.Count == 0) return;
        await RunAsync(async () =>
        {
            using IStorageFile file = selected[0]; await using Stream source = await file.OpenReadAsync();
            string path = await Session.Files.ImportAsync(source, _operation!.Token);
            await Task.Run(() => { PersistPosition(); Session.Open(path, _operation.Token); }); _back.Clear(); _forward.Clear(); await RenderAsync(); RefreshLists();
        });
    }
    private void PersistPosition()
    {
        // Called on a worker using a snapshot captured on the UI thread below;
        // the fallback history is safe for background lifecycle callbacks.
        if (_position is { } p) Session.RememberPosition(p.Id, p.Chapter, p.Block);
    }
    private sealed record Position(string Id, int Chapter, int Block);
    private Position? _position;
    private void CapturePosition()
    {
        if (_restoring || Session.Book == null || Session.Chapter == null) return;
        int top = Session.TargetBlock;
        foreach (var paragraph in _paragraphs)
        {
            var point = paragraph.Text.TranslatePoint(default, _scroll);
            if (point is { } p && p.Y + paragraph.Text.Bounds.Height >= 0) { top = paragraph.Index; break; }
        }
        _position = new Position(Session.Book.BookKey, Session.ChapterIndex, top);
    }
    public void PersistForBackground()
    {
        if (_closed) return; _operation?.Cancel(); CapturePosition();
        try { PersistPosition(); Session.Save(); }
        catch (Exception error) { _statusError = error.Message; RefreshStatus(); }
    }
    public bool TryClose()
    {
        if (_busy || _dialogOpen) { SetStatus("正在处理…"); return false; }
        PersistForBackground();
        try { Session.Save(); Session.Dispose(); _closed = true; _saveTimer.Stop(); foreach (var bitmap in _images) bitmap.Dispose(); return true; }
        catch (Exception error) { _statusError = error.Message; RefreshStatus(); return false; }
    }

    public async Task RenderAsync()
    {
        if (Session.Book == null || Session.Chapter == null) { ClearDocument(); _title.Text = "QuietRead"; _document.Children.Add(Label("打开一本书。")); return; }
        ParsedChapter chapter = Session.Chapter; int start = Session.SegmentStart;
        _restoring = true; int generation = ++_renderGeneration; ClearDocument(); _position = null;
        _title.Text = Session.Book.Title + " · " + Session.Book.Chapters[Session.ChapterIndex].Title;
        ApplyAppearance();
        var blocks = chapter.Blocks.Skip(start).Take(ReaderLimits.BlocksPerView).ToArray();
        var spans = TextRangeLocation.Resolve(chapter, Session.Data!.Annotations, Session.ChapterIndex, Session.Book.Chapters[Session.ChapterIndex].Path);
        // A cumulative budget applies to the segment as well as each image.
        var rasters = await Task.Run(() =>
        {
            var result = new Dictionary<string, RasterData>(); long pixels = 0; var budget = new ResourceReadBudget(24 * 1024 * 1024);
            foreach (string path in blocks.Where(b => b.ImagePath != null).Select(b => b.ImagePath!).Distinct())
            {
                _operation?.Token.ThrowIfCancellationRequested();
                try
                {
                    RasterData raster = Session.ReadImage(path, _operation?.Token ?? default, budget);
                    pixels += (long)raster.Info.Width * raster.Info.Height;
                    if (pixels > ReaderLimits.ImageViewPixels) break;
                    result.Add(path, raster);
                }
                catch (Exception e) when (e is EpubException or InvalidDataException or IOException) { }
            }
            return result;
        });
        if (generation != _renderGeneration) return;
        for (int offset = 0; offset < blocks.Length; offset++)
        {
            int index = start + offset; BookBlock block = blocks[offset];
            if (block.Kind == BlockKind.Image)
            {
                if (block.ImagePath is { } path && rasters.TryGetValue(path, out RasterData? data))
                {
                    try
                    {
                        using var source = new MemoryStream(data.Bytes, writable: false); var bitmap = new Bitmap(source); _images.Add(bitmap);
                        var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 480 };
                        AutomationProperties.SetName(image, block.Alt ?? Language.Get("书内图片")); _document.Children.Add(image);
                    }
                    catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidDataException) { _document.Children.Add(Label("（图片未显示）")); }
                }
                else _document.Children.Add(Label("（图片未显示）"));
                continue;
            }
            if (block.Kind == BlockKind.Rule) { _document.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0, 1, 0, 0) }); continue; }
            var paragraph = new SelectableTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = Session.Preferences.FontSize * (block.Kind == BlockKind.Heading ? 1.25 : 1),
                LineHeight = Session.Preferences.FontSize * Session.Preferences.LineSpacing,
                Margin = new Thickness(block.Kind == BlockKind.Quote ? 12 : 0, 0, 0, Session.Preferences.FontSize * Session.Preferences.ParagraphSpacing),
                FlowDirection = chapter.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
            };
            // Selected text offsets match PlainText exactly, including linked text.
            int position = 0;
            foreach (BookInline inline in block.Inlines)
            {
                int end = position + inline.Text.Length; var cuts = new SortedSet<int> { position, end };
                foreach (var span in spans.Where(s => s.Block == index && s.Start < end && s.End > position)) { cuts.Add(Math.Max(position, span.Start)); cuts.Add(Math.Min(end, span.End)); }
                int[] boundaries = cuts.ToArray();
                for (int part = 0; part < boundaries.Length - 1; part++)
                {
                    int a = boundaries[part], b = boundaries[part + 1]; var run = new Run(inline.Text.Substring(a - position, b - a));
                    if (inline.Style.HasFlag(TextStyle.Bold)) run.FontWeight = FontWeight.Bold;
                    if (inline.Style.HasFlag(TextStyle.Italic)) run.FontStyle = FontStyle.Italic;
                    if (inline.Style.HasFlag(TextStyle.Code)) run.FontFamily = new FontFamily("monospace");
                    if (spans.FirstOrDefault(s => s.Block == index && s.Start < b && s.End > a) is { } annotation)
                        run.Background = HighlightBrush(annotation.Color);
                    paragraph.Inlines!.Add(run);
                }
                position = end;
            }
            _paragraphs.Add((index, paragraph)); _document.Children.Add(paragraph);
            foreach (BookInline inline in block.Inlines.Where(i => i.Link != null).DistinctBy(i => i.Link))
            {
                LocalLink link = inline.Link!; string text = inline.Text;
                var button = Button(link.IsNote ? "查看注释：{0}" : "书内链接：{0}", () => LinkAsync(link));
                Language.Bind(button, ContentControl.ContentProperty, link.IsNote ? "查看注释：{0}" : "书内链接：{0}", text); Language.Bind(button, AutomationProperties.NameProperty, link.IsNote ? "查看注释：{0}" : "书内链接：{0}", text); _document.Children.Add(button);
            }
        }
        _scroll.Offset = default;
        Dispatcher.UIThread.Post(() =>
        {
            _paragraphs.FirstOrDefault(p => p.Index == Session.TargetBlock).Text?.BringIntoView();
            _restoring = false; CapturePosition();
        }, DispatcherPriority.Loaded);
    }
    private static IBrush HighlightBrush(string color) => color switch { "Green" => Brushes.LightGreen, "Blue" => Brushes.LightBlue, "Rose" => Brushes.LightPink, _ => Brushes.Gold };
    private void ClearDocument()
    { _document.Children.Clear(); _paragraphs.Clear(); foreach (var bitmap in _images) bitmap.Dispose(); _images.Clear(); _footnotePanel.IsVisible = false; }
    private void ApplyAppearance()
    {
        ReaderPreferences p = Session.Preferences;
        if (Application.Current != null) Application.Current.RequestedThemeVariant = p.Theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        Background = p.Theme == "Dark" ? new SolidColorBrush(Color.Parse("#181a1d")) : p.Theme == "Sepia" ? new SolidColorBrush(Color.Parse("#f4ecd8")) : Brushes.White;
        _document.MaxWidth = p.TextWidth; _document.HorizontalAlignment = HorizontalAlignment.Center;
        _document.Width = double.NaN; _document.MinWidth = 0;
    }
    private async Task NavigateAsync(int chapter, int block = 0, ReadingLocator? locator = null, string? fragment = null, bool remember = true)
    {
        if (_busy || _dialogOpen) return; CapturePosition();
        if (remember && Session.Data != null) { PushTrail(_back); _forward.Clear(); }
        await RunAsync(async () => { await Task.Run(() => { PersistPosition(); Session.Navigate(chapter, block, locator, fragment, _operation!.Token); }); await RenderAsync(); RefreshLists(); });
        if (Bounds.Width < 800) { _sidebar.IsVisible = false; ResizeBody(); }
    }
    private void PushTrail(Stack<(int Chapter, int Block, ReadingLocator? Locator)> trail)
    {
        int block = _position?.Block ?? Session.TargetBlock;
        if (trail.Count >= 64) { var retained = trail.Take(63).Reverse().ToArray(); trail.Clear(); foreach (var item in retained) trail.Push(item); }
        trail.Push((Session.ChapterIndex, block, Session.Chapter == null ? null : TextLocation.Capture(Session.Chapter, block, 0)));
    }
    private Task BackAsync()
    {
        if (_busy || _dialogOpen || !_back.TryPop(out var p)) return Task.CompletedTask;
        CapturePosition(); PushTrail(_forward);
        return NavigateAsync(p.Chapter, p.Block, p.Locator, remember: false);
    }
    private Task ForwardAsync()
    {
        if (_busy || _dialogOpen || !_forward.TryPop(out var p)) return Task.CompletedTask;
        CapturePosition(); PushTrail(_back);
        return NavigateAsync(p.Chapter, p.Block, p.Locator, remember: false);
    }
    private Task PageAsync(int direction)
    {
        if (_busy || _dialogOpen || Session.Chapter == null || Session.Book == null) return Task.CompletedTask;
        double height = _scroll.Viewport.Height;
        if (direction > 0 && _scroll.Offset.Y + height + 2 < _scroll.Extent.Height || direction < 0 && _scroll.Offset.Y > 2)
        { _scroll.Offset = new Vector(0, Math.Clamp(_scroll.Offset.Y + direction * height * .85, 0, Math.Max(0, _scroll.Extent.Height - height))); return Task.CompletedTask; }
        int next = Session.SegmentStart + direction * ReaderLimits.BlocksPerView;
        if (next >= 0 && next < Session.Chapter.Blocks.Count) return NavigateAsync(Session.ChapterIndex, next);
        int chapter = Session.ChapterIndex + direction;
        if (chapter < 0 || chapter >= Session.Book.Chapters.Count) { SetStatus(direction < 0 ? "已到书首。" : "已到书末。"); return Task.CompletedTask; }
        return NavigateAsync(chapter);
    }
    private async Task LinkAsync(LocalLink link)
    {
        if (Session.Book == null || _busy || _dialogOpen) return;
        if (link.IsNote)
        {
            FootnotePreview? preview = null;
            if (await RunAsync(async () => preview = await Task.Run(() => Session.Footnote(link, _operation!.Token))) && preview != null)
            {
                _footnote.Children.Clear(); _footnote.Children.Add(Label("脚注预览"));
                foreach (BookBlock block in preview.Blocks) _footnote.Children.Add(new SelectableTextBlock { Text = block.PlainText, TextWrapping = TextWrapping.Wrap });
                _footnote.Children.Add(Button("关闭", () => { _footnotePanel.IsVisible = false; return Task.CompletedTask; }));
                _footnote.Children.Add(Button("跳转到原文", () => NavigateAsync(Session.Book.FindChapter(link.Path), fragment: link.Fragment)));
                _footnotePanel.IsVisible = true; return;
            }
        }
        int index = Session.Book.FindChapter(link.Path);
        if (index >= 0) await NavigateAsync(index, fragment: link.Fragment);
    }
    private async Task SearchAsync()
    {
        string query = _query.Text ?? ""; var options = new SearchOptions(_matchCase.IsChecked == true, _wholeWord.IsChecked == true,
            _currentChapter.IsChecked == true ? Session.ChapterIndex : null);
        SearchOutcome? result = null;
        if (await RunAsync(async () => result = await Task.Run(() => Session.Search(query, options, _operation!.Token))) && result != null)
        {
            _searchList.ItemsSource = result.Hits.Select(hit => new HitRow(hit)).ToArray();
            SetStatus(result.LimitReached ? "显示前 200 个匹配段落，可缩小关键词范围。" : "找到 {0} 个匹配段落。", result.Hits.Count);
            if (result.FailedChapters > 0) SetStatus("找到 {0} 个匹配段落；{1} 个章节未能解析。", result.Hits.Count, result.FailedChapters);
        }
    }
    private async Task AddAnnotationAsync()
    {
        var selection = _paragraphs.FirstOrDefault(p => p.Text.SelectionStart != p.Text.SelectionEnd);
        if (selection.Text == null) { SetStatus("请在当前正文中选择文字。"); return; }
        int start = Math.Min(selection.Text.SelectionStart, selection.Text.SelectionEnd), end = Math.Max(selection.Text.SelectionStart, selection.Text.SelectionEnd);
        string note = _note.Text ?? "", color = Colors[Math.Clamp(_color.SelectedIndex, 0, Colors.Length - 1)];
        await RunAsync(async () => { await Task.Run(() => Session.AddAnnotation(selection.Index, start, end, note, color)); RefreshNotes(); await RenderAsync(); });
    }
    private Task SaveAnnotationAsync()
    {
        if (_notesList.SelectedItem is not NoteRow row) return Task.CompletedTask;
        string note = _note.Text ?? "", color = Colors[Math.Clamp(_color.SelectedIndex, 0, Colors.Length - 1)];
        return RunAsync(async () => { await Task.Run(() => Session.EditAnnotation(row.Note.Id, note, color)); RefreshNotes(); await RenderAsync(); });
    }
    private Task DeleteAnnotationAsync()
    {
        if (_notesList.SelectedItem is not NoteRow row) return Task.CompletedTask;
        return RunAsync(async () => { await Task.Run(() => Session.DeleteAnnotation(row.Note.Id)); RefreshNotes(); await RenderAsync(); });
    }
    private Task JumpAnnotationAsync() => _notesList.SelectedItem is NoteRow row ? NavigateAsync(row.Note.ChapterIndex, locator: row.Note.Range.Start) : Task.CompletedTask;
    private Task DeleteBookmarkAsync()
    {
        int index = _marksList.SelectedIndex;
        return index < 0 ? Task.CompletedTask : RunAsync(() => Task.Run(() => Session.RemoveBookmark(index)), refresh: true);
    }
    private Task SaveAppearanceAsync()
    {
        ReaderPreferences p = Session.Preferences.Copy(); p.FontSize = _font.Value; p.LineSpacing = _line.Value; p.TextWidth = _width.Value;
        p.Theme = Themes[Math.Clamp(_theme.SelectedIndex, 0, Themes.Length - 1)]; bool perBook = _perBook.IsChecked == true;
        return RunAsync(async () => { await Task.Run(() => { PersistPosition(); Session.SetAppearance(p, perBook); }); await RenderAsync(); });
    }
    private Task ResetAppearanceAsync() => RunAsync(async () => { await Task.Run(() => Session.SetAppearance(Session.State.Preferences.Copy(), false)); _perBook.IsChecked = false; await RenderAsync(); });
    private void RefreshLists()
    {
        RefreshLibraryRows(); RefreshToc(); RefreshNotes();
        _marksList.ItemsSource = Session.Data?.History.Bookmarks.Select(mark => new MarkRow(mark)).ToArray() ?? [];
        _perBook.IsChecked = Session.Data?.Appearance != null; _font.Value = Session.Preferences.FontSize; _line.Value = Session.Preferences.LineSpacing;
        _width.Value = Session.Preferences.TextWidth; _theme.SelectedIndex = Array.IndexOf(Themes, Session.Preferences.Theme);
    }
    private void RefreshLibraryRows()
    {
        string query = _libraryFilter.Text ?? ""; string? selected = (_libraryList.SelectedItem as LibraryRow)?.Entry.BookId;
        var rows = Session.Library.Entries().Where(e => (e.Title + " " + e.Author + " " + string.Join(' ', e.Tags) + " " + e.Series).Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.LastReadUtc).Select(e => new LibraryRow(e)).ToArray();
        _libraryList.ItemsSource = rows;
        if (selected != null) _libraryList.SelectedItem = rows.FirstOrDefault(r => r.Entry.BookId == selected);
    }
    private void RefreshToc()
    {
        string filter = _tocFilter.Text ?? "";
        _tocList.ItemsSource = Session.Book?.TableOfContents.Where(t => t.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)).Select(t => new TocRow(t)).ToArray() ?? [];
    }
    private void RefreshNotes() => _notesList.ItemsSource = Session.Data?.Annotations.Select(a => new NoteRow(a)).ToArray() ?? [];
    private string? _metadataBookId;
    private void LoadMetadata()
    {
        if (_libraryList.SelectedItem is not LibraryRow row || _metadataBookId == row.Entry.BookId) return;
        _metadataBookId = row.Entry.BookId; _metaTitle.Text = row.Entry.Title; _metaAuthor.Text = row.Entry.Author; _metaTags.Text = string.Join(", ", row.Entry.Tags); _metaSeries.Text = row.Entry.Series;
        _metaStatus.SelectedIndex = row.Entry.ReadingStatus == "Finished" ? 2 : row.Entry.ReadingStatus == "Reading" ? 1 : 0;
    }
    private Task SaveMetadataAsync()
    {
        if (_libraryList.SelectedItem is not LibraryRow row) return Task.CompletedTask;
        string title = _metaTitle.Text ?? "", author = _metaAuthor.Text ?? "", series = _metaSeries.Text ?? "";
        string[] tags = (_metaTags.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string status = _metaStatus.SelectedIndex == 2 ? "Finished" : _metaStatus.SelectedIndex == 1 ? "Reading" : "Unread";
        return RunAsync(async () => { await Task.Run(() => Session.Library.UpdateMetadata(row.Entry.BookId, title, author, tags, series, status)); _metadataBookId = null; }, refresh: true);
    }
    private async Task OpenSelectedAsync()
    {
        if (_libraryList.SelectedItem is not LibraryRow row) return;
        if (File.Exists(row.Entry.FilePath)) await OpenPathAsync(row.Entry.FilePath);
        else { SetStatus("尚未关联，请重新选择同一本 EPUB。"); await PickBookAsync(); }
    }
    private async Task RemoveSelectedAsync()
    {
        if (_libraryList.SelectedItem is not LibraryRow row || _busy) return;
        if (!await ConfirmAsync("移除此书的书库条目和阅读数据？请先导出需要保留的批注。\n原 EPUB 文件不会删除。")) return;
        await RunAsync(async () => { await Task.Run(() => Session.RemoveBook(row.Entry.BookId)); await RenderAsync(); RefreshLists(); });
    }
    private async Task ExportAsync(string type, bool single)
    {
        if (_busy || _dialogOpen) return; string? id = (_libraryList.SelectedItem as LibraryRow)?.Entry.BookId;
        if (single && id == null) { SetStatus("请先选择一本书。"); return; }
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider?.CanSave != true) { SetStatus("此平台暂时无法使用文件选择器。"); return; }
        using IStorageFile? target = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Language.Get(type == "md" ? "导出本书 Markdown 摘录" : "备份全部 JSON"),
            SuggestedFileName = "QuietRead." + type,
            DefaultExtension = type
        });
        if (target == null) return;
        string language = Language.Language;
        await RunAsync(async () =>
        {
            byte[] bytes = await Task.Run(() => { PersistPosition(); Session.Save(); return type == "md" ? Encoding.UTF8.GetBytes(Session.Library.ExportMarkdown(id!, language)) : Session.Library.ExportBackup(single ? id : null); });
            await using Stream output = await target.OpenWriteAsync();
            if (output.CanSeek) output.SetLength(0);
            await output.WriteAsync(bytes, _operation!.Token); await output.FlushAsync(_operation.Token);
        });
    }
    private async Task ImportBackupAsync()
    {
        if (_busy || _dialogOpen) return; var provider = TopLevel.GetTopLevel(this)?.StorageProvider; if (provider?.CanOpen != true) return;
        var selected = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Language.Get("导入阅读数据"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"], MimeTypes = ["application/json"], AppleUniformTypeIdentifiers = ["public.json"] }]
        });
        if (selected.Count == 0) return; byte[]? bytes = null; ImportPreview? preview = null;
        if (!await RunAsync(async () =>
        {
            using IStorageFile file = selected[0]; await using Stream input = await file.OpenReadAsync(); using var output = new MemoryStream();
            await ManagedBookFiles.CopyBoundedAsync(input, output, BookDataRules.MaxBackupBytes, _operation!.Token); bytes = output.ToArray();
            preview = await Task.Run(() => Session.Library.PreviewImport(bytes));
        })) return;
        bool? preferImported = await ChooseImportAsync(preview!);
        if (preferImported == null) return;
        await RunAsync(async () => { await Task.Run(() => Session.ImportBackup(bytes!, preferImported.Value)); _metadataBookId = null; await RenderAsync(); RefreshLists(); });
    }
    private Task<bool> ConfirmAsync(string key)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dialogOpen = true; _footnote.Children.Clear(); _footnote.Children.Add(Label(key));
        _footnote.Children.Add(Button("确认", () => { _dialogOpen = false; _footnotePanel.IsVisible = false; completion.TrySetResult(true); return Task.CompletedTask; }));
        _footnote.Children.Add(Button("取消", () => { _dialogOpen = false; _footnotePanel.IsVisible = false; completion.TrySetResult(false); return Task.CompletedTask; }));
        _footnotePanel.IsVisible = true; return completion.Task;
    }
    private Task<bool?> ChooseImportAsync(ImportPreview preview)
    {
        var completion = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dialogOpen = true; _footnote.Children.Clear(); var text = Label("导入预检与冲突选择"); _footnote.Children.Add(text);
        var counts = new TextBlock { TextWrapping = TextWrapping.Wrap }; Language.Bind(counts, TextBlock.TextProperty, "新增 {0} 本，已有 {1} 本，冲突 {2} 本。", preview.NewBooks, preview.ExistingBooks, preview.Conflicts.Count); _footnote.Children.Add(counts);
        foreach (var option in new (string Key, bool? Imported)[] { ("保留现有冲突数据", false), ("采用导入数据", true), ("取消", null) })
            _footnote.Children.Add(Button(option.Key, () => { _dialogOpen = false; _footnotePanel.IsVisible = false; completion.TrySetResult(option.Imported); return Task.CompletedTask; }));
        _footnotePanel.IsVisible = true; return completion.Task;
    }
    private async Task FinishSmokeAsync(string report, bool opened)
    {
        await Task.Delay(300); bool passed = opened && _paragraphs.Count > 0 && Bounds.Width > 0;
        string? directory = Path.GetDirectoryName(Path.GetFullPath(report)); Directory.CreateDirectory(directory!);
        try
        {
            if (TopLevel.GetTopLevel(this) is { } top)
            { using var bitmap = new RenderTargetBitmap(new PixelSize((int)top.Bounds.Width, (int)top.Bounds.Height)); bitmap.Render(top); bitmap.Save(Path.ChangeExtension(report, ".png"), PngBitmapEncoderOptions.Default); }
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Passed = passed, Platform = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, Blocks = _paragraphs.Count, Language = Language.Language, Error = _statusError }));
        }
        catch (Exception e) { passed = false; await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Passed = false, Error = e.ToString() })); }
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(passed ? 0 : 1);
    }
    private sealed record LibraryRow(LibraryEntry Entry) { public override string ToString() => Entry.Title + " · " + Entry.Author; }
    private sealed record TocRow(TocItem Item) { public override string ToString() => new string(' ', Math.Clamp(Item.Depth, 0, 12) * 2) + Item.Title; }
    private sealed record MarkRow(Bookmark Mark) { public override string ToString() => Mark.Label; }
    private sealed record NoteRow(BookAnnotation Note) { public override string ToString() => Note.Range.Text + (Note.Note.Length == 0 ? "" : " · " + Note.Note); }
    private sealed record HitRow(SearchHit Hit) { public override string ToString() => Hit.ChapterTitle + " · " + Hit.Snippet; }
}
