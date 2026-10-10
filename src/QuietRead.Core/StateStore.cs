using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace QuietRead.Core;

public sealed class ReaderPreferences
{
    public string Theme { get; set; } = "Paper";
    public string Font { get; set; } = "Serif";
    public double FontSize { get; set; } = 22;
    public double LineSpacing { get; set; } = 1.8;
    public double TextWidth { get; set; } = 800;
    public bool SidebarVisible { get; set; } = true;
}

public sealed class Bookmark
{
    public int ChapterIndex { get; set; }
    public int BlockIndex { get; set; }
    public double Fraction { get; set; }
    public string Label { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class BookHistory
{
    public string BookKey { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public int ChapterIndex { get; set; }
    public int BlockIndex { get; set; }
    public double Fraction { get; set; }
    public DateTime LastReadUtc { get; set; } = DateTime.UtcNow;
    public List<Bookmark> Bookmarks { get; set; } = [];
}

public sealed class AppState
{
    public int Schema { get; set; } = 1;
    public ReaderPreferences Preferences { get; set; } = new();
    public List<BookHistory> Books { get; set; } = [];

    public void Normalize()
    {
        Preferences ??= new ReaderPreferences();
        if (Preferences.Theme is not ("Paper" or "Sepia" or "Dark")) Preferences.Theme = "Paper";
        if (Preferences.Font is not ("Serif" or "Sans" or "Georgia")) Preferences.Font = "Serif";
        Preferences.FontSize = Clamp(Preferences.FontSize, 14, 36, 22);
        Preferences.LineSpacing = Clamp(Preferences.LineSpacing, 1.2, 2.4, 1.8);
        Preferences.TextWidth = Clamp(Preferences.TextWidth, 520, 1100, 800);
        Books ??= [];
        Books = Books.Where(x => x != null && x.BookKey?.Length == 64 && x.BookKey.All(Uri.IsHexDigit) &&
            !string.IsNullOrEmpty(x.FilePath) && x.FilePath.Length <= 2048)
            .DistinctBy(x => x.BookKey).OrderByDescending(x => x.LastReadUtc).Take(32).ToList();
        foreach (BookHistory book in Books)
        {
            book.Title = Clip(book.Title, 256); book.Author = Clip(book.Author, 256);
            book.ChapterIndex = Math.Clamp(book.ChapterIndex, 0, ReaderLimits.SpineItems - 1);
            book.BlockIndex = Math.Clamp(book.BlockIndex, 0, ReaderLimits.ChapterBlocks - 1);
            book.Fraction = Clamp(book.Fraction, 0, 1, 0);
            book.Bookmarks ??= [];
            book.Bookmarks = book.Bookmarks.Where(x => x != null).Take(64).ToList();
            foreach (Bookmark mark in book.Bookmarks)
            {
                mark.ChapterIndex = Math.Clamp(mark.ChapterIndex, 0, ReaderLimits.SpineItems - 1);
                mark.BlockIndex = Math.Clamp(mark.BlockIndex, 0, ReaderLimits.ChapterBlocks - 1);
                mark.Fraction = Clamp(mark.Fraction, 0, 1, 0); mark.Label = Clip(mark.Label, 100);
            }
        }
    }

    private static double Clamp(double value, double min, double max, double fallback) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    private static string Clip(string? value, int max) => value == null ? "" : value.Length <= max ? value : value[..max];
}

public sealed class StateStore
{
    // Fits 32 books with 64 bounded bookmarks each, even with escaped Unicode.
    public const int MaxStateBytes = 2 * 1024 * 1024;
    private readonly string _path;
    private readonly object _writeGate = new();
    private long _savedRevision;
    private static readonly JsonSerializerOptions Options = new()
    { WriteIndented = false, MaxDepth = 16, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
    public bool LoadFailed { get; private set; }

    public StateStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietRead");
        _path = Path.Combine(directory, "state.json");
    }

    // Hold for the application lifetime, before loading any mutable state.
    // Keep the lock file: deleting it would allow another process to lock a different inode.
    public IDisposable AcquireSession()
    {
        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        return new FileStream(Path.Combine(directory, "state.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    public AppState Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppState();
            using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            if (file.Length > MaxStateBytes) throw new InvalidDataException("Settings exceed size limit.");
            AppState state = JsonSerializer.Deserialize<AppState>(file, Options) ?? new AppState();
            if (state.Schema != 1) throw new InvalidDataException("Unsupported settings version.");
            state.Normalize(); return state;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { LoadFailed = true; return new AppState(); }
    }

    // Take the snapshot on the UI thread; write its immutable bytes in a worker.
    public static byte[] Snapshot(AppState state)
    {
        state.Normalize();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, Options);
        if (bytes.Length > MaxStateBytes) throw new IOException("Settings exceed size limit.");
        return bytes;
    }

    public void Save(byte[] bytes, long revision = 0)
    {
        if (bytes.Length > MaxStateBytes) throw new IOException("Settings exceed size limit.");
        lock (_writeGate)
        {
            if (revision > 0 && revision < _savedRevision) return;
            string directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "state-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes); file.Flush(true); }
                File.Move(temporary, _path, true);
                if (revision > 0) _savedRevision = revision;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
