using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuietRead.Core;

namespace QuietRead;

public partial class MainWindow
{
    internal ReadingLocator? CaptureReadingLocator() => CurrentPosition()?.Locator;
    private sealed record NavigationPosition(int Chapter, int Block, double Fraction, ReadingLocator? Locator);
    private readonly List<NavigationPosition> _back = [], _forward = [];
    private NavigationPosition? _searchOrigin;
    private IReadOnlyList<SearchHit> _searchHits = Array.Empty<SearchHit>();
    private int _searchIndex = -1, _searchGeneration;
    private bool _searching;
    private CancellationTokenSource? _noteToken;
    private LocalLink? _noteLink;
    private IInputElement? _noteFocus;
    private ReadingLocator? _layoutLocator;
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    private NavigationPosition? CurrentPosition()
    {
        CapturePosition();
        return _history == null || _chapter == null ? null : new NavigationPosition(_chapterIndex,
            _history.BlockIndex, _history.Fraction, _history.Locator);
    }

    private void PushHistory(NavigationPosition? position)
    {
        if (position == null || _back.LastOrDefault() == position) return;
        _back.Add(position);
        if (_back.Count > 64) _back.RemoveAt(0);
        _forward.Clear();
    }

    internal async Task MoveHistoryAsync(bool forward)
    {
        if (_busy) return;
        List<NavigationPosition> source = forward ? _forward : _back, destination = forward ? _back : _forward;
        if (source.Count == 0) return;
        NavigationPosition target = source[^1];
        NavigationPosition? current = CurrentPosition();
        source.RemoveAt(source.Count - 1);
        if (current != null) { destination.Add(current); if (destination.Count > 64) destination.RemoveAt(0); }
        await NavigateAsync(target.Chapter, target.Block, target.Fraction, locator: target.Locator, addHistory: false);
        UpdateButtons();
    }

    private async void Back_Click(object sender, RoutedEventArgs e) => await MoveHistoryAsync(false);
    private async void Forward_Click(object sender, RoutedEventArgs e) => await MoveHistoryAsync(true);

    internal async Task FollowLinkAsync(LocalLink link)
    {
        EpubBook? book = _book;
        if (book == null || _busy || _closed) return;
        CloseFootnote();
        _noteToken = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken token = _noteToken.Token;
        try
        {
            FootnotePreview? preview = await RunBoundedWorkAsync(_readingWork, () => book.ReadFootnote(link, token), token);
            if (_closed || token.IsCancellationRequested || book != _book) return;
            if (preview != null)
            {
                _noteLink = link; _noteFocus = Keyboard.FocusedElement;
                var chapter = new ParsedChapter(preview.Blocks, new Dictionary<string, int>(), _chapter?.RightToLeft ?? false,
                    preview.Blocks.Sum(b => b.PlainText.Length));
                FootnoteReader.Document = DocumentRenderer.Create(chapter, 0, chapter.Blocks.Count,
                    new PreparedImages(new Dictionary<string, ImageSource>(), 0), Preferences, "", _ => { }, out _);
                UiText.Text(FootnoteHint, preview.Truncated ? "注释过长或含图片，点击「跳转到原文」查看。" : "关闭注释后继续正文阅读。");
                FootnoteOverlay.Visibility = Visibility.Visible;
                RefreshDocumentAutomation(FootnoteReader);
                FootnoteCloseButton.Focus(); Announce(FootnoteHint);
                return;
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception exception) when (exception is EpubException or System.IO.IOException or UnauthorizedAccessException)
        { if (_closed || token.IsCancellationRequested) return; }
        int chapterIndex = book.FindChapter(link.Path);
        if (chapterIndex >= 0) await NavigateAsync(chapterIndex, fragment: link.Fragment);
    }

    internal void CloseFootnote()
    {
        Cancel(ref _noteToken);
        FootnoteOverlay.Visibility = Visibility.Collapsed; FootnoteReader.Document = null; _noteLink = null;
        if (_noteFocus is UIElement element && element.IsVisible && element.IsEnabled) element.Focus();
        else if (_noteFocus is ContentElement content) content.Focus();
        _noteFocus = null;
    }

    private void FootnoteClose_Click(object sender, RoutedEventArgs e) => CloseFootnote();
    private async void FootnoteJump_Click(object sender, RoutedEventArgs e)
    {
        LocalLink? link = _noteLink; CloseFootnote();
        if (link != null && _book != null) await NavigateAsync(_book.FindChapter(link.Path), fragment: link.Fragment);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && FootnoteOverlay.Visibility == Visibility.Visible) { CloseFootnote(); e.Handled = true; }
        else if (e.Key == Key.Escape && AppearancePopup.IsOpen) { AppearancePopup.IsOpen = false; AppearanceButton.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter && SearchBox.IsKeyboardFocused) { SearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
    }

    internal async Task ReceiveOpenRequestAsync(string? path)
    {
        if (_closed) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate();
        if (path != null) await OpenBookAsync(path);
    }

    private void CancelSearch_Click(object sender, RoutedEventArgs e) => StopSearch();
    internal void StopSearch()
    {
        _searchGeneration++; Cancel(ref _searchToken); _searching = false;
        UiText.Text(SearchStatusText, "搜索已停止。"); UpdateButtons(); Announce(SearchStatusText);
    }

    private async void SearchPrevious_Click(object sender, RoutedEventArgs e) => await MoveSearchAsync(-1);
    private async void SearchNext_Click(object sender, RoutedEventArgs e) => await MoveSearchAsync(1);
    internal async Task MoveSearchAsync(int direction)
    {
        int index = _searchIndex + direction;
        if (_busy || index < 0 || index >= _searchHits.Count) return;
        await GoToSearchHitAsync(_searchHits[index], index);
    }

    private async Task GoToSearchHitAsync(SearchHit hit, int index)
    {
        _searchIndex = index;
        await NavigateAsync(hit.ChapterIndex, hit.BlockIndex, characterOffset: hit.Offset);
        SearchPositionText.Text = $"{index + 1} / {_searchHits.Count}"; UpdateButtons();
    }

    private async void SearchReturn_Click(object sender, RoutedEventArgs e)
    {
        if (_searchOrigin is { } position) await NavigateAsync(position.Chapter, position.Block, position.Fraction, locator: position.Locator);
    }

    private static void Announce(UIElement element) =>
        (UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element))?
            .RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

    private static void RefreshDocumentAutomation(FlowDocumentScrollViewer viewer)
    {
        // The native viewer caches its document child. In-process providers may
        // otherwise retain the retired chapter until an external UIA tree walk.
        UIElementAutomationPeer.FromElement(viewer)?.ResetChildrenCache();
    }

    private void SystemSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast)) Dispatcher.InvokeAsync(RefreshSystemTheme);
    }

    internal void RefreshSystemTheme() { if (!_closed) ApplyTheme(Preferences.Theme); }

    private void Reader_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_ready || _restoring || _chapter == null || _closed) return;
        _layoutLocator ??= _history?.Locator;
        _resizeTimer.Stop(); _resizeTimer.Start();
    }

    private void RestoreTextPosition(ReadingLocator? locator)
    {
        if (_chapter == null || Reader.Document == null) return;
        if (TextLocation.Resolve(_chapter, locator) is { } location && _renderedBlocks.TryGetValue(location.Block, out Block? block))
        {
            double? offset = DocumentPosition.Offset(Reader.Document, block, location.Offset);
            if (offset != null) _scroll?.ScrollToVerticalOffset(offset.Value);
        }
    }
}
