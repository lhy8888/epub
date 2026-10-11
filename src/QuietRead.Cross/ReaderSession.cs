using QuietRead.Core;

namespace QuietRead.Cross;

// Serialized, platform-independent operations. Invoke I/O on a worker; the view
// only reads results after completion. Disk commits use the existing atomic stores.
public sealed class ReaderSession : IDisposable
{
    private readonly object _gate = new();
    private readonly StateStore _store;
    private readonly IDisposable _lease;
    private EpubBook? _book;
    private BookData? _data;
    private long _version;
    private bool _disposed;
    public AppState State { get; }
    public LibraryStore Library { get; }
    public ManagedBookFiles Files { get; }
    public EpubBook? Book => _book;
    public BookData? Data => _data;
    public ParsedChapter? Chapter { get; private set; }
    public int ChapterIndex { get; private set; }
    public int TargetBlock { get; private set; }
    public int SegmentStart => TargetBlock / ReaderLimits.BlocksPerView * ReaderLimits.BlocksPerView;
    public ReaderPreferences Preferences => _data?.Appearance ?? State.Preferences;
    public ReaderSession(string? directory = null)
    {
        _store = new StateStore(directory); _lease = _store.AcquireSession();
        try
        {
            State = _store.Load();
            if (_store.LoadFailed) throw new InvalidDataException("无法读取阅读设置，请先备份数据后重试。");
            Library = new LibraryStore(_store.DirectoryPath);
            Library.MigrateLegacy(State.Books); Files = new ManagedBookFiles(_store.DirectoryPath);
        }
        catch { _lease.Dispose(); throw; }
    }

