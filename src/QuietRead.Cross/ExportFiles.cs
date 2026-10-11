using Avalonia.Platform.Storage;
using QuietRead.Core;

namespace QuietRead.Cross;

public static class ExportFiles
{
    public static async Task WriteAsync(IStorageFile target, byte[] bytes, string extension,
        string dataDirectory, IEnumerable<string> bookPaths, CancellationToken token = default)
    {
        if (extension is not ("json" or "md") ||
            !string.Equals(Path.GetExtension(target.Name), "." + extension, StringComparison.OrdinalIgnoreCase) ||
            target.Name.Any(character => character is '/' or '\\' || char.IsControl(character)))
            throw new InvalidDataException("请选择正确类型的导出文件（JSON 或 Markdown）。");
        if (bytes.Length > BookDataRules.MaxBackupBytes) throw new InvalidDataException("导出超过 16 MiB。");
        token.ThrowIfCancellationRequested();
        string? local = target.TryGetLocalPath();
        if (local != null)
        {
            string path = Path.GetFullPath(local);
            if (!string.Equals(Path.GetExtension(path), "." + extension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请选择正确类型的导出文件（JSON 或 Markdown）。");
            string[] paths = bookPaths.ToArray();
            await Task.Run(() => ValidateLocalPath(path, dataDirectory, paths, token), token).ConfigureAwait(false);
            // Mobile file URLs can require security-scoped provider access and
            // need not grant permission to create/rename a sibling temporary file.
            if (!OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS())
            { await WriteLocalAsync(path, bytes, token).ConfigureAwait(false); return; }
        }
        await WriteNewDocumentAsync(target, bytes, token).ConfigureAwait(false);
    }

    private static void ValidateLocalPath(string path, string dataDirectory, string[] bookPaths, CancellationToken token)
    {
        string canonical = CanonicalPath(path), directory = CanonicalPath(Path.GetFullPath(dataDirectory));
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ?
            StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        if (canonical.Equals(directory, comparison) || canonical.StartsWith(prefix, comparison))
            throw new InvalidDataException("不能覆盖书籍或应用阅读数据，请选择其他导出位置。");
        foreach (string book in bookPaths)
        {
            token.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(book) && canonical.Equals(CanonicalPath(Path.GetFullPath(book)), comparison))
                throw new InvalidDataException("不能覆盖书籍或应用阅读数据，请选择其他导出位置。");
        }
    }

    private static string CanonicalPath(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        if (parent == null || parent == path) return path;
        string resolvedParent = CanonicalPath(parent);
        string candidate = Path.Combine(resolvedParent, Path.GetFileName(path));
        var directory = new DirectoryInfo(candidate);
        return directory.Exists && directory.LinkTarget != null ? directory.ResolveLinkTarget(true)!.FullName : candidate;
    }

    private static async Task WriteLocalAsync(string path, byte[] bytes, CancellationToken token)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".QuietRead-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            {
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
            }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task WriteNewDocumentAsync(IStorageFile target, byte[] bytes, CancellationToken token)
    {
        // A generic document stream has no atomic replace operation. Never open
        // a nonempty existing document for writing, even after an overwrite prompt.
        try
        {
            await using Stream input = await target.OpenReadAsync().ConfigureAwait(false);
            if (await input.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
                throw new InvalidDataException("请选择一个新的导出文件；已有文件不会被覆盖。");
        }
        catch (FileNotFoundException) { }
        token.ThrowIfCancellationRequested();
        try
        {
            await using Stream output = await target.OpenWriteAsync().ConfigureAwait(false);
            await output.WriteAsync(bytes, token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            try { await target.DeleteAsync().ConfigureAwait(false); }
            catch (Exception cleanup)
            { throw new IOException("导出失败，请删除未完成的文件后重试。", new AggregateException(error, cleanup)); }
            throw;
        }
    }
}
