using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using QuietRead.Core;

namespace QuietRead;

public partial class MainWindow
{
    private bool _dirty, _settingAppearance, _selectingAnnotations;
    private long _editRevision;
    private Task<bool>? _saveTask;
    private SearchOptions _highlightOptions = new();
    private TocNavigator? _toc;
    private readonly HashSet<int> _tocCollapsed = [];
    private int _currentToc = -1;
    internal LibraryStore Library => _library;

    internal async Task<bool> SaveReadingDataAsync(bool force = false)
    {
        // Wait before snapshotting: slow storage must not accumulate book-sized
        // snapshots for every timer tick or retry click. The next save captures
        // the newest edits after the pending generation has finished.
        while (_saveTask is { IsCompleted: false } pending)
            if (!await pending && !force) return false;
        if (_closed || !_dirty && !force) return true;
        _saveTask = SaveSnapshotAsync();
        return await _saveTask;
    }

    private async Task<bool> SaveSnapshotAsync()
    {
        CapturePosition();
        long edit = _editRevision, revision = ++_saveRevision;
        long libraryVersion = _libraryVersion;
        try
        {
            byte[] state = StateStore.Snapshot(_state);
            byte[]? book = _bookData == null ? null : BookDataRules.Snapshot(_bookData);
            await Task.Run(() => { if (book != null) _library.SaveBook(book, revision, libraryVersion); _store.Save(state, revision); });
            if (!_closed && edit == _editRevision)
            { _dirty = false; UiText.Text(SaveStatusText, "已保存"); RetrySaveButton.Visibility = Visibility.Collapsed; }
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { ShowSaveFailure(error); return false; }
    }

    internal bool TryFlushReadingData()
    {
        if (_closed) return true;
        try
        {
            _libraryReady.GetAwaiter().GetResult();
            if (!_dirty) return true;
            CapturePosition(); long revision = ++_saveRevision;
            byte[] state = StateStore.Snapshot(_state);
            if (_bookData != null) _library.SaveBook(BookDataRules.Snapshot(_bookData), revision, _libraryVersion);
            _store.Save(state, revision); _dirty = false;
            UiText.Text(SaveStatusText, "已保存"); RetrySaveButton.Visibility = Visibility.Collapsed; return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { ShowSaveFailure(error); return false; }
    }

    private void ShowSaveFailure(Exception? error = null)
    {
        if (_closed) return;
        _dirty = true;
        if (error is InvalidDataException or JsonException) UiText.Text(SaveStatusText, "保存失败：{0}", UiText.Error(error.Message));
        else UiText.Text(SaveStatusText, "保存失败，请检查磁盘和写入权限");
        RetrySaveButton.Visibility = Visibility.Visible; Announce(SaveStatusText);
    }
    private async void RetrySave_Click(object sender, RoutedEventArgs e) => await SaveReadingDataAsync(force: true);
    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    { if (e.Mode == PowerModes.Suspend && !_closed) Dispatcher.BeginInvoke(new Action(() => TryFlushReadingData()), DispatcherPriority.Send); }

    private async void Library_Click(object sender, RoutedEventArgs e)
    {
        if (!await SaveReadingDataAsync() || _closed) return;
        _saveTimer.Stop();
        try
        {
            await _libraryReady;
            if (_closed) return;
            var dialog = new LibraryWindow(_library) { Owner = this }; dialog.ShowDialog();
            if (_closed) return;
            _state.Books.RemoveAll(b => dialog.Removed.Contains(b.BookKey)); RefreshRecent();
            if (dialog.Removed.Count > 0) ScheduleSave();
            if (_bookData != null && dialog.Removed.Contains(_bookData.BookId))
            { _bookData = null; _history = null; ScheduleSave(); if (!await GoHomeAsync()) return; }
            if (dialog.Imported && _bookData != null)
            {
                string id = _bookData.BookId;
                EpubBook? book = _book;
                BookData loaded = await Task.Run(() => _library.LoadBook(id));
                if (_closed || book != _book || _bookData?.BookId != id) return;
                _bookData = loaded; _history = _bookData.History;
                _libraryVersion = _library.ChangeVersion;
                _state.Books.RemoveAll(b => b.BookKey == id); _state.Books.Insert(0, _history); RefreshRecent();
                RefreshBookmarks(); RefreshAnnotations(); LoadAppearanceControls(); ApplyTheme(Preferences.Theme);
                await NavigateAsync(_history.ChapterIndex, _history.BlockIndex, _history.Fraction, locator: _history.Locator, addHistory: false, saveCurrent: false);
            }
            if (_closed) return;
            _libraryVersion = _library.ChangeVersion;
            if (dialog.OpenPath is string path) await OpenBookAsync(path, dialog.AssociateId);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { if (!_closed) ShowError(UiText.Get("书库操作失败：{0}", UiText.Error(error.Message))); }
    }

    private void RefreshAnnotations(Guid? selected = null)
    {
        _selectingAnnotations = true;
        AnnotationsList.ItemsSource = _bookData?.Annotations.OrderBy(a => a.ChapterIndex).ThenBy(a => a.Range.Start.Paragraph).ThenBy(a => a.Range.Start.Offset).ToArray();
        if (selected != null) AnnotationsList.SelectedItem = _bookData?.Annotations.FirstOrDefault(a => a.Id == selected);
        _selectingAnnotations = false;
        if (AnnotationsList.SelectedItem is BookAnnotation annotation) LoadAnnotationEditor(annotation);
        UpdateButtons();
    }
    private void LoadAnnotationEditor(BookAnnotation a)
    {
        AnnotationNoteBox.Text = a.Note;
        AnnotationColorCombo.SelectedIndex = a.Color switch { "Green" => 1, "Blue" => 2, "Rose" => 3, _ => 0 };
    }
    private void Annotation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_selectingAnnotations && AnnotationsList.SelectedItem is BookAnnotation a) LoadAnnotationEditor(a); UpdateButtons(); }

    internal async Task AddAnnotationAsync(string note = "")
    {
        if (_bookData == null || _chapter == null || _book == null || _busy || Reader.Selection.IsEmpty) return;
        EpubBook book = _book; ParsedChapter chapter = _chapter; BookData data = _bookData;
        int generation = _readGeneration, index = _chapterIndex;
        CancellationToken token = _readToken?.Token ?? _lifetime.Token;
        int busy = BeginBusy("正在处理选区…");
        try
        {
            if (_bookData.Annotations.Count >= BookDataRules.MaxAnnotations) throw new InvalidDataException("每本书最多保存 128 条批注。");
            var start = DocumentPosition.Read(_renderedBlocks, Reader.Selection.Start);
            var end = DocumentPosition.Read(_renderedBlocks, Reader.Selection.End);
            if (start == null || end == null) throw new InvalidDataException("请在当前正文中选择文字。");
            string color = (AnnotationColorCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Yellow";
            TextRangeLocator range = await RunBoundedWorkAsync(_readingWork, () => TextRangeLocation.Capture(chapter,
                start.Value.Block, start.Value.Offset, end.Value.Block, end.Value.Offset, token), token);
            if (_closed || token.IsCancellationRequested || generation != _readGeneration || book != _book || data != _bookData) return;
            DateTime now = DateTime.UtcNow;
            var a = new BookAnnotation(Guid.NewGuid(), index, book.Chapters[index].Path, range, color, note, now, now);
            CommitAnnotationChange(data.Annotations.Append(a).ToList()); RefreshAnnotations(a.Id); ScheduleSave();
            await RefreshAnnotationDocumentAsync();
            if (!_closed && book == _book && data == _bookData) UiText.Text(StatusText, "已添加高亮，可在批注中编辑笔记。");
        }
        catch (OperationCanceledException) { }
        catch (InvalidDataException error) { if (!_closed) UiText.Text(StatusText, "{0}", UiText.Error(error.Message)); }
        finally { EndBusy(busy); }
    }

    private void CommitAnnotationChange(List<BookAnnotation> annotations)
    {
        if (_bookData == null) return;
        _ = BookDataRules.Snapshot(new BookData
        {
            BookId = _bookData.BookId,
            History = _bookData.History,
            Appearance = _bookData.Appearance,
            Annotations = annotations
        });
        _bookData.Annotations = annotations;
    }
    private async void AddAnnotation_Click(object sender, RoutedEventArgs e) => await AddAnnotationAsync();
    private async Task RefreshAnnotationDocumentAsync()
    {
        NavigationPosition? p = CurrentPosition();
        if (p != null) await NavigateAsync(p.Chapter, p.Block, p.Fraction, locator: p.Locator, addHistory: false);
    }
    private async void SaveAnnotation_Click(object sender, RoutedEventArgs e)
    {
        if (_bookData == null || AnnotationsList.SelectedItem is not BookAnnotation a || _busy) return;
        DateTime now = DateTime.UtcNow;
        BookAnnotation changed = a with
        {
            Note = AnnotationNoteBox.Text,
            Color = (string)((ComboBoxItem)AnnotationColorCombo.SelectedItem).Tag,
            UpdatedUtc = now < a.UpdatedUtc ? a.UpdatedUtc : now
        };
        int index = _bookData.Annotations.FindIndex(n => n.Id == a.Id); if (index < 0) return;
        var annotations = _bookData.Annotations.ToList(); annotations[index] = changed;
        try { CommitAnnotationChange(annotations); }
        catch (InvalidDataException error) { UiText.Text(StatusText, "{0}", UiText.Error(error.Message)); return; }
        RefreshAnnotations(changed.Id); ScheduleSave(); await RefreshAnnotationDocumentAsync();
    }
    private async void DeleteAnnotation_Click(object sender, RoutedEventArgs e)
    {
        if (_bookData == null || AnnotationsList.SelectedItem is not BookAnnotation a || _busy) return;
        _bookData.Annotations.RemoveAll(n => n.Id == a.Id); AnnotationNoteBox.Text = "";
        RefreshAnnotations(); ScheduleSave(); await RefreshAnnotationDocumentAsync();
    }
    private async void JumpAnnotation_Click(object sender, RoutedEventArgs e)
    { if (AnnotationsList.SelectedItem is BookAnnotation a && !_busy) await NavigateAsync(a.ChapterIndex, locator: a.Range.Start); }

    private void LoadAppearanceControls()
    {
        _settingAppearance = true;
        try
        {
            ReaderPreferences p = Preferences;
            FontSizeSlider.Value = p.FontSize; LineSlider.Value = p.LineSpacing; WidthSlider.Value = p.TextWidth;
            ParagraphSpacingSlider.Value = p.ParagraphSpacing; IndentSlider.Value = p.FirstLineIndent;
            if (p.FirstLineIndent < 0) UiText.Text(IndentLabel, "遵循来源");
            else IndentLabel.Text = p.FirstLineIndent.ToString("0.#", CultureInfo.InvariantCulture) + " em";
            FontCombo.SelectedIndex = p.Font switch { "Sans" => 1, "Georgia" => 2, _ => 0 };
            AlignmentCombo.SelectedIndex = p.Alignment switch { "Left" => 1, "Center" => 2, "Right" => 3, "Justify" => 4, _ => 0 };
            BookAppearance.IsChecked = _bookData?.Appearance != null; BookAppearance.IsEnabled = _bookData != null;
        }
        finally { _settingAppearance = false; }
    }
    private void AppearanceScope_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _settingAppearance || _bookData == null) return;
        _bookData.Appearance = BookAppearance.IsChecked == true ? _state.Preferences.Copy() : null;
        LoadAppearanceControls(); ApplyTheme(Preferences.Theme); ApplyTypography(); ScheduleSave();
    }
    private void Alignment_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _settingAppearance || AlignmentCombo.SelectedItem is not ComboBoxItem item) return;
        Preferences.Alignment = (string)item.Tag; ApplyTypography(); ScheduleSave();
    }
    private void AppearancePreset_Click(object sender, RoutedEventArgs e)
    {
        ReaderPreferences p = Preferences;
        if ((string)((Button)sender).Tag == "Novel")
        { p.Font = "Serif"; p.FontSize = 24; p.LineSpacing = 1.8; p.ParagraphSpacing = 0.6; p.FirstLineIndent = 2; p.Alignment = "Justify"; }
        else { p.Font = "Sans"; p.FontSize = 20; p.LineSpacing = 1.5; p.ParagraphSpacing = 0.8; p.FirstLineIndent = 0; p.Alignment = "Left"; }
        LoadAppearanceControls(); ApplyTypography(); ScheduleSave();
    }
    private void ResetBookAppearance_Click(object sender, RoutedEventArgs e)
    { if (_bookData != null) { _bookData.Appearance = null; LoadAppearanceControls(); ApplyTheme(Preferences.Theme); ApplyTypography(); ScheduleSave(); } }

    private void TocFilter_Changed(object sender, TextChangedEventArgs e) { if (_ready) RefreshToc(); }
    private void TocToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not int index) return;
        if (!_tocCollapsed.Add(index)) _tocCollapsed.Remove(index); RefreshToc(); e.Handled = true;
    }
    private void RefreshToc()
    {
        bool selecting = _selectingToc; _selectingToc = true;
        TocList.ItemsSource = _toc?.Visible(TocFilter.Text, _tocCollapsed).Select(i =>
            new TocRow(_toc.Items[i], i, _toc.HasChildren(i), !_tocCollapsed.Contains(i), i == _currentToc)).ToArray();
        _selectingToc = selecting;
    }
    private void UpdateCurrentToc(int? block = null)
    {
        if (_toc == null || _chapter == null) return;
        int current = _toc.Current(_chapterIndex, block ?? _segments[_segmentIndex], _chapter.Anchors);
        if (current == _currentToc) return;
        _currentToc = current; RefreshToc();
    }
}