    public void Open(string path, CancellationToken token = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed(); Save();
            EpubBook candidate = EpubBook.Open(path, token);
            try
            {
                BookData data = Library.OpenBook(candidate.BookKey, candidate.LegacyBookKey, candidate.FilePath, candidate.Title, candidate.Author);
                int index = Math.Clamp(data.History.ChapterIndex, 0, candidate.Chapters.Count - 1);
                ParsedChapter chapter = candidate.ReadChapter(index, token);
                int block = TextLocation.Resolve(chapter, data.History.Locator, token)?.Block ?? data.History.BlockIndex;
                _book?.Dispose(); _book = candidate; _data = data;
                ChapterIndex = index; Chapter = chapter; TargetBlock = ClampBlock(block);
                RememberPositionCore(TargetBlock, 0); Save();
            }
            catch { if (!ReferenceEquals(_book, candidate)) candidate.Dispose(); throw; }
        }
    }

    public void Navigate(int chapterIndex, int block = 0, ReadingLocator? locator = null, string? fragment = null, CancellationToken token = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed(); if (_book == null) return;
            int index = Math.Clamp(chapterIndex, 0, _book.Chapters.Count - 1);
            ParsedChapter parsed = _book.ReadChapter(index, token);
            if (locator != null) block = TextLocation.Resolve(parsed, locator, token)?.Block ?? block;
            if (!string.IsNullOrEmpty(fragment)) block = parsed.Anchors.GetValueOrDefault(fragment, 0);
            ChapterIndex = index; Chapter = parsed; TargetBlock = ClampBlock(block);
            RememberPositionCore(TargetBlock, 0); Save();
        }
    }

    public void RememberPosition(string id, int chapter, int block, int offset = 0)
    {
        lock (_gate)
        {
            if (_disposed || _book?.BookKey != id || ChapterIndex != chapter) return;
            RememberPositionCore(block, offset); Save();
        }
    }
    private int ClampBlock(int block) => Math.Clamp(block, 0, Math.Max(0, (Chapter?.Blocks.Count ?? 0) - 1));
    private void RememberPositionCore(int block, int offset)
    {
        if (_data == null || Chapter == null) return;
        block = ClampBlock(block); _data.History.ChapterIndex = ChapterIndex;
        _data.History.BlockIndex = block; _data.History.Locator = TextLocation.Capture(Chapter, block, offset);
        _data.History.LastReadUtc = DateTime.UtcNow;
    }
    public SearchOutcome Search(string query, SearchOptions options, CancellationToken token)
    { lock (_gate) { ThrowIfDisposed(); return _book?.Search(query, token, options: options) ?? new SearchOutcome([], 0, false); } }
    public RasterData ReadImage(string path, CancellationToken token, ResourceReadBudget? budget = null)
    { lock (_gate) { ThrowIfDisposed(); return _book!.ReadImage(path, token, budget: budget); } }
    public FootnotePreview? Footnote(LocalLink link, CancellationToken token)
    { lock (_gate) { ThrowIfDisposed(); return _book?.ReadFootnote(link, token); } }

    public void SetLanguage(string language)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var candidate = new AppState { InterfaceLanguage = InterfaceText.NormalizeLanguage(language), Preferences = State.Preferences.Copy(), Books = State.Books.ToList() };
            _store.Save(StateStore.Snapshot(candidate), ++_version); State.InterfaceLanguage = candidate.InterfaceLanguage;
        }
    }
    public void SetAppearance(ReaderPreferences preferences, bool perBook)
    {
        ReaderPreferences.Validate(preferences);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (perBook && _data != null) _data.Appearance = preferences.Copy();
            else { State.Preferences = preferences.Copy(); if (_data != null) _data.Appearance = null; }
            Save();
        }
    }
    public void AddBookmark()
    {
        lock (_gate)
        {
            ThrowIfDisposed(); if (_data == null || Chapter == null || _book == null) return;
            var candidate = BookDataRules.Parse(BookDataRules.Snapshot(_data));
            if (candidate.History.Bookmarks.Count >= 64) throw new InvalidDataException("每本书最多保存 64 个书签。");
            candidate.History.Bookmarks.Add(new Bookmark
            {
                ChapterIndex = ChapterIndex,
                BlockIndex = candidate.History.BlockIndex,
                Locator = candidate.History.Locator,
                Label = _book.Chapters[ChapterIndex].Title[..Math.Min(100, _book.Chapters[ChapterIndex].Title.Length)]
            });
            CommitCandidate(candidate);
        }
    }
    public void RemoveBookmark(int index)
    { lock (_gate) { ThrowIfDisposed(); if (_data == null) return; var c = BookDataRules.Parse(BookDataRules.Snapshot(_data)); c.History.Bookmarks.RemoveAt(index); CommitCandidate(c); } }
    public BookAnnotation? AddAnnotation(int block, int start, int end, string note, string color)
    {
        lock (_gate)
        {
            ThrowIfDisposed(); if (_book == null || _data == null || Chapter == null) return null;
            var range = TextRangeLocation.Capture(Chapter, block, start, block, end);
            var now = DateTime.UtcNow;
            var annotation = new BookAnnotation(Guid.NewGuid(), ChapterIndex, _book.Chapters[ChapterIndex].Path, range, color, note, now, now);
            var c = BookDataRules.Parse(BookDataRules.Snapshot(_data)); c.Annotations.Add(annotation); CommitCandidate(c); return annotation;
        }
    }
    public void EditAnnotation(Guid id, string note, string color)
    {
        lock (_gate)
        {
            ThrowIfDisposed(); if (_data == null) return;
            var c = BookDataRules.Parse(BookDataRules.Snapshot(_data)); int at = c.Annotations.FindIndex(a => a.Id == id);
            if (at < 0) return; c.Annotations[at] = c.Annotations[at] with { Note = note, Color = color, UpdatedUtc = DateTime.UtcNow }; CommitCandidate(c);
        }
    }
    public void DeleteAnnotation(Guid id)
    { lock (_gate) { ThrowIfDisposed(); if (_data == null) return; var c = BookDataRules.Parse(BookDataRules.Snapshot(_data)); c.Annotations.RemoveAll(a => a.Id == id); CommitCandidate(c); } }
    private void CommitCandidate(BookData candidate)
    {
        byte[] snapshot = BookDataRules.Snapshot(candidate);
        Library.SaveBook(snapshot, ++_version, Library.ChangeVersion); _data = candidate;
    }
    public void ImportBackup(byte[] bytes, bool preferImported)
    {
        lock (_gate)
        {
            ThrowIfDisposed(); Save(); Library.Import(bytes, preferImported);
            if (_book != null && _data != null) { _data = Library.LoadBook(_data.BookId); Navigate(_data.History.ChapterIndex, _data.History.BlockIndex, _data.History.Locator); }
        }
    }
    public void RemoveBook(string id)
    {
        lock (_gate)
        {
            ThrowIfDisposed(); Library.Remove(id);
            if (_data?.BookId == id) { _data = null; _book?.Dispose(); _book = null; Chapter = null; }
            // Keep the private EPUB, as removal only removes reading data.
            State.Books.RemoveAll(b => b.BookKey == id); Save();
        }
    }
    public void Save()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_data != null) Library.SaveBook(BookDataRules.Snapshot(_data), ++_version, Library.ChangeVersion);
            _store.Save(StateStore.Snapshot(State), ++_version);
        }
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose()
    { lock (_gate) { if (_disposed) return; Save(); _disposed = true; _book?.Dispose(); _lease.Dispose(); } }
}
