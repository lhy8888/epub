using System.IO;
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
        Reload();
    }
    private sealed record Row(LibraryEntry Entry)
    {
        public string Title => Entry.Title;
        public string Author => Entry.Author;
        public string Summary => (Entry.ReadingStatus switch { "Unread" => "未读", "Finished" => "已读", _ => "在读" }) +
            (Entry.Series.Length > 0 ? " · " + Entry.Series : "") + (Entry.Tags.Length > 0 ? " · " + string.Join("、", Entry.Tags) : "") +
            (Entry.FilePath.Length == 0 ? " · 文件需关联" : "");
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
        LibraryStatus.Text = $"显示 {BooksList.Items.Count} / {_entries.Length} 本。清除最近记录不会删除书库。";
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
        { LibraryStatus.Text = "操作失败，现有数据已保留：" + error.Message; }
        finally { _busy = false; IsEnabled = true; }
    }
    private async void SaveMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not Row row) return;
        string title = TitleBox.Text, author = AuthorBox.Text, series = SeriesBox.Text, status = (string)((ComboBoxItem)ReadingState.SelectedItem).Tag;
        string[] tags = TagsBox.Text.Split([',', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        await Work(async () => { await Task.Run(() => _store.UpdateMetadata(row.Entry.BookId, title, author, tags, series, status)); Reload(); LibraryStatus.Text = "本地元数据已保存。"; });
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not Row row) return;
        if (row.Entry.FilePath.Length == 0) { LibraryStatus.Text = "尚未关联，请选择「重新关联文件」。"; return; }
        OpenPath = row.Entry.FilePath; AssociateId = row.Entry.BookId; Close();
    }
    private void Associate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not Row row) return;
        if (row.Entry.Legacy && MessageBox.Show(this, "此记录尚未计算内容身份。首次关联将以您选择的 EPUB 建立身份，之后严格按内容校验。继续吗？",
            "关联旧记录", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        var picker = new OpenFileDialog { Filter = "EPUB 书籍 (*.epub)|*.epub", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) { OpenPath = picker.FileName; AssociateId = row.Entry.BookId; Close(); }
    }
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not Row row || MessageBox.Show(this, "移除此书的书库条目和阅读数据？请先导出需要保留的批注。\n原 EPUB 文件不会删除。",
            "移除书库条目", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Work(async () => { await Task.Run(() => _store.Remove(row.Entry.BookId)); Removed.Add(row.Entry.BookId); Reload(); });
    }
    private async Task Export(bool all, bool markdown)
    {
        string? id = Selected?.Entry.BookId;
        if (!all && id == null) { LibraryStatus.Text = "请先选择一本书。"; return; }
        string extension = markdown ? ".md" : ".json";
        var picker = new SaveFileDialog { DefaultExt = extension, Filter = markdown ? "Markdown (*.md)|*.md" : "JSON (*.json)|*.json", FileName = markdown ? "QuietRead-摘录.md" : all ? "QuietRead-阅读数据.json" : "QuietRead-单书数据.json", OverwritePrompt = true };
        if (picker.ShowDialog(this) != true) return;
        await Work(async () =>
        {
            string path = LocalFilePath.Check(picker.FileName, extension, mustExist: false);
            await Task.Run(() => WriteExport(path, markdown ? Encoding.UTF8.GetBytes(_store.ExportMarkdown(id!)) : _store.ExportBackup(all ? null : id)));
            LibraryStatus.Text = "已导出；备份中不包含设备文件路径。";
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
        var picker = new OpenFileDialog { Filter = "QuietRead 阅读数据 (*.json)|*.json", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        await Work(async () =>
        {
            string path = LocalFilePath.Check(picker.FileName, ".json");
            byte[] bytes = await Task.Run(() => LibraryStore.ReadBounded(path, BookDataRules.MaxBackupBytes));
            ImportPreview preview = await Task.Run(() => _store.PreviewImport(bytes));
            string details = string.Join("\n", preview.Conflicts.Take(10).Select(c => $"• {c.Title}：笔记冲突 {c.AnnotationConflicts}" + (c.ProgressDiffers ? "，阅读位置不同" : "") + (c.AppearanceDiffers ? "，外观不同" : "") + (c.MetadataDiffers ? "，元数据不同" : "")));
            MessageBoxResult choice = MessageBox.Show(this, $"新增 {preview.NewBooks} 本，已有 {preview.ExistingBooks} 本，冲突 {preview.Conflicts.Count} 本。\n{details}\n\n「是」保留现有冲突数据；「否」采用导入数据；「取消」不导入。\n不依据较高页码自动覆盖阅读位置。",
                "导入预检与冲突选择", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
            if (choice == MessageBoxResult.Cancel) return;
            await Task.Run(() => _store.Import(bytes, preferImported: choice == MessageBoxResult.No)); Imported = true; Reload(); LibraryStatus.Text = "导入成功；尚无本地文件路径的书籍需重新关联。";
        });
    }
}
