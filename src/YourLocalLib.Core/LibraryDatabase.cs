using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace YourLocalLib.Core;

public sealed partial class LibraryDatabase : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();
    private readonly IClock _clock;
    private string? _lastLiveIsbn;
    private DateTime _lastLiveAt;

    public string Path { get; }
    public IClock Clock => _clock;

    private LibraryDatabase(string path, SqliteConnection conn, IClock clock)
    {
        Path = path;
        _conn = conn;
        _clock = clock;
    }

    public static LibraryDatabase Open(string path, IClock? clock = null)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        return OpenWith(builder, clock ?? new SystemClock(), "WAL");
    }

    private static LibraryDatabase OpenWith(SqliteConnectionStringBuilder builder, IClock clock, string journalMode)
    {
        var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        var db = new LibraryDatabase(builder.DataSource, conn, clock);
        try
        {
            db.Exec("PRAGMA foreign_keys = ON;");
            db.Exec("PRAGMA journal_mode = " + journalMode + ";");
            db.EnsureSchema();
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    public void Dispose() => _conn.Dispose();

    public void BackupTo(string folder, int keep = 10)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(folder);
            var destPath = System.IO.Path.Combine(folder, "library-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".db");
            SnapshotTo(destPath);
            var stale = Directory.GetFiles(folder, "library-*.db")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .Skip(keep);
            foreach (var file in stale)
                File.Delete(file);
        }
    }

    public int DebounceMs
    {
        get => GetInt("debounce_ms", 400);
        set => SetSetting("debounce_ms", Math.Clamp(value, 50, 5000).ToString(CultureInfo.InvariantCulture));
    }

    public bool SpeechEnabled
    {
        get => GetSetting("speech") != "off";
        set => SetSetting("speech", value ? "on" : "off");
    }

    public string Theme
    {
        get => GetSetting("theme") is "dark" or "light" ? GetSetting("theme")! : "light";
        set => SetSetting("theme", value == "dark" ? "dark" : "light");
    }

    public string? BackupFolder
    {
        get => BlankToNull(GetSetting("backup_folder"));
        set => SetSetting("backup_folder", value ?? "");
    }

    public bool AutoMove
    {
        get => GetSetting("auto_move") == "on";
        set => SetSetting("auto_move", value ? "on" : "off");
    }

    public string? GoogleBooksKey
    {
        get => BlankToNull(GetSetting("google_books_key"))?.Trim();
        set => SetSetting("google_books_key", value?.Trim() ?? "");
    }

    public string ScanKind
    {
        get => MediaKind.Parse(GetSetting("scan_kind"));
        set => SetSetting("scan_kind", MediaKind.Parse(value));
    }

    public double WindowWidth
    {
        get => double.TryParse(GetSetting("window_width"), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 1180;
        set => SetSetting("window_width", value.ToString(CultureInfo.InvariantCulture));
    }

    public double WindowHeight
    {
        get => double.TryParse(GetSetting("window_height"), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 780;
        set => SetSetting("window_height", value.ToString(CultureInfo.InvariantCulture));
    }

    public bool HasTitles => Locked(() => ScalarLong("SELECT COUNT(*) FROM titles") > 0);
    public bool IsSample => Locked(() => HasTitlesCore() && ScalarLong("SELECT COUNT(*) FROM titles WHERE is_sample = 0") == 0);

    public void LoadSample() => Locked(() =>
    {
        if (ScalarLong("SELECT COUNT(*) FROM titles") > 0)
            throw new InvalidOperationException("The sample library can only be loaded into an empty catalog.");
        SampleLibrary.Insert(this);
    });

    public void DeleteSample() => Locked(() =>
    {
        if (!HasTitlesCore() || ScalarLong("SELECT COUNT(*) FROM titles WHERE is_sample = 0") > 0)
            throw new InvalidOperationException("There is no sample library to remove.");
        WipeCatalog();
    });

    public void ReplaceWithImport(IReadOnlyList<LibibRow> rows, string? defaultKind = null, IReadOnlyList<string>? headers = null) => Locked(() =>
    {
        var preview = BuildPreview(rows, defaultKind, headers);
        if (!preview.CanCommit)
            throw new InvalidDataException(preview.Errors.FirstOrDefault() ?? "That file cannot be imported.");
        using var tx = _conn.BeginTransaction();
        WipeCatalog();
        CommitResolved(LibibCsv.Resolve(rows, defaultKind, headers));
        tx.Commit();
    });

    public ImportPreview Preview(IReadOnlyList<LibibRow> rows, string? defaultKind = null, IReadOnlyList<string>? headers = null) =>
        Locked(() => BuildPreview(rows, defaultKind, headers));

    public ImportPreview Commit(IReadOnlyList<LibibRow> rows, string? defaultKind = null, IReadOnlyList<string>? headers = null) => Locked(() =>
    {
        var preview = BuildPreview(rows, defaultKind, headers);
        if (!preview.CanCommit)
            throw new InvalidDataException(preview.Errors.FirstOrDefault() ?? "That file cannot be imported.");
        using var tx = _conn.BeginTransaction();
        CommitResolved(LibibCsv.Resolve(rows, defaultKind, headers));
        tx.Commit();
        return preview;
    });

    public IReadOnlyList<LocationInfo> Locations() => Locked(LoadLocations);

    public LocationInfo AddLocation(string name, long? parentId) => Locked(() =>
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            throw new InvalidOperationException("A shelf needs a name.");
        if (parentId is long parent && LoadLocations().All(l => l.Id != parent))
            throw new InvalidOperationException("Choose a shelf that still exists.");
        var sort = ScalarLong("SELECT COALESCE(MAX(sort_order), 0) + 1 FROM locations WHERE IFNULL(parent_id, -1) = $p", ("$p", parentId ?? -1));
        var code = NextCode();
        Exec("INSERT INTO locations(name, parent_id, sort_order, code, is_sample) VALUES($n, $p, $s, $c, 0)",
            ("$n", trimmed), ("$p", parentId), ("$s", sort), ("$c", code));
        var id = LastId();
        return LoadLocations().First(l => l.Id == id);
    });

    public void RenameLocation(long id, string name) => Locked(() =>
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            throw new InvalidOperationException("A shelf needs a name.");
        Exec("UPDATE locations SET name = $n WHERE id = $id", ("$n", trimmed), ("$id", id));
    });

    public void SetParent(long id, long? parentId) => Locked(() =>
    {
        if (parentId == id)
            throw new InvalidOperationException("A shelf cannot sit inside itself.");
        if (parentId is long parent && IsDescendant(parent, id))
            throw new InvalidOperationException("A shelf cannot sit inside one of its own shelves.");
        Exec("UPDATE locations SET parent_id = $p WHERE id = $id", ("$p", parentId), ("$id", id));
    });

    public void MoveLocation(long id, int direction) => Locked(() =>
    {
        var all = LoadLocations();
        var item = all.FirstOrDefault(l => l.Id == id) ?? throw new InvalidOperationException("That shelf is gone.");
        var siblings = all.Where(l => l.ParentId == item.ParentId).OrderBy(l => l.SortOrder).ThenBy(l => l.Id).ToList();
        var index = siblings.FindIndex(l => l.Id == id);
        var swap = index + direction;
        if (index < 0 || swap < 0 || swap >= siblings.Count)
            return;
        Exec("UPDATE locations SET sort_order = $s WHERE id = $id", ("$s", siblings[swap].SortOrder), ("$id", id));
        Exec("UPDATE locations SET sort_order = $s WHERE id = $id", ("$s", item.SortOrder), ("$id", siblings[swap].Id));
    });

    public void DeleteLocation(long id, long? moveBooksTo) => Locked(() =>
    {
        var all = LoadLocations();
        var item = all.FirstOrDefault(l => l.Id == id) ?? throw new InvalidOperationException("That shelf is gone.");
        if (moveBooksTo == id || (moveBooksTo is long dest && IsDescendant(dest, id)))
            throw new InvalidOperationException("Choose a different shelf for the books, or leave them unlocated.");
        Exec("INSERT OR IGNORE INTO retired_location_codes(code) VALUES($c)", ("$c", item.Code));
        Exec("UPDATE copies SET location_id = $to WHERE location_id = $id", ("$to", moveBooksTo), ("$id", id));
        Exec("UPDATE locations SET parent_id = $p WHERE parent_id = $id", ("$p", item.ParentId), ("$id", id));
        Exec("UPDATE sessions SET current_location_id = NULL WHERE current_location_id = $id", ("$id", id));
        Exec("DELETE FROM locations WHERE id = $id", ("$id", id));
    });

    public SessionInfo? ActiveSession() => Locked(() => MapSession(GetActive()));

    public SessionInfo EnsureSession(string mode) => Locked(() =>
    {
        var active = GetActive();
        if (active == null)
            return MapSession(CreateSession(mode))!;
        if (active.Mode != mode && CountScans(active.Id) == 0)
        {
            Exec("UPDATE sessions SET mode = $m WHERE id = $id", ("$m", mode), ("$id", active.Id));
            active = GetActive()!;
        }
        return MapSession(active)!;
    });

    public bool NeedsModeConfirmation(string mode) => Locked(() =>
    {
        var active = GetActive();
        return active != null && active.Mode != mode && CountScans(active.Id) > 0;
    });

    public SessionInfo SwitchMode(string mode, bool abandon) => Locked(() =>
    {
        var active = GetActive();
        if (active == null)
            return MapSession(CreateSession(mode))!;
        if (active.Mode == mode)
            return MapSession(active)!;
        if (CountScans(active.Id) == 0)
        {
            Exec("UPDATE sessions SET mode = $m WHERE id = $id", ("$m", mode), ("$id", active.Id));
            return MapSession(GetActive())!;
        }
        if (!abandon)
            throw new InvalidOperationException("This session already has scans.");
        var shelf = active.CurrentLocationId;
        Exec("UPDATE sessions SET is_active = 0, abandoned = 1 WHERE id = $id", ("$id", active.Id));
        ClearPending(active.Id);
        var created = CreateSession(mode);
        if (shelf != null)
            Exec("UPDATE sessions SET current_location_id = $l WHERE id = $id", ("$l", shelf), ("$id", created.Id));
        return MapSession(GetActive())!;
    });

    public void SetShelf(long locationId) => Locked(() =>
    {
        var session = GetActive() ?? CreateSession(SessionMode.Place);
        if (LoadLocations().All(l => l.Id != locationId))
            throw new InvalidOperationException("That shelf is gone.");
        Exec("UPDATE sessions SET current_location_id = $l WHERE id = $id", ("$l", locationId), ("$id", session.Id));
    });

    public bool FinishLibraryAudit() => Locked(() =>
    {
        var session = GetActive();
        if (session == null || session.Mode != SessionMode.Library)
            return false;
        if (HasPending(session.Id))
            ResolvePending(session, false);
        Exec("UPDATE sessions SET is_active = 0, finished_utc = $t WHERE id = $id",
            ("$t", Stamp()), ("$id", session.Id));
        return true;
    });

    public long? LastFinishedLibrarySessionId() => Locked(() =>
    {
        var value = ScalarObj("SELECT id FROM sessions WHERE mode = 'library' AND finished_utc IS NOT NULL ORDER BY finished_utc DESC LIMIT 1");
        return value is long id ? id : (long?)null;
    });

    public (int Confirmed, int NotInLibib, int Extras) SessionCounts(long sessionId) => Locked(() => Counts(sessionId));

    public IReadOnlyList<string> RecentLines(long sessionId) => Locked(() =>
        Query("""
            SELECT status, headline FROM scans
            WHERE session_id = $s AND result NOT IN ('held')
            ORDER BY id DESC LIMIT 8
            """, r =>
        {
            var status = r.GetString(0);
            var headline = r.IsDBNull(1) ? "" : r.GetString(1);
            return string.IsNullOrWhiteSpace(headline) || headline == status ? status : status + "  ·  " + headline;
        }, ("$s", sessionId)));

    public IReadOnlyList<CatalogRow> Search(string? query, string filter, long? shelfId, double? userRating = null, bool unratedOnly = false, long? keepTitleId = null) => Locked(() =>
    {
        var rows = LoadCatalog();
        var alternate = new HashSet<(long TitleId, string Isbn)>();
        using (var isbnReader = Reader("SELECT title_id, isbn13 FROM title_isbns"))
        {
            while (isbnReader.Read())
                alternate.Add((isbnReader.GetInt64(0), isbnReader.GetString(1)));
        }
        var tests = new List<Func<CatalogRow, bool>>();
        if (filter == "unlocated")
            tests.Add(r => r.CopyId != null && r.LocationId == null);
        else if (filter == "needs-isbn")
            tests.Add(r => r.NeedsIsbn);
        else if (filter is MediaKind.Book or MediaKind.Movie or MediaKind.Music or MediaKind.VideoGame or MediaKind.BoardGame)
            tests.Add(r => r.Kind == filter);
        else if (filter == "read")
            tests.Add(r => r.UserRead);
        else if (filter == "unread")
            tests.Add(r => !r.UserRead);
        else if (filter == "shelf" && shelfId is long shelf)
            tests.Add(r => r.LocationId == shelf);
        if (unratedOnly)
            tests.Add(r => r.ShownRating == null);
        else if (userRating is double wanted && UserRatingScale.TryCanonical(wanted, out var canonical))
            tests.Add(r => r.ShownRating is double have && Math.Abs(have - canonical) < 0.001);
        else if (userRating != null)
            tests.Add(_ => false);
        var needle = TextMatch.Normalize(query);
        if (needle.Length > 0)
        {
            var raw = query!.Trim();
            tests.Add(r =>
                TextMatch.Normalize(r.Title).Contains(needle, StringComparison.Ordinal)
                || TextMatch.Normalize(r.Creators).Contains(needle, StringComparison.Ordinal)
                || TextMatch.Normalize(r.Series).Contains(needle, StringComparison.Ordinal)
                || TextMatch.Normalize(r.Isbn13).Contains(needle, StringComparison.Ordinal)
                || TextMatch.Normalize(r.Upc).Contains(needle, StringComparison.Ordinal)
                || TextMatch.Normalize(r.KindLabel).Contains(needle, StringComparison.Ordinal)
                || TextMatch.Normalize(r.ShelfPath).Contains(needle, StringComparison.Ordinal)
                || TextMatch.Normalize(r.UserTags).Contains(needle, StringComparison.Ordinal)
                || TextMatch.Normalize(r.LibibTags).Contains(needle, StringComparison.Ordinal)
                || TextMatch.Normalize(r.Description).Contains(needle, StringComparison.Ordinal)
                || TextMatch.Normalize(r.Notes).Contains(needle, StringComparison.Ordinal)
                || TextMatch.Normalize(r.Publisher).Contains(needle, StringComparison.Ordinal)
                || (r.Isbn13 != null && r.Isbn13.Contains(raw, StringComparison.OrdinalIgnoreCase))
                || (r.Upc != null && r.Upc.Contains(raw, StringComparison.OrdinalIgnoreCase))
                || alternate.Any(pair => pair.TitleId == r.TitleId && pair.Isbn.Contains(raw, StringComparison.OrdinalIgnoreCase)));
        }
        return rows.Where(r => r.TitleId == keepTitleId || tests.All(test => test(r))).ToList();
    });

    public TitleDetail? Title(long id) => Locked(() =>
    {
        using var reader = Reader("""
            SELECT id, title, creators, series, isbn13, isbn10, publisher, publish_date, description, notes, rating, reading_status, cover_path, in_libib,
                   kind, upc, length_of, number_of_discs, ensemble, aspect_ratio, esrb, artists, number_of_players, age_group
            FROM titles WHERE id = $id
            """, ("$id", id));
        if (!reader.Read())
            return null;
        var detail = new TitleDetail(
            reader.GetInt64(0), reader.GetString(1), ReadString(reader, 2), ReadString(reader, 3), ReadString(reader, 4),
            ReadString(reader, 5), ReadString(reader, 6), ReadString(reader, 7), ReadString(reader, 8), ReadString(reader, 9),
            ReadString(reader, 10), ReadString(reader, 11), null,
            ReadString(reader, 12), reader.GetInt64(13) == 1,
            MediaKind.Parse(ReadString(reader, 14)), ReadString(reader, 15), ReadString(reader, 16), ReadString(reader, 17),
            ReadString(reader, 18), ReadString(reader, 19), ReadString(reader, 20), ReadString(reader, 21),
            ReadString(reader, 22), ReadString(reader, 23));
        reader.Dispose();
        var collections = string.Join(", ", QueryStrings("SELECT name FROM title_collections WHERE title_id = $id ORDER BY name", ("$id", id)));
        return detail with { Collections = collections };
    });

    public string? AttachIsbn(long titleId, string raw) => Locked(() =>
    {
        var parsed = IsbnRules.Parse(raw);
        if (!parsed.IsCode || parsed.Isbn13 == null)
            return "Not an ISBN or UPC.";
        var owner = FindTitleIdByCode(parsed);
        if (owner is long other && other != titleId)
        {
            var name = ScalarString("SELECT title FROM titles WHERE id = $id", ("$id", other));
            return "That barcode already belongs to " + name + ".";
        }
        Exec("""
            UPDATE titles SET
              isbn13 = COALESCE(isbn13, $isbn),
              isbn10 = COALESCE(isbn10, $ten),
              upc = COALESCE(upc, $upc),
              online_checked_utc = NULL,
              updated_utc = $t
            WHERE id = $id
            """,
            ("$isbn", parsed.Isbn13), ("$ten", parsed.Isbn10), ("$upc", parsed.Upc), ("$t", Stamp()), ("$id", titleId));
        Exec("INSERT OR REPLACE INTO title_isbns(isbn13, title_id, isbn10) VALUES($isbn, $id, $ten)",
            ("$isbn", parsed.Isbn13), ("$id", titleId), ("$ten", parsed.Isbn10));
        return null;
    });

    public void MoveCopy(long copyId, long? locationId) => Locked(() =>
    {
        if (locationId is long id && ScalarLong("SELECT COUNT(*) FROM locations WHERE id = $id", ("$id", id)) == 0)
            throw new InvalidOperationException("That shelf is gone.");
        Exec("UPDATE copies SET location_id = $l, last_confirmed_utc = $t WHERE id = $id",
            ("$l", locationId), ("$t", Stamp()), ("$id", copyId));
    });

    public string? UpdateBookDetails(long titleId, BookDetails details) => Locked(() =>
    {
        if (ScalarLong("SELECT COUNT(*) FROM titles WHERE id = $id", ("$id", titleId)) == 0)
            return "That book is no longer in the catalog.";
        var title = (details.Title ?? "").Trim();
        if (title.Length == 0)
            return "A book needs a title.";
        if (title.Length > 300)
            return "A title can be up to 300 characters.";
        var kind = details.Kind == null
            ? MediaKind.Parse(ScalarString("SELECT kind FROM titles WHERE id = $id", ("$id", titleId)))
            : MediaKind.Parse(details.Kind);
        Exec("""
            UPDATE titles SET
              title = $title,
              creators = $creators,
              series = $series,
              publisher = $publisher,
              publish_date = $published,
              description = $description,
              notes = $notes,
              kind = $kind,
              length_of = $length,
              number_of_discs = $discs,
              ensemble = $ensemble,
              aspect_ratio = $aspect,
              esrb = $esrb,
              artists = $artists,
              number_of_players = $players,
              age_group = $age,
              details_custom = 1,
              updated_utc = $t
            WHERE id = $id
            """,
            ("$title", title),
            ("$creators", Clean(details.Creators, 300)),
            ("$series", Clean(details.Series, 200)),
            ("$publisher", Clean(details.Publisher, 200)),
            ("$published", Clean(details.PublishDate, 40)),
            ("$description", Clean(details.Description, 8000)),
            ("$notes", Clean(details.Notes, 2000)),
            ("$kind", kind),
            ("$length", Clean(details.LengthOf, 40)),
            ("$discs", Clean(details.NumberOfDiscs, 20)),
            ("$ensemble", Clean(details.Ensemble, 500)),
            ("$aspect", Clean(details.AspectRatio, 40)),
            ("$esrb", Clean(details.Esrb, 20)),
            ("$artists", Clean(details.Artists, 300)),
            ("$players", Clean(details.NumberOfPlayers, 40)),
            ("$age", Clean(details.AgeGroup, 40)),
            ("$t", Stamp()),
            ("$id", titleId));
        return null;
    });

    public string? SetCoverPath(long titleId, string? path) => Locked(() =>
    {
        if (ScalarLong("SELECT COUNT(*) FROM titles WHERE id = $id", ("$id", titleId)) == 0)
            return "That book is no longer in the catalog.";
        Exec("UPDATE titles SET cover_path = $p, cover_locked = 1, details_custom = 1, updated_utc = $t WHERE id = $id",
            ("$p", string.IsNullOrWhiteSpace(path) ? null : path.Trim()), ("$t", Stamp()), ("$id", titleId));
        return null;
    });

    public string? MissingCover(long titleId) => Locked(() => IsbnIf(titleId, "t.cover_locked = 0 AND (t.cover_path IS NULL OR trim(t.cover_path) = '')"));

    public string? MissingSubjects(long titleId) => Locked(() => IsbnIf(titleId, "t.subjects_fetched = 0"));

    public bool SaveFetchedCover(long titleId, string? path) => Locked(() => SaveFetchedCoverCore(titleId, path));

    public int StoreFetchedSubjects(long titleId, IReadOnlyList<string>? subjects) => Locked(() => StoreFetchedSubjectsCore(titleId, subjects));

    public FillNeeds? FillNeedsFor(long titleId) => Locked(() =>
    {
        using var reader = Reader("""
            SELECT COALESCE(NULLIF(trim(t.isbn13), ''), (
                     SELECT i.isbn13 FROM title_isbns i WHERE i.title_id = t.id LIMIT 1
                   )),
                   t.cover_locked = 0 AND (t.cover_path IS NULL OR trim(t.cover_path) = ''),
                   t.details_custom = 0 AND (t.description IS NULL OR trim(t.description) = ''),
                   t.subjects_fetched = 0,
                   t.online_checked_utc IS NOT NULL,
                   t.is_sample = 1,
                   t.kind
            FROM titles t WHERE t.id = $id
            """, ("$id", titleId));
        if (!reader.Read())
            return null;
        return new FillNeeds(ReadString(reader, 0), reader.GetInt64(1) == 1, reader.GetInt64(2) == 1,
            reader.GetInt64(3) == 1, reader.GetInt64(4) == 1, reader.GetInt64(5) == 1,
            MediaKind.Parse(ReadString(reader, 6)));
    });

    /// <summary>
    /// Saves what an online lookup found into empty fields only, so your edits and a cover you chose or removed stay.
    /// <paramref name="complete"/> means every site answered, which stops automatic lookups for this book.
    /// </summary>
    /// <param name="generation">The <see cref="Generation"/> the lookup began in. A lookup from before a restore is dropped.</param>
    public FillResult ApplyFetched(long titleId, string? description, IReadOnlyList<string>? subjects, string? coverPath, bool complete, long? generation = null) => Locked(() =>
    {
        if (generation is long began && began != Generation)
            return FillResult.Skipped;
        if (ScalarLong("SELECT COUNT(*) FROM titles WHERE id = $id", ("$id", titleId)) == 0)
            return FillResult.Skipped;
        using var tx = _conn.BeginTransaction();
        var descriptionAdded = FillDescription(titleId, description);
        var coverAdded = SaveFetchedCoverCore(titleId, coverPath);
        var tagsAdded = StoreFetchedSubjectsCore(titleId, subjects) > 0;
        if (complete)
            Exec("UPDATE titles SET online_checked_utc = $t WHERE id = $id", ("$t", Stamp()), ("$id", titleId));
        tx.Commit();
        return new FillResult(true, coverAdded, descriptionAdded, tagsAdded, complete);
    });

    public string? DeleteTitle(long titleId) => Locked(() =>
    {
        if (ScalarLong("SELECT COUNT(*) FROM titles WHERE id = $id", ("$id", titleId)) == 0)
            return "That book is no longer in the catalog.";
        Exec("""
            DELETE FROM pending_actions
            WHERE scan_id IN (
              SELECT s.id FROM scans s
              WHERE s.title_id = $id
                 OR s.copy_id IN (SELECT c.id FROM copies c WHERE c.title_id = $id)
            )
            """, ("$id", titleId));
        Exec("""
            UPDATE scans
            SET title_id = NULL, copy_id = NULL, undoable = 0
            WHERE title_id = $id
               OR copy_id IN (SELECT id FROM copies WHERE title_id = $id)
            """, ("$id", titleId));
        Exec("DELETE FROM titles WHERE id = $id", ("$id", titleId));
        return null;
    });

    public string? CurrentCoverPath(long titleId) => Locked(() =>
        ScalarString("SELECT cover_path FROM titles WHERE id = $id", ("$id", titleId)));

    public bool CoverInUse(string? path) => Locked(() =>
        !string.IsNullOrWhiteSpace(path) && ScalarLong("SELECT COUNT(*) FROM titles WHERE cover_path = $p", ("$p", path)) > 0);

    public string? SetUserRating(long titleId, double? rating) => Locked(() =>
    {
        if (ScalarLong("SELECT COUNT(*) FROM titles WHERE id = $id", ("$id", titleId)) == 0)
            return "That book is no longer in the catalog.";
        if (rating is double value)
        {
            if (!UserRatingScale.TryCanonical(value, out var canonical))
                return "Choose 0.5, 1, 1.5, 2, 2.5, 3, 3.5, 4, 4.5, or 5.";
            Exec("UPDATE titles SET user_rating = $r, user_read = 1, updated_utc = $t WHERE id = $id",
                ("$r", canonical), ("$t", Stamp()), ("$id", titleId));
        }
        else
        {
            Exec("UPDATE titles SET user_rating = NULL, updated_utc = $t WHERE id = $id",
                ("$t", Stamp()), ("$id", titleId));
        }
        return null;
    });

    public string? SetRead(long titleId, bool read) => Locked(() =>
    {
        if (ScalarLong("SELECT COUNT(*) FROM titles WHERE id = $id", ("$id", titleId)) == 0)
            return "That book is no longer in the catalog.";
        Exec("UPDATE titles SET user_read = $r, updated_utc = $t WHERE id = $id",
            ("$r", read ? 1 : 0), ("$t", Stamp()), ("$id", titleId));
        return null;
    });

    public IReadOnlyList<string> UserTags(long titleId) => Locked(() =>
        QueryStrings("SELECT tag FROM title_tags WHERE title_id = $id ORDER BY tag", ("$id", titleId)));

    public string? AddUserTags(long titleId, string? raw) => Locked(() =>
    {
        if (ScalarLong("SELECT COUNT(*) FROM titles WHERE id = $id", ("$id", titleId)) == 0)
            return "That book is no longer in the catalog.";
        var parts = (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return "Type a tag first.";
        var existing = QueryStrings("SELECT tag FROM title_tags WHERE title_id = $id", ("$id", titleId))
            .Select(TextMatch.Normalize)
            .ToHashSet(StringComparer.Ordinal);
        var added = 0;
        var tooLong = false;
        var duplicate = false;
        foreach (var part in parts)
        {
            var display = string.Join(' ', part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (display.Length == 0)
                continue;
            if (display.Length > 48)
            {
                tooLong = true;
                continue;
            }
            var key = TextMatch.Normalize(display);
            if (key.Length == 0)
                continue;
            if (!existing.Add(key))
            {
                duplicate = true;
                continue;
            }
            Exec("INSERT INTO title_tags(title_id, tag) VALUES($id, $tag)", ("$id", titleId), ("$tag", display));
            added++;
        }
        if (added > 0)
        {
            Exec("UPDATE titles SET updated_utc = $t WHERE id = $id", ("$t", Stamp()), ("$id", titleId));
            return null;
        }
        if (tooLong)
            return "A tag can be up to 48 characters.";
        if (duplicate)
            return "That tag is already on this book.";
        return "Type a tag first.";
    });

    public void RemoveUserTag(long titleId, string tag) => Locked(() =>
    {
        var key = TextMatch.Normalize(tag);
        foreach (var existing in QueryStrings("SELECT tag FROM title_tags WHERE title_id = $id", ("$id", titleId)))
        {
            if (TextMatch.Normalize(existing) == key)
                Exec("DELETE FROM title_tags WHERE title_id = $id AND tag = $tag", ("$id", titleId), ("$tag", existing));
        }
        Exec("UPDATE titles SET updated_utc = $t WHERE id = $id", ("$t", Stamp()), ("$id", titleId));
    });

    public IReadOnlyList<GapRow> NotScannedOnShelf(long sessionId, long locationId) => Locked(() => ShelfGaps(sessionId, locationId));

    public IReadOnlyList<DisagreementRow> Disagreements(long sessionId, long locationId) => Locked(() => ShelfDisagreements(sessionId, locationId));

    public IReadOnlyList<GapRow> UnseenInSession(long sessionId) => Locked(() => Unseen(sessionId));

    public IReadOnlyList<GapRow> Extras(long sessionId) => Locked(() =>
        Query(ExtraSql, MapGap, ("$s", sessionId)));

    public IReadOnlyList<GapRow> NotInLibib() => Locked(() =>
        Query("""
            SELECT c.id, t.id, t.title, t.creators, t.isbn13
            FROM titles t JOIN copies c ON c.title_id = t.id
            WHERE t.in_libib = 0
            ORDER BY t.title
            """, MapGap));

    public IReadOnlyList<CatalogRow> AllCopies() => Locked(LoadCatalog);

    private void EnsureSchema()
    {
        var version = ScalarLong("PRAGMA user_version");
        if (version > SchemaVersion)
            throw new InvalidDataException("This library file was written by a newer version of " + AppBrand.Name + ".");
        Exec("""
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS titles (
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
            CREATE TABLE IF NOT EXISTS title_isbns (
              isbn13 TEXT PRIMARY KEY,
              title_id INTEGER NOT NULL REFERENCES titles(id) ON DELETE CASCADE,
              isbn10 TEXT
            );
            CREATE TABLE IF NOT EXISTS title_collections (
              title_id INTEGER NOT NULL REFERENCES titles(id) ON DELETE CASCADE,
              name TEXT NOT NULL,
              PRIMARY KEY (title_id, name)
            );
            CREATE TABLE IF NOT EXISTS locations (
              id INTEGER PRIMARY KEY,
              name TEXT NOT NULL,
              parent_id INTEGER REFERENCES locations(id),
              sort_order INTEGER NOT NULL,
              code TEXT NOT NULL UNIQUE,
              is_sample INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS retired_location_codes (code TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS copies (
              id INTEGER PRIMARY KEY,
              title_id INTEGER NOT NULL REFERENCES titles(id) ON DELETE CASCADE,
              location_id INTEGER REFERENCES locations(id),
              copy_index INTEGER NOT NULL,
              last_confirmed_utc TEXT,
              last_session_id INTEGER
            );
            CREATE TABLE IF NOT EXISTS sessions (
              id INTEGER PRIMARY KEY,
              mode TEXT NOT NULL,
              started_utc TEXT NOT NULL,
              finished_utc TEXT,
              abandoned INTEGER NOT NULL DEFAULT 0,
              current_location_id INTEGER,
              last_scan_utc TEXT,
              is_active INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS scans (
              id INTEGER PRIMARY KEY,
              session_id INTEGER NOT NULL,
              raw_text TEXT NOT NULL,
              isbn13 TEXT,
              title_id INTEGER,
              copy_id INTEGER,
              location_id INTEGER,
              result TEXT NOT NULL,
              headline TEXT NOT NULL,
              author TEXT,
              status TEXT NOT NULL,
              detail TEXT,
              scanned_utc TEXT NOT NULL,
              undone INTEGER NOT NULL DEFAULT 0,
              counts_as_seen INTEGER NOT NULL DEFAULT 0,
              undoable INTEGER NOT NULL DEFAULT 1,
              undo_payload TEXT,
              cover_path TEXT
            );
            CREATE TABLE IF NOT EXISTS pending_actions (
              id INTEGER PRIMARY KEY,
              session_id INTEGER NOT NULL,
              scan_id INTEGER NOT NULL,
              kind TEXT NOT NULL,
              payload TEXT NOT NULL,
              created_utc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_titles_libib ON titles(libib_id) WHERE libib_id IS NOT NULL AND libib_id <> '';
            """);
        if (version < 2)
        {
            if (!HasColumn("titles", "user_rating"))
                Exec("ALTER TABLE titles ADD COLUMN user_rating REAL");
            Exec("""
                CREATE TABLE IF NOT EXISTS title_tags (
                  title_id INTEGER NOT NULL REFERENCES titles(id) ON DELETE CASCADE,
                  tag TEXT NOT NULL,
                  PRIMARY KEY (title_id, tag)
                );
                """);
            Exec("PRAGMA user_version = 2;");
        }
        if (version < 3)
        {
            if (!HasColumn("titles", "details_custom"))
                Exec("ALTER TABLE titles ADD COLUMN details_custom INTEGER NOT NULL DEFAULT 0");
            Exec("PRAGMA user_version = 3;");
        }
        if (version < 4)
        {
            if (!HasColumn("titles", "cover_locked"))
                Exec("ALTER TABLE titles ADD COLUMN cover_locked INTEGER NOT NULL DEFAULT 0");
            if (!HasColumn("titles", "subjects_fetched"))
                Exec("ALTER TABLE titles ADD COLUMN subjects_fetched INTEGER NOT NULL DEFAULT 0");
            Exec("PRAGMA user_version = 4;");
        }
        if (version < 5)
        {
            using var tx = _conn.BeginTransaction();
            if (!HasColumn("titles", "user_read"))
                Exec("ALTER TABLE titles ADD COLUMN user_read INTEGER NOT NULL DEFAULT 0");
            if (!HasColumn("titles", "online_checked_utc"))
                Exec("ALTER TABLE titles ADD COLUMN online_checked_utc TEXT");
            Exec("UPDATE titles SET user_read = 1 WHERE user_rating IS NOT NULL");
            // Books whose earlier lookup found no subjects get one more try.
            Exec("""
                UPDATE titles SET subjects_fetched = 0
                WHERE subjects_fetched = 1
                  AND NOT EXISTS (SELECT 1 FROM title_tags g WHERE g.title_id = titles.id)
                """);
            Exec("PRAGMA user_version = 5;");
            tx.Commit();
        }
        if (version < 6)
        {
            if (!HasColumn("titles", "kind"))
                Exec("ALTER TABLE titles ADD COLUMN kind TEXT NOT NULL DEFAULT 'book'");
            foreach (var column in new[] { "upc", "length_of", "number_of_discs", "ensemble", "aspect_ratio", "esrb", "artists", "number_of_players", "age_group" })
            {
                if (!HasColumn("titles", column))
                    Exec("ALTER TABLE titles ADD COLUMN " + column + " TEXT");
            }
            Exec("PRAGMA user_version = 6;");
        }
    }

    private string? IsbnIf(long titleId, string predicate)
    {
        var isbn = ScalarString("""
            SELECT COALESCE(NULLIF(trim(t.isbn13), ''), (
              SELECT i.isbn13 FROM title_isbns i WHERE i.title_id = t.id LIMIT 1
            ))
            FROM titles t
            WHERE t.id = $id AND 
            """ + predicate, ("$id", titleId));
        return string.IsNullOrWhiteSpace(isbn) ? null : isbn;
    }

    private bool FillDescription(long titleId, string? description)
    {
        var text = Clean(description, 8000);
        if (text == null)
            return false;
        return ExecCount("""
            UPDATE titles SET description = $d, updated_utc = $t
            WHERE id = $id AND details_custom = 0 AND (description IS NULL OR trim(description) = '')
            """, ("$d", text), ("$t", Stamp()), ("$id", titleId)) > 0;
    }

    /// <summary>A lookup with no usable subjects leaves the book open to a later answer.</summary>
    private int StoreFetchedSubjectsCore(long titleId, IReadOnlyList<string>? subjects)
    {
        if (ScalarLong("SELECT COUNT(*) FROM titles WHERE id = $id", ("$id", titleId)) == 0)
            return 0;
        if (ScalarLong("SELECT subjects_fetched FROM titles WHERE id = $id", ("$id", titleId)) == 1)
            return 0;
        var chosen = OpenLibraryClient.ChooseSubjects(subjects);
        if (chosen.Count == 0)
            return 0;
        var existing = QueryStrings("SELECT tag FROM title_tags WHERE title_id = $id", ("$id", titleId))
            .Select(TextMatch.Normalize)
            .ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var display in chosen)
        {
            if (display.Length > 48)
                continue;
            var key = TextMatch.Normalize(display);
            if (key.Length == 0 || !existing.Add(key))
                continue;
            Exec("INSERT INTO title_tags(title_id, tag) VALUES($id, $tag)", ("$id", titleId), ("$tag", display));
            added++;
        }
        Exec("UPDATE titles SET subjects_fetched = 1 WHERE id = $id", ("$id", titleId));
        if (added > 0)
            Exec("UPDATE titles SET updated_utc = $t WHERE id = $id", ("$t", Stamp()), ("$id", titleId));
        return added;
    }

    private bool SaveFetchedCoverCore(long titleId, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE titles
            SET cover_path = $p, updated_utc = $t
            WHERE id = $id
              AND cover_locked = 0
              AND (cover_path IS NULL OR trim(cover_path) = '')
            """;
        cmd.Parameters.AddWithValue("$p", path.Trim());
        cmd.Parameters.AddWithValue("$t", Stamp());
        cmd.Parameters.AddWithValue("$id", titleId);
        return cmd.ExecuteNonQuery() > 0;
    }

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();
        return text.Length <= max ? text : text[..max];
    }

    private bool HasColumn(string table, string column)
    {
        using var reader = Reader("PRAGMA table_info(" + table + ")");
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private ImportPreview BuildPreview(IReadOnlyList<LibibRow> rows, string? defaultKind = null, IReadOnlyList<string>? headers = null)
    {
        var fallback = string.IsNullOrWhiteSpace(defaultKind)
            ? headers == null ? MediaKind.Book : MediaKind.FromHeaders(headers)
            : MediaKind.Parse(defaultKind);
        var resolved = rows.Select(row => LibibCsv.ResolveRow(row, fallback)).ToList();
        var books = LibibCsv.Resolve(rows, defaultKind, headers);
        var errors = new List<string>();
        var invalid = resolved.Count(r => r.CopiesInvalid);
        if (invalid > 0)
            errors.Add(invalid == 1
                ? "One row has a copy count that is not a whole number."
                : invalid + " rows have a copy count that is not a whole number.");
        if (books.Count == 0 && resolved.Count > 0)
            errors.Add("That file has no titles to import.");
        var repeated = resolved.Where(r => r.Isbn13 != null)
            .GroupBy(r => r.Kind + ":" + r.Isbn13)
            .Count(g => g.Count() > 1);
        var missing = books.Count(b => b.Isbn13 == null);
        var notes = 0;
        foreach (var book in books.Where(b => !b.CopiesInvalid && b.Isbn13 != null))
        {
            var existing = FindTitleIdByIsbn(book.Isbn13!);
            if (existing is long id)
            {
                var have = (int)ScalarLong("SELECT COUNT(*) FROM copies WHERE title_id = $id", ("$id", id));
                if (book.Copies < have)
                    notes++;
            }
        }
        var collections = books.Select(b => b.Collection).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).Cast<string>().OrderBy(c => c).ToList();
        var kinds = books.GroupBy(b => b.Kind, StringComparer.Ordinal)
            .OrderBy(g => Array.IndexOf(MediaKind.All, g.Key))
            .Select(g => new KindCount(g.Key, g.Count()))
            .ToList();
        return new ImportPreview(books.Count, missing, repeated, 0, invalid, notes, collections, errors, errors.Count == 0 && books.Count > 0, kinds);
    }

    private void CommitResolved(IReadOnlyList<ResolvedBook> books)
    {
        foreach (var book in books)
        {
            long? titleId = null;
            if (book.Isbn13 != null)
                titleId = FindTitleIdByIsbn(book.Isbn13);
            if (titleId == null && book.LibibId != null)
                titleId = ScalarNullable("SELECT id FROM titles WHERE libib_id = $id", ("$id", book.LibibId));
            if (titleId == null && book.Isbn13 == null)
                titleId = FindLoose(book);

            if (titleId is long existing)
                UpdateExisting(existing, book);
            else
                InsertNew(book);
        }
    }

    private void UpdateExisting(long titleId, ResolvedBook book)
    {
        var custom = ScalarLong("SELECT details_custom FROM titles WHERE id = $id", ("$id", titleId)) == 1;
        if (custom)
        {
            Exec("""
                UPDATE titles SET
                  libib_id = COALESCE($libib, libib_id),
                  isbn13 = COALESCE($isbn, isbn13),
                  isbn10 = COALESCE($ten, isbn10),
                  upc = COALESCE($upc, upc),
                  kind = $kind,
                  tags = $tags,
                  reading_status = $status,
                  rating = $rating,
                  in_libib = 1,
                  updated_utc = $t
                WHERE id = $id
                """,
                ("$libib", book.LibibId), ("$isbn", book.Isbn13), ("$ten", book.Isbn10), ("$upc", book.Upc),
                ("$kind", book.Kind), ("$tags", book.Tags),
                ("$status", book.Status), ("$rating", book.Rating), ("$t", Stamp()), ("$id", titleId));
        }
        else
        {
            Exec("""
                UPDATE titles SET
                  libib_id = COALESCE($libib, libib_id),
                  isbn13 = COALESCE($isbn, isbn13),
                  isbn10 = COALESCE($ten, isbn10),
                  upc = COALESCE($upc, upc),
                  kind = $kind,
                  title = $title,
                  creators = $creators,
                  publisher = $publisher,
                  publish_date = $published,
                  series = $series,
                  tags = $tags,
                  notes = $notes,
                  description = COALESCE($description, description),
                  length_of = COALESCE($length, length_of),
                  number_of_discs = COALESCE($discs, number_of_discs),
                  ensemble = COALESCE($ensemble, ensemble),
                  aspect_ratio = COALESCE($aspect, aspect_ratio),
                  esrb = COALESCE($esrb, esrb),
                  artists = COALESCE($artists, artists),
                  number_of_players = COALESCE($players, number_of_players),
                  age_group = COALESCE($age, age_group),
                  reading_status = $status,
                  rating = $rating,
                  in_libib = 1,
                  updated_utc = $t
                WHERE id = $id
                """,
                ("$libib", book.LibibId), ("$isbn", book.Isbn13), ("$ten", book.Isbn10), ("$upc", book.Upc),
                ("$kind", book.Kind), ("$title", book.Title),
                ("$creators", book.Creators), ("$publisher", book.Publisher), ("$published", book.PublishDate),
                ("$series", book.Series), ("$tags", book.Tags), ("$notes", book.Notes), ("$description", book.Description),
                ("$length", book.LengthOf), ("$discs", book.NumberOfDiscs), ("$ensemble", book.Ensemble),
                ("$aspect", book.AspectRatio), ("$esrb", book.Esrb), ("$artists", book.Artists),
                ("$players", book.NumberOfPlayers), ("$age", book.AgeGroup),
                ("$status", book.Status), ("$rating", book.Rating), ("$t", Stamp()), ("$id", titleId));
        }
        if (book.Isbn13 != null)
            Exec("INSERT OR REPLACE INTO title_isbns(isbn13, title_id, isbn10) VALUES($isbn, $id, $ten)",
                ("$isbn", book.Isbn13), ("$id", titleId), ("$ten", book.Isbn10));
        RememberCollection(titleId, book.Collection);
        var have = (int)ScalarLong("SELECT COUNT(*) FROM copies WHERE title_id = $id", ("$id", titleId));
        if (book.Copies > have)
        {
            for (var i = 0; i < book.Copies - have; i++)
                Exec("INSERT INTO copies(title_id, location_id, copy_index) VALUES($id, NULL, $n)",
                    ("$id", titleId), ("$n", have + i + 1));
            Exec("UPDATE titles SET expected_copies = $n WHERE id = $id", ("$n", book.Copies), ("$id", titleId));
        }
        else
        {
            var expected = Math.Max(have, book.Copies);
            Exec("UPDATE titles SET expected_copies = $n WHERE id = $id", ("$n", expected), ("$id", titleId));
        }
    }

    private void InsertNew(ResolvedBook book)
    {
        Exec("""
            INSERT INTO titles(libib_id, isbn13, isbn10, upc, kind, title, creators, publisher, publish_date, series, tags, notes, description, length_of, number_of_discs, ensemble, aspect_ratio, esrb, artists, number_of_players, age_group, expected_copies, reading_status, rating, in_libib, is_sample, created_utc, updated_utc)
            VALUES($libib, $isbn, $ten, $upc, $kind, $title, $creators, $publisher, $published, $series, $tags, $notes, $description, $length, $discs, $ensemble, $aspect, $esrb, $artists, $players, $age, $copies, $status, $rating, 1, 0, $t, $t)
            """,
            ("$libib", book.LibibId), ("$isbn", book.Isbn13), ("$ten", book.Isbn10), ("$upc", book.Upc), ("$kind", book.Kind),
            ("$title", book.Title), ("$creators", book.Creators), ("$publisher", book.Publisher), ("$published", book.PublishDate),
            ("$series", book.Series), ("$tags", book.Tags), ("$notes", book.Notes), ("$description", book.Description),
            ("$length", book.LengthOf), ("$discs", book.NumberOfDiscs), ("$ensemble", book.Ensemble), ("$aspect", book.AspectRatio),
            ("$esrb", book.Esrb), ("$artists", book.Artists), ("$players", book.NumberOfPlayers), ("$age", book.AgeGroup),
            ("$copies", book.Copies), ("$status", book.Status), ("$rating", book.Rating), ("$t", Stamp()));
        var id = LastId();
        if (book.Isbn13 != null)
            Exec("INSERT OR REPLACE INTO title_isbns(isbn13, title_id, isbn10) VALUES($isbn, $id, $ten)",
                ("$isbn", book.Isbn13), ("$id", id), ("$ten", book.Isbn10));
        RememberCollection(id, book.Collection);
        for (var i = 1; i <= book.Copies; i++)
            Exec("INSERT INTO copies(title_id, location_id, copy_index) VALUES($id, NULL, $n)", ("$id", id), ("$n", i));
    }

    private long? FindLoose(ResolvedBook book)
    {
        var title = TextMatch.Normalize(book.Title);
        var creators = TextMatch.Normalize(book.Creators);
        var collection = book.Collection ?? "";
        using var reader = Reader("""
            SELECT t.id, t.title, t.creators
            FROM titles t
            LEFT JOIN title_isbns i ON i.title_id = t.id
            WHERE t.isbn13 IS NULL AND i.isbn13 IS NULL AND t.in_libib = 1
            """);
        while (reader.Read())
        {
            if (TextMatch.Normalize(reader.GetString(1)) != title || TextMatch.Normalize(ReadString(reader, 2)) != creators)
                continue;
            var id = reader.GetInt64(0);
            var names = QueryStrings("SELECT name FROM title_collections WHERE title_id = $id", ("$id", id));
            if (names.Count == 0 && string.IsNullOrEmpty(collection))
                return id;
            if (names.Any(n => string.Equals(n, collection, StringComparison.OrdinalIgnoreCase)))
                return id;
        }
        return null;
    }

    private void RememberCollection(long titleId, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        Exec("INSERT OR IGNORE INTO title_collections(title_id, name) VALUES($id, $n)", ("$id", titleId), ("$n", name.Trim()));
    }

    private void WipeCatalog()
    {
        Exec("DELETE FROM pending_actions");
        Exec("DELETE FROM scans");
        Exec("DELETE FROM sessions");
        Exec("DELETE FROM copies");
        Exec("DELETE FROM title_isbns");
        Exec("DELETE FROM title_collections");
        Exec("DELETE FROM title_tags");
        Exec("DELETE FROM titles");
        Exec("DELETE FROM locations");
        Exec("DELETE FROM retired_location_codes");
    }

    private bool HasTitlesCore() => ScalarLong("SELECT COUNT(*) FROM titles") > 0;

    private List<LocationInfo> LoadLocations()
    {
        var raw = Query("SELECT id, name, parent_id, sort_order, code FROM locations ORDER BY sort_order, id", r =>
            (r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? (long?)null : r.GetInt64(2), r.GetInt32(3), r.GetString(4)));
        var byParent = raw.GroupBy(r => r.Item3).ToDictionary(g => g.Key ?? -1, g => g.OrderBy(x => x.Item4).ToList());
        var list = new List<LocationInfo>();
        void Walk(long? parent, string prefix, int depth)
        {
            if (!byParent.TryGetValue(parent ?? -1, out var kids))
                return;
            foreach (var kid in kids)
            {
                var path = prefix.Length == 0 ? kid.Item2 : prefix + " / " + kid.Item2;
                list.Add(new LocationInfo(kid.Item1, kid.Item2, kid.Item3, kid.Item4, kid.Item5, path, depth));
                Walk(kid.Item1, path, depth + 1);
            }
        }
        Walk(null, "", 0);
        return list;
    }

    private string NextCode()
    {
        var numbers = QueryStrings("SELECT code FROM locations UNION SELECT code FROM retired_location_codes")
            .Select(code => int.TryParse(code.Length > 4 ? code[4..] : "", out var n) ? n : 0);
        var next = numbers.DefaultIfEmpty(0).Max() + 1;
        return "LOC-" + next.ToString("D4", CultureInfo.InvariantCulture);
    }

    private bool IsDescendant(long maybeChild, long ancestor)
    {
        var parent = ScalarNullable("SELECT parent_id FROM locations WHERE id = $id", ("$id", maybeChild));
        var guard = 0;
        while (parent is long id && guard++ < 64)
        {
            if (id == ancestor)
                return true;
            parent = ScalarNullable("SELECT parent_id FROM locations WHERE id = $id", ("$id", id));
        }
        return false;
    }

    private List<CatalogRow> LoadCatalog()
    {
        var locations = LoadLocations().ToDictionary(l => l.Id);
        var rows = new List<CatalogRow>();
        using var reader = Reader("""
            SELECT t.id, t.title, t.creators, t.series, t.isbn13, t.rating, t.reading_status, t.in_libib, t.cover_path,
                   c.id, c.location_id, c.last_confirmed_utc,
                   (SELECT group_concat(name, ', ') FROM (SELECT name FROM title_collections tc WHERE tc.title_id = t.id ORDER BY name)),
                   t.user_rating,
                   (SELECT group_concat(tag, ', ') FROM (SELECT tag FROM title_tags tt WHERE tt.title_id = t.id ORDER BY tag)),
                   t.tags, t.description, t.notes, t.publisher, t.user_read, t.kind, t.upc
            FROM titles t
            LEFT JOIN copies c ON c.title_id = t.id
            ORDER BY t.title, c.copy_index
            """);
        while (reader.Read())
        {
            var titleId = reader.GetInt64(0);
            long? locationId = reader.IsDBNull(10) ? null : reader.GetInt64(10);
            string? path = locationId is long loc && locations.TryGetValue(loc, out var info) ? info.Path : null;
            var isbn = ReadString(reader, 4);
            var collections = ReadString(reader, 12);
            var upc = ReadString(reader, 21);
            rows.Add(new CatalogRow(
                reader.IsDBNull(9) ? null : reader.GetInt64(9),
                titleId,
                reader.GetString(1),
                ReadString(reader, 2),
                ReadString(reader, 3),
                isbn,
                path,
                locationId,
                ReadString(reader, 11),
                ReadString(reader, 5),
                ReadString(reader, 6),
                reader.GetInt64(7) == 1,
                string.IsNullOrEmpty(isbn) && string.IsNullOrEmpty(upc),
                ReadString(reader, 8),
                collections,
                reader.IsDBNull(13) ? null : reader.GetDouble(13),
                ReadString(reader, 14),
                ReadString(reader, 15),
                ReadString(reader, 16),
                ReadString(reader, 17),
                ReadString(reader, 18),
                reader.GetInt64(19) == 1,
                MediaKind.Parse(ReadString(reader, 20)),
                upc));
        }
        return rows;
    }

    internal long InsertTitleRaw(string title, string? creators, string? isbn13, string? isbn10, int copies, long? locationId, bool sample, string? series = null, string? kind = null, string? upc = null)
    {
        Exec("""
            INSERT INTO titles(isbn13, isbn10, upc, kind, title, creators, series, expected_copies, in_libib, is_sample, created_utc, updated_utc)
            VALUES($isbn, $ten, $upc, $kind, $title, $creators, $series, $copies, 1, $sample, $t, $t)
            """,
            ("$isbn", isbn13), ("$ten", isbn10), ("$upc", upc), ("$kind", MediaKind.Parse(kind)),
            ("$title", title), ("$creators", creators), ("$series", series),
            ("$copies", Math.Max(copies, 1)), ("$sample", sample ? 1 : 0), ("$t", Stamp()));
        var id = LastId();
        if (isbn13 != null)
            Exec("INSERT OR REPLACE INTO title_isbns(isbn13, title_id, isbn10) VALUES($isbn, $id, $ten)",
                ("$isbn", isbn13), ("$id", id), ("$ten", isbn10));
        for (var i = 1; i <= Math.Max(copies, 1); i++)
            Exec("INSERT INTO copies(title_id, location_id, copy_index) VALUES($id, $loc, $n)",
                ("$id", id), ("$loc", i == 1 ? locationId : null), ("$n", i));
        return id;
    }

    internal long InsertLocationRaw(string name, long? parentId, bool sample)
    {
        var sort = ScalarLong("SELECT COALESCE(MAX(sort_order), 0) + 1 FROM locations WHERE IFNULL(parent_id, -1) = $p", ("$p", parentId ?? -1));
        var code = NextCode();
        Exec("INSERT INTO locations(name, parent_id, sort_order, code, is_sample) VALUES($n, $p, $s, $c, $sample)",
            ("$n", name), ("$p", parentId), ("$s", sort), ("$c", code), ("$sample", sample ? 1 : 0));
        return LastId();
    }

    private const string ExtraSql = """
        SELECT COALESCE(c.id, 0), COALESCE(t.id, 0), COALESCE(t.title, s.headline), t.creators, s.isbn13
        FROM scans s
        LEFT JOIN titles t ON t.id = s.title_id
        LEFT JOIN copies c ON c.id = s.copy_id
        WHERE s.session_id = $s AND s.result = 'extra' AND s.undone = 0
        ORDER BY s.id
        """;

    private List<GapRow> ShelfGaps(long sessionId, long locationId) => Query("""
        SELECT c.id, t.id, t.title, t.creators, t.isbn13
        FROM copies c JOIN titles t ON t.id = c.title_id
        WHERE c.location_id = $l
          AND NOT EXISTS (
            SELECT 1 FROM scans s
            WHERE s.session_id = $s AND s.copy_id = c.id AND s.undone = 0
              AND s.location_id = $l
              AND s.result IN ('on_shelf', 'placed', 'moved')
          )
        ORDER BY t.title
        """, MapGap, ("$s", sessionId), ("$l", locationId));

    private List<DisagreementRow> ShelfDisagreements(long sessionId, long locationId)
    {
        var locations = LoadLocations().ToDictionary(l => l.Id);
        return Query("""
            SELECT t.title, t.creators, s.status, c.location_id
            FROM scans s
            LEFT JOIN titles t ON t.id = s.title_id
            LEFT JOIN copies c ON c.id = s.copy_id
            WHERE s.session_id = $s AND s.undone = 0 AND s.location_id = $l
              AND s.result IN ('seen_unchanged', 'already_shelved')
            ORDER BY s.id
            """, r =>
        {
            long? recorded = r.IsDBNull(3) ? null : r.GetInt64(3);
            var path = recorded is long id && locations.TryGetValue(id, out var info) ? info.Path : null;
            return new DisagreementRow(r.IsDBNull(0) ? "Unknown book" : r.GetString(0), ReadString(r, 1), r.GetString(2), path);
        }, ("$s", sessionId), ("$l", locationId));
    }

    private List<GapRow> Unseen(long sessionId) => Query("""
        SELECT c.id, t.id, t.title, t.creators, t.isbn13
        FROM copies c JOIN titles t ON t.id = c.title_id
        WHERE t.in_libib = 1
          AND NOT EXISTS (
            SELECT 1 FROM scans s
            WHERE s.session_id = $s AND s.copy_id = c.id AND s.undone = 0 AND s.counts_as_seen = 1
          )
        ORDER BY t.title
        """, MapGap, ("$s", sessionId));

    private static GapRow MapGap(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? "" : r.GetString(2), ReadString(r, 3), ReadString(r, 4));

    private (int Confirmed, int NotInLibib, int Extras) Counts(long sessionId)
    {
        var confirmed = (int)ScalarLong("""
            SELECT COUNT(*) FROM scans
            WHERE session_id = $s AND undone = 0
              AND result IN ('on_shelf', 'placed', 'moved', 'already_shelved', 'seen_unchanged', 'added_copy')
            """, ("$s", sessionId));
        var missing = (int)ScalarLong("""
            SELECT COUNT(*) FROM scans
            WHERE session_id = $s AND undone = 0 AND result IN ('not_in_library', 'kept_separate')
            """, ("$s", sessionId));
        var extras = (int)ScalarLong("""
            SELECT COUNT(*) FROM scans WHERE session_id = $s AND undone = 0 AND result = 'extra'
            """, ("$s", sessionId));
        return (confirmed, missing, extras);
    }

    private T Locked<T>(Func<T> action)
    {
        lock (_gate)
            return action();
    }

    private void Locked(Action action)
    {
        lock (_gate)
            action();
    }

    private string Stamp() => _clock.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private string? GetSetting(string key) => Locked(() => ScalarString("SELECT value FROM settings WHERE key = $k", ("$k", key)));

    private void SetSetting(string key, string value) => Locked(() =>
        Exec("INSERT INTO settings(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$k", key), ("$v", value)));

    private int GetInt(string key, int fallback) =>
        int.TryParse(GetSetting(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    private static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private void Exec(string sql, params (string Name, object? Value)[] args) => ExecCount(sql, args);

    private int ExecCount(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, args);
        return cmd.ExecuteNonQuery();
    }

    private long LastId() => ScalarLong("SELECT last_insert_rowid()");

    private long ScalarLong(string sql, params (string Name, object? Value)[] args)
    {
        var value = ScalarObj(sql, args);
        return value switch
        {
            long n => n,
            int n => n,
            _ => Convert.ToInt64(value ?? 0, CultureInfo.InvariantCulture)
        };
    }

    private long? ScalarNullable(string sql, params (string Name, object? Value)[] args)
    {
        var value = ScalarObj(sql, args);
        return value == null ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private string? ScalarString(string sql, params (string Name, object? Value)[] args)
    {
        var value = ScalarObj(sql, args);
        return value as string;
    }

    private object? ScalarObj(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, args);
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private SqliteDataReader Reader(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, args);
        return cmd.ExecuteReader();
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using var reader = Reader(sql, args);
        var list = new List<T>();
        while (reader.Read())
            list.Add(map(reader));
        return list;
    }

    private List<string> QueryStrings(string sql, params (string Name, object? Value)[] args)
    {
        using var reader = Reader(sql, args);
        var list = new List<string>();
        while (reader.Read())
            if (!reader.IsDBNull(0))
                list.Add(reader.GetString(0));
        return list;
    }

    private static void Bind(SqliteCommand cmd, (string Name, object? Value)[] args)
    {
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static string? ReadString(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    internal static string Json(UndoSnapshot snapshot) => JsonSerializer.Serialize(snapshot);

    internal static UndoSnapshot? ReadJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<UndoSnapshot>(json);
}
