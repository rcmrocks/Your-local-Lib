namespace YourLocalLib.Core;

public static class SessionMode
{
    public const string Place = "place";
    public const string Shelf = "shelf";
    public const string Library = "library";
}

public static class ScanResult
{
    public const string ShelfSwitched = "shelf_switched";
    public const string ChooseShelf = "choose_shelf";
    public const string NotIsbn = "not_isbn";
    public const string Held = "held";
    public const string OnShelf = "on_shelf";
    public const string Placed = "placed";
    public const string AlreadyShelved = "already_shelved";
    public const string Extra = "extra";
    public const string NotInLibrary = "not_in_library";
    public const string MovePrompt = "move_prompt";
    public const string PlacePrompt = "place_prompt";
    public const string EditionPrompt = "edition_prompt";
    public const string Moved = "moved";
    public const string SeenUnchanged = "seen_unchanged";
    public const string KeptSeparate = "kept_separate";
    public const string AddedCopy = "added_copy";
    public const string Undone = "undone";
    public const string LookupFailed = "lookup_failed";
    public const string LookupReady = "lookup_ready";
}

public enum ScanOrigin
{
    Live,
    Dump
}

public sealed class ScanOutcome
{
    public string Result { get; init; } = "";
    public string Headline { get; init; } = "";
    public string Author { get; init; } = "";
    public string Status { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool Speak { get; init; }
    public string? Speech { get; init; }
    public bool Chime { get; init; }
    public bool Problem { get; init; }
    public bool Prompt { get; init; }
    public bool KeepPreviousHero { get; init; }
    public bool StartLookup { get; init; }
    public long? ScanId { get; init; }
    public string? Isbn13 { get; init; }
    public string? CoverPath { get; init; }
    public long? TitleId { get; init; }

    public static ScanOutcome Held() => new()
    {
        Result = ScanResult.Held,
        Status = "Held. Scan again if this is another copy.",
        Detail = "That read came in again too quickly to count as another copy.",
        KeepPreviousHero = true
    };

    public static ScanOutcome Nothing(string status) => new()
    {
        Result = ScanResult.Undone,
        Status = status,
        KeepPreviousHero = true
    };
}

public sealed record LibibFile(IReadOnlyList<LibibRow> Rows, IReadOnlyList<string> Headers)
{
    public string GuessedKind => MediaKind.FromHeaders(Headers);
}

public sealed record LibibRow
{
    public string? Id { get; init; }
    public string? ItemType { get; init; }
    public string? Title { get; init; }
    public string? Creators { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Collection { get; init; }
    public string? EanIsbn13 { get; init; }
    public string? UpcIsbn10 { get; init; }
    public string? Description { get; init; }
    public string? Publisher { get; init; }
    public string? PublishDate { get; init; }
    public string? Group { get; init; }
    public string? Tags { get; init; }
    public string? Notes { get; init; }
    public string? Rating { get; init; }
    public string? Status { get; init; }
    public string? Copies { get; init; }
    public string? LengthOf { get; init; }
    public string? Length { get; init; }
    public string? NumberOfDiscs { get; init; }
    public string? Ensemble { get; init; }
    public string? AspectRatio { get; init; }
    public string? Esrb { get; init; }
    public string? Artists { get; init; }
    public string? NumberOfPlayers { get; init; }
    public string? AgeGroup { get; init; }
}

public sealed record ResolvedBook
{
    public bool IsBook { get; init; }
    public string Kind { get; init; } = MediaKind.Book;
    public bool CopiesInvalid { get; init; }
    public string? LibibId { get; init; }
    public string? Isbn13 { get; init; }
    public string? Isbn10 { get; init; }
    public string? Upc { get; init; }
    public string Title { get; init; } = "";
    public string? Creators { get; init; }
    public string? Collection { get; init; }
    public int Copies { get; init; } = 1;
    public string? Description { get; init; }
    public string? Publisher { get; init; }
    public string? PublishDate { get; init; }
    public string? Series { get; init; }
    public string? Tags { get; init; }
    public string? Notes { get; init; }
    public string? Rating { get; init; }
    public string? Status { get; init; }
    public string? LengthOf { get; init; }
    public string? NumberOfDiscs { get; init; }
    public string? Ensemble { get; init; }
    public string? AspectRatio { get; init; }
    public string? Esrb { get; init; }
    public string? Artists { get; init; }
    public string? NumberOfPlayers { get; init; }
    public string? AgeGroup { get; init; }
    public string GroupKey { get; init; } = "";
}

public sealed record KindCount(string Kind, int Count);

public sealed record ImportPreview(
    int Titles,
    int MissingIsbn,
    int RepeatedIsbnGroups,
    int SkippedNonBooks,
    int InvalidCopyRows,
    int CopyCountNotes,
    IReadOnlyList<string> Collections,
    IReadOnlyList<string> Errors,
    bool CanCommit,
    IReadOnlyList<KindCount> Kinds);

public sealed record SessionInfo(long Id, string Mode, long? CurrentLocationId, string? ShelfPath, bool Active, string? FinishedUtc);

public sealed record LocationInfo(long Id, string Name, long? ParentId, int SortOrder, string Code, string Path, int Depth);

public sealed record CatalogRow(
    long? CopyId,
    long TitleId,
    string Title,
    string? Creators,
    string? Series,
    string? Isbn13,
    string? ShelfPath,
    long? LocationId,
    string? LastConfirmedUtc,
    string? Rating,
    string? ReadingStatus,
    bool InLibib,
    bool NeedsIsbn,
    string? CoverPath,
    string? Collections,
    double? UserRating = null,
    string? UserTags = null,
    string? LibibTags = null,
    string? Description = null,
    string? Notes = null,
    string? Publisher = null,
    bool UserRead = false,
    string Kind = MediaKind.Book,
    string? Upc = null)
{
    public string KindLabel => MediaKind.Label(Kind);
    public double? ShownRating => UserRead ? UserRating : null;

    public string? ListMarks
    {
        get
        {
            var parts = new List<string>();
            if (ShownRating is double rating)
            {
                var label = UserRatingScale.Label(rating);
                parts.Add(label + (rating == 1 ? " star" : " stars"));
            }
            else if (UserRead)
            {
                parts.Add("Read");
            }
            if (!string.IsNullOrWhiteSpace(UserTags))
                parts.Add(UserTags);
            return parts.Count == 0 ? null : string.Join("   ·   ", parts);
        }
    }
}

public sealed record BookDetails(
    string Title,
    string? Creators,
    string? Series,
    string? Publisher,
    string? PublishDate,
    string? Description,
    string? Notes,
    string? Kind = null,
    string? LengthOf = null,
    string? NumberOfDiscs = null,
    string? Ensemble = null,
    string? AspectRatio = null,
    string? Esrb = null,
    string? Artists = null,
    string? NumberOfPlayers = null,
    string? AgeGroup = null);

public sealed record TitleDetail(
    long Id,
    string Title,
    string? Creators,
    string? Series,
    string? Isbn13,
    string? Isbn10,
    string? Publisher,
    string? PublishDate,
    string? Description,
    string? Notes,
    string? Rating,
    string? ReadingStatus,
    string? Collections,
    string? CoverPath,
    bool InLibib,
    string Kind = MediaKind.Book,
    string? Upc = null,
    string? LengthOf = null,
    string? NumberOfDiscs = null,
    string? Ensemble = null,
    string? AspectRatio = null,
    string? Esrb = null,
    string? Artists = null,
    string? NumberOfPlayers = null,
    string? AgeGroup = null);

public sealed record GapRow(long CopyId, long TitleId, string Title, string? Creators, string? Isbn13);

public sealed record DisagreementRow(string Title, string? Creators, string Status, string? RecordedOn);

public sealed record LookupBook(string Title, string Authors);

public sealed record OpenLibraryLookup(LookupStatus Status, LookupBook? Book, IReadOnlyList<string> Subjects, string? CoverUrl = null)
{
    public static OpenLibraryLookup None(LookupStatus status) => new(status, null, Array.Empty<string>());
}

public sealed record OpenLibraryDetails(LookupStatus Status, string? Description, IReadOnlyList<long> CoverIds);

public sealed record GoogleLookup(LookupStatus Status, LookupBook? Book, string? Description, IReadOnlyList<string> Subjects, string? CoverUrl)
{
    public static GoogleLookup None(LookupStatus status) => new(status, null, null, Array.Empty<string>(), null);
}

/// <summary>What a title still lacks that an online lookup could fill.</summary>
public sealed record FillNeeds(string? Isbn13, bool Cover, bool Description, bool Subjects, bool Checked, bool Sample, string Kind = MediaKind.Book)
{
    public bool LookupSupported => MediaKind.LooksUpOnline(Kind);
    public bool Any => LookupSupported && (Cover || Description || Subjects);
}

public sealed record FillResult(bool Ran, bool CoverAdded, bool DescriptionAdded, bool TagsAdded, bool Complete)
{
    public static FillResult Skipped { get; } = new(false, false, false, false, false);
    public bool Changed => CoverAdded || DescriptionAdded || TagsAdded;
}

public sealed record LibraryCounts(int Books, int Copies, int Shelves, int Tags, int Ratings, int Read);

public sealed record BackupReport(string Path, LibraryCounts Counts, int Covers, long Bytes);

/// <param name="DatabaseOnly">An automatic library-*.db copy, which holds no covers.</param>
public sealed record BackupCheck(DateTime? CreatedUtc, string? AppVersion, LibraryCounts Counts, int Covers, bool DatabaseOnly);

/// <param name="SafetyCopy">Where the library from before the restore was saved, or null when it had no books or shelves of its own.</param>
public sealed record RestoreReport(LibraryCounts Counts, int Covers, string? SafetyCopy);

public sealed class UndoSnapshot
{
    public string Kind { get; set; } = "";
    public long? CopyId { get; set; }
    public long? PreviousLocationId { get; set; }
    public string? PreviousConfirmed { get; set; }
    public long? CreatedTitleId { get; set; }
    public long? CreatedCopyId { get; set; }
    public long? ExpectedTitleId { get; set; }
    public int? PreviousExpected { get; set; }
    public string? ProvisionalTitle { get; set; }
    public string? ProvisionalCreators { get; set; }
    public string? ProvisionalIsbn13 { get; set; }
    public string? ProvisionalIsbn10 { get; set; }
    public string? ProvisionalDescription { get; set; }
    public string? ProvisionalCover { get; set; }
    public List<string>? ProvisionalTags { get; set; }
    public bool CarriedDescription { get; set; }
    public bool CarriedCover { get; set; }
    public bool CarriedTags { get; set; }
}
