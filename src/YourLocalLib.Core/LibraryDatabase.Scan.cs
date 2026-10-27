using System.Text.Json;

namespace YourLocalLib.Core;

public sealed partial class LibraryDatabase
{
    private sealed class SessionRow
    {
        public long Id { get; init; }
        public string Mode { get; init; } = SessionMode.Place;
        public long? CurrentLocationId { get; init; }
        public string? FinishedUtc { get; init; }
        public bool Active { get; init; }
    }

    private sealed class CopyState
    {
        public long Id { get; init; }
        public long TitleId { get; init; }
        public long? LocationId { get; init; }
        public string? LastConfirmed { get; init; }
        public int CopyIndex { get; init; }
    }

    private sealed class TitleState
    {
        public long Id { get; init; }
        public string Title { get; init; } = "";
        public string? Creators { get; init; }
        public string? Isbn13 { get; init; }
        public string? CoverPath { get; init; }
        public int Expected { get; init; }
        public bool InLibib { get; init; }
    }

    public ScanOutcome Submit(string raw, ScanOrigin origin, string? kind = null) =>
        Locked(() => SubmitCore(raw, origin, MediaKind.Parse(kind)));

    public ScanOutcome Answer(bool accept) => Locked(() =>
    {
        var session = GetActive();
        if (session == null || !HasPending(session.Id))
            return ScanOutcome.Nothing("There is no question to answer.");
        return ResolvePending(session, accept);
    });

    public ScanOutcome Undo() => Locked(UndoCore);

    /// <param name="notFound">Every site answered and none knows this ISBN, so a retry would not help.</param>
    /// <param name="generation">The <see cref="Generation"/> the lookup began in. A lookup from before a restore is dropped.</param>
    public ScanOutcome ApplyLookup(long scanId, LookupBook? book, string? coverPath, bool notFound = false, long? generation = null) =>
        Locked(() => generation is long began && began != Generation
            ? ScanOutcome.Nothing("The library was replaced while that lookup ran.")
            : ApplyLookupCore(scanId, book, coverPath, notFound));

    private ScanOutcome SubmitCore(string raw, ScanOrigin origin, string kind)
    {
        var session = GetActive() ?? CreateSession(SessionMode.Place);
        var parsed = IsbnRules.Parse(raw);
        Touch(session.Id);

        if (parsed.IsShelf)
        {
            if (HasPending(session.Id))
                ResolvePending(session, false);
            var location = LoadLocations().FirstOrDefault(l => string.Equals(l.Code, parsed.ShelfCode, StringComparison.OrdinalIgnoreCase));
            if (location == null)
            {
                return Record(session, raw, null, null, null, ScanResult.NotIsbn, "Unknown shelf code", null,
                    "Unknown shelf code", "That code is not one of your shelves.", false, false, true, false, false, null);
            }
            Exec("UPDATE sessions SET current_location_id = $l WHERE id = $id", ("$l", location.Id), ("$id", session.Id));
            session = GetActive()!;
            return Record(session, raw, null, null, null, ScanResult.ShelfSwitched, location.Path, null,
                location.Path, "Now scanning " + location.Path + ".", false, false, false, false, false,
                speech: location.Path);
        }

        if (!parsed.IsCode || parsed.Isbn13 == null)
        {
            return Record(session, raw, null, null, null, ScanResult.NotIsbn, "Not an ISBN or UPC", null,
                "Not an ISBN or UPC", "That barcode is not an ISBN or UPC, so it was not counted.",
                false, false, true, true, false, speech: "Not an ISBN or UPC");
        }

        if (origin == ScanOrigin.Live && IsHeld(parsed.Isbn13))
        {
            _lastLiveAt = _clock.UtcNow;
            return Record(session, raw, parsed.Isbn13, null, null, ScanResult.Held, "", null,
                "Held. Scan again if this is another copy.", "That read came in again too quickly to count as another copy.",
                false, false, false, false, true);
        }

        if (origin == ScanOrigin.Live)
        {
            _lastLiveIsbn = parsed.Isbn13;
            _lastLiveAt = _clock.UtcNow;
        }

        if (HasPending(session.Id))
            ResolvePending(session, false);

        session = GetActive() ?? session;
        if (session.Mode is SessionMode.Place or SessionMode.Shelf && session.CurrentLocationId == null)
        {
            return Record(session, raw, parsed.Isbn13, null, null, ScanResult.ChooseShelf, "Choose a shelf first", null,
                "Choose a shelf first", "Pick the shelf you are standing at, or scan its QR code. This book was not recorded.",
                false, false, true, false, false, speech: "Choose a shelf first");
        }

        var titleId = FindTitleIdByCode(parsed);
        if (titleId == null)
            return CreateUnknown(session, raw, parsed, parsed.IsIsbn ? MediaKind.Book : kind);

        return MatchKnown(session, raw, parsed, LoadTitle(titleId.Value));
    }

