using QuietRead.Core;

namespace QuietRead.Cross;

// Storage-provider streams include Android content:// and iOS security-scoped files.
// Copy once into private storage; never persist a URI or trust the provider's length.
public sealed class ManagedBookFiles(string directory)
{
    public string DirectoryPath { get; } = Path.Combine(directory, "epubs");

    public async Task<string> ImportAsync(Stream source, CancellationToken token = default)
    {
        Directory.CreateDirectory(DirectoryPath);
        string temporary = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            {
                await CopyBoundedAsync(source, target, ReaderLimits.ArchiveBytes, token).ConfigureAwait(false);
                await target.FlushAsync(token).ConfigureAwait(false);
                target.Flush(true);
            }
            string id = await Task.Run(() =>
            {
                using EpubBook book = EpubBook.Open(temporary, token);
                // Detect a malformed first chapter before publishing an imported file.
                book.ReadChapter(0, token);
                return book.BookKey;
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            string path = Path.Combine(DirectoryPath, id + ".epub");
            if (File.Exists(path))
            {
                using EpubBook existing = EpubBook.Open(path, token);
                if (existing.BookKey != id) throw new InvalidDataException("已存 EPUB 的文件指纹不匹配，请先备份并检查本机文件。");
                return path;
            }
            File.Move(temporary, path);
            return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task CopyBoundedAsync(Stream source, Stream target, long maximum, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        byte[] buffer = new byte[64 * 1024]; long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            total = checked(total + count);
            if (total > maximum) throw new InvalidDataException("所选文件超过大小限制。");
            await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
    }
}
