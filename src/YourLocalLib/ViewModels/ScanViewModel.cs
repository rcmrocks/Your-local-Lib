using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YourLocalLib.Core;
using YourLocalLib.Services;

namespace YourLocalLib.ViewModels;

public partial class ScanViewModel : ObservableObject
{
    private readonly LibraryDatabase _db;
    private readonly BookFill _fill;
    private readonly IScanFeedback _feedback;
    private readonly AppEvents _events;
    private readonly HashSet<long> _busyScans = new();
    private bool _loading;
    private long? _retryScanId;
    private string? _retryIsbn;
    private long? _retryTitleId;
    private string? _pendingMode;
    private long? _shownScanId;

    public ScanViewModel(LibraryDatabase db, BookFill fill, IScanFeedback feedback, AppEvents events)
    {
        _db = db;
        _fill = fill;
        _feedback = feedback;
        _events = events;
        foreach (var kind in MediaKind.All)
            Kinds.Add(new KindChoice(MediaKind.Label(kind), kind));
        _loading = true;
        SelectedKind = Kinds[0];
        _loading = false;
    }

    public ObservableCollection<LocationInfo> Shelves { get; } = new();
    public ObservableCollection<string> Recent { get; } = new();
    public ObservableCollection<KindChoice> Kinds { get; } = new();

    [ObservableProperty] private KindChoice? selectedKind;
    [ObservableProperty] private string headline = "Scan a title";
    [ObservableProperty] private string author = "";
    [ObservableProperty] private string status = "Ready";
        [ObservableProperty] private string detail = "Type an ISBN or UPC, or scan a barcode. Enter records it.";
    [ObservableProperty] private bool isProblem;
    [ObservableProperty] private bool hasPrompt;
    [ObservableProperty] private string promptYes = "Move here";
    [ObservableProperty] private bool showEmpty = true;
    [ObservableProperty] private bool modeConfirm;
    [ObservableProperty] private string mode = SessionMode.Place;
    [ObservableProperty] private string modeLabel = "Place";
    [ObservableProperty] private string shelfLabel = "No shelf selected";
    [ObservableProperty] private int confirmed;
    [ObservableProperty] private int notInLibib;
    [ObservableProperty] private int extras;
    [ObservableProperty] private string? error;
    [ObservableProperty] private bool canRetry;
    [ObservableProperty] private bool canFinish;
    [ObservableProperty] private LocationInfo? selectedShelf;
    [ObservableProperty] private Bitmap? cover;
    [ObservableProperty] private bool hasCover;
    [ObservableProperty] private bool autoMove;
    [ObservableProperty] private bool lookingUp;

    public string CountLine => $"{Confirmed} confirmed   ·   {NotInLibib} not in your library   ·   {Extras} extras";
    public bool IsPlace => Mode == SessionMode.Place;
    public bool IsShelfMode => Mode == SessionMode.Shelf;
    public bool IsLibraryMode => Mode == SessionMode.Library;