    private ScanOutcome MatchKnown(SessionRow session, string raw, IsbnParse parsed, TitleState title)
    {
        var copies = LoadCopies(title.Id);
        var seen = SeenCopies(session.Id);
        CopyState? Next(Func<CopyState, bool> pred) =>
            copies.FirstOrDefault(c => !seen.Contains(c.Id) && pred(c));

        if (session.Mode == SessionMode.Shelf)
        {
            var here = Next(c => c.LocationId == session.CurrentLocationId);
            if (here != null)
                return Confirm(session, raw, parsed, title, here, ScanResult.OnShelf, "On this shelf", "On this shelf.", true, false, null);
            var target = AutoMove ? session.CurrentLocationId : null;
            var elsewhere = Next(c => c.LocationId != null && c.LocationId != session.CurrentLocationId);
            if (elsewhere != null)
                return target is long moveTo
                    ? MoveExisting(session, raw, parsed, title, elsewhere, moveTo)
                    : Prompt(session, raw, parsed, title, elsewhere, ScanResult.MovePrompt, "move");
            var loose = Next(c => c.LocationId == null);
            if (loose != null)
                return target is long placeOn
                    ? PlaceExisting(session, raw, parsed, title, loose, placeOn, speech: "Placed")
                    : Prompt(session, raw, parsed, title, loose, ScanResult.PlacePrompt, "place");
            return Extra(session, raw, parsed, title);
        }

        var unlocated = Next(c => c.LocationId == null);
        if (unlocated != null)
        {
            if (session.CurrentLocationId is long shelf)
                return PlaceExisting(session, raw, parsed, title, unlocated, shelf);
            return Confirm(session, raw, parsed, title, unlocated, ScanResult.SeenUnchanged, "Seen, still unlocated",
                "No shelf was selected, so this copy stays unlocated.", false, false, null);
        }

        var shelved = Next(c => c.LocationId != null);
        if (shelved != null)
        {
            var path = PathOf(shelved.LocationId);
            return Confirm(session, raw, parsed, title, shelved, ScanResult.AlreadyShelved, "Already on " + path,
                "Left on " + path + ". Place mode does not move a book that already has a shelf.",
                true, false, null);
        }

        return Extra(session, raw, parsed, title);
    }

    private ScanOutcome CreateUnknown(SessionRow session, string raw, IsbnParse parsed, string kind)
    {
        var headline = parsed.IsIsbn
            ? "ISBN " + parsed.Isbn13
            : parsed.Upc != null
                ? "UPC " + parsed.Upc
                : "EAN " + parsed.Isbn13;
        Exec("""
            INSERT INTO titles(isbn13, isbn10, upc, kind, title, expected_copies, in_libib, is_sample, created_utc, updated_utc)
            VALUES($isbn, $ten, $upc, $kind, $title, 1, 0, 0, $t, $t)
            """,
            ("$isbn", parsed.Isbn13), ("$ten", parsed.Isbn10), ("$upc", parsed.Upc), ("$kind", kind),
            ("$title", headline), ("$t", Stamp()));
        var titleId = LastId();
        Exec("INSERT OR REPLACE INTO title_isbns(isbn13, title_id, isbn10) VALUES($isbn, $id, $ten)",
            ("$isbn", parsed.Isbn13), ("$id", titleId), ("$ten", parsed.Isbn10));
        Exec("INSERT INTO copies(title_id, location_id, copy_index, last_confirmed_utc, last_session_id) VALUES($id, $loc, 1, $t, $s)",
            ("$id", titleId), ("$loc", session.CurrentLocationId), ("$t", Stamp()), ("$s", session.Id));
        var copyId = LastId();
        var snapshot = Json(new UndoSnapshot
        {
            Kind = "unknown",
            CreatedTitleId = titleId,
            CreatedCopyId = copyId,
            CopyId = copyId
        });
        var detail = session.CurrentLocationId == null
            ? "Not in your Libib list. Looking up the title. It stays unlocated until you choose a shelf."
            : "Not in your Libib list. Looking up the title. Placed on " + PathOf(session.CurrentLocationId) + ".";
        return Record(session, raw, parsed.Isbn13, titleId, copyId, ScanResult.NotInLibrary, headline, null,
            "Not in your library", detail, true, false, true, true, false,
            speech: "Not in your library", snapshot: snapshot, startLookup: true);
    }

    private ScanOutcome PlaceExisting(SessionRow session, string raw, IsbnParse parsed, TitleState title, CopyState copy, long shelf, string? speech = null)
    {
        var previous = copy.LastConfirmed;
        Exec("UPDATE copies SET location_id = $l, last_confirmed_utc = $t, last_session_id = $s WHERE id = $id",
            ("$l", shelf), ("$t", Stamp()), ("$s", session.Id), ("$id", copy.Id));
        var path = PathOf(shelf);
        var snapshot = Json(new UndoSnapshot
        {
            Kind = "place",
            CopyId = copy.Id,
            PreviousLocationId = copy.LocationId,
            PreviousConfirmed = previous
        });
        return Record(session, raw, parsed.Isbn13, title.Id, copy.Id, ScanResult.Placed, title.Title, title.Creators,
            "On this shelf", "Placed on " + path + ".", true, true, false, true, false,
            speech: speech, snapshot: snapshot, cover: title.CoverPath);
    }

    private ScanOutcome MoveExisting(SessionRow session, string raw, IsbnParse parsed, TitleState title, CopyState copy, long shelf)
    {
        var from = PathOf(copy.LocationId);
        Exec("UPDATE copies SET location_id = $l, last_confirmed_utc = $t, last_session_id = $s WHERE id = $id",
            ("$l", shelf), ("$t", Stamp()), ("$s", session.Id), ("$id", copy.Id));
        var snapshot = Json(new UndoSnapshot
        {
            Kind = "move",
            CopyId = copy.Id,
            PreviousLocationId = copy.LocationId,
            PreviousConfirmed = copy.LastConfirmed
        });
        return Record(session, raw, parsed.Isbn13, title.Id, copy.Id, ScanResult.Moved, title.Title, title.Creators,
            "Moved here", "Moved from " + from + " to " + PathOf(shelf) + ".", true, true, false, true, false,
            speech: "Moved", snapshot: snapshot, cover: title.CoverPath);
    }

