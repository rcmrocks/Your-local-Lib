using System.Net;
using System.Text;
using YourLocalLib.Core;
using Xunit;

namespace YourLocalLib.Tests;

public class LookupTests : IDisposable
{
    private const string DataApi = "https://openlibrary.org/api/books?bibkeys=ISBN:";
    private const string Editions = "https://openlibrary.org/isbn/";
    private const string Works = "https://openlibrary.org/works/";
    private const string Covers = "https://covers.openlibrary.org/";
    private const string CoversById = "https://covers.openlibrary.org/b/id/";
    private const string CoversByIsbn = "https://covers.openlibrary.org/b/isbn/";
    private const string Google = "https://www.googleapis.com/books/v1/volumes?q=isbn:";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ilt-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private readonly FakeWeb _web = new();
    private readonly LibraryDatabase _db;
    private readonly BookFill _fill;

    public LookupTests()
    {
        Directory.CreateDirectory(_dir);
        _db = LibraryDatabase.Open(Path.Combine(_dir, "library.db"), _clock);
        var web = new WebSource(new HttpClient(_web), _clock);
        _fill = new BookFill(_db, web, new OpenLibraryClient(web), new GoogleBooksClient(web, () => _db.GoogleBooksKey), CoverFolder);
    }

    private string CoverFolder => Path.Combine(_dir, "covers");

    [Fact]
    public async Task Open_Library_supplies_the_title_cover_description_and_tags()
    {
        var shelf = _db.AddLocation("Shelf 1", null);
        _db.SetShelf(shelf.Id);
        _web.Json(DataApi + "9780000000019", """
            {"ISBN:9780000000019": {
              "title": "Guards! Guards!",
              "authors": [{"name": "Terry Pratchett"}],
              "subjects": [{"name": "Fantasy"}, {"name": "Ankh-Morpork (Imaginary place)"}, {"name": "1989"}],
              "cover": {"small": "https://covers.openlibrary.org/b/id/111-S.jpg", "large": "https://covers.openlibrary.org/b/id/111-L.jpg"}
            }}
            """);
        _web.Json(Editions + "9780000000019.json", """{"works": [{"key": "/works/OL45W"}], "covers": [111]}""");
        _web.Json(Works + "OL45W.json", """{"description": {"type": "/type/text", "value": "The Night Watch\r\nmeets a dragon. "}, "covers": [111, 112]}""");
        _web.Image(CoversById + "111-L.jpg");

        var scan = _db.Submit("9780000000019", ScanOrigin.Live);
        Assert.Equal(ScanResult.NotInLibrary, scan.Result);
        var answer = await _fill.FindTitleAsync("9780000000019");
        Assert.Equal(new LookupBook("Guards! Guards!", "Terry Pratchett"), answer.Book);
        Assert.Null(answer.Google);
        Assert.Equal(ScanResult.LookupReady, _db.ApplyLookup(scan.ScanId!.Value, answer.Book, null, answer.NotFound).Result);

        var id = scan.TitleId!.Value;
        Assert.Equal(new FillResult(true, true, true, true, true), await _fill.RunAsync(id, first: answer));
        Assert.Equal("The Night Watch\nmeets a dragon.", _db.Title(id)!.Description);
        Assert.Equal(new[] { "Ankh-Morpork (Imaginary place)", "Fantasy" }, _db.UserTags(id));
        Assert.Equal(SavedCover("9780000000019"), _db.CurrentCoverPath(id));
        Assert.Equal(1, _web.Count(DataApi));
        Assert.Equal(1, _web.Count(Covers));
        Assert.False(_fill.Wants(id, force: true));

        var mort = AddBook("Mort", "9780000000026");
        _web.Json(DataApi + "9780000000026", """{"ISBN:9780000000026": {"title": "Mort", "subjects": ["Death (Fictitious character)"]}}""");
        _web.Json(Editions + "9780000000026.json", """{"description": "Death takes an apprentice.", "works": [{"key": "/works/OL46W"}], "covers": [-1, 333]}""");
        _web.Image(CoversById + "333-L.jpg");

        Assert.Equal(new FillResult(true, true, true, true, true), await _fill.RunAsync(mort));
        Assert.Equal("Death takes an apprentice.", _db.Title(mort)!.Description);
        Assert.Equal(new[] { "Death (Fictitious character)" }, _db.UserTags(mort));
        Assert.Equal(SavedCover("9780000000026"), _db.CurrentCoverPath(mort));
        Assert.Equal(0, _web.Count(Works + "OL46W"));
        Assert.Equal(0, _web.Count(CoversById + "-1"));
        Assert.Equal(0, _web.Count(CoversByIsbn));
        Assert.Equal(0, _web.Count(Google));
    }

