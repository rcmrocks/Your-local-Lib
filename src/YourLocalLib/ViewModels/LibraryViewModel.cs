using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YourLocalLib.Core;

namespace YourLocalLib.ViewModels;

public partial class LibraryViewModel : ObservableObject
{
    private readonly LibraryDatabase _db;
    private readonly AppPaths _paths;
    private readonly BookFill _fill;
    private readonly HashSet<long> _filling = new();

    public LibraryViewModel(LibraryDatabase db, AppPaths paths, BookFill fill)
    {
        _db = db;
        _paths = paths;
        _fill = fill;
        RatingFilters.Add(new RatingChoice("Any rating", null, false));
        RatingFilters.Add(new RatingChoice("Unrated", null, true));
        foreach (var step in UserRatingScale.Steps)
        {
            var label = UserRatingScale.Label(step);
            RatingFilters.Add(new RatingChoice(label + (step == 1 ? " star" : " stars"), step, false));
        }
        foreach (var kind in MediaKind.All)
            KindChoices.Add(new KindChoice(MediaKind.Label(kind), kind));
        _loading = true;
        RatingFilter = RatingFilters[0];
        EditKind = KindChoices[0];
        _loading = false;
        _copiedTimer.Tick += (_, _) =>
        {
            _copiedTimer.Stop();
            CopyIsbnLabel = "Copy";
        };
    }

    public ObservableCollection<CatalogRow> Rows { get; } = new();
    public ObservableCollection<LocationInfo> Shelves { get; } = new();
    public ObservableCollection<ShelfChoice> Filters { get; } = new();
    public ObservableCollection<RatingChoice> RatingFilters { get; } = new();
    public ObservableCollection<KindChoice> KindChoices { get; } = new();
    public ObservableCollection<TagChip> Tags { get; } = new();

    [ObservableProperty] private string query = "";
    [ObservableProperty] private ShelfChoice? filter;
    [ObservableProperty] private RatingChoice? ratingFilter;
    [ObservableProperty] private CatalogRow? selected;
    [ObservableProperty] private bool isRead;
    [ObservableProperty] private double? userRating;
    [ObservableProperty] private string tagDraft = "";
    [ObservableProperty] private string detailTitle = "Select a title";
    [ObservableProperty] private string detailAuthor = "";
    [ObservableProperty] private string detailMeta = "";
    [ObservableProperty] private string detailIsbn = "";
    [ObservableProperty] private bool hasIsbn;
    [ObservableProperty] private string copyIsbnLabel = "Copy";
    [ObservableProperty] private string detailDescription = "";
    [ObservableProperty] private bool descriptionIsPlaceholder;
    [ObservableProperty] private string detailNotes = "";
    [ObservableProperty] private bool hasNotes;
    [ObservableProperty] private string detailLibib = "";
    [ObservableProperty] private bool hasLibib;
    [ObservableProperty] private bool editing;
    [ObservableProperty] private string editTitle = "";
    [ObservableProperty] private string editCreators = "";
    [ObservableProperty] private string editSeries = "";
    [ObservableProperty] private string editPublisher = "";
    [ObservableProperty] private string editPublished = "";
    [ObservableProperty] private string editDescription = "";
    [ObservableProperty] private string editNotes = "";
    [ObservableProperty] private bool needsIsbn;
    [ObservableProperty] private string isbnText = "";
    [ObservableProperty] private string? error;
    [ObservableProperty] private bool empty;
    [ObservableProperty] private int shownCount;
    [ObservableProperty] private LocationInfo? shelfForCopy;
    [ObservableProperty] private Bitmap? cover;
    [ObservableProperty] private bool hasCover;
    [ObservableProperty] private bool confirmingDelete;
    [ObservableProperty] private bool lookingUp;
    [ObservableProperty] private bool canLookUpAgain;
    [ObservableProperty] private string lookupNote = "";
    [ObservableProperty] private KindChoice? editKind;
    [ObservableProperty] private string editLength = "";
    [ObservableProperty] private string editDiscs = "";
    [ObservableProperty] private string editEnsemble = "";
    [ObservableProperty] private string editAspect = "";
    [ObservableProperty] private string editEsrb = "";
    [ObservableProperty] private string editArtists = "";
    [ObservableProperty] private string editPlayers = "";
    [ObservableProperty] private string editAge = "";
    [ObservableProperty] private string detailExtras = "";
    [ObservableProperty] private bool hasExtras;
    [ObservableProperty] private string doneLabel = "Read";
    [ObservableProperty] private string creatorLabel = "Author";
    [ObservableProperty] private string codeLabel = "ISBN";