    private ScanOutcome Confirm(SessionRow session, string raw, IsbnParse parsed, TitleState title, CopyState copy, string result, string status, string detail, bool chime, bool problem, string? speech)
    {
        var snapshot = Json(new UndoSnapshot
        {
            Kind = "confirm",
            CopyId = copy.Id,
            PreviousLocationId = copy.LocationId,
            PreviousConfirmed = copy.LastConfirmed
        });
        Exec("UPDATE copies SET last_confirmed_utc = $t, last_session_id = $s WHERE id = $id",
            ("$t", Stamp()), ("$s", session.Id), ("$id", copy.Id));
        return Record(session, raw, parsed.Isbn13, title.Id, copy.Id, result, title.Title, title.Creators,
            status, detail, true, chime, problem, true, false, speech: speech, snapshot: snapshot, cover: title.CoverPath);
    }

    private ScanOutcome Extra(SessionRow session, string raw, IsbnParse parsed, TitleState title) =>
        Record(session, raw, parsed.Isbn13, title.Id, null, ScanResult.Extra, title.Title, title.Creators,
            "Extra copy", "Every copy from your Libib list has already been seen in this session.",
            false, false, true, true, false, speech: "Extra copy", cover: title.CoverPath);

    private ScanOutcome Prompt(SessionRow session, string raw, IsbnParse parsed, TitleState title, CopyState copy, string result, string kind)
    {
        var recorded = PathOf(copy.LocationId);
        var here = PathOf(session.CurrentLocationId);
        var question = kind == "move"
            ? "This is " + title.Title + ". It is recorded on " + recorded + ". Move it here?"
            : "This is " + title.Title + ". It has no shelf yet. Place it on " + here + "?";
        var status = kind == "move" ? "Recorded on " + recorded : "No shelf yet";
        var outcome = Record(session, raw, parsed.Isbn13, title.Id, copy.Id, result, title.Title, title.Creators,
            status, question, false, false, true, true, false, speech: question, cover: title.CoverPath);
        var payload = Pack(new Dictionary<string, object?>
        {
            ["copyId"] = copy.Id,
            ["fromLocationId"] = copy.LocationId,
            ["toLocationId"] = session.CurrentLocationId,
            ["previousConfirmed"] = copy.LastConfirmed,
            ["title"] = title.Title,
            ["recorded"] = recorded
        });
        Exec("INSERT INTO pending_actions(session_id, scan_id, kind, payload, created_utc) VALUES($s, $scan, $k, $p, $t)",
            ("$s", session.Id), ("$scan", outcome.ScanId), ("$k", kind), ("$p", payload), ("$t", Stamp()));
        return new ScanOutcome
        {
            Result = outcome.Result,
            Headline = outcome.Headline,
            Author = outcome.Author,
            Status = outcome.Status,
            Detail = outcome.Detail,
            Speak = true,
            Speech = question,
            Problem = true,
            Prompt = true,
            ScanId = outcome.ScanId,
            Isbn13 = parsed.Isbn13,
            CoverPath = title.CoverPath,
            TitleId = title.Id
        };
    }

    private ScanOutcome ResolvePending(SessionRow session, bool accept)
    {
        long scanId;
        string kind;
        string payload;
        using (var reader = Reader("SELECT scan_id, kind, payload FROM pending_actions WHERE session_id = $s ORDER BY id DESC LIMIT 1", ("$s", session.Id)))
        {
            if (!reader.Read())
                return ScanOutcome.Nothing("There is no question to answer.");
            scanId = reader.GetInt64(0);
            kind = reader.GetString(1);
            payload = reader.GetString(2);
        }
        Exec("DELETE FROM pending_actions WHERE session_id = $s", ("$s", session.Id));

        if (kind == "edition")
            return ResolveEdition(session, scanId, accept, payload);

        var doc = JsonDocument.Parse(payload);
        var copyId = doc.RootElement.GetProperty("copyId").GetInt64();
        long? from = doc.RootElement.TryGetProperty("fromLocationId", out var fromEl) && fromEl.ValueKind != JsonValueKind.Null ? fromEl.GetInt64() : null;
        long? to = doc.RootElement.TryGetProperty("toLocationId", out var toEl) && toEl.ValueKind != JsonValueKind.Null ? toEl.GetInt64() : null;
        string? previous = doc.RootElement.TryGetProperty("previousConfirmed", out var prev) && prev.ValueKind == JsonValueKind.String ? prev.GetString() : null;
        var title = doc.RootElement.GetProperty("title").GetString() ?? "This book";
        var recorded = doc.RootElement.TryGetProperty("recorded", out var rec) && rec.ValueKind == JsonValueKind.String ? rec.GetString() : PathOf(from);

        if (accept && to is long dest)
        {
            Exec("UPDATE copies SET location_id = $l, last_confirmed_utc = $t, last_session_id = $s WHERE id = $id",
                ("$l", dest), ("$t", Stamp()), ("$s", session.Id), ("$id", copyId));
            var snapshot = Json(new UndoSnapshot
            {
                Kind = kind == "move" ? "move" : "place",
                CopyId = copyId,
                PreviousLocationId = from,
                PreviousConfirmed = previous
            });
            var path = PathOf(dest);
            var result = kind == "move" ? ScanResult.Moved : ScanResult.Placed;
            var status = "On this shelf";
            var detail = (kind == "move" ? "Moved to " : "Placed on ") + path + ".";
            UpdateScan(scanId, result, status, detail, true, true, snapshot);
            return new ScanOutcome
            {
                Result = result,
                Headline = title,
                Status = status,
                Detail = detail,
                Chime = true,
                ScanId = scanId,
                TitleId = FindTitleIdOfCopy(copyId)
            };
        }

        Exec("UPDATE copies SET last_confirmed_utc = $t, last_session_id = $s WHERE id = $id",
            ("$t", Stamp()), ("$s", session.Id), ("$id", copyId));
        var unchanged = Json(new UndoSnapshot
        {
            Kind = "seen",
            CopyId = copyId,
            PreviousLocationId = from,
            PreviousConfirmed = previous
        });
        var unchangedStatus = kind == "move"
            ? "Seen here, still recorded on " + recorded
            : "Still unlocated";
        var unchangedDetail = kind == "move"
            ? "The location was left unchanged."
            : "This copy is still unlocated.";
        UpdateScan(scanId, ScanResult.SeenUnchanged, unchangedStatus, unchangedDetail, true, true, unchanged);
        return new ScanOutcome
        {
            Result = ScanResult.SeenUnchanged,
            Headline = title,
            Status = unchangedStatus,
            Detail = unchangedDetail,
            Problem = true,
            ScanId = scanId
        };
    }

