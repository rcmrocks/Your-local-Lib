namespace YourLocalLib.Core;

/// <summary>The title answer for a new scan, kept so the fill that follows does not ask the same sites again.</summary>
public sealed record TitleAnswer(LookupBook? Book, OpenLibraryLookup OpenLibrary, GoogleLookup? Google, MusicBrainzLookup? Music = null)
{
    public bool NotFound => Book == null && (
        (Music != null && Music.Status.Answered())
        || (OpenLibrary.Status.Answered() && Google?.Status.Answered() == true));
}

/// <summary>
/// Fills a missing cover, description, and subject tags. Books use Open Library then Google Books.
/// Music uses MusicBrainz. Movies, video games, and board games keep the imported details.
/// </summary>
public sealed class BookFill
{
    private readonly LibraryDatabase _db;
    private readonly WebSource _web;
    private readonly OpenLibraryClient _openLibrary;
    private readonly GoogleBooksClient _google;
    private readonly MusicBrainzClient _music;
    private readonly string _covers;
    private readonly SemaphoreSlim _slots;
    private readonly object _gate = new();
    private readonly Dictionary<(long Generation, long TitleId), Task<FillResult>> _running = new();

    public BookFill(LibraryDatabase db, WebSource web, OpenLibraryClient openLibrary, GoogleBooksClient google, string coversFolder, int parallel = 2, MusicBrainzClient? music = null)
    {
        _db = db;
        _web = web;
        _openLibrary = openLibrary;
        _google = google;
        _music = music ?? new MusicBrainzClient(web);
        _covers = coversFolder;
        _slots = new SemaphoreSlim(Math.Max(1, parallel));
    }

    /// <param name="force">Look again even though an earlier lookup got a full answer.</param>
    public bool Wants(long titleId, bool force = false) =>
        _db.FillNeedsFor(titleId) is { Isbn13: not null, Sample: false, Any: true } needs && (force || !needs.Checked);

    public Task<TitleAnswer> FindTitleAsync(string isbn13) => FindTitleAsync(isbn13, MediaKind.Book);

    public async Task<TitleAnswer> FindTitleAsync(string isbn13, string? kind)
    {
        await _slots.WaitAsync();
        try
        {
            var parsed = MediaKind.Parse(kind);
            if (parsed == MediaKind.Music)
                return await FindMusicAsync(isbn13);
            if (parsed != MediaKind.Book)
                return new TitleAnswer(null, OpenLibraryLookup.None(LookupStatus.NotFound), GoogleLookup.None(LookupStatus.NotFound));
            var openLibrary = await _openLibrary.LookupAsync(isbn13);
            if (openLibrary.Book != null)
                return new TitleAnswer(openLibrary.Book, openLibrary, null);
            var google = await _google.LookupAsync(isbn13);
            return new TitleAnswer(google.Book, openLibrary, google);
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task<TitleAnswer> FindMusicAsync(string barcode)
    {
        var none = OpenLibraryLookup.None(LookupStatus.NotFound);
        var music = await _music.LookupAsync(barcode);
        return new TitleAnswer(music.Book, none, null, music);
    }

    /// <summary>A second request for a book that is already being filled gets the same task.</summary>
    public Task<FillResult> RunAsync(long titleId, bool force = false, TitleAnswer? first = null)
    {
        lock (_gate)
        {
            // A restored library reuses book ids, so a fill still running from before is not this book's fill.
            var key = (_db.Generation, titleId);
            if (_running.TryGetValue(key, out var running) && !running.IsCompleted)
                return running;
            if (!Wants(titleId, force))
                return Task.FromResult(FillResult.Skipped);
            var task = Task.Run(() => FillAsync(key.Generation, titleId, first));
            _running[key] = task;
            task.ContinueWith(done =>
            {
                lock (_gate)
                {
                    if (_running.TryGetValue(key, out var current) && current == done)
                        _running.Remove(key);
                }
            }, TaskScheduler.Default);
            return task;
        }
    }

    private async Task<FillResult> FillAsync(long generation, long titleId, TitleAnswer? first)
    {
        await _slots.WaitAsync();
        try
        {
            return await FillCoreAsync(generation, titleId, first);
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task<FillResult> FillCoreAsync(long generation, long titleId, TitleAnswer? first)
    {
        if (_db.Generation != generation
            || _db.FillNeedsFor(titleId) is not { Isbn13: string isbn, Sample: false, Any: true } needs)
            return FillResult.Skipped;

        if (needs.Kind == MediaKind.Music)
            return await FillMusicAsync(generation, titleId, isbn, needs, first);

        var data = first?.OpenLibrary ?? await _openLibrary.LookupAsync(isbn);
        var complete = data.Status.Answered();
        var subjects = data.Subjects;
        string? description = null;
        var covers = new List<string>();
        if (data.CoverUrl != null)
            covers.Add(data.CoverUrl);

        if (data.Status == LookupStatus.Found && (needs.Description || (needs.Cover && data.CoverUrl == null)))
        {
            var details = await _openLibrary.DetailsAsync(isbn);
            complete &= details.Status.Answered();
            description = details.Description;
            covers.AddRange(details.CoverIds.Select(OpenLibraryClient.CoverUrlById));
        }
        covers.Add(OpenLibraryClient.IsbnCoverUrl(isbn));

        string? coverPath = null;
        var noCoverAnywhere = true;
        if (needs.Cover)
            (coverPath, noCoverAnywhere) = await FirstCoverAsync(covers, isbn);

        var google = first?.Google;
        if ((needs.Description && description == null) || (needs.Subjects && subjects.Count == 0) || (needs.Cover && coverPath == null))
        {
            google ??= await _google.LookupAsync(isbn);
            complete &= google.Status.Answered();
            description ??= google.Description;
            if (subjects.Count == 0)
                subjects = google.Subjects;
            if (needs.Cover && coverPath == null && google.CoverUrl != null)
            {
                var (path, none) = await FirstCoverAsync(new[] { google.CoverUrl }, isbn);
                coverPath = path;
                noCoverAnywhere &= none;
            }
        }
        if (needs.Cover && coverPath == null)
            complete &= noCoverAnywhere;

        return _db.ApplyFetched(titleId, description, subjects, coverPath, complete, generation);
    }

    private async Task<FillResult> FillMusicAsync(long generation, long titleId, string barcode, FillNeeds needs, TitleAnswer? first)
    {
        var music = first?.Music ?? await _music.LookupAsync(barcode);
        var complete = music.Status.Answered();
        string? coverPath = null;
        if (needs.Cover && music.CoverUrl != null)
        {
            var (path, none) = await FirstCoverAsync(new[] { music.CoverUrl }, barcode);
            coverPath = path;
            complete &= path != null || none;
        }
        else if (needs.Cover)
            complete &= music.Status.Answered();
        return _db.ApplyFetched(titleId, null, null, coverPath, complete, generation);
    }

    /// <summary>Tries each address in order. The flag is true only when every one answered that it has no cover.</summary>
    private async Task<(string? Path, bool NoneAnywhere)> FirstCoverAsync(IEnumerable<string> urls, string isbn)
    {
        var none = true;
        foreach (var url in urls.Distinct(StringComparer.Ordinal))
        {
            var saved = await _web.CacheImageAsync(url, _covers, isbn);
            if (saved.Status == LookupStatus.Found)
                return (saved.Path, false);
            if (saved.Status != LookupStatus.NotFound)
                none = false;
        }
        return (null, none);
    }
}
