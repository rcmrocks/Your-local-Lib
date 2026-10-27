using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YourLocalLib.Core;

namespace YourLocalLib.ViewModels;

public partial class ReportsViewModel : ObservableObject
{
    private readonly LibraryDatabase _db;

    public ReportsViewModel(LibraryDatabase db) => _db = db;

    public ObservableCollection<ShelfChoice> Shelves { get; } = new();
    public ObservableCollection<string> NotScanned { get; } = new();
    public ObservableCollection<string> SeenElsewhere { get; } = new();
    public ObservableCollection<string> UnseenLibrary { get; } = new();
    public ObservableCollection<string> ExtraRows { get; } = new();
    public ObservableCollection<string> UnknownRows { get; } = new();

    [ObservableProperty] private ShelfChoice? shelf;
    [ObservableProperty] private string summary = "Finish a shelf audit or a whole-library audit to fill these lists.";
    [ObservableProperty] private string? error;
    [ObservableProperty] private bool empty;

    public Func<string, string, Task<string?>>? PickSave { get; set; }

    public void Reset()
    {
        Shelf = null;
        Refresh();
    }

    public void Refresh()
    {
        var selected = Shelf?.ShelfId;
        Shelves.Clear();
        foreach (var location in _db.Locations())
            Shelves.Add(new ShelfChoice(location.Path, "shelf", location.Id));
        Shelf = Shelves.FirstOrDefault(s => s.ShelfId == selected) ?? Shelves.FirstOrDefault();
        Empty = !_db.HasTitles;
        LoadLists();
    }

    partial void OnShelfChanged(ShelfChoice? value) => LoadLists();

    [RelayCommand] private Task ExportCatalog() => Save("library-catalog.csv", path => CsvExports.WriteCatalog(path, _db.AllCopies()));
    [RelayCommand] private Task ExportUnknowns() => Save("add-to-libib.csv", path => CsvExports.WriteAddToLibib(path, _db.NotInLibib()));
    [RelayCommand] private Task ExportShelf() => Save("not-scanned-on-shelf.csv", path =>
    {
        var session = _db.ActiveSession()?.Id ?? _db.LastFinishedLibrarySessionId();
        var rows = session != null && Shelf?.ShelfId is long id ? _db.NotScannedOnShelf(session.Value, id) : Array.Empty<GapRow>();
        CsvExports.WriteGaps(path, rows);
    });

    private void LoadLists()
    {
        NotScanned.Clear();
        SeenElsewhere.Clear();
        UnseenLibrary.Clear();
        ExtraRows.Clear();
        UnknownRows.Clear();
        var session = _db.ActiveSession();
        var sessionId = session?.Id;
        if (sessionId != null && Shelf?.ShelfId is long shelfId)
        {
            foreach (var row in _db.NotScannedOnShelf(sessionId.Value, shelfId))
                NotScanned.Add(Line(row));
            foreach (var row in _db.Disagreements(sessionId.Value, shelfId))
                SeenElsewhere.Add(row.Title + "  ·  " + row.Status);
            foreach (var row in _db.Extras(sessionId.Value))
                ExtraRows.Add(Line(row));
        }
        var finished = _db.LastFinishedLibrarySessionId();
        if (finished != null)
        {
            foreach (var row in _db.UnseenInSession(finished.Value))
                UnseenLibrary.Add(Line(row));
        }
        foreach (var row in _db.NotInLibib())
            UnknownRows.Add(Line(row));
        var name = session?.Mode switch
        {
            SessionMode.Shelf => "shelf audit",
            SessionMode.Library => "whole-library audit",
            SessionMode.Place => "place",
            _ => null
        };
        Summary = name == null
            ? "No scan session is open. The lists below are from the last finished whole-library audit, if you have one."
            : "Showing the open " + name + " session" + (Shelf == null ? "." : " at " + Shelf.Label + ".");
    }

    private async Task Save(string name, Action<string> write)
    {
        if (PickSave == null)
            return;
        var path = await PickSave(name, "CSV");
        if (path == null)
            return;
        try
        {
            write(path);
            Error = null;
            Summary = "Saved " + Path.GetFileName(path) + ".";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private static string Line(GapRow row) =>
        row.Title + (string.IsNullOrWhiteSpace(row.Creators) ? "" : "  ·  " + row.Creators) +
        (string.IsNullOrWhiteSpace(row.Isbn13) ? "" : "  ·  " + row.Isbn13);
}