    private ScanOutcome ResolveEdition(SessionRow session, long scanId, bool accept, string payload)
    {
        var doc = JsonDocument.Parse(payload);
        var provisionalId = doc.RootElement.GetProperty("provisionalTitleId").GetInt64();
        var existingId = doc.RootElement.GetProperty("existingTitleId").GetInt64();
        var isbn = doc.RootElement.GetProperty("isbn13").GetString();
        var isbn10 = doc.RootElement.TryGetProperty("isbn10", out var ten) && ten.ValueKind == JsonValueKind.String ? ten.GetString() : null;
        var existing = LoadTitle(existingId);
        if (!accept)
        {
            UpdateScan(scanId, ScanResult.KeptSeparate, "Kept as its own book",
                "It stays separate from " + existing.Title + ".", true, true, null);
            return new ScanOutcome
            {
                Result = ScanResult.KeptSeparate,
                Headline = LoadTitle(provisionalId).Title,
                Status = "Kept as its own book",
                Detail = "It stays separate from " + existing.Title + " by " + (existing.Creators ?? "an unknown author") + ".",
                ScanId = scanId,
                TitleId = provisionalId,
                CoverPath = LoadTitle(provisionalId).CoverPath
            };
        }

        var provisional = LoadTitle(provisionalId);
        var copy = LoadCopies(provisionalId).FirstOrDefault();
        var shelf = copy?.LocationId ?? session.CurrentLocationId;
        var previousExpected = existing.Expected;
        var provisionalDescription = ScalarString("SELECT description FROM titles WHERE id = $id", ("$id", provisionalId));
        var provisionalTags = QueryStrings("SELECT tag FROM title_tags WHERE title_id = $id ORDER BY tag", ("$id", provisionalId));
        var carriedDescription = FillDescription(existingId, provisionalDescription);
        var carriedCover = SaveFetchedCoverCore(existingId, provisional.CoverPath);
        var carriedTags = CarryTags(existingId, provisionalTags);
        Exec("UPDATE scans SET title_id = NULL, copy_id = NULL WHERE title_id = $id", ("$id", provisionalId));
        Exec("DELETE FROM copies WHERE title_id = $id", ("$id", provisionalId));
        Exec("DELETE FROM title_isbns WHERE title_id = $id", ("$id", provisionalId));
        Exec("DELETE FROM titles WHERE id = $id", ("$id", provisionalId));
        Exec("UPDATE titles SET expected_copies = expected_copies + 1, updated_utc = $t WHERE id = $id",
            ("$t", Stamp()), ("$id", existingId));
        var index = (int)ScalarLong("SELECT COALESCE(MAX(copy_index), 0) + 1 FROM copies WHERE title_id = $id", ("$id", existingId));
        Exec("INSERT INTO copies(title_id, location_id, copy_index, last_confirmed_utc, last_session_id) VALUES($id, $l, $n, $t, $s)",
            ("$id", existingId), ("$l", shelf), ("$n", index), ("$t", Stamp()), ("$s", session.Id));
        var newCopy = LastId();
        if (isbn != null)
            Exec("INSERT OR REPLACE INTO title_isbns(isbn13, title_id, isbn10) VALUES($isbn, $id, $ten)",
                ("$isbn", isbn), ("$id", existingId), ("$ten", isbn10));
        var snapshot = Json(new UndoSnapshot
        {
            Kind = "edition",
            CreatedCopyId = newCopy,
            ExpectedTitleId = existingId,
            PreviousExpected = previousExpected,
            ProvisionalTitle = provisional.Title,
            ProvisionalCreators = provisional.Creators,
            ProvisionalIsbn13 = isbn,
            ProvisionalIsbn10 = isbn10,
            ProvisionalDescription = provisionalDescription,
            ProvisionalCover = provisional.CoverPath,
            ProvisionalTags = provisionalTags,
            CarriedDescription = carriedDescription,
            CarriedCover = carriedCover,
            CarriedTags = carriedTags,
            PreviousLocationId = shelf
        });
        var path = shelf == null ? "It is still unlocated." : "The new copy is on " + PathOf(shelf) + ".";
        UpdateScan(scanId, ScanResult.AddedCopy, "Added as another copy", path, true, true, snapshot, existingId, newCopy);
        return new ScanOutcome
        {
            Result = ScanResult.AddedCopy,
            Headline = existing.Title,
            Author = existing.Creators ?? "",
            Status = "Added as another copy",
            Detail = "Joined " + existing.Title + " by " + (existing.Creators ?? "an unknown author") + ". " + path,
            Chime = true,
            ScanId = scanId,
            TitleId = existingId,
            CoverPath = carriedCover ? provisional.CoverPath : existing.CoverPath
        };
    }