    private bool _loading;
    private bool _applying;
    private long? _shownTitleId;
    private readonly DispatcherTimer _copiedTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    public bool HasUserRating => UserRating != null;
    public bool HasSelection => Selected != null;
    public bool ShowEditButton => HasSelection && !Editing;
    public bool ShowLookUpAgain => CanLookUpAgain && !Editing;
    public string CoverPlaceholder => LookingUp ? "Looking up…" : "No cover";
    public bool ShowMovieFields => CurrentKind == MediaKind.Movie;
    public bool ShowMusicFields => CurrentKind == MediaKind.Music;
    public bool ShowGameFields => CurrentKind == MediaKind.VideoGame;
    public bool ShowBoardFields => CurrentKind == MediaKind.BoardGame;
    private string CurrentKind => MediaKind.Parse(Editing ? EditKind?.Kind : Selected?.Kind);
    public Func<Task<string?>>? PickCover { get; set; }
    public Func<string, Task>? CopyText { get; set; }

    partial void OnUserRatingChanged(double? value) => OnPropertyChanged(nameof(HasUserRating));
    partial void OnLookingUpChanged(bool value) => OnPropertyChanged(nameof(CoverPlaceholder));
    partial void OnCanLookUpAgainChanged(bool value) => OnPropertyChanged(nameof(ShowLookUpAgain));