    partial void OnModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsPlace));
        OnPropertyChanged(nameof(IsShelfMode));
        OnPropertyChanged(nameof(IsLibraryMode));
        ModeLabel = value switch
        {
            SessionMode.Shelf => "Shelf audit",
            SessionMode.Library => "Whole library",
            _ => "Place"
        };
    }

    partial void OnConfirmedChanged(int value) => OnPropertyChanged(nameof(CountLine));
    partial void OnNotInLibibChanged(int value) => OnPropertyChanged(nameof(CountLine));
    partial void OnExtrasChanged(int value) => OnPropertyChanged(nameof(CountLine));

    public void Refresh()
    {
        _loading = true;
        var session = _db.EnsureSession(Mode);
        Mode = session.Mode;
        ModeLabel = Label(session.Mode);
        CanFinish = session.Mode == SessionMode.Library;
        AutoMove = _db.AutoMove;
        var kind = _db.ScanKind;
        SelectedKind = Kinds.FirstOrDefault(k => k.Kind == kind) ?? Kinds[0];
        Shelves.Clear();
        foreach (var shelf in _db.Locations())
            Shelves.Add(shelf);
        SelectedShelf = Shelves.FirstOrDefault(s => s.Id == session.CurrentLocationId);
        ShelfLabel = SelectedShelf?.Path ?? "No shelf selected";
        ShowEmpty = !_db.HasTitles;
        if (session.Id > 0)
        {
            var counts = _db.SessionCounts(session.Id);
            Confirmed = counts.Confirmed;
            NotInLibib = counts.NotInLibib;
            Extras = counts.Extras;
            Recent.Clear();
            foreach (var line in _db.RecentLines(session.Id))
                Recent.Add(line);
        }
        _loading = false;
        if (ShowEmpty)
        {
            Headline = "Your shelves are not in here yet";
            Author = "";
            Status = "Import a Libib CSV";
            Detail = "On the Libib website, open Settings and export one collection as CSV. Import that file, then scan the barcode on each title.";
            IsProblem = false;
            HasPrompt = false;
        }
    }

    partial void OnSelectedShelfChanged(LocationInfo? value)
    {
        if (_loading || value == null)
            return;
        _db.SetShelf(value.Id);
        ShelfLabel = value.Path;
    }

    partial void OnAutoMoveChanged(bool value)
    {
        if (!_loading)
            _db.AutoMove = value;
    }

    partial void OnSelectedKindChanged(KindChoice? value)
    {
        if (!_loading && value?.Kind != null)
            _db.ScanKind = value.Kind;
    }

    public void Reset()
    {
        _busyScans.Clear();
        _retryScanId = null;
        _retryIsbn = null;
        _retryTitleId = null;
        _pendingMode = null;
        _shownScanId = null;
        ModeConfirm = false;
        Mode = SessionMode.Place;
        Headline = "Scan a title";
        Author = "";
        Status = "Ready";
        Detail = "Type an ISBN or UPC, or scan a barcode. Enter records it.";
        IsProblem = false;
        HasPrompt = false;
        CanRetry = false;
        LookingUp = false;
        Error = null;
        SetCover(null);
        Refresh();
    }

    [RelayCommand]
    private void ChooseMode(string mode)
    {
        if (_db.NeedsModeConfirmation(mode))
        {
            _pendingMode = mode;
            ModeConfirm = true;
            return;
        }
        _pendingMode = null;
        var session = _db.SwitchMode(mode, false);
        Mode = session.Mode;
        ModeConfirm = false;
        Refresh();
    }

    [RelayCommand]
    private void ConfirmNewSession()
    {
        if (_pendingMode == null)
            return;
        var mode = _pendingMode;
        _pendingMode = null;
        Mode = mode;
        _db.SwitchMode(mode, true);
        ModeConfirm = false;
        Refresh();
    }

    [RelayCommand]
    private void StayInSession()
    {
        _pendingMode = null;
        ModeConfirm = false;
        Refresh();
    }

    [RelayCommand]
    private void Finish()
    {
        if (_db.FinishLibraryAudit())
        {
            Status = "Whole-library audit finished";
            Detail = "The report lists Libib copies that were not seen. Closing the window would not have done this.";
            HasPrompt = false;
            _events.Go("reports");
        }
    }

    [RelayCommand] private void GoImport() => _events.Go("import");

    [RelayCommand]
    private void LoadSample()
    {
        try
        {
            _db.LoadSample();
            Error = null;
            Refresh();
            _events.Changed();
            Headline = "Sample library ready";
            Status = "Two shelves, five pretend books";
            Detail = "Leave the type on Book, scan 9780000000019 or type it, then press Enter. Remove the sample whenever you import your own CSV.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private void Accept()
    {
        var outcome = _db.Answer(true);
        Apply(outcome);
        if (outcome.Result == ScanResult.AddedCopy && outcome.TitleId is long titleId)
            _ = FillKnownAsync(titleId, outcome.ScanId);
    }

    [RelayCommand]
    private void Decline() => Apply(_db.Answer(false));

    [RelayCommand]
    private void Undo()
    {
        _feedback.Cancel();
        Apply(_db.Undo());
    }

    [RelayCommand]
    private async Task Retry()
    {
        if (_retryScanId is long scanId && _retryIsbn != null)
            await LookupAsync(scanId, _retryIsbn, _retryTitleId);
    }

    public void SubmitScan(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;
        _feedback.Cancel();
        var outcome = _db.Submit(raw.Trim(), ScanOrigin.Live, SelectedKind?.Kind);
        Apply(outcome);
        QueueFill(outcome);
    }

    public void SubmitDump(string raw)
    {
        var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            _feedback.Cancel();
            var outcome = _db.Submit(line, ScanOrigin.Dump, SelectedKind?.Kind);
            Apply(outcome);
            QueueFill(outcome);
        }
    }

    private void QueueFill(ScanOutcome outcome)
    {
        if (outcome.Isbn13 == null)
            return;
        if (outcome.StartLookup && outcome.ScanId is long scanId)
            _ = LookupAsync(scanId, outcome.Isbn13, outcome.TitleId);
        else if (outcome.TitleId is long titleId)
            _ = FillKnownAsync(titleId, outcome.ScanId);
    }

    public void ClearBuffer()
    {
        if (HasPrompt)
        {
            Decline();
            return;
        }
        Status = "Ready";
        Detail = "The scan box is clear.";
    }

    /// <summary>The title comes first so the card updates quickly. The cover, description, and tags follow.</summary>
    private async Task LookupAsync(long scanId, string isbn, long? titleId)
    {
        _retryScanId = scanId;
        _retryIsbn = isbn;
        _retryTitleId = titleId;
        SetBusy(scanId, true);
        var generation = _db.Generation;
        try
        {
            TitleAnswer? first = null;
            ScanOutcome outcome;
            try
            {
                first = await _fill.FindTitleAsync(isbn, SelectedKind?.Kind);
                outcome = _db.ApplyLookup(scanId, first.Book, null, first.NotFound, generation);
            }
            catch
            {
                outcome = _db.ApplyLookup(scanId, null, null, false, generation);
            }
            if (_db.Generation != generation)
                return;
            // An undone or deleted scan has nothing to show, and its card has already moved on.
            if (outcome.Result != ScanResult.Undone)
                Apply(outcome);
            if ((outcome.TitleId ?? titleId) is long id)
                await FillAndShowAsync(id, scanId, first);
        }
        catch
        {
            // The scan or the book is already gone.
        }
        finally
        {
            SetBusy(scanId, false);
        }
    }

    private async Task FillKnownAsync(long titleId, long? scanId)
    {
        SetBusy(scanId, true);
        try
        {
            await FillAndShowAsync(titleId, scanId, null);
        }
        catch
        {
            // The book stays unchecked, so the next scan or opening it in Library tries again.
        }
        finally
        {
            SetBusy(scanId, false);
        }
    }

    private async Task FillAndShowAsync(long titleId, long? scanId, TitleAnswer? first)
    {
        var result = await _fill.RunAsync(titleId, false, first);
        if (result.CoverAdded && _shownScanId == scanId)
            SetCover(_db.CurrentCoverPath(titleId));
    }

    private void SetBusy(long? scanId, bool busy)
    {
        if (scanId is long id)
        {
            if (busy)
                _busyScans.Add(id);
            else
                _busyScans.Remove(id);
        }
        ShowBusy();
    }

    private void ShowBusy() => LookingUp = _shownScanId is long shown && _busyScans.Contains(shown);

    private void Apply(ScanOutcome outcome)
    {
        if (string.IsNullOrEmpty(outcome.Result))
            return;
        if (!outcome.KeepPreviousHero && !string.IsNullOrWhiteSpace(outcome.Headline))
        {
            Headline = outcome.Headline;
            Author = outcome.Author;
            SetCover(outcome.CoverPath);
            _shownScanId = outcome.ScanId;
            ShowBusy();
        }
        if (!string.IsNullOrWhiteSpace(outcome.Status))
            Status = outcome.Status;
        if (!string.IsNullOrWhiteSpace(outcome.Detail))
            Detail = outcome.Detail;
        IsProblem = outcome.Problem;
        HasPrompt = outcome.Prompt;
        PromptYes = outcome.Result == ScanResult.PlacePrompt ? "Place it here" : "Move here";
        CanRetry = outcome.Result == ScanResult.LookupFailed;
        if (outcome.Chime)
            _feedback.Chime();
        if (outcome.Problem)
            _feedback.Problem();
        if (outcome.Speak && outcome.Speech != null && _db.SpeechEnabled)
            _feedback.Speak(outcome.Speech);
        Refresh();
        _events.Changed();
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

    private static string Label(string mode) => mode switch
    {
        SessionMode.Shelf => "Shelf audit",
        SessionMode.Library => "Whole library",
        _ => "Place"
    };
}