    [Fact]
    public async Task Google_Books_fills_what_Open_Library_does_not_know()
    {
        var shelf = _db.AddLocation("Shelf 1", null);
        _db.SetShelf(shelf.Id);
        _web.Json(DataApi + "9780000000033", "{}");
        _web.Json(Google + "9780000000033", """
            {"totalItems": 2, "items": [
              {"volumeInfo": {"title": "A Different Book", "industryIdentifiers": [{"type": "ISBN_13", "identifier": "9781111111113"}]}},
              {"volumeInfo": {
                "title": "The Hollow Hills",
                "authors": ["Mary Stewart"],
                "industryIdentifiers": [{"type": "ISBN_10", "identifier": "0000000035"}],
                "description": "<p>Merlin &amp; Arthur.</p><p>Book two<br>of three.</p>",
                "categories": ["Fiction / Fantasy / General", "Juvenile Fiction"],
                "imageLinks": {"thumbnail": "http://books.google.com/books/content?id=hh1&printsec=frontcover&img=1&zoom=1&edge=curl&source=gbs_api"}
              }}
            ]}
            """);
        _web.Image("https://books.google.com/books/content?id=hh1&printsec=frontcover&img=1&zoom=1&source=gbs_api");

        var scan = _db.Submit("9780000000033", ScanOrigin.Live);
        var answer = await _fill.FindTitleAsync("9780000000033");
        Assert.Equal(new LookupBook("The Hollow Hills", "Mary Stewart"), answer.Book);
        Assert.False(answer.NotFound);
        Assert.Equal(ScanResult.LookupReady, _db.ApplyLookup(scan.ScanId!.Value, answer.Book, null, answer.NotFound).Result);

        var id = scan.TitleId!.Value;
        Assert.Equal("The Hollow Hills", _db.Title(id)!.Title);
        Assert.Equal(new FillResult(true, true, true, true, true), await _fill.RunAsync(id, first: answer));
        Assert.Equal("Merlin & Arthur.\n\nBook two\nof three.", _db.Title(id)!.Description);
        Assert.Equal(new[] { "Fantasy", "Fiction", "Juvenile Fiction" }, _db.UserTags(id));
        Assert.Equal(SavedCover("9780000000033"), _db.CurrentCoverPath(id));
        Assert.Equal(1, _web.Count(DataApi));
        Assert.Equal(1, _web.Count(Google));
        Assert.Equal(1, _web.Count(CoversByIsbn));
        Assert.Equal(0, _web.Count(Editions));
    }

    [Fact]
    public async Task A_refused_lookup_leaves_the_book_open_and_a_later_run_fills_it()
    {
        var id = AddBook("Quiet Atlas", "9780000000040");
        _web.Json(DataApi + "9780000000040", "{}");
        _web.Status(Google, HttpStatusCode.TooManyRequests);

        Assert.Equal(new FillResult(true, false, false, false, false), await _fill.RunAsync(id));
        Assert.True(_fill.Wants(id));
        Assert.Equal("9780000000040", _db.MissingSubjects(id));
        Assert.Equal("9780000000040", _db.MissingCover(id));

        Assert.Equal(new FillResult(true, false, false, false, false), await _fill.RunAsync(id));
        Assert.Equal(1, _web.Count(Google));

        _db.GoogleBooksKey = " abc 123 ";
        Assert.Equal("abc 123", _db.GoogleBooksKey);
        _clock.UtcNow += TimeSpan.FromMinutes(11);
        _web.Json(Google, """{"items": [{"volumeInfo": {"title": "Quiet Atlas", "description": "Maps of nowhere.", "categories": ["Travel"]}}]}""");

        Assert.Equal(new FillResult(true, false, true, true, true), await _fill.RunAsync(id));
        Assert.Contains("&key=abc%20123", _web.Requests.Last(url => url.StartsWith(Google, StringComparison.Ordinal)));
        Assert.Equal("Maps of nowhere.", _db.Title(id)!.Description);
        Assert.Equal(new[] { "Travel" }, _db.UserTags(id));
        Assert.False(_fill.Wants(id));
        Assert.True(_fill.Wants(id, force: true));

        _db.GoogleBooksKey = "  ";
        Assert.Null(_db.GoogleBooksKey);
    }