    partial void OnEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEditButton));
        OnPropertyChanged(nameof(ShowLookUpAgain));
        RaiseKindFields();
    }

    partial void OnEditKindChanged(KindChoice? value) => RaiseKindFields();

    private void RaiseKindFields()
    {
        OnPropertyChanged(nameof(ShowMovieFields));
        OnPropertyChanged(nameof(ShowMusicFields));
        OnPropertyChanged(nameof(ShowGameFields));
        OnPropertyChanged(nameof(ShowBoardFields));
    }

    public void Reset()
    {
        _filling.Clear();
        _loading = true;
        Query = "";
        Filter = null;
        RatingFilter = RatingFilters[0];
        _loading = false;
        _applying = true;
        Selected = null;
        _applying = false;
        Refresh();
    }

    public void Refresh()
    {
        _loading = true;
        var kind = Filter?.Kind;
        var shelfId = Filter?.ShelfId;
        Shelves.Clear();
        Filters.Clear();
        Filters.Add(new ShelfChoice("All titles", "all", null));
        foreach (var media in MediaKind.All)
            Filters.Add(new ShelfChoice(MediaKind.Label(media), media, null));
        Filters.Add(new ShelfChoice("Done", "read", null));
        Filters.Add(new ShelfChoice("Not done", "unread", null));
        Filters.Add(new ShelfChoice("Unlocated", "unlocated", null));
        Filters.Add(new ShelfChoice("Needs a barcode", "needs-isbn", null));
        foreach (var shelf in _db.Locations())
        {
            Shelves.Add(shelf);
            Filters.Add(new ShelfChoice(shelf.Path, "shelf", shelf.Id));
        }
        Filter = Filters.FirstOrDefault(f => f.Kind == kind && f.ShelfId == shelfId) ?? Filters[0];
        _loading = false;
        ApplySearch();
    }

    partial void OnQueryChanged(string value)
    {
        if (!_loading)
            ApplySearch();
    }

    partial void OnFilterChanged(ShelfChoice? value)
    {
        if (!_loading)
            ApplySearch();
    }

    partial void OnRatingFilterChanged(RatingChoice? value)
    {
        if (!_loading)
            ApplySearch();
    }

    partial void OnSelectedChanged(CatalogRow? value)
    {
        if (!_applying)
            ShowBook(value);
    }

    partial void OnIsReadChanged(bool value)
    {
        if (_loading || Selected == null)
            return;
        var message = _db.SetRead(Selected.TitleId, value);
        if (message != null)
        {
            Error = message;
            return;
        }
        Error = null;
        ApplySearch(keepOpenBook: true);
    }

    private void ShowBook(CatalogRow? value)
    {
        Error = null;
        IsbnText = "";
        var sameBook = value != null && _shownTitleId == value.TitleId;
        _shownTitleId = value?.TitleId;
        if (!sameBook)
        {
            TagDraft = "";
            Editing = false;
            ConfirmingDelete = false;
            LookupNote = "";
            _copiedTimer.Stop();
            CopyIsbnLabel = "Copy";
        }
        _loading = true;
        IsRead = value?.UserRead ?? false;
        UserRating = value?.UserRating;
        Tags.Clear();
        if (value != null)
        {
            foreach (var tag in _db.UserTags(value.TitleId))
                Tags.Add(new TagChip(tag, RemoveTagNow));
        }
        _loading = false;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ShowEditButton));
        if (value == null)
        {
            DetailTitle = "Select a title";
            DoneLabel = "Read";
            CreatorLabel = "Author";
            CodeLabel = "ISBN";
            DetailExtras = "";
            HasExtras = false;
            DetailAuthor = "";
            DetailMeta = "";
            DetailIsbn = "";
            HasIsbn = false;
            DetailDescription = "Choose a title in the list. Search and the filters above narrow that list.";
            DescriptionIsPlaceholder = true;
            DetailNotes = "";
            HasNotes = false;
            DetailLibib = "";
            HasLibib = false;
            NeedsIsbn = false;
            LookingUp = false;
            CanLookUpAgain = false;
            SetCover(null);
            return;
        }
        var title = _db.Title(value.TitleId);
        DetailTitle = value.Title;
        DetailAuthor = value.Creators ?? "";
        var when = FormatWhen(value.LastConfirmedUtc);
        DetailMeta = string.Join("   ·   ", new[]
        {
            value.KindLabel,
            value.ShelfPath ?? "Unlocated",
            when,
            string.IsNullOrWhiteSpace(value.Series) ? null : value.Series,
            string.IsNullOrWhiteSpace(title?.Publisher) ? null : title.Publisher,
            string.IsNullOrWhiteSpace(title?.PublishDate) ? null : title.PublishDate
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        DoneLabel = MediaKind.DoneLabel(value.Kind);
        CreatorLabel = MediaKind.CreatorLabel(value.Kind);
        CodeLabel = MediaKind.CodeLabel(value.Kind);
        OnPropertyChanged(nameof(ShowMovieFields));
        OnPropertyChanged(nameof(ShowMusicFields));
        OnPropertyChanged(nameof(ShowGameFields));
        OnPropertyChanged(nameof(ShowBoardFields));
        HasIsbn = !string.IsNullOrWhiteSpace(value.Isbn13) || !string.IsNullOrWhiteSpace(value.Upc);
        var code = value.Kind == MediaKind.Book ? value.Isbn13 : value.Upc ?? value.Isbn13;
        DetailIsbn = HasIsbn ? CodeLabel + " " + code : "No " + CodeLabel;
        var extras = new List<string>();
        if (!string.IsNullOrWhiteSpace(title?.LengthOf))
            extras.Add((value.Kind == MediaKind.Book ? "Pages " : "Length ") + title!.LengthOf);
        if (!string.IsNullOrWhiteSpace(title?.NumberOfDiscs))
            extras.Add(title!.NumberOfDiscs + (title.NumberOfDiscs == "1" ? " disc" : " discs"));
        if (!string.IsNullOrWhiteSpace(title?.Ensemble))
            extras.Add(title!.Ensemble);
        if (!string.IsNullOrWhiteSpace(title?.AspectRatio))
            extras.Add(title!.AspectRatio);
        if (!string.IsNullOrWhiteSpace(title?.Esrb))
            extras.Add("ESRB " + title!.Esrb);
        if (!string.IsNullOrWhiteSpace(title?.Artists))
            extras.Add(title!.Artists);
        if (!string.IsNullOrWhiteSpace(title?.NumberOfPlayers))
            extras.Add(title!.NumberOfPlayers + " players");
        if (!string.IsNullOrWhiteSpace(title?.AgeGroup))
            extras.Add("Ages " + title!.AgeGroup);
        DetailExtras = string.Join("   ·   ", extras);
        HasExtras = extras.Count > 0;
        DescriptionIsPlaceholder = string.IsNullOrWhiteSpace(title?.Description);
        DetailDescription = DescriptionIsPlaceholder ? "No description yet." : title!.Description!;
        DetailNotes = title?.Notes ?? "";
        HasNotes = !string.IsNullOrWhiteSpace(title?.Notes);
        var bits = new List<string>();
        if (!string.IsNullOrWhiteSpace(value.ReadingStatus))
            bits.Add("Status from Libib: " + value.ReadingStatus);
        if (!string.IsNullOrWhiteSpace(value.Rating))
            bits.Add("Rating from Libib: " + value.Rating);
        if (!string.IsNullOrWhiteSpace(value.LibibTags))
            bits.Add("Tags from Libib: " + value.LibibTags);
        if (!string.IsNullOrWhiteSpace(value.Collections))
            bits.Add("Collection: " + value.Collections);
        DetailLibib = string.Join("\n", bits);
        HasLibib = bits.Count > 0;
        NeedsIsbn = value.NeedsIsbn;
        ShelfForCopy = value.LocationId is long id ? Shelves.FirstOrDefault(s => s.Id == id) : null;
        var coverPath = value.CoverPath;
        if (string.IsNullOrWhiteSpace(coverPath))
            coverPath = _db.CurrentCoverPath(value.TitleId);
        SetCover(coverPath);
        ShowLookup();
        if (!sameBook && _fill.Wants(value.TitleId))
            _ = FillSelectedAsync(value.TitleId, force: false);
    }

    private void ShowLookup()
    {
        var titleId = Selected?.TitleId;
        LookingUp = titleId is long open && _filling.Contains(open);
        CanLookUpAgain = titleId is long book && !LookingUp && _fill.Wants(book, force: true);
        if (titleId != null && DescriptionIsPlaceholder)
            DetailDescription = LookingUp ? "Looking up…" : "No description yet.";
    }

    [RelayCommand]
    private void LookUpAgain()
    {
        if (Selected != null)
            _ = FillSelectedAsync(Selected.TitleId, force: true);
    }

    [RelayCommand]
    private void BeginEdit()
    {
        if (Selected == null)
            return;
        var title = _db.Title(Selected.TitleId);
        if (title == null)
        {
            Error = "That book is no longer in the catalog.";
            return;
        }
        ConfirmingDelete = false;
        EditTitle = title.Title;
        EditCreators = title.Creators ?? "";
        EditSeries = title.Series ?? "";
        EditPublisher = title.Publisher ?? "";
        EditPublished = title.PublishDate ?? "";
        EditDescription = title.Description ?? "";
        EditNotes = title.Notes ?? "";
        IsbnText = "";
        EditKind = KindChoices.FirstOrDefault(k => k.Kind == title.Kind) ?? KindChoices[0];
        EditLength = title.LengthOf ?? "";
        EditDiscs = title.NumberOfDiscs ?? "";
        EditEnsemble = title.Ensemble ?? "";
        EditAspect = title.AspectRatio ?? "";
        EditEsrb = title.Esrb ?? "";
        EditArtists = title.Artists ?? "";
        EditPlayers = title.NumberOfPlayers ?? "";
        EditAge = title.AgeGroup ?? "";
        Error = null;
        Editing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        Editing = false;
        Error = null;
        IsbnText = "";
    }

    [RelayCommand]
    private void SaveDetails()
    {
        if (Selected == null)
            return;
        var message = _db.UpdateBookDetails(Selected.TitleId, new BookDetails(
            EditTitle, EditCreators, EditSeries, EditPublisher, EditPublished, EditDescription, EditNotes,
            EditKind?.Kind, EditLength, EditDiscs, EditEnsemble, EditAspect, EditEsrb, EditArtists, EditPlayers, EditAge));
        if (message != null)
        {
            Error = message;
            return;
        }
        Error = null;
        Editing = false;
        ApplySearch(keepOpenBook: true);
    }

    [RelayCommand]
    private async Task ChooseCover()
    {
        if (Selected == null || PickCover == null)
            return;
        var path = await PickCover();
        if (path == null)
            return;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not ".png" and not ".jpg" and not ".jpeg" and not ".webp")
        {
            Error = "Choose a PNG, JPEG, or WebP image.";
            return;
        }
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            Error = "That image could not be read.";
            return;
        }
        if (info.Length > 30 * 1024 * 1024)
        {
            Error = "That image is larger than 30 MB.";
            return;
        }
        try
        {
            using var probe = new Bitmap(path);
        }
        catch
        {
            Error = "That file could not be read as an image.";
            return;
        }
        Directory.CreateDirectory(_paths.Covers);
        var dest = Path.Combine(_paths.Covers, "book-" + Selected.TitleId + "-" + Guid.NewGuid().ToString("N") + ext);
        var previous = _db.CurrentCoverPath(Selected.TitleId);
        try
        {
            File.Copy(path, dest, true);
        }
        catch
        {
            Error = "That image could not be copied into the catalog.";
            return;
        }
        var message = _db.SetCoverPath(Selected.TitleId, dest);
        if (message != null)
        {
            TryDelete(dest);
            Error = message;
            return;
        }
        DeleteIfOwned(previous, dest);
        Error = null;
        ApplySearch(keepOpenBook: true);
    }

    [RelayCommand]
    private void RemoveCover()
    {
        if (Selected == null)
            return;
        var previous = _db.CurrentCoverPath(Selected.TitleId);
        var message = _db.SetCoverPath(Selected.TitleId, null);
        if (message != null)
        {
            Error = message;
            return;
        }
        DeleteIfOwned(previous, null);
        Error = null;
        ApplySearch(keepOpenBook: true);
    }

    [RelayCommand]
    private void LeaveUnlocated()
    {
        ShelfForCopy = null;
        SaveShelf();
    }

    [RelayCommand]
    private void SaveShelf()
    {
        if (Selected?.CopyId is not long copyId)
        {
            Error = "This book has no copy to move.";
            return;
        }
        _db.MoveCopy(copyId, ShelfForCopy?.Id);
        Error = null;
        ApplySearch(keepOpenBook: true);
    }

    [RelayCommand]
    private void SetRating(double? rating)
    {
        if (Selected == null || _loading)
            return;
        var message = _db.SetUserRating(Selected.TitleId, rating);
        if (message != null)
        {
            Error = message;
            return;
        }
        Error = null;
        ApplySearch(keepOpenBook: true);
    }

    [RelayCommand]
    private void ClearRating() => SetRating(null);

    [RelayCommand]
    private async Task CopyIsbn()
    {
        var isbn = Selected?.Kind == MediaKind.Book ? Selected?.Isbn13 : Selected?.Upc ?? Selected?.Isbn13;
        if (string.IsNullOrWhiteSpace(isbn) || CopyText == null)
            return;
        try
        {
            await CopyText(isbn);
        }
        catch
        {
            Error = "The " + CodeLabel + " could not be copied.";
            return;
        }
        CopyIsbnLabel = "Copied";
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    [RelayCommand]
    private void AddTag()
    {
        if (Selected == null)
            return;
        var message = _db.AddUserTags(Selected.TitleId, TagDraft);
        if (message != null)
        {
            Error = message;
            return;
        }
        TagDraft = "";
        Error = null;
        ApplySearch(keepOpenBook: true);
    }

    private void RemoveTagNow(string tag)
    {
        if (Selected == null)
            return;
        _db.RemoveUserTag(Selected.TitleId, tag);
        Error = null;
        ApplySearch(keepOpenBook: true);
    }

    [RelayCommand]
    private void AskDelete()
    {
        if (Selected == null)
            return;
        Error = null;
        ConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete() => ConfirmingDelete = false;

    [RelayCommand]
    private void DeleteBook()
    {
        if (Selected == null)
            return;
        var message = _db.DeleteTitle(Selected.TitleId);
        if (message != null)
        {
            Error = message;
            return;
        }
        Error = null;
        ConfirmingDelete = false;
        Selected = null;
        ApplySearch();
    }

    [RelayCommand]
    private void AttachIsbn()
    {
        if (Selected == null)
            return;
        var titleId = Selected.TitleId;
        var message = _db.AttachIsbn(titleId, IsbnText);
        if (message != null)
        {
            Error = message;
            return;
        }
        Error = null;
        IsbnText = "";
        ApplySearch(keepOpenBook: true);
        _ = FillSelectedAsync(titleId, force: true);
    }

    private void ApplySearch(bool keepOpenBook = false)
    {
        var choice = Filter ?? new ShelfChoice("All titles", "all", null);
        var rating = RatingFilter ?? RatingFilters[0];
        var selectedId = Selected?.CopyId;
        var selectedTitle = Selected?.TitleId;
        var rows = _db.Search(Query, choice.Kind, choice.ShelfId, rating.Stars, rating.UnratedOnly,
            keepOpenBook ? selectedTitle : null);
        CatalogRow? next = null;
        // Clearing the list makes the ListBox push a null selection, which must not close the open book.
        _applying = true;
        try
        {
            Rows.Clear();
            foreach (var row in rows)
                Rows.Add(row);
            if (selectedId != null)
                next = Rows.FirstOrDefault(r => r.CopyId == selectedId) ?? Rows.FirstOrDefault(r => r.TitleId == selectedTitle);
            else if (selectedTitle != null)
                next = Rows.FirstOrDefault(r => r.TitleId == selectedTitle);
            Selected = next;
        }
        finally
        {
            _applying = false;
        }
        OnPropertyChanged(nameof(Selected));
        ShowBook(next);
        ShownCount = Rows.Count;
        Empty = !_db.HasTitles;
    }

    private void DeleteIfOwned(string? previous, string? keep)
    {
        if (string.IsNullOrWhiteSpace(previous))
            return;
        if (keep != null && string.Equals(Path.GetFullPath(previous), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
            return;
        var full = Path.GetFullPath(previous);
        var root = Path.GetFullPath(_paths.Covers);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return;
        if (_db.CoverInUse(previous) || _db.CoverInUse(full))
            return;
        TryDelete(full);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A cover file left behind is better than blocking the catalog.
        }
    }

    private async Task FillSelectedAsync(long titleId, bool force)
    {
        _filling.Add(titleId);
        if (Selected?.TitleId == titleId)
        {
            LookupNote = "";
            ShowLookup();
        }
        var result = FillResult.Skipped;
        try
        {
            result = await _fill.RunAsync(titleId, force);
        }
        catch
        {
            // The book stays unchecked, so opening it again tries again.
        }
        _filling.Remove(titleId);
        if (Selected?.TitleId != titleId)
            return;
        if (result.Changed)
            ApplySearch(keepOpenBook: true);
        else
            ShowLookup();
        if (force && result.Ran && !result.Changed)
            LookupNote = result.Complete
                ? "Nothing more was found online."
                : "The lookup sites did not answer. Try again in a few minutes.";
    }

    private void SetCover(string? path)
    {
        Cover?.Dispose();
        if (path != null && File.Exists(path))
        {
            Cover = new Bitmap(path);
            HasCover = true;
        }
        else
        {
            Cover = null;
            HasCover = false;
        }
    }

    private static string FormatWhen(string? utc)
    {
        if (string.IsNullOrWhiteSpace(utc) || !DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time))
            return "Not confirmed yet";
        return "Last confirmed " + time.ToLocalTime().ToString("MMM d, yyyy  h:mm tt", CultureInfo.CurrentCulture);
    }
}

public sealed record ShelfChoice(string Label, string Kind, long? ShelfId);

public sealed record RatingChoice(string Label, double? Stars, bool UnratedOnly);

public sealed partial class TagChip : ObservableObject
{
    private readonly Action<string> _remove;

    public TagChip(string name, Action<string> remove)
    {
        Name = name;
        _remove = remove;
    }

    public string Name { get; }

    [RelayCommand]
    private void Remove() => _remove(Name);
}