    private ScanOutcome ApplyLookupCore(long scanId, LookupBook? book, string? coverPath, bool notFound)
    {
        var scan = LoadScan(scanId);
        if (scan == null)
            return ScanOutcome.Nothing("That scan is no longer in this session.");
        if (scan.TitleId is not long titleId)
            return ScanOutcome.Nothing("That scan has no book to update.");

        if (ScalarLong("SELECT COUNT(*) FROM titles WHERE id = $id", ("$id", titleId)) == 0)
            return ScanOutcome.Nothing("That book is no longer in the catalog.");

        var custom = ScalarLong("SELECT details_custom FROM titles WHERE id = $id", ("$id", titleId)) == 1;
        if (!custom && book != null)
        {
            Exec("UPDATE titles SET title = $t, creators = $c, updated_utc = $u WHERE id = $id",
                ("$t", book.Title), ("$c", string.IsNullOrWhiteSpace(book.Authors) ? null : book.Authors),
                ("$u", Stamp()), ("$id", titleId));
        }
        SaveFetchedCoverCore(titleId, coverPath);

        var latest = LatestScanId(scan.SessionId) == scanId && !scan.Undone;
        var title = LoadTitle(titleId);
        if (!latest)
            return new ScanOutcome { KeepPreviousHero = true, TitleId = titleId };

        if (book == null)
        {
            return new ScanOutcome
            {
                Result = notFound ? ScanResult.LookupReady : ScanResult.LookupFailed,
                Headline = title.Title,
                Status = "Not in your library",
                Detail = notFound
                    ? "No title was found online for this barcode. It is saved, and you can type the details in Library."
                    : "The title lookup did not answer. The barcode is saved. You can retry.",
                ScanId = scanId,
                Isbn13 = title.Isbn13,
                CoverPath = title.CoverPath,
                TitleId = titleId,
                StartLookup = false
            };
        }

        var match = FindEdition(titleId, book.Title, book.Authors);
        if (match != null && latest && !scan.Undone)
        {
            var session = GetActive();
            if (session != null)
                ClearPending(session.Id);
            var question = "Different edition of a book already on your list: " + match.Title + " by " + (match.Creators ?? "an unknown author") + ". Add this as another copy?";
            UpdateScan(scanId, ScanResult.EditionPrompt, "Different edition", question, false, true, null);
            var payload = Pack(new Dictionary<string, object?>
            {
                ["provisionalTitleId"] = titleId,
                ["existingTitleId"] = match.Id,
                ["isbn13"] = title.Isbn13,
                ["isbn10"] = ScalarString("SELECT isbn10 FROM titles WHERE id = $id", ("$id", titleId))
            });
            if (session != null)
            {
                Exec("INSERT INTO pending_actions(session_id, scan_id, kind, payload, created_utc) VALUES($s, $scan, 'edition', $p, $t)",
                    ("$s", session.Id), ("$scan", scanId), ("$p", payload), ("$t", Stamp()));
            }
            return new ScanOutcome
            {
                Result = ScanResult.EditionPrompt,
                Headline = book.Title,
                Author = book.Authors,
                Status = "Different edition",
                Detail = question,
                Speak = true,
                Speech = "Different edition of " + match.Title,
                Problem = true,
                Prompt = true,
                ScanId = scanId,
                Isbn13 = title.Isbn13,
                CoverPath = title.CoverPath,
                TitleId = titleId
            };
        }

        var shownTitle = custom ? title.Title : book.Title;
        var shownAuthor = custom ? title.Creators ?? "" : book.Authors;
        var shownCover = title.CoverPath;
        Exec("UPDATE scans SET headline = $h, author = $a, cover_path = $c, detail = $d WHERE id = $id",
            ("$h", shownTitle), ("$a", shownAuthor), ("$c", shownCover), ("$d", "Saved from the lookup. It is not in your Libib list."), ("$id", scanId));
        return new ScanOutcome
        {
            Result = ScanResult.LookupReady,
            Headline = shownTitle,
            Author = shownAuthor,
            Status = "Not in your library",
            Detail = custom
                ? "Your title was left as you saved it."
                : "Saved from the lookup. It is not in your Libib list.",
            Speak = true,
            Speech = "Not in your library. " + shownTitle,
            Problem = true,
            ScanId = scanId,
            Isbn13 = title.Isbn13,
            CoverPath = shownCover,
            TitleId = titleId
        };
    }