    [Fact]
    public async Task Your_edits_and_a_removed_cover_are_never_filled()
    {
        var id = AddBook("Kept Plain", "9780000000057");
        Assert.Null(_db.UpdateBookDetails(id, new BookDetails("Kept Plain", "Author", null, null, null, null, null)));
        Assert.Null(_db.SetCoverPath(id, null));
        _web.Json(DataApi + "9780000000057", """
            {"ISBN:9780000000057": {"title": "Kept Plain", "subjects": ["Poetry"], "cover": {"large": "https://covers.openlibrary.org/b/id/57-L.jpg"}}}
            """);
        _web.Json(Editions, """{"description": "Never read."}""");
        _web.Image(CoversById);

        Assert.Equal(new FillResult(true, false, false, true, true), await _fill.RunAsync(id));
        Assert.Equal(new[] { "Poetry" }, _db.UserTags(id));
        Assert.Null(_db.Title(id)!.Description);
        Assert.Null(_db.CurrentCoverPath(id));
        Assert.Equal(0, _web.Count(Editions));
        Assert.Equal(0, _web.Count(Covers));
        Assert.Equal(0, _web.Count(Google));

        Assert.Equal(new FillResult(true, false, false, false, true),
            _db.ApplyFetched(id, "Fetched words.", new[] { "Fantasy" }, Path.Combine(CoverFolder, "other.jpg"), complete: true));
        Assert.Null(_db.Title(id)!.Description);
        Assert.Null(_db.CurrentCoverPath(id));
        Assert.Equal(new[] { "Poetry" }, _db.UserTags(id));
        Assert.False(_fill.Wants(id, force: true));
    }

    [Fact]
    public void Reimport_keeps_a_fetched_description_unless_Libib_has_one()
    {
        _db.Commit(new[] { new LibibRow { Title = "Hello", Creators = "A. Author", EanIsbn13 = "9780000000064", Copies = "1" } });
        var id = _db.Search("9780000000064", "all", null).Single().TitleId;
        Assert.True(_db.ApplyFetched(id, "Fetched words.", null, null, complete: true).DescriptionAdded);

        _db.Commit(new[] { new LibibRow { Title = "Hello", Creators = "A. Author", EanIsbn13 = "9780000000064", Description = "  ", Copies = "1" } });
        Assert.Equal("Fetched words.", _db.Title(id)!.Description);

        _db.Commit(new[] { new LibibRow { Title = "Hello", Creators = "A. Author", EanIsbn13 = "9780000000064", Description = "Libib words.", Copies = "1" } });
        Assert.Equal("Libib words.", _db.Title(id)!.Description);
    }

    [Fact]
    public void Joining_an_edition_carries_its_details_and_undo_takes_them_back()
    {
        var shelf = _db.AddLocation("Shelf 1", null);
        var existing = AddBook("Selected Poems", "9780000000071", "John Keats", shelf.Id);
        _db.SetShelf(shelf.Id);
        var scan = _db.Submit("9780000000088", ScanOrigin.Live);
        Assert.Equal(ScanResult.NotInLibrary, scan.Result);
        var provisional = scan.TitleId!.Value;
        var prompt = _db.ApplyLookup(scan.ScanId!.Value, new LookupBook("Selected Poems", "John Keats"), null);
        Assert.Equal(ScanResult.EditionPrompt, prompt.Result);
        var cover = Path.Combine(CoverFolder, "9780000000088.jpg");
        Assert.True(_db.ApplyFetched(provisional, "Odes and sonnets.", new[] { "Poetry" }, cover, complete: true).Changed);

        var joined = _db.Answer(true);
        Assert.Equal(ScanResult.AddedCopy, joined.Result);
        Assert.Equal(existing, joined.TitleId);
        Assert.Equal(cover, joined.CoverPath);
        Assert.Null(_db.Title(provisional));
        Assert.Equal("Odes and sonnets.", _db.Title(existing)!.Description);
        Assert.Equal(cover, _db.CurrentCoverPath(existing));
        Assert.Equal(new[] { "Poetry" }, _db.UserTags(existing));

        Assert.Equal(ScanResult.Undone, _db.Undo().Result);
        Assert.Null(_db.Title(existing)!.Description);
        Assert.Null(_db.CurrentCoverPath(existing));
        Assert.Empty(_db.UserTags(existing));
        Assert.Equal("9780000000071", _db.MissingSubjects(existing));
        var restored = _db.Search("9780000000088", "all", null).Single();
        Assert.NotEqual(existing, restored.TitleId);
        Assert.Equal("Odes and sonnets.", restored.Description);
        Assert.Equal(cover, restored.CoverPath);
        Assert.Equal(new[] { "Poetry" }, _db.UserTags(restored.TitleId));
        Assert.Null(_db.MissingSubjects(restored.TitleId));
    }

