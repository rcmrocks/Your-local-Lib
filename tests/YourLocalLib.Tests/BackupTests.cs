using System.IO.Compression;
using YourLocalLib.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace YourLocalLib.Tests;

public class BackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ilt-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private readonly LibraryDatabase _db;

    public BackupTests()
    {
        Directory.CreateDirectory(_dir);
        _db = LibraryDatabase.Open(Path.Combine(_dir, "home", "library.db"), _clock);
    }

    private string Covers => Path.Combine(_dir, "home", "covers");
    private string Safety => Path.Combine(_dir, "home", "backups");

    [Fact]
    public void A_backup_restored_on_another_computer_keeps_books_tags_ratings_shelves_covers_and_settings()
    {
        var study = _db.AddLocation("Study", null);
        var top = _db.AddLocation("Top shelf", study.Id);
        _db.Commit(new[]
        {
            new LibibRow { Title = "Dune", Creators = "Frank Herbert", EanIsbn13 = "9780000000019", Copies = "2", Rating = "5", Status = "Completed", Tags = "classic" },
            new LibibRow { Title = "Mort", Creators = "Terry Pratchett", EanIsbn13 = "9780000000026", Copies = "1" },
            new LibibRow { Title = "Field Notes", Creators = "A. Walker", Copies = "1" }
        });
        var dune = _db.Search("9780000000019", "all", null).First().TitleId;
        var mort = _db.Search("9780000000026", "all", null).Single().TitleId;
        var notes = _db.Search("Field Notes", "all", null).Single().TitleId;
        Assert.Null(_db.AddUserTags(dune, "Cozy, Reread"));
        Assert.Null(_db.SetUserRating(dune, 4.5));
        Assert.Null(_db.SetRead(mort, true));
        Assert.Null(_db.UpdateBookDetails(notes, new BookDetails("Field Notes, Revised", "A. Walker", null, null, null, "My own words.", "Signed copy.")));
        _db.MoveCopy(_db.Search("9780000000019", "all", null).First().CopyId!.Value, top.Id);
        var chosen = WriteCover(Path.Combine(Covers, "book-" + notes + "-abc.png"), 7);
        Assert.Null(_db.SetCoverPath(notes, chosen));
        var fetched = WriteCover(Path.Combine(Covers, "9780000000026.jpg"), 9);
        Assert.True(_db.ApplyFetched(mort, null, null, fetched, complete: true).CoverAdded);
        _db.SetShelf(top.Id);
        Assert.Equal(ScanResult.Placed, _db.Submit("9780000000026", ScanOrigin.Live).Result);
        _db.Theme = "dark";
        _db.AutoMove = true;
        _db.GoogleBooksKey = "my-key";

        var zip = Path.Combine(_dir, "thumb drive", "Your Local Lib backup 2026-03-09.zip");
        var report = LibraryBackup.Save(_db, zip, "1.4.0");
        Assert.Equal(zip, report.Path);
        Assert.False(File.Exists(zip + ".part"));
        Assert.Equal(new LibraryCounts(3, 4, 2, 2, 1, 2), report.Counts);
        Assert.Equal(2, report.Covers);
        Assert.Equal(new FileInfo(zip).Length, report.Bytes);
        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.NotNull(archive.GetEntry("library.db"));
            Assert.NotNull(archive.GetEntry("backup.json"));
            Assert.Equal(new[] { "covers/9780000000026.jpg", "covers/book-" + notes + "-abc.png" },
                archive.Entries.Select(e => e.FullName).Where(name => name.StartsWith("covers/")).Order());
        }

        var check = LibraryBackup.Inspect(zip);
        Assert.Equal(report.Counts, check.Counts);
        Assert.Equal(2, check.Covers);
        Assert.False(check.DatabaseOnly);
        Assert.Equal("1.4.0", check.AppVersion);
        Assert.Equal(_clock.UtcNow, check.CreatedUtc);

        var newPc = Path.Combine(_dir, "new pc");
        var newCovers = Path.Combine(newPc, "covers");
        using var fresh = LibraryDatabase.Open(Path.Combine(newPc, "library.db"), _clock);
        var result = LibraryBackup.Restore(fresh, zip, newCovers, Path.Combine(newPc, "backups"));
        Assert.Null(result.SafetyCopy);
        Assert.Equal(2, result.Covers);
        Assert.Equal(report.Counts, result.Counts);
        Assert.Equal(1, fresh.Generation);

        var duneRows = fresh.Search("9780000000019", "all", null);
        Assert.Equal(2, duneRows.Count);
        Assert.Equal("Study / Top shelf", duneRows.Single(r => r.LocationId != null).ShelfPath);
        Assert.All(duneRows, row =>
        {
            Assert.Equal(4.5, row.UserRating);
            Assert.True(row.UserRead);
            Assert.Equal("Cozy, Reread", row.UserTags);
            Assert.Equal("5", row.Rating);
            Assert.Equal("classic", row.LibibTags);
        });
        var mortRow = fresh.Search("9780000000026", "all", null).Single();
        Assert.True(mortRow.UserRead);
        Assert.Equal("Study / Top shelf", mortRow.ShelfPath);
        var restoredNotes = fresh.Title(notes)!;
        Assert.Equal("Field Notes, Revised", restoredNotes.Title);
        Assert.Equal("My own words.", restoredNotes.Description);
        Assert.Equal("Signed copy.", restoredNotes.Notes);
        Assert.Equal(new[] { "Study", "Study / Top shelf" }, fresh.Locations().Select(l => l.Path));
        Assert.Equal(_db.Locations().Select(l => l.Code), fresh.Locations().Select(l => l.Code));
        Assert.Equal(top.Id, fresh.ActiveSession()!.CurrentLocationId);

        var notesCover = Path.Combine(newCovers, Path.GetFileName(chosen));
        Assert.Equal(notesCover, fresh.CurrentCoverPath(notes));
        Assert.Equal(File.ReadAllBytes(chosen), File.ReadAllBytes(notesCover));
        Assert.Equal(Path.Combine(newCovers, "9780000000026.jpg"), fresh.CurrentCoverPath(mort));
        Assert.Equal(new[] { Path.Combine(newCovers, "9780000000026.jpg") }, Column(fresh.Path, "SELECT cover_path FROM scans"));

        Assert.Equal("dark", fresh.Theme);
        Assert.True(fresh.AutoMove);
        Assert.Equal("my-key", fresh.GoogleBooksKey);
        Assert.Equal(new[] { "6" }, Column(fresh.Path, "PRAGMA user_version"));
    }

    [Fact]
    public void Restoring_over_your_library_saves_it_first_so_you_can_go_back()
    {
        _db.InsertForTest("Mine", "Me", "9780000000019", 1, null);
        var mine = _db.Search("Mine", "all", null).Single().TitleId;
        Assert.Null(_db.AddUserTags(mine, "keep"));
        var zip = OtherLibrary("Theirs", "9780000000026");

        var result = LibraryBackup.Restore(_db, zip, Covers, Safety);
        Assert.Equal("Theirs", _db.Search(null, "all", null).Single().Title);
        Assert.NotNull(result.SafetyCopy);
        Assert.Equal(Safety, Path.GetDirectoryName(result.SafetyCopy));
        Assert.StartsWith("before-restore-", Path.GetFileName(result.SafetyCopy));
        Assert.Equal(1, LibraryBackup.Inspect(result.SafetyCopy).Counts.Books);

        var back = LibraryBackup.Restore(_db, result.SafetyCopy, Covers, Safety);
        var row = _db.Search(null, "all", null).Single();
        Assert.Equal("Mine", row.Title);
        Assert.Equal("keep", row.UserTags);
        Assert.NotEqual(result.SafetyCopy, back.SafetyCopy);
        Assert.Equal(2, Directory.GetFiles(Safety, "before-restore-*.zip").Length);
        Assert.Equal(2, _db.Generation);
    }

    [Fact]
    public void The_sample_library_and_an_empty_one_are_replaced_without_a_safety_copy()
    {
        var zip = OtherLibrary("Theirs", "9780000000026");
        Assert.Null(LibraryBackup.Restore(_db, zip, Covers, Safety).SafetyCopy);

        using var sample = LibraryDatabase.Open(Path.Combine(_dir, "sample", "library.db"), _clock);
        sample.LoadSample();
        Assert.Null(LibraryBackup.Restore(sample, zip, Covers, Safety).SafetyCopy);
        Assert.False(sample.IsSample);
        Assert.Equal("Theirs", sample.Search(null, "all", null).Single().Title);
        Assert.False(Directory.Exists(Safety));
    }

    [Fact]
    public void A_newer_foreign_or_damaged_file_is_refused_and_nothing_changes()
    {
        _db.InsertForTest("Mine", "Me", "9780000000019", 1, null);

        var words = Path.Combine(_dir, "words.zip");
        File.WriteAllText(words, "just some words");
        Assert.StartsWith("That file is not a library backup.", Refused(words));

        var photos = Path.Combine(_dir, "photos.zip");
        using (var archive = ZipFile.Open(photos, ZipArchiveMode.Create))
            archive.CreateEntry("photo.jpg");
        Assert.StartsWith("That file is not a library backup.", Refused(photos));

        var plain = Path.Combine(_dir, "plain.db");
        Sql(plain, "CREATE TABLE things (id INTEGER PRIMARY KEY);");
        Assert.StartsWith("That file is not a library backup.", Refused(plain));

        var newer = Path.Combine(_dir, "newer.db");
        Sql(newer, "CREATE TABLE titles (id INTEGER PRIMARY KEY); PRAGMA user_version = 7;");
        Assert.Contains("newer version", Refused(newer));
        Assert.Contains("newer version", Refused(Zipped(newer)));

        var damaged = Path.Combine(_dir, "damaged.db");
        Sql(damaged, """
            CREATE TABLE titles (id INTEGER PRIMARY KEY, title TEXT NOT NULL);
            CREATE TABLE copies (id INTEGER PRIMARY KEY);
            CREATE TABLE locations (id INTEGER PRIMARY KEY);
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 3000)
            INSERT INTO titles(title) SELECT printf('Book %d with a title long enough to fill pages', i) FROM n;
            """);
        var bytes = File.ReadAllBytes(damaged);
        Array.Fill(bytes, (byte)0x5A, bytes.Length / 2, 3 * 4096);
        File.WriteAllBytes(damaged, bytes);
        Assert.StartsWith("This backup is damaged", Refused(damaged));
        Assert.StartsWith("This backup is damaged", Refused(Zipped(damaged)));

        Assert.Contains("this app has open", Refused(_db.Path));

        Assert.Equal("Mine", _db.Search(null, "all", null).Single().Title);
        Assert.Equal(0, _db.Generation);
        Assert.False(Directory.Exists(Safety));
        Assert.False(Directory.Exists(Covers));
    }

    [Fact]
    public void A_cover_name_cannot_reach_outside_the_covers_folder()
    {
        var crafted = Path.Combine(_dir, "crafted.zip");
        File.Copy(OtherLibrary("Theirs", "9780000000026"), crafted);
        using (var archive = ZipFile.Open(crafted, ZipArchiveMode.Update))
        {
            foreach (var name in new[] { "covers/../escaped1.jpg", "covers/..\\escaped2.jpg", "../escaped3.jpg", "covers/sub/escaped4.jpg", "covers/C:escaped5.jpg", "covers/" })
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(new byte[] { 1, 2, 3 });
            }
            using var fine = archive.CreateEntry("covers/fine.jpg").Open();
            fine.Write(new byte[] { 4, 5, 6 });
        }

        Assert.Equal(1, LibraryBackup.Inspect(crafted).Covers);
        Assert.Equal(1, LibraryBackup.Restore(_db, crafted, Covers, Safety).Covers);
        Assert.Equal(new[] { "fine.jpg" }, Directory.GetFiles(Covers).Select(Path.GetFileName));
        Assert.Empty(Directory.GetFiles(_dir, "escaped*", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_cover_already_on_this_computer_is_kept_and_a_different_one_gets_its_own_name()
    {
        string zip;
        long theirs;
        var otherCovers = Path.Combine(_dir, "other", "covers");
        using (var other = LibraryDatabase.Open(Path.Combine(_dir, "other", "library.db"), _clock))
        {
            theirs = other.InsertTitleRaw("Theirs", "Them", "9780000000026", null, 1, null, false);
            other.ApplyFetched(theirs, null, null, WriteCover(Path.Combine(otherCovers, "9780000000026.jpg"), 1), complete: true);
            var same = other.InsertTitleRaw("Same", "Them", "9780000000033", null, 1, null, false);
            other.ApplyFetched(same, null, null, WriteCover(Path.Combine(otherCovers, "9780000000033.jpg"), 2), complete: true);
            zip = LibraryBackup.Save(other, Path.Combine(_dir, "theirs.zip")).Path;
        }
        var mine = WriteCover(Path.Combine(Covers, "9780000000026.jpg"), 99);
        WriteCover(Path.Combine(Covers, "9780000000033.jpg"), 2);

        LibraryBackup.Restore(_db, zip, Covers, Safety);

        var restored = _db.CurrentCoverPath(theirs)!;
        Assert.Equal(Path.Combine(Covers, "9780000000026-restored.jpg"), restored);
        Assert.Equal(File.ReadAllBytes(Path.Combine(otherCovers, "9780000000026.jpg")), File.ReadAllBytes(restored));
        Assert.Equal(99 + 2, File.ReadAllBytes(mine)[2]);
        var sameRow = _db.Search("Same", "all", null).Single();
        Assert.Equal(Path.Combine(Covers, "9780000000033.jpg"), sameRow.CoverPath);
        Assert.Equal(3, Directory.GetFiles(Covers).Length);
    }

    [Fact]
    public void An_automatic_database_copy_can_be_restored_and_a_missing_cover_is_cleared()
    {
        var kept = _db.InsertTitleRaw("Kept", "Author", "9780000000019", null, 1, null, false);
        var keptCover = WriteCover(Path.Combine(Covers, "9780000000019.jpg"), 3);
        Assert.True(_db.ApplyFetched(kept, null, null, keptCover, complete: true).CoverAdded);
        var gone = _db.InsertTitleRaw("Gone", "Author", "9780000000026", null, 1, null, false);
        var lostCover = WriteCover(Path.Combine(Covers, "9780000000026.jpg"), 4);
        Assert.True(_db.ApplyFetched(gone, null, null, lostCover, complete: true).CoverAdded);
        var folder = Path.Combine(_dir, "automatic");
        _db.BackupTo(folder);
        var copy = Directory.GetFiles(folder, "library-*.db").Single();
        Assert.False(File.Exists(copy + "-wal"));
        File.Delete(lostCover);

        var check = LibraryBackup.Inspect(copy);
        Assert.True(check.DatabaseOnly);
        Assert.Equal(0, check.Covers);
        Assert.Equal(2, check.Counts.Books);

        var newPc = Path.Combine(_dir, "new pc");
        using var fresh = LibraryDatabase.Open(Path.Combine(newPc, "library.db"), _clock);
        var result = LibraryBackup.Restore(fresh, copy, Path.Combine(newPc, "covers"), Path.Combine(newPc, "backups"));
        Assert.Equal(0, result.Covers);
        Assert.Equal(keptCover, fresh.CurrentCoverPath(kept));
        Assert.Null(fresh.CurrentCoverPath(gone));
        Assert.Equal("9780000000026", fresh.MissingCover(gone));
    }

    [Fact]
    public void Undo_after_a_restore_takes_back_a_joined_edition_with_its_restored_cover()
    {
        var shelf = _db.AddLocation("Shelf 1", null);
        var existing = _db.InsertTitleRaw("Selected Poems", "John Keats", "9780000000071", null, 1, shelf.Id, false);
        _db.SetShelf(shelf.Id);
        var scan = _db.Submit("9780000000088", ScanOrigin.Live);
        var provisional = scan.TitleId!.Value;
        Assert.Equal(ScanResult.EditionPrompt, _db.ApplyLookup(scan.ScanId!.Value, new LookupBook("Selected Poems", "John Keats"), null).Result);
        var cover = WriteCover(Path.Combine(Covers, "9780000000088.jpg"), 5);
        Assert.True(_db.ApplyFetched(provisional, null, null, cover, complete: true).CoverAdded);
        Assert.Equal(ScanResult.AddedCopy, _db.Answer(true).Result);
        Assert.Equal(cover, _db.CurrentCoverPath(existing));
        var zip = LibraryBackup.Save(_db, Path.Combine(_dir, "joined.zip")).Path;

        var newPc = Path.Combine(_dir, "new pc");
        using var fresh = LibraryDatabase.Open(Path.Combine(newPc, "library.db"), _clock);
        LibraryBackup.Restore(fresh, zip, Path.Combine(newPc, "covers"), Path.Combine(newPc, "backups"));
        var moved = Path.Combine(newPc, "covers", "9780000000088.jpg");
        Assert.Equal(moved, fresh.CurrentCoverPath(existing));

        Assert.Equal(ScanResult.Undone, fresh.Undo().Result);
        Assert.Null(fresh.CurrentCoverPath(existing));
        Assert.Equal(moved, fresh.Search("9780000000088", "all", null).Single().CoverPath);
    }

    [Fact]
    public async Task A_lookup_that_began_before_a_restore_does_not_touch_the_restored_library()
    {
        var web = new FakeWeb();
        var source = new WebSource(new HttpClient(web), _clock);
        var fill = new BookFill(_db, source, new OpenLibraryClient(source), new GoogleBooksClient(source, () => null), Covers);
        var mine = _db.InsertTitleRaw("Mine", "Me", "9780000000019", null, 1, null, false);
        _db.SetShelf(_db.AddLocation("Shelf 1", null).Id);
        var scan = _db.Submit("9780000000033", ScanOrigin.Live);
        Assert.True(scan.StartLookup);

        string zip;
        long theirs;
        using (var other = LibraryDatabase.Open(Path.Combine(_dir, "other", "library.db"), _clock))
        {
            theirs = other.InsertTitleRaw("Theirs", "Them", "9780000000026", null, 1, null, false);
            other.SetShelf(other.AddLocation("Their shelf", null).Id);
            Assert.Equal(scan.ScanId, other.Submit("9780000000040", ScanOrigin.Live).ScanId);
            zip = LibraryBackup.Save(other, Path.Combine(_dir, "theirs.zip")).Path;
        }
        Assert.Equal(mine, theirs);

        web.Json("https://openlibrary.org/api/books?bibkeys=ISBN:9780000000019", """{"ISBN:9780000000019": {"title": "Mine", "subjects": ["Fantasy"]}}""");
        web.Json("https://openlibrary.org/isbn/9780000000019.json", """{"description": "Words about Mine."}""");
        var hold = new TaskCompletionSource();
        web.Hold = hold.Task;
        var running = fill.RunAsync(mine);
        for (var i = 0; i < 500 && web.Requests.Count == 0; i++)
            await Task.Delay(10);
        Assert.NotEmpty(web.Requests);
        var before = _db.Generation;

        LibraryBackup.Restore(_db, zip, Covers, Safety);
        hold.SetResult();

        Assert.Equal(FillResult.Skipped, await running);
        Assert.Empty(_db.UserTags(theirs));
        Assert.Null(_db.Title(theirs)!.Description);
        Assert.Equal("9780000000026", _db.MissingSubjects(theirs));

        Assert.Equal(ScanResult.Undone, _db.ApplyLookup(scan.ScanId!.Value, new LookupBook("Wrong Book", "Nobody"), null, false, before).Result);
        Assert.DoesNotContain(_db.Search(null, "all", null), row => row.Title == "Wrong Book");
        Assert.Equal(FillResult.Skipped, _db.ApplyFetched(theirs, "Wrong words.", new[] { "Wrong" }, null, complete: true, before));
        Assert.Null(_db.Title(theirs)!.Description);
        Assert.True(_db.ApplyFetched(theirs, "Right words.", null, null, complete: true, _db.Generation).DescriptionAdded);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_dir, true);
    }

    private string Refused(string path)
    {
        var inspected = Assert.Throws<BackupException>(() => LibraryBackup.Inspect(path, _db.Path));
        var restored = Assert.Throws<BackupException>(() => LibraryBackup.Restore(_db, path, Covers, Safety));
        Assert.Equal(inspected.Message, restored.Message);
        return inspected.Message;
    }

    private string OtherLibrary(string title, string isbn)
    {
        var folder = Path.Combine(_dir, "other-" + Guid.NewGuid().ToString("N"));
        using var other = LibraryDatabase.Open(Path.Combine(folder, "library.db"), _clock);
        other.InsertForTest(title, "Them", isbn, 1, null);
        return LibraryBackup.Save(other, Path.Combine(folder, "backup.zip")).Path;
    }

    private string Zipped(string database)
    {
        var zip = Path.ChangeExtension(database, ".zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(database, "library.db");
        return zip;
    }

    private static string WriteCover(string path, byte seed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[2400];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(seed + i);
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void Sql(string path, string sql)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static List<string> Column(string path, string sql)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
            values.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? "");
        return values;
    }
}
