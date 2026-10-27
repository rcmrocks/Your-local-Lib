using YourLocalLib.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace YourLocalLib.Tests;

public class IsbnTests
{
    [Theory]
    [InlineData("978-0-141-43951-8", "9780141439518")]
    [InlineData("9780141439518", "9780141439518")]
    [InlineData("]E09780141439518", "9780141439518")]
    [InlineData("978014143951812345", "9780141439518")]
    [InlineData("9791234567896", "9791234567896")]
    public void Cleans_isbn13(string raw, string expected) =>
        Assert.Equal(expected, IsbnRules.Parse(raw).Isbn13);

    [Fact]
    public void Converts_isbn10_with_x()
    {
        var parsed = IsbnRules.Parse("080442957X");
        Assert.True(parsed.IsIsbn);
        Assert.Equal("9780804429573", parsed.Isbn13);
        Assert.Equal("080442957X", parsed.Isbn10);
    }

    [Theory]
    [InlineData("9780141439519")]
    [InlineData("0043920352")]
    [InlineData("$4.99")]
    public void Rejects_non_isbns(string raw)
    {
        var parsed = IsbnRules.Parse(raw);
        Assert.False(parsed.IsIsbn);
        Assert.Equal("Not an ISBN or UPC.", parsed.Message);
    }

    [Fact]
    public void Accepts_a_upc_and_an_ean()
    {
        var upc = IsbnRules.Parse("024543074052");
        Assert.True(upc.IsCode);
        Assert.False(upc.IsIsbn);
        Assert.Equal("0024543074052", upc.Isbn13);
        Assert.Equal("024543074052", upc.Upc);

        var ean = IsbnRules.Parse("1234567890128");
        Assert.True(ean.IsCode);
        Assert.False(ean.IsIsbn);
        Assert.Equal("1234567890128", ean.Isbn13);
    }

    [Fact]
    public void Reads_a_shelf_code()
    {
        var parsed = IsbnRules.Parse("loc-0007");
        Assert.True(parsed.IsShelf);
        Assert.Equal("LOC-0007", parsed.ShelfCode);
    }
}

public class KeyRouterTests
{
    [Fact]
    public void Enter_never_accepts_a_prompt() =>
        Assert.Equal(KeyDecision.None, KeyRouter.Decide("Enter", false, false, true));

    [Fact]
    public void Y_and_n_answer_a_prompt()
    {
        Assert.Equal(KeyDecision.AcceptPrompt, KeyRouter.Decide("Y", false, false, true));
        Assert.Equal(KeyDecision.DeclinePrompt, KeyRouter.Decide("N", false, false, true));
    }

    [Fact]
    public void Escape_dismisses_a_prompt_before_clearing() =>
        Assert.Equal(KeyDecision.DismissPrompt, KeyRouter.Decide("Escape", false, false, true));

    [Fact]
    public void Ctrl_z_undoes_only_outside_a_text_box()
    {
        Assert.Equal(KeyDecision.UndoScan, KeyRouter.Decide("Z", true, false, false));
        Assert.Equal(KeyDecision.None, KeyRouter.Decide("Z", true, true, false));
        Assert.Equal(KeyDecision.OpenSearch, KeyRouter.Decide("F", true, true, false));
    }

    [Fact]
    public void Off_the_scan_screen_only_ctrl_f_does_anything()
    {
        foreach (var promptOpen in new[] { false, true })
        {
            Assert.Equal(KeyDecision.None, KeyRouter.Decide("Z", true, false, promptOpen, onScanScreen: false));
            Assert.Equal(KeyDecision.None, KeyRouter.Decide("Escape", false, false, promptOpen, onScanScreen: false));
            Assert.Equal(KeyDecision.None, KeyRouter.Decide("Y", false, false, promptOpen, onScanScreen: false));
            Assert.Equal(KeyDecision.None, KeyRouter.Decide("N", false, false, promptOpen, onScanScreen: false));
            Assert.Equal(KeyDecision.None, KeyRouter.Decide("Enter", false, false, promptOpen, onScanScreen: false));
            Assert.Equal(KeyDecision.OpenSearch, KeyRouter.Decide("F", true, false, promptOpen, onScanScreen: false));
            Assert.Equal(KeyDecision.OpenSearch, KeyRouter.Decide("F", true, true, promptOpen, onScanScreen: false));
        }
        Assert.Equal(KeyDecision.UndoScan, KeyRouter.Decide("Z", true, false, false, onScanScreen: true));
    }
}

