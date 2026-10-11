using System.IO;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using QuietRead.Core;

namespace QuietRead;

public partial class LibraryWindow : Window
{
    private readonly LibraryStore _store;
    private LibraryEntry[] _entries = [];
    private bool _ready, _busy;
    internal string? OpenPath { get; private set; }
    internal string? AssociateId { get; private set; }
    internal bool Imported { get; private set; }
    internal HashSet<string> Removed { get; } = [];
    internal LibraryWindow(LibraryStore store)
    {
        _store = store; InitializeComponent(); _ready = true;
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        UiText.Current.PropertyChanged += InterfaceLanguageChanged;
        Closed += (_, _) => UiText.Current.PropertyChanged -= InterfaceLanguageChanged;
        Reload();
    }
    private void InterfaceLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        foreach (Row row in BooksList.Items) row.RefreshLanguage();
    }
    private sealed record Row(LibraryEntry Entry) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void RefreshLanguage() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        public string Title => Entry.Title;
        public string Author => Entry.Author;
        public string Summary => (Entry.ReadingStatus switch { "Unread" => UiText.Get("未读"), "Finished" => UiText.Get("已读"), _ => UiText.Get("在读") }) +
            (Entry.Series.Length > 0 ? " · " + Entry.Series : "") + (Entry.Tags.Length > 0 ? " · " + string.Join("、", Entry.Tags) : "") +
            (Entry.FilePath.Length == 0 ? UiText.Get(" · 文件需关联") : "");
    }
    private Row? Selected => BooksList.SelectedItem as Row;
    internal void Reload()
    { _entries = _store.Entries().ToArray(); FilterRows(); }
    private void Filter_Changed(object sender, RoutedEventArgs e) { if (_ready) FilterRows(); }
    internal void FilterRows()
    {
        string text = FilterBox.Text, status = (StatusFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        IEnumerable<LibraryEntry> rows = _entries.Where(e => (status.Length == 0 || e.ReadingStatus == status) &&
            (text.Length == 0 || (e.Title + " " + e.Author + " " + e.Series + " " + string.Join(" ", e.Tags)).Contains(text, StringComparison.OrdinalIgnoreCase)));
        rows = SortCombo.SelectedIndex switch { 1 => rows.OrderBy(e => e.Title, StringComparer.CurrentCulture), 2 => rows.OrderBy(e => e.Author, StringComparer.CurrentCulture), _ => rows.OrderByDescending(e => e.LastReadUtc) };
        BooksList.ItemsSource = rows.Select(e => new Row(e)).ToArray();
        UiText.Text(LibraryStatus, "显示 {0} / {1} 本。清除最近记录不会删除书库。", BooksList.Items.Count, _entries.Length);
    }
    private void Book_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is not Row row) return;
        LibraryEntry b = row.Entry; TitleBox.Text = b.Title; AuthorBox.Text = b.Author; TagsBox.Text = string.Join(", ", b.Tags); SeriesBox.Text = b.Series;
        ReadingState.SelectedIndex = b.ReadingStatus switch { "Unread" => 0, "Finished" => 2, _ => 1 };
    }
    private async Task Work(Func<Task> action)
    {
        if (_busy) return; _busy = true; IsEnabled = false;
        try { await action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or EpubException or ArgumentException)
        { UiText.Text(LibraryStatus, "操作失败，现有数据已保留：{0}", UiText.Error(error.Message)); }
        finally { _busy = false; IsEnabled = true; }
    }
    private async void SaveMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not Row row) return;
        string title = TitleBox.Text, author = AuthorBox.Text, series = SeriesBox.Text, status = (string)((ComboBoxItem)ReadingState.SelectedItem).Tag;
        string[] tags = TagsBox.Text.Split([',', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        await Work(async () => { await Task.Run(() => _store.UpdateMetadata(row.Entry.BookId, title, author, tags, series, status)); Reload(); UiText.Text(LibraryStatus, "本地元数据已保存。"); });
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not Row row) return;
        if (row.Entry.FilePath.Length == 0) { UiText.Text(LibraryStatus, "尚未关联，请选择「重新关联文件」。"); return; }
        OpenPath = row.Entry.FilePath; AssociateId = row.Entry.BookId; Close();
    }
    private void Associate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not Row row) return;
        if (row.Entry.Legacy && MessageBox.Show(this, UiText.Get("此记录尚未保存书籍文件指纹。首次关联将用所选 EPUB 识别这本书，以后会检查文件内容是否一致。继续吗？"),
            UiText.Get("关联旧记录"), MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        var picker = new OpenFileDialog { Filter = UiText.Get("EPUB 书籍 (*.epub)|*.epub"), CheckFileExists = true };
        if (picker.ShowDialog(this) == true) { OpenPath = picker.FileName; AssociateId = row.Entry.BookId; Close(); }
    }
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not Row row || MessageBox.Show(this, UiText.Get("移除此书的书库条目和阅读数据？请先导出需要保留的批注。\n原 EPUB 文件不会删除。"),
            UiText.Get("移除书库条目"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Work(async () => { await Task.Run(() => _store.Remove(row.Entry.BookId)); Removed.Add(row.Entry.BookId); Reload(); });
    }
    private async Task Export(bool all, bool markdown)
    {
        string? id = Selected?.Entry.BookId;
        if (!all && id == null) { UiText.Text(LibraryStatus, "请先选择一本书。"); return; }
        string extension = markdown ? ".md" : ".json";
        var picker = new SaveFileDialog { DefaultExt = extension, Filter = markdown ? "Markdown (*.md)|*.md" : "JSON (*.json)|*.json", FileName = markdown ? UiText.Get("QuietRead-摘录.md") : all ? UiText.Get("QuietRead-阅读数据.json") : UiText.Get("QuietRead-单书数据.json"), OverwritePrompt = true };
        if (picker.ShowDialog(this) != true) return;
        await Work(async () =>
        {
            string path = LocalFilePath.Check(picker.FileName, extension, mustExist: false);
            string language = UiText.Current.Language;
            await Task.Run(() => WriteExport(path, markdown ? Encoding.UTF8.GetBytes(_store.ExportMarkdown(id!, language)) : _store.ExportBackup(all ? null : id)));
            UiText.Text(LibraryStatus, "已导出；备份中不包含本机文件路径。");
        });
    }
    internal static void WriteExport(string path, byte[] bytes)
    {
        if (bytes.Length > BookDataRules.MaxBackupBytes) throw new InvalidDataException("导出超过 16 MiB。");
        string temp = Path.Combine(Path.GetDirectoryName(path)!, ".QuietRead-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private async void ExportAll_Click(object sender, RoutedEventArgs e) => await Export(all: true, markdown: false);
    private async void ExportBook_Click(object sender, RoutedEventArgs e) => await Export(all: false, markdown: false);
    private async void ExportMarkdown_Click(object sender, RoutedEventArgs e) => await Export(all: false, markdown: true);
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = UiText.Get("QuietRead 阅读数据 (*.json)|*.json"), CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        await Work(async () =>
        {
            string path = LocalFilePath.Check(picker.FileName, ".json");
            byte[] bytes = await Task.Run(() => LibraryStore.ReadBounded(path, BookDataRules.MaxBackupBytes));
            ImportPreview preview = await Task.Run(() => _store.PreviewImport(bytes));
            string details = string.Join("\n", preview.Conflicts.Take(10).Select(c => UiText.Get("• {0}：笔记冲突 {1}{2}{3}{4}", c.Title, c.AnnotationConflicts,
                c.ProgressDiffers ? UiText.Part("，阅读位置不同") : "", c.AppearanceDiffers ? UiText.Part("，外观不同") : "", c.MetadataDiffers ? UiText.Part("，元数据不同") : "")));
            MessageBoxResult choice = MessageBox.Show(this, UiText.Get("新增 {0} 本，已有 {1} 本，冲突 {2} 本。\n{3}\n\n「是」保留现有冲突数据；「否」采用导入数据；「取消」不导入。\n不依据较高页码自动覆盖阅读位置。", preview.NewBooks, preview.ExistingBooks, preview.Conflicts.Count, details),
                UiText.Get("导入预检与冲突选择"), MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
            if (choice == MessageBoxResult.Cancel) return;
            await Task.Run(() => _store.Import(bytes, preferImported: choice == MessageBoxResult.No)); Imported = true; Reload(); UiText.Text(LibraryStatus, "导入成功；尚无本地文件路径的书籍需重新关联。");
        });
    }
}