    private ScanOutcome UndoCore()
    {
        var session = GetActive();
        if (session == null)
            return ScanOutcome.Nothing("There is no scan to undo.");
        var scan = LatestUndoable(session.Id);
        if (scan == null)
            return ScanOutcome.Nothing("There is no scan to undo.");

        if (scan.Result is ScanResult.MovePrompt or ScanResult.PlacePrompt or ScanResult.EditionPrompt)
        {
            ClearPending(session.Id);
            Exec("UPDATE scans SET undone = 1, result = 'undone', status = 'Undone' WHERE id = $id", ("$id", scan.Id));
            return ScanOutcome.Nothing("Question cancelled.");
        }

        var snapshot = ReadJson(scan.UndoPayload);
        if (snapshot?.Kind == "unknown" && snapshot.CreatedTitleId is long created)
        {
            Exec("UPDATE scans SET title_id = NULL, copy_id = NULL, undone = 1, result = 'undone', status = 'Undone' WHERE id = $id OR title_id = $t",
                ("$id", scan.Id), ("$t", created));
            Exec("DELETE FROM copies WHERE title_id = $id", ("$id", created));
            Exec("DELETE FROM title_isbns WHERE title_id = $id", ("$id", created));
            Exec("DELETE FROM titles WHERE id = $id", ("$id", created));
            return new ScanOutcome { Result = ScanResult.Undone, Status = "Last scan undone", Headline = "Undone", Detail = "The unknown book was removed." };
        }

        if (snapshot?.Kind == "edition" && snapshot.CreatedCopyId is long extraCopy && snapshot.ExpectedTitleId is long expected)
        {
            if (ScalarLong("SELECT COUNT(*) FROM titles WHERE id = $id", ("$id", expected)) == 0)
            {
                Exec("UPDATE scans SET undone = 1, result = 'undone', status = 'Undone', undoable = 0 WHERE id = $id", ("$id", scan.Id));
                return new ScanOutcome
                {
                    Result = ScanResult.Undone,
                    Status = "Last scan undone",
                    Headline = "Undone",
                    Detail = "That book is no longer in the catalog."
                };
            }
            Exec("DELETE FROM copies WHERE id = $id", ("$id", extraCopy));
            Exec("UPDATE titles SET expected_copies = $n WHERE id = $id", ("$n", snapshot.PreviousExpected ?? 1), ("$id", expected));
            if (snapshot.ProvisionalIsbn13 != null)
                Exec("DELETE FROM title_isbns WHERE isbn13 = $isbn AND title_id = $id", ("$isbn", snapshot.ProvisionalIsbn13), ("$id", expected));
            TakeBackCarried(expected, snapshot);
            var tags = snapshot.ProvisionalTags ?? new List<string>();
            Exec("""
                INSERT INTO titles(isbn13, isbn10, title, creators, description, expected_copies, in_libib, is_sample, cover_path, subjects_fetched, created_utc, updated_utc)
                VALUES($isbn, $ten, $title, $creators, $description, 1, 0, 0, $cover, $fetched, $t, $t)
                """,
                ("$isbn", snapshot.ProvisionalIsbn13), ("$ten", snapshot.ProvisionalIsbn10),
                ("$title", snapshot.ProvisionalTitle ?? snapshot.ProvisionalIsbn13 ?? "Unknown"),
                ("$creators", snapshot.ProvisionalCreators), ("$description", snapshot.ProvisionalDescription),
                ("$cover", snapshot.ProvisionalCover), ("$fetched", tags.Count > 0 ? 1 : 0), ("$t", Stamp()));
            var restored = LastId();
            foreach (var tag in tags)
                Exec("INSERT OR IGNORE INTO title_tags(title_id, tag) VALUES($id, $tag)", ("$id", restored), ("$tag", tag));
            if (snapshot.ProvisionalIsbn13 != null)
                Exec("INSERT OR REPLACE INTO title_isbns(isbn13, title_id, isbn10) VALUES($isbn, $id, $ten)",
                    ("$isbn", snapshot.ProvisionalIsbn13), ("$id", restored), ("$ten", snapshot.ProvisionalIsbn10));
            Exec("INSERT INTO copies(title_id, location_id, copy_index, last_confirmed_utc, last_session_id) VALUES($id, $l, 1, $t, $s)",
                ("$id", restored), ("$l", snapshot.PreviousLocationId), ("$t", Stamp()), ("$s", session.Id));
            Exec("UPDATE scans SET undone = 1, result = 'undone', status = 'Undone' WHERE id = $id", ("$id", scan.Id));
            return new ScanOutcome { Result = ScanResult.Undone, Status = "Last scan undone", Headline = "Undone", Detail = "The extra copy was removed and the scanned book is separate again." };
        }

        if (snapshot?.CopyId is long copyId)
        {
            Exec("UPDATE copies SET location_id = $l, last_confirmed_utc = $t WHERE id = $id",
                ("$l", snapshot.PreviousLocationId), ("$t", snapshot.PreviousConfirmed), ("$id", copyId));
        }
        Exec("UPDATE scans SET undone = 1, result = 'undone', status = 'Undone', counts_as_seen = 0 WHERE id = $id", ("$id", scan.Id));
        return new ScanOutcome
        {
            Result = ScanResult.Undone,
            Status = "Last scan undone",
            Headline = scan.Headline,
            Author = scan.Author ?? "",
            Detail = "The last scan was reversed."
        };
    }

    private bool IsHeld(string isbn) =>
        _lastLiveIsbn == isbn && (_clock.UtcNow - _lastLiveAt).TotalMilliseconds < DebounceMsCore() && (_clock.UtcNow - _lastLiveAt).TotalMilliseconds >= 0;

    private int DebounceMsCore() =>
        int.TryParse(ScalarString("SELECT value FROM settings WHERE key = 'debounce_ms'"), out var n) ? n : 400;

    /// <summary>Tags from the other edition join a book that has none and was never given any by a lookup.</summary>
    private bool CarryTags(long titleId, IReadOnlyList<string> tags)
    {
        if (tags.Count == 0
            || ScalarLong("SELECT subjects_fetched FROM titles WHERE id = $id", ("$id", titleId)) == 1
            || ScalarLong("SELECT COUNT(*) FROM title_tags WHERE title_id = $id", ("$id", titleId)) > 0)
            return false;
        foreach (var tag in tags)
            Exec("INSERT OR IGNORE INTO title_tags(title_id, tag) VALUES($id, $tag)", ("$id", titleId), ("$tag", tag));
        Exec("UPDATE titles SET subjects_fetched = 1, updated_utc = $t WHERE id = $id", ("$t", Stamp()), ("$id", titleId));
        return true;
    }