public class ImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ilt-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDatabase _db;

    public ImportTests()
    {
        Directory.CreateDirectory(_dir);
        _db = LibraryDatabase.Open(Path.Combine(_dir, "library.db"));
    }

    [Fact]
    public void Parses_the_real_export_shape()
    {
        var rows = LibibCsv.ReadFile(Path.Combine(AppContext.BaseDirectory, "libib-export.csv"));
        var description = rows.Single(r => r.Id == "aaa1111111").Description;
        Assert.Contains("Line one", description);
        Assert.Contains("Line two, still quoted", description);
        Assert.Contains("\n", description);

        var preview = _db.Preview(rows);
        Assert.Equal(6, preview.Titles);
        Assert.Equal(1, preview.MissingIsbn);
        Assert.Equal(1, preview.RepeatedIsbnGroups);
        Assert.Equal(0, preview.SkippedNonBooks);
        Assert.True(preview.CanCommit);
        Assert.Contains("Test Collection", preview.Collections);
        Assert.Equal(5, preview.Kinds.Single(k => k.Kind == MediaKind.Book).Count);
        Assert.Equal(1, preview.Kinds.Single(k => k.Kind == MediaKind.Movie).Count);

        _db.Commit(rows);
        var quoted = _db.Search("Quoted Title", "all", null).First();
        Assert.Equal("9780141439518", quoted.Isbn13);
        Assert.Equal(3, _db.Search("9780141439518", "all", null).Count(r => r.CopyId != null));

        var conflict = _db.Title(_db.Search("Conflicting Ten", "all", null).First().TitleId)!;
        Assert.Equal("9780440224259", conflict.Isbn13);
        Assert.NotEqual("9780044022428", conflict.Isbn13);

        var badTen = _db.Title(_db.Search("Bad Ten", "all", null).First().TitleId)!;
        Assert.Equal("9780439203524", badTen.Isbn13);

        var thirteen = _db.Search("9791234567896", "all", null).Single();
        Assert.Equal("Only Thirteen", thirteen.Title);
        Assert.Contains("Omnibus Sample", _db.Search(null, "needs-isbn", null).Select(r => r.Title));
        var movie = _db.Search("Not a Book", "all", null).Single();
        Assert.Equal(MediaKind.Movie, movie.Kind);
        Assert.Equal("Not a Book", _db.Search(null, MediaKind.Movie, null).Single().Title);
    }

    [Fact]
    public void Accepts_a_bom_and_blocks_a_bad_copy_count()
    {
        var bom = "\uFEFFtitle,ean_isbn13,copies\nHello,9780141439518,1\n";
        var rows = LibibCsv.Read(bom);
        Assert.Equal("Hello", rows[0].Title);

        var bad = "title,copies\nHello,many\n";
        var preview = _db.Preview(LibibCsv.Read(bad));
        Assert.False(preview.CanCommit);
        Assert.Contains("whole number", preview.Errors[0]);
    }

    [Fact]
    public void Reimport_keeps_the_shelf_and_matches_a_libib_id()
    {
        var rows = LibibCsv.ReadFile(Path.Combine(AppContext.BaseDirectory, "libib-export.csv"));
        _db.Commit(rows);
        var shelf = _db.AddLocation("Shelf 1", null);
        var omnibus = _db.Search("Omnibus Sample", "all", null).Single();
        _db.MoveCopy(omnibus.CopyId!.Value, shelf.Id);
        var quoted = _db.Search("9780141439518", "all", null).First();
        _db.MoveCopy(quoted.CopyId!.Value, shelf.Id);

        var again = rows.Select(row => row.Id == "ddd4444444"
            ? new LibibRow
            {
                Id = row.Id,
                ItemType = row.ItemType,
                Title = "Omnibus Sample Revised",
                Creators = row.Creators,
                Collection = row.Collection,
                Copies = "1"
            }
            : row).ToList();
        _db.Commit(again);

        var revised = _db.Search("Omnibus Sample Revised", "all", null).Single();
        Assert.Equal(shelf.Id, revised.LocationId);
        var stillThere = _db.Search("9780141439518", "all", null).First(r => r.CopyId == quoted.CopyId);
        Assert.Equal(shelf.Id, stillThere.LocationId);
        Assert.Equal(3, _db.Search("9780141439518", "all", null).Count(r => r.CopyId != null));
    }

    [Fact]
    public void Imports_movies_music_games_and_a_upc()
    {
        var rows = new[]
        {
            new LibibRow { Title = "Arrival", Creators = "Denis Villeneuve", ItemType = "movie", UpcIsbn10 = "024543074052", Ensemble = "Amy Adams", AspectRatio = "2.39:1", NumberOfDiscs = "1", LengthOf = "116", Copies = "1" },
            new LibibRow { Title = "Kind of Blue", Creators = "Miles Davis", ItemType = "record", EanIsbn13 = "0075713494224", NumberOfDiscs = "1", LengthOf = "46", Copies = "1" },
            new LibibRow { Title = "Hades", Creators = "Supergiant", ItemType = "videogame", UpcIsbn10 = "081194903247", Esrb = "E10+", Copies = "1" },
            new LibibRow { Title = "Ticket to Ride", Creators = "Alan R. Moon", ItemType = "boardgame", Artists = "Days of Wonder", NumberOfPlayers = "2-5", AgeGroup = "8+", Copies = "1" }
        };
        var preview = _db.Preview(rows);
        Assert.True(preview.CanCommit);
        Assert.Equal(4, preview.Titles);
        Assert.Equal(4, preview.Kinds.Count);

        _db.Commit(rows);
        var movie = _db.Search("Arrival", MediaKind.Movie, null).Single();
        Assert.Equal(MediaKind.Movie, movie.Kind);
        Assert.Equal("024543074052", movie.Upc);
        Assert.Equal("0024543074052", movie.Isbn13);
        var detail = _db.Title(movie.TitleId)!;
        Assert.Equal("Amy Adams", detail.Ensemble);
        Assert.Equal("2.39:1", detail.AspectRatio);
        Assert.Equal("116", detail.LengthOf);

        var album = _db.Search("Kind of Blue", MediaKind.Music, null).Single();
        Assert.Equal(MediaKind.Music, album.Kind);
        Assert.Equal("0075713494224", album.Isbn13);

        var game = _db.Search("Hades", MediaKind.VideoGame, null).Single();
        Assert.Equal("E10+", _db.Title(game.TitleId)!.Esrb);

        var board = _db.Search("Ticket to Ride", MediaKind.BoardGame, null).Single();
        Assert.Equal("2-5", _db.Title(board.TitleId)!.NumberOfPlayers);
        Assert.Equal("8+", _db.Title(board.TitleId)!.AgeGroup);
        Assert.Equal("Days of Wonder", _db.Title(board.TitleId)!.Artists);

        var shelf = _db.AddLocation("Media", null);
        _db.SetShelf(shelf.Id);
        var scan = _db.Submit("024543074052", ScanOrigin.Live, MediaKind.Movie);
        Assert.Equal(ScanResult.Placed, scan.Result);
        Assert.Equal("Arrival", _db.Search("024543074052", "all", null).Single().Title);
    }

    [Fact]
    public void A_movie_csv_without_item_type_is_still_movies()
    {
        var csv = "title,creators,upc_isbn10,ensemble,aspect_ratio,copies\nHeat,Michael Mann,012345678905,Al Pacino,2.39:1,1\n";
        var file = LibibCsv.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv)));
        Assert.Equal(MediaKind.Movie, file.GuessedKind);
        _db.Commit(file.Rows, file.GuessedKind, file.Headers);
        Assert.Equal(MediaKind.Movie, _db.Search("Heat", "all", null).Single().Kind);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_dir, true);
    }
}