    [Fact]
    public async Task At_most_two_books_are_looked_up_at_once_and_each_only_once()
    {
        var isbns = new[] { "9780000000019", "9780000000026", "9780000000033", "9780000000040", "9780000000057" };
        var ids = isbns.Select((isbn, n) => AddBook("Book " + n, isbn)).ToList();
        foreach (var isbn in isbns)
            _web.Json(DataApi + isbn, "{\"ISBN:" + isbn + "\": {\"title\": \"Book\", \"subjects\": [\"Fantasy\"]}}");
        _web.Delay = TimeSpan.FromMilliseconds(40);

        var tasks = ids.Select(id => _fill.RunAsync(id)).ToList();
        Assert.Same(tasks[0], _fill.RunAsync(ids[0]));
        var results = await Task.WhenAll(tasks);

        Assert.InRange(_web.MaxActive, 1, 2);
        Assert.All(isbns, isbn => Assert.Equal(1, _web.Count(DataApi + isbn)));
        Assert.All(results, result => Assert.True(result.Complete));
        Assert.All(ids, id => Assert.Equal(new[] { "Fantasy" }, _db.UserTags(id)));
        Assert.Equal(FillResult.Skipped, await _fill.RunAsync(ids[0]));
    }

    [Fact]
    public async Task Sample_books_and_books_without_an_isbn_are_never_looked_up()
    {
        _db.LoadSample();
        var plain = _db.InsertTitleRaw("No Barcode", "Author", null, null, 1, null, false);
        var ids = _db.Search(null, "all", null).Select(row => row.TitleId).Distinct().ToList();
        Assert.Contains(plain, ids);
        foreach (var id in ids)
        {
            Assert.False(_fill.Wants(id, force: true));
            Assert.Equal(FillResult.Skipped, await _fill.RunAsync(id, force: true));
        }
        Assert.Empty(_web.Requests);
    }

    [Fact]
    public async Task Look_up_again_asks_once_more_and_an_added_isbn_opens_the_book_to_lookups()
    {
        var id = _db.InsertTitleRaw("Rare Pamphlet", "Author", null, null, 1, null, false);
        Assert.False(_fill.Wants(id));
        Assert.Null(_db.AttachIsbn(id, "9780000000095"));
        Assert.True(_fill.Wants(id));

        _web.Json(DataApi + "9780000000095", "{}");
        Assert.Equal(new FillResult(true, false, false, false, true), await _fill.RunAsync(id));
        Assert.False(_fill.Wants(id));
        Assert.Equal(FillResult.Skipped, await _fill.RunAsync(id));
        Assert.True(_fill.Wants(id, force: true));

        _web.Json(DataApi + "9780000000095", """{"ISBN:9780000000095": {"title": "Rare Pamphlet", "subjects": ["Local history"]}}""");
        Assert.Equal(new FillResult(true, false, false, true, true), await _fill.RunAsync(id, force: true));
        Assert.Equal(new[] { "Local history" }, _db.UserTags(id));
    }