    private void TakeBackCarried(long titleId, UndoSnapshot snapshot)
    {
        if (snapshot.CarriedDescription)
            Exec("UPDATE titles SET description = NULL WHERE id = $id AND details_custom = 0 AND description = $d",
                ("$id", titleId), ("$d", Clean(snapshot.ProvisionalDescription, 8000)));
        if (snapshot.CarriedCover)
            Exec("UPDATE titles SET cover_path = NULL WHERE id = $id AND cover_locked = 0 AND cover_path = $c",
                ("$id", titleId), ("$c", snapshot.ProvisionalCover?.Trim()));
        if (snapshot.CarriedTags)
        {
            foreach (var tag in snapshot.ProvisionalTags ?? new List<string>())
                Exec("DELETE FROM title_tags WHERE title_id = $id AND tag = $tag", ("$id", titleId), ("$tag", tag));
            Exec("UPDATE titles SET subjects_fetched = 0 WHERE id = $id", ("$id", titleId));
        }
    }

    private TitleState? FindEdition(long provisionalId, string title, string? authors)
    {
        long? match = null;
        using (var reader = Reader("SELECT id, title, creators FROM titles WHERE id <> $id AND in_libib = 1", ("$id", provisionalId)))
        {
            while (reader.Read())
            {
                if (TextMatch.SameTitle(title, reader.GetString(1)) && TextMatch.AuthorsOverlap(authors, ReadString(reader, 2)))
                {
                    match = reader.GetInt64(0);
                    break;
                }
            }
        }
        return match is long id ? LoadTitle(id) : null;
    }

    private ScanOutcome Record(
        SessionRow session, string raw, string? isbn, long? titleId, long? copyId, string result,
        string headline, string? author, string status, string detail, bool seen, bool chime, bool problem,
        bool undoable, bool keepHero, string? speech = null, string? snapshot = null, string? cover = null, bool startLookup = false)
    {
        Exec("""
            INSERT INTO scans(session_id, raw_text, isbn13, title_id, copy_id, location_id, result, headline, author, status, detail, scanned_utc, undone, counts_as_seen, undoable, undo_payload, cover_path)
            VALUES($s, $raw, $isbn, $title, $copy, $loc, $result, $headline, $author, $status, $detail, $t, 0, $seen, $undo, $payload, $cover)
            """,
            ("$s", session.Id), ("$raw", raw.Trim()), ("$isbn", isbn), ("$title", titleId), ("$copy", copyId),
            ("$loc", session.CurrentLocationId), ("$result", result), ("$headline", headline), ("$author", author),
            ("$status", status), ("$detail", detail), ("$t", Stamp()), ("$seen", seen ? 1 : 0), ("$undo", undoable ? 1 : 0),
            ("$payload", snapshot), ("$cover", cover));
        return new ScanOutcome
        {
            Result = result,
            Headline = headline,
            Author = author ?? "",
            Status = status,
            Detail = detail,
            Speak = speech != null,
            Speech = speech,
            Chime = chime,
            Problem = problem,
            KeepPreviousHero = keepHero,
            StartLookup = startLookup,
            ScanId = LastId(),
            Isbn13 = isbn,
            CoverPath = cover,
            TitleId = titleId
        };
    }

    private void UpdateScan(long scanId, string result, string status, string detail, bool seen, bool undoable, string? snapshot, long? titleId = null, long? copyId = null)
    {
        if (titleId == null && copyId == null)
        {
            Exec("""
                UPDATE scans SET result = $r, status = $s, detail = $d, counts_as_seen = $seen, undoable = $u, undo_payload = $p
                WHERE id = $id
                """,
                ("$r", result), ("$s", status), ("$d", detail), ("$seen", seen ? 1 : 0), ("$u", undoable ? 1 : 0),
                ("$p", snapshot), ("$id", scanId));
            return;
        }
        Exec("""
            UPDATE scans SET result = $r, status = $s, detail = $d, counts_as_seen = $seen, undoable = $u, undo_payload = $p,
              title_id = COALESCE($title, title_id), copy_id = COALESCE($copy, copy_id), headline = COALESCE((SELECT title FROM titles WHERE id = $title), headline)
            WHERE id = $id
            """,
            ("$r", result), ("$s", status), ("$d", detail), ("$seen", seen ? 1 : 0), ("$u", undoable ? 1 : 0),
            ("$p", snapshot), ("$title", titleId), ("$copy", copyId), ("$id", scanId));
    }

    private static string Pack(Dictionary<string, object?> payload) =>
        JsonSerializer.Serialize(payload);

    private SessionRow? GetActive()
    {
        using var reader = Reader("SELECT id, mode, current_location_id, finished_utc, is_active FROM sessions WHERE is_active = 1 ORDER BY id DESC LIMIT 1");
        if (!reader.Read())
            return null;
        return new SessionRow
        {
            Id = reader.GetInt64(0),
            Mode = reader.GetString(1),
            CurrentLocationId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
            FinishedUtc = ReadString(reader, 3),
            Active = reader.GetInt64(4) == 1
        };
    }

    private SessionRow CreateSession(string mode)
    {
        Exec("INSERT INTO sessions(mode, started_utc, is_active) VALUES($m, $t, 1)", ("$m", mode), ("$t", Stamp()));
        return GetActive()!;
    }