public class ScanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ilt-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private readonly LibraryDatabase _db;

    public ScanTests()
    {
        Directory.CreateDirectory(_dir);
        _db = LibraryDatabase.Open(Path.Combine(_dir, "library.db"), _clock);
        _db.DebounceMs = 400;
    }

    [Fact]
    public void Place_assigns_unlocated_copies_and_leaves_shelved_ones()
    {
        var home = _db.AddLocation("Home", null);
        var shelf = _db.AddLocation("Shelf 1", home.Id);
        var other = _db.AddLocation("Shelf 2", home.Id);
        _db.InsertForTest("Dune", "Frank Herbert", "9780000000019", 2, null);
        _db.InsertForTest("Placed Already", "Frank Herbert", "9780000000026", 1, other.Id);
        _db.SwitchMode(SessionMode.Place, false);
        _db.SetShelf(shelf.Id);

        var first = _db.Submit("9780000000019", ScanOrigin.Live);
        Assert.Equal(ScanResult.Placed, first.Result);
        _clock.UtcNow = _clock.UtcNow.AddMilliseconds(500);
        var second = _db.Submit("9780000000019", ScanOrigin.Dump);
        Assert.Equal(ScanResult.Placed, second.Result);
        _clock.UtcNow = _clock.UtcNow.AddMilliseconds(500);
        var extra = _db.Submit("9780000000019", ScanOrigin.Live);
        Assert.Equal(ScanResult.Extra, extra.Result);

        var left = _db.Submit("9780000000026", ScanOrigin.Live);
        Assert.Equal(ScanResult.AlreadyShelved, left.Result);
        Assert.Contains("Shelf 2", left.Status);
        Assert.Equal(other.Id, _db.Search("Placed Already", "all", null).Single().LocationId);
    }

    [Fact]
    public void Live_double_read_is_held_and_a_dump_counts_twice()
    {
        var shelf = _db.AddLocation("Shelf 1", null);
        _db.InsertForTest("Dune", "Frank Herbert", "9780000000019", 2, null);
        _db.SetShelf(shelf.Id);
        _db.Submit("9780000000019", ScanOrigin.Live);
        _clock.UtcNow = _clock.UtcNow.AddMilliseconds(200);
        var held = _db.Submit("9780000000019", ScanOrigin.Live);
        Assert.Equal(ScanResult.Held, held.Result);
        Assert.Single(_db.Search("Dune", "shelf", shelf.Id));

        _db.Submit("9780000000019", ScanOrigin.Dump);
        Assert.Equal(2, _db.Search("Dune", "shelf", shelf.Id).Count());
    }

    [Fact]
    public void Shelf_audit_does_not_mark_another_shelf_missing_and_enter_does_not_move()
    {
        var a = _db.AddLocation("Shelf A", null);
        var b = _db.AddLocation("Shelf B", null);
        _db.InsertForTest("Here", "Author", "9780000000019", 1, a.Id);
        _db.InsertForTest("There", "Author", "9780000000026", 1, b.Id);
        _db.InsertForTest("Away", "Author", "9780000000033", 1, a.Id);
        _db.SwitchMode(SessionMode.Shelf, false);
        _db.SetShelf(a.Id);
        var session = _db.ActiveSession()!;

        Assert.Equal(ScanResult.OnShelf, _db.Submit("9780000000019", ScanOrigin.Live).Result);
        Assert.DoesNotContain(_db.NotScannedOnShelf(session.Id, a.Id), row => row.Title == "Here");
        Assert.Contains(_db.NotScannedOnShelf(session.Id, a.Id), row => row.Title == "Away");
        Assert.Contains(_db.NotScannedOnShelf(session.Id, b.Id), row => row.Title == "There");

        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        _db.SetShelf(b.Id);
        var prompt = _db.Submit("9780000000033", ScanOrigin.Live);
        Assert.Equal(ScanResult.MovePrompt, prompt.Result);
        Assert.True(prompt.Prompt);
        Assert.Equal(KeyDecision.None, KeyRouter.Decide("Enter", false, false, true));
        _db.Answer(false);
        Assert.Equal(a.Id, _db.Search("9780000000033", "all", null).Single().LocationId);
        Assert.Contains(_db.Disagreements(session.Id, b.Id), row => row.Status.Contains("still recorded"));

        _db.SetShelf(a.Id);
        _db.Submit("978-0-00-000002-6", ScanOrigin.Live);
        _db.Answer(true);
        Assert.Equal(a.Id, _db.Search("There", "all", null).Single().LocationId);
        var undone = _db.Undo();
        Assert.Equal(ScanResult.Undone, undone.Result);
        Assert.Equal(b.Id, _db.Search("There", "all", null).Single().LocationId);
    }

    [Fact]
    public void A_new_barcode_answers_not_now()
    {
        var a = _db.AddLocation("Shelf A", null);
        var b = _db.AddLocation("Shelf B", null);
        _db.InsertForTest("Here", "Author", "9780000000019", 1, a.Id);
        _db.InsertForTest("Other", "Author", "9780000000026", 1, null);
        _db.SwitchMode(SessionMode.Shelf, false);
        _db.SetShelf(b.Id);
        Assert.Equal(ScanResult.MovePrompt, _db.Submit("9780000000019", ScanOrigin.Live).Result);
        var next = _db.Submit("9780000000026", ScanOrigin.Live);
        Assert.Equal(ScanResult.PlacePrompt, next.Result);
        Assert.Equal(a.Id, _db.Search("Here", "all", null).Single().LocationId);
    }

    [Fact]
    public void Move_without_asking_moves_and_places_and_undo_puts_the_book_back()
    {
        const string noQuestion = "There is no question to answer.";
        var a = _db.AddLocation("Shelf A", null);
        var b = _db.AddLocation("Shelf B", null);
        _db.InsertForTest("Away", "Author", "9780000000019", 1, a.Id);
        _db.InsertForTest("Loose", "Author", "9780000000026", 1, null);
        Assert.False(_db.AutoMove);
        _db.SetShelf(a.Id);
        Assert.Equal(ScanResult.AlreadyShelved, _db.Submit("9780000000019", ScanOrigin.Live).Result);
        var confirmed = _db.Search("Away", "all", null).Single().LastConfirmedUtc;
        Assert.NotNull(confirmed);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        _db.AutoMove = true;
        _db.SwitchMode(SessionMode.Shelf, true);
        _db.SetShelf(b.Id);
        var moved = _db.Submit("9780000000019", ScanOrigin.Live);
        Assert.Equal(ScanResult.Moved, moved.Result);
        Assert.False(moved.Prompt);
        Assert.False(moved.Problem);
        Assert.True(moved.Chime);
        Assert.Equal("Moved here", moved.Status);
        Assert.Equal("Moved from Shelf A to Shelf B.", moved.Detail);
        Assert.Equal("Moved", moved.Speech);
        Assert.Equal(noQuestion, _db.Answer(true).Status);
        var away = _db.Search("Away", "all", null).Single();
        Assert.Equal(b.Id, away.LocationId);
        Assert.NotEqual(confirmed, away.LastConfirmedUtc);

        var placed = _db.Submit("9780000000026", ScanOrigin.Live);
        Assert.Equal(ScanResult.Placed, placed.Result);
        Assert.False(placed.Prompt);
        Assert.Equal("Placed", placed.Speech);
        Assert.Equal(noQuestion, _db.Answer(true).Status);
        Assert.Equal(b.Id, _db.Search("Loose", "all", null).Single().LocationId);
        Assert.Equal((2, 0, 0), _db.SessionCounts(_db.ActiveSession()!.Id));

        Assert.Equal(ScanResult.Undone, _db.Undo().Result);
        Assert.Null(_db.Search("Loose", "all", null).Single().LocationId);
        Assert.Equal(ScanResult.Undone, _db.Undo().Result);
        var back = _db.Search("Away", "all", null).Single();
        Assert.Equal(a.Id, back.LocationId);
        Assert.Equal(confirmed, back.LastConfirmedUtc);
    }

    [Fact]
    public void Stored_scans_all_move_without_asking_and_a_new_edition_still_asks()
    {
        var a = _db.AddLocation("Shelf A", null);
        var b = _db.AddLocation("Shelf B", null);
        _db.InsertForTest("First", "Author", "9780000000019", 1, a.Id);
        _db.InsertForTest("Second", "Author", "9780000000026", 1, a.Id);
        _db.InsertForTest("Third", "Author", "9780000000033", 1, null);
        _db.InsertForTest("Selected Poems", "John Keats", "9780000000040", 1, a.Id);
        _db.AutoMove = true;
        _db.SwitchMode(SessionMode.Shelf, false);
        _db.SetShelf(b.Id);

        foreach (var isbn in new[] { "9780000000019", "9780000000026", "9780000000033" })
        {
            var outcome = _db.Submit(isbn, ScanOrigin.Dump);
            Assert.False(outcome.Prompt);
            Assert.Contains(outcome.Result, new[] { ScanResult.Moved, ScanResult.Placed });
        }
        Assert.Equal(new[] { "First", "Second", "Third" }, _db.Search(null, "shelf", b.Id).Select(r => r.Title).OrderBy(t => t));

        var scan = _db.Submit("9780000000057", ScanOrigin.Dump);
        Assert.Equal(ScanResult.NotInLibrary, scan.Result);
        var edition = _db.ApplyLookup(scan.ScanId!.Value, new LookupBook("Selected Poems", "John Keats"), null);
        Assert.Equal(ScanResult.EditionPrompt, edition.Result);
        Assert.True(edition.Prompt);
        Assert.Equal(ScanResult.AddedCopy, _db.Answer(true).Result);
        Assert.Equal(new long?[] { a.Id, b.Id }, _db.Search("Selected Poems", "all", null).Select(r => r.LocationId).OrderBy(id => id));
    }

    [Fact]
    public void Move_without_asking_leaves_place_mode_and_an_open_question_alone()
    {
        var a = _db.AddLocation("Shelf A", null);
        var b = _db.AddLocation("Shelf B", null);
        _db.InsertForTest("Asked", "Author", "9780000000019", 1, a.Id);
        _db.InsertForTest("Shelved", "Author", "9780000000026", 1, a.Id);
        _db.InsertForTest("Loose", "Author", "9780000000033", 1, null);
        _db.SwitchMode(SessionMode.Shelf, false);
        _db.SetShelf(b.Id);
        Assert.Equal(ScanResult.MovePrompt, _db.Submit("9780000000019", ScanOrigin.Live).Result);

        _db.AutoMove = true;
        Assert.Equal(a.Id, _db.Search("Asked", "all", null).Single().LocationId);
        Assert.Equal(ScanResult.SeenUnchanged, _db.Answer(false).Result);
        Assert.Equal(a.Id, _db.Search("Asked", "all", null).Single().LocationId);

        _db.SwitchMode(SessionMode.Place, true);
        var left = _db.Submit("9780000000026", ScanOrigin.Live);
        Assert.Equal(ScanResult.AlreadyShelved, left.Result);
        Assert.Equal(a.Id, _db.Search("Shelved", "all", null).Single().LocationId);
        var placed = _db.Submit("9780000000033", ScanOrigin.Live);
        Assert.Equal(ScanResult.Placed, placed.Result);
        Assert.False(placed.Speak);
        Assert.Equal(b.Id, _db.Search("Loose", "all", null).Single().LocationId);

        using var reopened = LibraryDatabase.Open(Path.Combine(_dir, "library.db"), _clock);
        Assert.True(reopened.AutoMove);
        reopened.AutoMove = false;
        Assert.False(_db.AutoMove);
    }

    [Fact]
    public void Whole_library_finish_lists_unseen_books_and_abandon_does_not()
    {
        _db.InsertForTest("Seen", "Author", "9780000000019", 1, null);
        _db.InsertForTest("Unseen", "Author", "9780000000026", 1, null);
        _db.SwitchMode(SessionMode.Library, false);
        _db.Submit("9780000000019", ScanOrigin.Live);
        var session = _db.ActiveSession()!;
        Assert.True(_db.FinishLibraryAudit());
        Assert.Null(_db.ActiveSession());
        Assert.Contains(_db.UnseenInSession(session.Id), row => row.Title == "Unseen");
        Assert.DoesNotContain(_db.UnseenInSession(session.Id), row => row.Title == "Seen");
        Assert.Equal(session.Id, _db.LastFinishedLibrarySessionId());

        _db.SwitchMode(SessionMode.Library, false);
        _db.Submit("9780000000026", ScanOrigin.Live);
        _db.SwitchMode(SessionMode.Place, true);
        Assert.Equal(session.Id, _db.LastFinishedLibrarySessionId());
    }

    [Fact]
    public void A_new_session_keeps_the_shelf_you_are_standing_at()
    {
        var shelf = _db.AddLocation("Shelf 1", null);
        _db.InsertForTest("Dune", "Frank Herbert", "9780000000019", 1, shelf.Id);
        _db.SwitchMode(SessionMode.Place, false);
        _db.SetShelf(shelf.Id);
        _db.Submit("9780000000019", ScanOrigin.Live);
        var next = _db.SwitchMode(SessionMode.Shelf, true);
        Assert.Equal(SessionMode.Shelf, next.Mode);
        Assert.Equal(shelf.Id, next.CurrentLocationId);
    }

    [Fact]
    public void No_shelf_does_not_create_a_book_in_place_mode()
    {
        var before = _db.Search(null, "all", null).Count;
        var outcome = _db.Submit("9780000000019", ScanOrigin.Live);
        Assert.Equal(ScanResult.ChooseShelf, outcome.Result);
        Assert.Equal(before, _db.Search(null, "all", null).Count);
    }

    [Fact]
    public void Different_edition_can_join_the_existing_book()
    {
        var shelf = _db.AddLocation("Shelf 1", null);
        _db.InsertForTest("Selected Poems", "John Keats", "9780000000019", 1, shelf.Id);
        _db.SetShelf(shelf.Id);
        var scan = _db.Submit("9780000000057", ScanOrigin.Live);
        Assert.Equal(ScanResult.NotInLibrary, scan.Result);
        var prompt = _db.ApplyLookup(scan.ScanId!.Value, new LookupBook("Selected Poems", "John Keats"), null);
        Assert.Equal(ScanResult.EditionPrompt, prompt.Result);
        Assert.Contains("Selected Poems", prompt.Detail);
        Assert.Contains("John Keats", prompt.Detail);
        _db.Answer(true);
        var rows = _db.Search("Selected Poems", "all", null).Where(r => r.CopyId != null).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(r => r.TitleId).Distinct().Count() == 1 ? 2 : 0);
        Assert.Single(rows.Select(r => r.TitleId).Distinct());
        Assert.Contains(_db.Search("9780000000057", "all", null), r => r.Title == "Selected Poems");
    }

    [Fact]
    public void A_lookup_without_a_title_still_saves_the_cover()
    {
        var shelf = _db.AddLocation("Shelf", null);
        _db.SetShelf(shelf.Id);
        var scan = _db.Submit("9780000000071", ScanOrigin.Live);
        Assert.Equal(ScanResult.NotInLibrary, scan.Result);
        var outcome = _db.ApplyLookup(scan.ScanId!.Value, null, "/covers/9780000000071.jpg");
        Assert.Equal(ScanResult.LookupFailed, outcome.Result);
        Assert.Equal("/covers/9780000000071.jpg", outcome.CoverPath);
        var id = scan.TitleId!.Value;
        Assert.Equal("/covers/9780000000071.jpg", _db.Title(id)!.CoverPath);

        _db.Commit(new[]
        {
            new LibibRow
            {
                Title = "Looked Up",
                Creators = "Someone",
                EanIsbn13 = "9780000000071",
                Copies = "1"
            }
        });
        Assert.Equal("Looked Up", _db.Title(id)!.Title);
        Assert.Equal("/covers/9780000000071.jpg", _db.Title(id)!.CoverPath);
    }

    [Fact]
    public void Undo_after_a_delete_does_not_bring_the_book_back()
    {
        var shelf = _db.AddLocation("Shelf A", null);
        _db.InsertForTest("Stays", "Author", "9780000000019", 1, null);
        _db.SetShelf(shelf.Id);
        Assert.Equal(ScanResult.Placed, _db.Submit("9780000000019", ScanOrigin.Live).Result);
        var created = _db.Submit("9780000000095", ScanOrigin.Live);
        Assert.Equal(ScanResult.NotInLibrary, created.Result);
        Assert.Null(_db.DeleteTitle(created.TitleId!.Value));
        Assert.Equal(ScanResult.Undone, _db.Undo().Result);
        Assert.Empty(_db.Search("9780000000095", "all", null));
        Assert.Null(_db.Search("Stays", "all", null).Single().LocationId);
        Assert.Contains(_db.Locations(), location => location.Id == shelf.Id);
    }

    [Fact]
    public void Labels_include_a_qr_code()
    {
        var shelf = _db.AddLocation("Shelf 1", null);
        var path = Path.Combine(_dir, "labels.pdf");
        LabelService.WritePdf(path, _db.Locations());
        var header = File.ReadAllBytes(path)[..4];
        Assert.Equal("%PDF"u8.ToArray(), header);
        Assert.Equal(shelf.Code, _db.Locations().Single(l => l.Id == shelf.Id).Code);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_dir, true);
    }
}

