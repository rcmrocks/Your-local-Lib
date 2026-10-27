using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YourLocalLib.Core;

namespace YourLocalLib.ViewModels;

public partial class ImportViewModel : ObservableObject
{
    private readonly LibraryDatabase _db;
    private readonly AppEvents _events;
    private IReadOnlyList<LibibRow> _rows = Array.Empty<LibibRow>();
    private IReadOnlyList<string> _headers = Array.Empty<string>();
    private bool _loading;

    public ImportViewModel(LibraryDatabase db, AppEvents events, BackupViewModel backup)
    {
        _db = db;
        _events = events;
        Backup = backup;
        KindChoices.Add(new KindChoice("Auto — from the file", null));
        foreach (var kind in MediaKind.All)
            KindChoices.Add(new KindChoice(MediaKind.Label(kind), kind));
        _loading = true;
        SelectedKind = KindChoices[0];
        _loading = false;
    }

    public BackupViewModel Backup { get; }
    public ObservableCollection<KindChoice> KindChoices { get; } = new();

    public Func<Task<string?>>? PickCsv { get; set; }

    [ObservableProperty] private KindChoice? selectedKind;
    [ObservableProperty] private string fileName = "No file chosen";
    [ObservableProperty] private string preview = "Choose the CSV you exported from Libib. Books, movies, music, video games, and board games all import. You will see the counts before anything is saved.";
    [ObservableProperty] private bool canImport;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private string? error;
    [ObservableProperty] private bool confirmReplace;
    [ObservableProperty] private string status = "Waiting for a file";

    public void Refresh()
    {
        Error = null;
        if (_rows.Count == 0)
            Status = "Waiting for a file";
    }

    partial void OnSelectedKindChanged(KindChoice? value)
    {
        if (_loading || _rows.Count == 0)
            return;
        ShowPreview();
    }

    [RelayCommand]
    private async Task ChooseFile()
    {
        if (PickCsv == null)
            return;
        Error = null;
        Busy = true;
        try
        {
            var path = await PickCsv();
            if (path == null)
                return;
            var file = await Task.Run(() => LibibCsv.LoadFile(path));
            _rows = file.Rows;
            _headers = file.Headers;
            FileName = Path.GetFileName(path);
            ConfirmReplace = false;
            ShowPreview();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            CanImport = false;
            Status = "The file could not be read";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Import()
    {
        if (_db.IsSample && !ConfirmReplace)
        {
            ConfirmReplace = true;
            Status = "This will replace the sample library";
            Preview = "The sample titles and shelves will be removed. Your Libib file will become the catalog. Shelves you add after this import are kept when you export from Libib again.";
            return;
        }
        Busy = true;
        try
        {
            if (_db.IsSample)
                _db.ReplaceWithImport(_rows, KindOverride(), _headers);
            else
                _db.Commit(_rows, KindOverride(), _headers);
            Status = "Import saved";
            Error = null;
            CanImport = false;
            ConfirmReplace = false;
            Preview = "The catalog is updated. Shelves already stored were left in place. Go to Scan, choose the type you are holding, and choose the shelf you are standing at.";
            _events.Changed();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = "Import failed";
        }
        finally
        {
            Busy = false;
        }
    }

    private void ShowPreview()
    {
        var preview = _db.Preview(_rows, KindOverride(), _headers);
        Preview = Describe(preview);
        CanImport = preview.CanCommit;
        Status = preview.CanCommit ? "Ready to import" : "This file needs a look";
        Error = null;
    }

    private string? KindOverride() => SelectedKind?.Kind;

    private static string Describe(ImportPreview preview)
    {
        var collections = preview.Collections.Count == 0 ? "No collection name was in the file." : "Collection: " + string.Join(", ", preview.Collections);
        var lines = new List<string>
        {
            MediaKind.Summarize(preview.Kinds),
            preview.MissingIsbn + " with no ISBN or UPC",
            preview.RepeatedIsbnGroups + (preview.RepeatedIsbnGroups == 1 ? " repeated barcode, copies will be added together" : " repeated barcodes, copies will be added together"),
            collections
        };
        if (preview.CopyCountNotes > 0)
            lines.Add(preview.CopyCountNotes + " titles list fewer copies than you have already shelved. Those copies will be kept.");
        if (preview.Errors.Count > 0)
            lines.Add(string.Join(" ", preview.Errors));
        return string.Join("\n", lines);
    }
}

public sealed record KindChoice(string Label, string? Kind);