    private SessionInfo? MapSession(SessionRow? session)
    {
        if (session == null)
            return null;
        return new SessionInfo(session.Id, session.Mode, session.CurrentLocationId, PathOf(session.CurrentLocationId), session.Active, session.FinishedUtc);
    }

    private int CountScans(long sessionId) =>
        (int)ScalarLong("SELECT COUNT(*) FROM scans WHERE session_id = $s AND result NOT IN ('held', 'choose_shelf')", ("$s", sessionId));

    private void Touch(long sessionId) =>
        Exec("UPDATE sessions SET last_scan_utc = $t WHERE id = $id", ("$t", Stamp()), ("$id", sessionId));

    private bool HasPending(long sessionId) =>
        ScalarLong("SELECT COUNT(*) FROM pending_actions WHERE session_id = $s", ("$s", sessionId)) > 0;

    private void ClearPending(long sessionId) =>
        Exec("DELETE FROM pending_actions WHERE session_id = $s", ("$s", sessionId));

    private long? FindTitleIdByIsbn(string isbn13) =>
        ScalarNullable("SELECT title_id FROM title_isbns WHERE isbn13 = $isbn", ("$isbn", isbn13));

    private long? FindTitleIdByCode(IsbnParse parsed)
    {
        if (parsed.Isbn13 != null)
        {
            var id = FindTitleIdByIsbn(parsed.Isbn13);
            if (id != null)
                return id;
            id = ScalarNullable("SELECT id FROM titles WHERE isbn13 = $isbn", ("$isbn", parsed.Isbn13));
            if (id != null)
                return id;
        }
        if (parsed.Upc != null)
        {
            var id = ScalarNullable("SELECT id FROM titles WHERE upc = $u", ("$u", parsed.Upc));
            if (id != null)
                return id;
        }
        return null;
    }

    private long? FindTitleIdOfCopy(long copyId) =>
        ScalarNullable("SELECT title_id FROM copies WHERE id = $id", ("$id", copyId));

    private TitleState LoadTitle(long id)
    {
        using var reader = Reader("SELECT id, title, creators, isbn13, cover_path, expected_copies, in_libib FROM titles WHERE id = $id", ("$id", id));
        if (!reader.Read())
            throw new InvalidOperationException("That book is no longer in the catalog.");
        return new TitleState
        {
            Id = reader.GetInt64(0),
            Title = reader.GetString(1),
            Creators = ReadString(reader, 2),
            Isbn13 = ReadString(reader, 3),
            CoverPath = ReadString(reader, 4),
            Expected = Convert.ToInt32(reader.GetValue(5), System.Globalization.CultureInfo.InvariantCulture),
            InLibib = reader.GetInt64(6) == 1
        };
    }

    private List<CopyState> LoadCopies(long titleId) => Query("""
        SELECT id, title_id, location_id, last_confirmed_utc, copy_index
        FROM copies WHERE title_id = $id ORDER BY copy_index, id
        """, r => new CopyState
    {
        Id = r.GetInt64(0),
        TitleId = r.GetInt64(1),
        LocationId = r.IsDBNull(2) ? null : r.GetInt64(2),
        LastConfirmed = ReadString(r, 3),
        CopyIndex = r.GetInt32(4)
    }, ("$id", titleId));

    private HashSet<long> SeenCopies(long sessionId)
    {
        var ids = Query("SELECT copy_id FROM scans WHERE session_id = $s AND undone = 0 AND counts_as_seen = 1 AND copy_id IS NOT NULL",
            r => r.GetInt64(0), ("$s", sessionId));
        return ids.ToHashSet();
    }

    private string PathOf(long? locationId)
    {
        if (locationId == null)
            return "Unlocated";
        return LoadLocations().FirstOrDefault(l => l.Id == locationId)?.Path ?? "Unlocated";
    }

    private sealed class ScanState
    {
        public long Id { get; init; }
        public long SessionId { get; init; }
        public string Result { get; init; } = "";
        public string Headline { get; init; } = "";
        public string? Author { get; init; }
        public long? TitleId { get; init; }
        public bool Undone { get; init; }
        public string? UndoPayload { get; init; }
    }

    private ScanState? LoadScan(long id)
    {
        using var reader = Reader("SELECT id, session_id, result, headline, author, title_id, undone, undo_payload FROM scans WHERE id = $id", ("$id", id));
        if (!reader.Read())
            return null;
        return new ScanState
        {
            Id = reader.GetInt64(0),
            SessionId = reader.GetInt64(1),
            Result = reader.GetString(2),
            Headline = reader.GetString(3),
            Author = ReadString(reader, 4),
            TitleId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
            Undone = reader.GetInt64(6) == 1,
            UndoPayload = ReadString(reader, 7)
        };
    }

    private ScanState? LatestUndoable(long sessionId)
    {
        using var reader = Reader("""
            SELECT id, session_id, result, headline, author, title_id, undone, undo_payload
            FROM scans WHERE session_id = $s AND undone = 0 AND undoable = 1
            ORDER BY id DESC LIMIT 1
            """, ("$s", sessionId));
        if (!reader.Read())
            return null;
        return new ScanState
        {
            Id = reader.GetInt64(0),
            SessionId = reader.GetInt64(1),
            Result = reader.GetString(2),
            Headline = reader.GetString(3),
            Author = ReadString(reader, 4),
            TitleId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
            Undone = false,
            UndoPayload = ReadString(reader, 7)
        };
    }

    private long? LatestScanId(long sessionId) =>
        ScalarNullable("SELECT id FROM scans WHERE session_id = $s ORDER BY id DESC LIMIT 1", ("$s", sessionId));
}