internal static class TestInsert
{
    public static void InsertForTest(this LibraryDatabase db, string title, string creators, string isbn, int copies, long? locationId) =>
        db.InsertTitleRaw(title, creators, isbn, IsbnRules.ToIsbn10(isbn), copies, locationId, false);
}

public class RatingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ilt-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDatabase _db;

    public RatingTests()
    {
        Directory.CreateDirectory(_dir);
        _db = LibraryDatabase.Open(Path.Combine(_dir, "library.db"));
    }

    [Fact]
    public void Tags_are_searchable_and_ratings_match_one_half_step()
    {
        _db.InsertForTest("Dune", "Frank Herbert", "9780000000019", 1, null);
        _db.InsertForTest("Quiet Atlas", "A. Mapper", "9780000000026", 1, null);
        var dune = _db.Search("9780000000019", "all", null).Single().TitleId;

        Assert.Null(_db.AddUserTags(dune, "  Cozy Mystery , Night "));
        Assert.Equal(new[] { "Cozy Mystery", "Night" }, _db.UserTags(dune));
        Assert.Equal("That tag is already on this book.", _db.AddUserTags(dune, "cozy mystery"));
        Assert.Equal("A tag can be up to 48 characters.", _db.AddUserTags(dune, new string('a', 49)));
        Assert.Equal("Type a tag first.", _db.AddUserTags(dune, "   "));

        Assert.Contains(_db.Search("night", "all", null), row => row.TitleId == dune);
        Assert.Contains(_db.Search("cozy mystery", "all", null), row => row.TitleId == dune);
        Assert.DoesNotContain(_db.Search("night", "all", null), row => row.Title == "Quiet Atlas");

        Assert.False(UserRatingScale.TryCanonical(0, out _));
        Assert.False(UserRatingScale.TryCanonical(4.2, out _));
        Assert.Equal("Choose 0.5, 1, 1.5, 2, 2.5, 3, 3.5, 4, 4.5, or 5.", _db.SetUserRating(dune, 4.2));
        Assert.Null(_db.SetUserRating(dune, 4.5));
        Assert.Equal(4.5, _db.Search("9780000000019", "all", null).Single().UserRating);
        Assert.Equal("4.5 stars", _db.Search("9780000000019", "all", null).Single().ListMarks!.Split("   ·   ")[0]);

        Assert.Single(_db.Search(null, "all", null, 4.5));
        Assert.Empty(_db.Search(null, "all", null, 4));
        Assert.Empty(_db.Search("4", "all", null));
        Assert.Single(_db.Search(null, "all", null, unratedOnly: true));
        Assert.Equal("Quiet Atlas", _db.Search(null, "all", null, unratedOnly: true).Single().Title);

        Assert.Null(_db.SetUserRating(dune, null));
        Assert.Equal(2, _db.Search(null, "all", null, unratedOnly: true).Count);
        _db.RemoveUserTag(dune, "NIGHT");
        Assert.Equal(new[] { "Cozy Mystery" }, _db.UserTags(dune));

        var export = Path.Combine(_dir, "catalog.csv");
        _db.SetUserRating(dune, 4.5);
        CsvExports.WriteCatalog(export, _db.AllCopies());
        var csv = File.ReadAllText(export);
        Assert.Contains("user_rating", csv);
        Assert.Contains("4.5", csv);
        Assert.Contains("Cozy Mystery", csv);
    }

    [Fact]
    public void Reimport_keeps_your_rating_and_tags()
    {
        _db.InsertForTest("Kept", "Author", "9780141439518", 1, null);
        var id = _db.Search("9780141439518", "all", null).Single().TitleId;
        Assert.Null(_db.SetUserRating(id, 3.5));
        Assert.Null(_db.AddUserTags(id, "reread"));

        _db.Commit(new[]
        {
            new LibibRow
            {
                Title = "Kept",
                Creators = "Author",
                EanIsbn13 = "9780141439518",
                Copies = "1",
                Rating = "5",
                Tags = "from-libib"
            }
        });

        var row = _db.Search("9780141439518", "all", null).Single();
        Assert.Equal(3.5, row.UserRating);
        Assert.Equal("reread", row.UserTags);
        Assert.Equal("5", row.Rating);
        Assert.Equal("from-libib", row.LibibTags);
        Assert.Contains(_db.Search("reread", "all", null), item => item.TitleId == id);
        Assert.Contains(_db.Search("from-libib", "all", null), item => item.TitleId == id);
        Assert.Equal("5", _db.Title(id)!.Rating);
    }

    [Fact]
    public void Opens_a_version_1_catalog_without_dropping_books()
    {
        var path = Path.Combine(_dir, "v1.db");
        using (var conn = new SqliteConnection("Data Source=" + path))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE titles (
                  id INTEGER PRIMARY KEY,
                  libib_id TEXT,
                  isbn13 TEXT,
                  isbn10 TEXT,
                  title TEXT NOT NULL,
                  creators TEXT,
                  publisher TEXT,
                  publish_date TEXT,
                  series TEXT,
                  tags TEXT,
                  notes TEXT,
                  description TEXT,
                  expected_copies INTEGER NOT NULL DEFAULT 1,
                  reading_status TEXT,
                  rating TEXT,
                  in_libib INTEGER NOT NULL DEFAULT 1,
                  is_sample INTEGER NOT NULL DEFAULT 0,
                  cover_path TEXT,
                  created_utc TEXT NOT NULL,
                  updated_utc TEXT NOT NULL
                );
                INSERT INTO titles(title, creators, rating, tags, created_utc, updated_utc)
                VALUES('Kept From Before', 'Old Author', '5', 'imported-tag', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
        }

        using (var db = LibraryDatabase.Open(path))
        {
            var row = db.Search("Kept From Before", "all", null).Single();
            Assert.Null(row.UserRating);
            Assert.Equal("5", row.Rating);
            Assert.Equal("imported-tag", row.LibibTags);
            Assert.Contains(db.Search("imported-tag", "all", null), item => item.TitleId == row.TitleId);
            Assert.Null(db.SetUserRating(row.TitleId, 2.5));
            Assert.Null(db.AddUserTags(row.TitleId, "shelf-check"));
            Assert.Equal(2.5, db.Search("Kept From Before", "all", null).Single().UserRating);
        }

        using (var db = LibraryDatabase.Open(path))
        {
            var row = db.Search("shelf-check", "all", null).Single();
            Assert.Equal("Kept From Before", row.Title);
            Assert.Equal(2.5, row.UserRating);
        }

        using (var conn = new SqliteConnection("Data Source=" + path))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            Assert.Equal(6L, (long)cmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public void Opens_a_version_4_catalog_once_and_marks_rated_books_read()
    {
        var path = Path.Combine(_dir, "v4.db");
        using (var conn = new SqliteConnection("Data Source=" + path))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE titles (
                  id INTEGER PRIMARY KEY,
                  libib_id TEXT,
                  isbn13 TEXT,
                  isbn10 TEXT,
                  title TEXT NOT NULL,
                  creators TEXT,
                  publisher TEXT,
                  publish_date TEXT,
                  series TEXT,
                  tags TEXT,
                  notes TEXT,
                  description TEXT,
                  expected_copies INTEGER NOT NULL DEFAULT 1,
                  reading_status TEXT,
                  rating TEXT,
                  in_libib INTEGER NOT NULL DEFAULT 1,
                  is_sample INTEGER NOT NULL DEFAULT 0,
                  cover_path TEXT,
                  created_utc TEXT NOT NULL,
                  updated_utc TEXT NOT NULL,
                  user_rating REAL,
                  details_custom INTEGER NOT NULL DEFAULT 0,
                  cover_locked INTEGER NOT NULL DEFAULT 0,
                  subjects_fetched INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE title_tags (
                  title_id INTEGER NOT NULL REFERENCES titles(id) ON DELETE CASCADE,
                  tag TEXT NOT NULL,
                  PRIMARY KEY (title_id, tag)
                );
                CREATE TABLE title_isbns (
                  isbn13 TEXT PRIMARY KEY,
                  title_id INTEGER NOT NULL REFERENCES titles(id) ON DELETE CASCADE,
                  isbn10 TEXT
                );
                CREATE TABLE copies (
                  id INTEGER PRIMARY KEY,
                  title_id INTEGER NOT NULL REFERENCES titles(id) ON DELETE CASCADE,
                  location_id INTEGER,
                  copy_index INTEGER NOT NULL,
                  last_confirmed_utc TEXT,
                  last_session_id INTEGER
                );
                INSERT INTO titles(id, isbn13, title, creators, description, cover_path, cover_locked, user_rating, subjects_fetched, created_utc, updated_utc) VALUES
                  (1, '9780000000019', 'Rated Book', 'A. Author', 'Kept words', '/covers/rated.jpg', 1, 4.5, 1, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z'),
                  (2, '9780000000026', 'Plain Book', 'B. Author', NULL, NULL, 0, NULL, 0, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z'),
                  (3, '9780000000033', 'Tagless Book', 'C. Author', NULL, NULL, 0, NULL, 1, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                INSERT INTO title_isbns(isbn13, title_id) VALUES ('9780000000019', 1), ('9780000000026', 2), ('9780000000033', 3);
                INSERT INTO copies(title_id, copy_index) VALUES (1, 1), (2, 1), (3, 1);
                INSERT INTO title_tags(title_id, tag) VALUES (1, 'Fantasy');
                PRAGMA user_version = 4;
                """;
            cmd.ExecuteNonQuery();
        }

        using (var db = LibraryDatabase.Open(path))
        {
            Assert.Equal(3, db.AllCopies().Count);
            var rated = db.Search("9780000000019", "all", null).Single();
            Assert.True(rated.UserRead);
            Assert.Equal(4.5, rated.UserRating);
            Assert.Equal("Kept words", rated.Description);
            Assert.Equal("/covers/rated.jpg", rated.CoverPath);
            Assert.Equal("4.5 stars   ·   Fantasy", rated.ListMarks);
            Assert.Null(db.MissingCover(1));
            Assert.Null(db.MissingSubjects(1));
            Assert.False(db.Search("9780000000026", "all", null).Single().UserRead);
            Assert.Equal("9780000000033", db.MissingSubjects(3));
            Assert.Null(db.SetRead(1, false));
        }

        using (var conn = new SqliteConnection("Data Source=" + path))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE titles SET subjects_fetched = 1 WHERE id = 3; PRAGMA user_version;";
            Assert.Equal(6L, (long)cmd.ExecuteScalar()!);
        }

        using (var db = LibraryDatabase.Open(path))
        {
            var rated = db.Search("9780000000019", "all", null).Single();
            Assert.False(rated.UserRead);
            Assert.Equal(4.5, rated.UserRating);
            Assert.Null(db.MissingSubjects(3));
        }
    }

    [Fact]
    public void A_rating_shows_only_on_a_read_book()
    {
        _db.InsertForTest("Dune", "Frank Herbert", "9780000000019", 1, null);
        _db.InsertForTest("Quiet Atlas", "A. Mapper", "9780000000026", 2, null);
        var dune = _db.Search("9780000000019", "all", null).Single().TitleId;
        var atlas = _db.Search("9780000000026", "all", null).First().TitleId;
        Assert.False(_db.Search("Dune", "all", null).Single().UserRead);

        Assert.Null(_db.SetUserRating(dune, 5));
        var rated = _db.Search("Dune", "all", null).Single();
        Assert.True(rated.UserRead);
        Assert.Equal("5 stars", rated.ListMarks);

        Assert.Null(_db.SetRead(dune, false));
        var hidden = _db.Search("Dune", "all", null).Single();
        Assert.Equal(5, hidden.UserRating);
        Assert.Null(hidden.ShownRating);
        Assert.Null(hidden.ListMarks);
        Assert.Empty(_db.Search(null, "all", null, 5));
        Assert.Contains(_db.Search(null, "all", null, unratedOnly: true), row => row.TitleId == dune);
        Assert.DoesNotContain(_db.Search(null, "read", null), row => row.TitleId == dune);
        Assert.Contains(_db.Search(null, "read", null, keepTitleId: dune), row => row.TitleId == dune);

        Assert.Null(_db.SetRead(dune, true));
        Assert.Equal("5 stars", _db.Search("Dune", "all", null).Single().ListMarks);
        Assert.Single(_db.Search(null, "read", null, 5));
        Assert.Single(_db.Search("herbert", "read", null));
        Assert.Empty(_db.Search("herbert", "unread", null));
        Assert.Empty(_db.Search(null, "unread", null, 5));
        Assert.DoesNotContain(_db.Search(null, "all", null, unratedOnly: true), row => row.TitleId == dune);
        Assert.Equal(2, _db.Search(null, "unread", null).Count);
        Assert.All(_db.Search(null, "unread", null), row => Assert.Equal(atlas, row.TitleId));

        Assert.Null(_db.SetRead(atlas, true));
        Assert.Equal("Read", _db.Search("Quiet Atlas", "all", null).First().ListMarks);
        Assert.Contains(_db.Search(null, "read", null, unratedOnly: true), row => row.TitleId == atlas);
        Assert.Null(_db.SetUserRating(dune, null));
        Assert.True(_db.Search("Dune", "all", null).Single().UserRead);
        Assert.Equal("That book is no longer in the catalog.", _db.SetRead(9999, true));
    }

    [Fact]
    public void Reimport_keeps_read_and_the_export_shows_it()
    {
        _db.InsertForTest("Kept", "Author", "9780141439518", 1, null);
        _db.InsertForTest("Shelved", "Author", "9780000000019", 1, null);
        var kept = _db.Search("9780141439518", "all", null).Single().TitleId;
        var shelved = _db.Search("9780000000019", "all", null).Single().TitleId;
        Assert.Null(_db.SetUserRating(kept, 4));
        Assert.Null(_db.SetUserRating(shelved, 2));
        Assert.Null(_db.SetRead(shelved, false));

        _db.Commit(new[]
        {
            new LibibRow { Title = "Kept", Creators = "Author", EanIsbn13 = "9780141439518", Status = "Not begun", Copies = "1" },
            new LibibRow { Title = "Shelved", Creators = "Author", EanIsbn13 = "9780000000019", Copies = "1" }
        });
        Assert.True(_db.Search("9780141439518", "all", null).Single().UserRead);
        Assert.False(_db.Search("9780000000019", "all", null).Single().UserRead);

        var export = Path.Combine(_dir, "read.csv");
        CsvExports.WriteCatalog(export, _db.AllCopies());
        var lines = File.ReadAllLines(export);
        var header = lines[0].Split(',');
        var read = Array.IndexOf(header, "read");
        var rating = Array.IndexOf(header, "user_rating");
        Assert.True(read >= 0);
        var keptLine = lines.Single(line => line.StartsWith("Kept,", StringComparison.Ordinal)).Split(',');
        Assert.Equal("yes", keptLine[read]);
        Assert.Equal("4", keptLine[rating]);
        var shelvedLine = lines.Single(line => line.StartsWith("Shelved,", StringComparison.Ordinal)).Split(',');
        Assert.Equal("no", shelvedLine[read]);
        Assert.Equal("", shelvedLine[rating]);
    }

    [Fact]
    public void Refuses_a_newer_library_file()
    {
        var path = Path.Combine(_dir, "newer.db");
        using (var conn = new SqliteConnection("Data Source=" + path))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 7;";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<InvalidDataException>(() => LibraryDatabase.Open(path));
        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public void A_book_without_an_isbn_keeps_a_description_cover_and_edits()
    {
        _db.Commit(new[]
        {
            new LibibRow
            {
                Id = "omnibus1",
                Title = "Omnibus of Nothing",
                Creators = "N. Nobody",
                Description = "First",
                Publisher = "Old House",
                Copies = "1"
            },
            new LibibRow
            {
                Id = "hello1",
                Title = "Hello",
                Creators = "A. Author",
                EanIsbn13 = "9780000000019",
                Description = "First hello",
                Copies = "1"
            }
        });

        var omnibus = _db.Search("Omnibus of Nothing", "all", null).Single();
        Assert.True(omnibus.NeedsIsbn);
        Assert.Equal("A book needs a title.", _db.UpdateBookDetails(omnibus.TitleId, new BookDetails("", null, null, null, null, null, null)));
        Assert.Null(_db.UpdateBookDetails(omnibus.TitleId, new BookDetails(
            "Omnibus Renamed", "N. Nobody", null, "Kept Press", null, "A collected volume with no barcode.", "Shelf note")));
        Assert.Null(_db.SetCoverPath(omnibus.TitleId, "/covers/omnibus.jpg"));

        _db.Commit(new[]
        {
            new LibibRow
            {
                Id = "omnibus1",
                Title = "Omnibus of Nothing",
                Creators = "N. Nobody",
                Description = "Second omnibus",
                Publisher = "New House",
                Notes = "from libib",
                Copies = "1"
            },
            new LibibRow
            {
                Id = "hello1",
                Title = "Hello Revised",
                Creators = "A. Author",
                EanIsbn13 = "9780000000019",
                Description = "Second",
                Copies = "1"
            }
        });

        var kept = _db.Search("collected volume", "all", null).Single();
        Assert.Equal("Omnibus Renamed", kept.Title);
        Assert.Equal("A collected volume with no barcode.", kept.Description);
        Assert.Equal("Shelf note", kept.Notes);
        var detail = _db.Title(kept.TitleId);
        Assert.Equal("Kept Press", detail!.Publisher);
        Assert.Equal("/covers/omnibus.jpg", detail.CoverPath);
        Assert.DoesNotContain(_db.Search(null, "all", null), row => row.Title == "Omnibus of Nothing");

        var hello = _db.Search("9780000000019", "all", null).Single();
        Assert.Equal("Hello Revised", hello.Title);
        Assert.Equal("Second", hello.Description);
    }

    [Fact]
    public void An_automatic_cover_does_not_lock_the_title_and_a_removed_cover_stays_off()
    {
        _db.InsertForTest("Hello", "A. Author", "9780000000019", 1, null);
        var id = _db.Search("9780000000019", "all", null).Single().TitleId;
        Assert.Equal("9780000000019", _db.MissingCover(id));
        Assert.True(_db.SaveFetchedCover(id, "/covers/9780000000019.jpg"));
        Assert.Null(_db.MissingCover(id));
        Assert.False(_db.SaveFetchedCover(id, "/covers/other.jpg"));

        _db.Commit(new[]
        {
            new LibibRow
            {
                Title = "Hello Revised",
                Creators = "B. Author",
                EanIsbn13 = "9780000000019",
                Description = "From Libib",
                Copies = "1"
            }
        });
        var revised = _db.Search("9780000000019", "all", null).Single();
        Assert.Equal("Hello Revised", revised.Title);
        Assert.Equal("From Libib", revised.Description);
        Assert.Equal("/covers/9780000000019.jpg", revised.CoverPath);

        var edited = revised.TitleId;
        Assert.Null(_db.UpdateBookDetails(edited, new BookDetails("Hello Revised", "B. Author", null, null, null, "Kept note", null)));
        Assert.Null(_db.SetCoverPath(edited, null));
        Assert.Null(_db.MissingCover(edited));
        Assert.False(_db.SaveFetchedCover(edited, "/covers/again.jpg"));
        Assert.Null(_db.Title(edited)!.CoverPath);

        _db.InsertForTest("Still Open", "A. Author", "9780000000026", 1, null);
        var open = _db.Search("9780000000026", "all", null).Single().TitleId;
        Assert.Null(_db.UpdateBookDetails(open, new BookDetails("Still Open", "A. Author", null, null, null, "Edited description", null)));
        Assert.Equal("9780000000026", _db.MissingCover(open));
        Assert.True(_db.SaveFetchedCover(open, "/covers/open.jpg"));
        Assert.Equal("/covers/open.jpg", _db.Title(open)!.CoverPath);
    }

    [Fact]
    public void Subject_tags_are_parsed_once_and_a_removed_tag_stays_removed()
    {
        var parsed = OpenLibraryClient.ChooseSubjects(new string?[]
        {
            "  Fantasy  ",
            "fantasy",
            "1998",
            "42",
            "c. 1842",
            "Juvenile fiction",
            "A subject that is definitely longer than thirty-two characters",
            "Foxes",
            "   ",
            null,
            "Science Fiction",
            "Mystery",
            "Horror",
            "Romance",
            "Adventure",
            "Drama",
            "Poetry",
            "Westerns"
        });
        Assert.Equal(8, parsed.Count);
        Assert.Contains("Fantasy", parsed);
        Assert.Contains("Foxes", parsed);
        Assert.DoesNotContain(parsed, subject => subject.All(char.IsDigit));
        Assert.DoesNotContain(parsed, subject => subject.Length > 32);
        Assert.DoesNotContain("Adventure", parsed);
        Assert.DoesNotContain("c. 1842", parsed);
        Assert.Equal("Foxes", parsed[0]);

        using var document = System.Text.Json.JsonDocument.Parse("""
            {
              "ISBN:9780141439518": {
                "title": "Jane Eyre",
                "authors": [{"name": "Charlotte Brontë"}],
                "subjects": [
                  {"name": "  Fantasy "},
                  {"name": "1999"},
                  {"name": "Science Fiction"},
                  "Romance"
                ]
              }
            }
            """);
        var lookup = OpenLibraryClient.Parse(document);
        Assert.Equal(LookupStatus.Found, lookup.Status);
        Assert.Equal("Jane Eyre", lookup.Book!.Title);
        Assert.Equal("Charlotte Brontë", lookup.Book.Authors);
        Assert.Equal(new[] { "Fantasy", "Romance", "Science Fiction" }, lookup.Subjects);

        _db.Commit(new[]
        {
            new LibibRow
            {
                Title = "Jane Eyre",
                Creators = "Charlotte Brontë",
                EanIsbn13 = "9780141439518",
                Tags = "from-libib",
                Copies = "1"
            }
        });
        var id = _db.Search("9780141439518", "all", null).Single().TitleId;
        Assert.Null(_db.AddUserTags(id, "reread"));
        Assert.Equal(2, _db.StoreFetchedSubjects(id, new[] { "Fantasy", "reread", "Science Fiction", "1999" }));
        Assert.Equal(new[] { "Fantasy", "Science Fiction", "reread" }, _db.UserTags(id));
        Assert.Equal("from-libib", _db.Search("9780141439518", "all", null).Single().LibibTags);
        Assert.Contains(_db.Search("fantasy", "all", null), row => row.TitleId == id);

        Assert.Equal(0, _db.StoreFetchedSubjects(id, new[] { "Fantasy", "Adventure" }));
        Assert.DoesNotContain("Adventure", _db.UserTags(id));
        Assert.Equal(3, _db.UserTags(id).Count);

        _db.RemoveUserTag(id, "Fantasy");
        Assert.Equal(0, _db.StoreFetchedSubjects(id, new[] { "Fantasy", "Adventure" }));
        Assert.DoesNotContain(_db.UserTags(id), tag => tag.Equals("Fantasy", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("reread", _db.UserTags(id));
        Assert.Null(_db.MissingSubjects(id));

        _db.InsertForTest("Empty Subjects", "Author", "9780000000033", 1, null);
        var emptyId = _db.Search("9780000000033", "all", null).Single().TitleId;
        Assert.Equal(0, _db.StoreFetchedSubjects(emptyId, Array.Empty<string>()));
        Assert.Equal(0, _db.StoreFetchedSubjects(emptyId, new[] { "1999", "  " }));
        Assert.Equal("9780000000033", _db.MissingSubjects(emptyId));
        Assert.Equal(1, _db.StoreFetchedSubjects(emptyId, new[] { "Fantasy" }));
        Assert.Equal(new[] { "Fantasy" }, _db.UserTags(emptyId));
        Assert.Null(_db.MissingSubjects(emptyId));
    }

    [Fact]
    public void Deleting_a_book_removes_its_copies_and_leaves_the_shelf()
    {
        var shelf = _db.AddLocation("Shelf A", null);
        _db.InsertForTest("Gone", "Author", "9780000000019", 2, shelf.Id);
        _db.InsertForTest("Stays", "Author", "9780000000026", 1, shelf.Id);
        var gone = _db.Search("Gone", "all", null).First();
        Assert.Equal(2, _db.Search("Gone", "all", null).Count);
        Assert.Null(_db.DeleteTitle(gone.TitleId));
        Assert.Empty(_db.Search("Gone", "all", null));
        Assert.Single(_db.Search("Stays", "all", null));
        Assert.Contains(_db.Locations(), location => location.Id == shelf.Id);
        Assert.Equal("That book is no longer in the catalog.", _db.DeleteTitle(gone.TitleId));
        Assert.Null(_db.DeleteTitle(_db.Search("Stays", "all", null).Single().TitleId));
        Assert.False(_db.HasTitles);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_dir, true);
    }
}

public class CoverFileTests
{
    [Fact]
    public void Cover_bytes_must_be_a_real_image_of_at_least_2000_bytes()
    {
        Assert.Equal(".jpg", WebSource.CoverExtension(Image(0xFF, 0xD8, 0xFF), 2000));
        Assert.Equal(".png", WebSource.CoverExtension(Image(0x89, 0x50, 0x4E, 0x47), 2000));
        var webp = new byte[12];
        "RIFF"u8.CopyTo(webp);
        "WEBP"u8.CopyTo(webp.AsSpan(8));
        Assert.Equal(".webp", WebSource.CoverExtension(webp, 2000));
        Assert.Null(WebSource.CoverExtension(Image(0xFF, 0xD8), 1999));
        Assert.Null(WebSource.CoverExtension(Image(0x47, 0x49, 0x46, 0x38), 3000));
    }

    [Fact]
    public async Task A_cover_is_cached_once_and_a_placeholder_is_rejected()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ilt-covers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var handler = new CoverHandler { StatusCode = System.Net.HttpStatusCode.NotFound, Body = Image(0xFF, 0xD8) };
            var web = new WebSource(new HttpClient(handler));
            var url = OpenLibraryClient.IsbnCoverUrl("9780000000019");
            var missing = await web.CacheImageAsync(url, folder, "9780000000019");
            Assert.Equal(new CoverFile(LookupStatus.NotFound, null), missing);
            Assert.Contains("default=false", handler.LastUri!.Query);
            Assert.Contains("YourLocalLib", handler.UserAgent);
            Assert.Empty(Directory.GetFiles(folder));

            handler.StatusCode = System.Net.HttpStatusCode.OK;
            var tiny = new byte[1500];
            tiny[0] = 0xFF;
            tiny[1] = 0xD8;
            handler.Body = tiny;
            Assert.Equal(LookupStatus.NotFound, (await web.CacheImageAsync(url, folder, "9780000000019")).Status);
            Assert.Empty(Directory.GetFiles(folder));

            handler.Body = Jpeg();
            var saved = await web.CacheImageAsync(url, folder, "9780000000019");
            Assert.Equal(new CoverFile(LookupStatus.Found, Path.Combine(folder, "9780000000019.jpg")), saved);
            Assert.Single(Directory.GetFiles(folder));
            var calls = handler.Calls;
            Assert.Equal(saved, await web.CacheImageAsync(OpenLibraryClient.CoverUrlById(5), folder, "9780000000019"));
            Assert.Equal(calls, handler.Calls);

            var owned = new CoverHandler { StatusCode = System.Net.HttpStatusCode.OK, Body = Jpeg() };
            var preset = new HttpClient(owned);
            preset.DefaultRequestHeaders.UserAgent.ParseAdd("Already/1");
            await new WebSource(preset).CacheImageAsync(OpenLibraryClient.IsbnCoverUrl("9780000000026"), folder, "9780000000026");
            Assert.StartsWith("Already/1", owned.UserAgent);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task A_refused_isbn_cover_address_pauses_only_that_address_for_ten_minutes()
    {
        var clock = new ManualClock();
        var handler = new CoverHandler { StatusCode = System.Net.HttpStatusCode.Forbidden, Body = Jpeg() };
        var web = new WebSource(new HttpClient(handler), clock);
        Assert.Equal(LookupStatus.Limited, (await web.GetAsync(OpenLibraryClient.IsbnCoverUrl("9780000000019"))).Status);
        Assert.Equal(LookupStatus.Limited, (await web.GetAsync(OpenLibraryClient.IsbnCoverUrl("9780000000026"))).Status);
        Assert.Equal(1, handler.Calls);

        handler.StatusCode = System.Net.HttpStatusCode.OK;
        Assert.Equal(LookupStatus.Found, (await web.GetAsync(OpenLibraryClient.CoverUrlById(7))).Status);
        Assert.Equal(2, handler.Calls);

        clock.UtcNow += TimeSpan.FromMinutes(9);
        Assert.Equal(LookupStatus.Limited, (await web.GetAsync(OpenLibraryClient.IsbnCoverUrl("9780000000026"))).Status);
        clock.UtcNow += TimeSpan.FromMinutes(2);
        Assert.Equal(LookupStatus.Found, (await web.GetAsync(OpenLibraryClient.IsbnCoverUrl("9780000000026"))).Status);
        Assert.Equal(3, handler.Calls);

        handler.Throw = true;
        Assert.Equal(LookupStatus.Failed, (await web.GetAsync(OpenLibraryClient.CoverUrlById(7))).Status);
        handler.Throw = false;
        Assert.Equal(LookupStatus.Found, (await web.GetAsync(OpenLibraryClient.CoverUrlById(7))).Status);
        Assert.Equal(LookupStatus.NotFound, (await web.GetAsync("http://covers.openlibrary.org/b/id/7-L.jpg")).Status);
        Assert.Equal(5, handler.Calls);
    }

    private static byte[] Image(params byte[] header)
    {
        var bytes = new byte[Math.Max(header.Length, 16)];
        header.CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] Jpeg()
    {
        var jpeg = new byte[2400];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        return jpeg;
    }

    private sealed class CoverHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }
        public string? UserAgent { get; private set; }
        public bool Throw { get; set; }
        public System.Net.HttpStatusCode StatusCode { get; set; } = System.Net.HttpStatusCode.OK;
        public byte[] Body { get; set; } = Array.Empty<byte>();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            UserAgent = request.Headers.UserAgent.ToString();
            if (Throw)
                throw new HttpRequestException("No network.");
            return Task.FromResult(new HttpResponseMessage(StatusCode)
            {
                Content = new ByteArrayContent(Body)
            });
        }
    }
}