    [Fact]
    public async Task A_scan_no_site_knows_says_so_and_an_offline_scan_can_be_retried()
    {
        var shelf = _db.AddLocation("Shelf 1", null);
        _db.SetShelf(shelf.Id);
        _web.Json(DataApi, "{}");

        var unknown = _db.Submit("9780000000019", ScanOrigin.Live);
        var answer = await _fill.FindTitleAsync("9780000000019");
        Assert.True(answer.NotFound);
        var outcome = _db.ApplyLookup(unknown.ScanId!.Value, answer.Book, null, answer.NotFound);
        Assert.Equal(ScanResult.LookupReady, outcome.Result);
        Assert.StartsWith("No title was found online", outcome.Detail);
        Assert.Equal(2, _web.Requests.Count);

        _web.Offline = true;
        var offline = _db.Submit("9780000000026", ScanOrigin.Live);
        var none = await _fill.FindTitleAsync("9780000000026");
        Assert.False(none.NotFound);
        var retry = _db.ApplyLookup(offline.ScanId!.Value, none.Book, null, none.NotFound);
        Assert.Equal(ScanResult.LookupFailed, retry.Result);
        Assert.Contains("You can retry", retry.Detail);
        var id = offline.TitleId!.Value;
        Assert.Equal(new FillResult(true, false, false, false, false), await _fill.RunAsync(id, first: none));
        Assert.True(_fill.Wants(id));
    }

    [Fact]
    public async Task MusicBrainz_fills_an_album_from_its_barcode()
    {
        var shelf = _db.AddLocation("Records", null);
        _db.SetShelf(shelf.Id);
        const string upc = "024543074052";
        _web.Json("https://musicbrainz.org/ws/2/release/?query=barcode:", """
            {"releases":[{"id":"rel-1","title":"Kind of Blue","date":"1959","artist-credit":[{"name":"Miles Davis"}]}]}
            """);
        _web.Image("https://coverartarchive.org/release/rel-1/front-500");

        var scan = _db.Submit(upc, ScanOrigin.Live, MediaKind.Music);
        var answer = await _fill.FindTitleAsync(scan.Isbn13!, MediaKind.Music);
        Assert.Equal(new LookupBook("Kind of Blue", "Miles Davis"), answer.Book);
        Assert.Equal(ScanResult.LookupReady, _db.ApplyLookup(scan.ScanId!.Value, answer.Book, null, answer.NotFound).Result);
        Assert.Equal(new FillResult(true, true, false, false, true), await _fill.RunAsync(scan.TitleId!.Value, first: answer));
        Assert.Equal("Kind of Blue", _db.Title(scan.TitleId!.Value)!.Title);
        Assert.Equal(Path.Combine(CoverFolder, "0024543074052.jpg"), _db.CurrentCoverPath(scan.TitleId!.Value));
        Assert.DoesNotContain(_web.Requests, url => url.Contains("openlibrary", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_dir, true);
    }

    private long AddBook(string title, string isbn, string creators = "Author", long? shelf = null) =>
        _db.InsertTitleRaw(title, creators, isbn, IsbnRules.ToIsbn10(isbn), 1, shelf, false);

    private string SavedCover(string isbn) => Path.Combine(CoverFolder, isbn + ".jpg");
}

/// <summary>Answers by address prefix, the longest match first. Anything else gets 404, so no test reaches the internet.</summary>
internal sealed class FakeWeb : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
    private readonly List<string> _requests = new();
    private int _active;

    public TimeSpan Delay { get; set; }

    /// <summary>When set, every request waits for this task before it answers.</summary>
    public Task? Hold { get; set; }
    public bool Offline { get; set; }
    public int MaxActive { get; private set; }

    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_gate)
                return _requests.ToList();
        }
    }

    public void Json(string prefix, string json) =>
        Route(prefix, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    public void Status(string prefix, HttpStatusCode status) => Route(prefix, () => new HttpResponseMessage(status));

    public void Image(string prefix) => Route(prefix, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Jpeg()) });

    public int Count(string prefix) => Requests.Count(url => url.StartsWith(prefix, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Func<HttpResponseMessage>? answer;
        lock (_gate)
        {
            _requests.Add(url);
            MaxActive = Math.Max(MaxActive, ++_active);
            answer = _routes.Where(route => url.StartsWith(route.Key, StringComparison.Ordinal))
                .OrderByDescending(route => route.Key.Length)
                .Select(route => route.Value)
                .FirstOrDefault();
        }
        try
        {
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);
            if (Hold is Task hold)
                await hold.WaitAsync(cancellationToken);
            if (Offline)
                throw new HttpRequestException("No network.");
            return answer?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        finally
        {
            lock (_gate)
                _active--;
        }
    }

    private void Route(string prefix, Func<HttpResponseMessage> answer)
    {
        lock (_gate)
            _routes[prefix] = answer;
    }

    private static byte[] Jpeg()
    {
        var jpeg = new byte[2400];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        return jpeg;
    }
}
