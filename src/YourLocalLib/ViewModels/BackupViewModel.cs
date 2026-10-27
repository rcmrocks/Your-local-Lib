using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YourLocalLib.Core;

namespace YourLocalLib.ViewModels;

public partial class BackupViewModel : ObservableObject
{
    private readonly LibraryDatabase _db;
    private readonly AppPaths _paths;
    private readonly AppEvents _events;
    private string? _restorePath;

    public BackupViewModel(LibraryDatabase db, AppPaths paths, AppEvents events)
    {
        _db = db;
        _paths = paths;
        _events = events;
    }

    public Func<string, Task<string?>>? PickSave { get; set; }
    public Func<Task<string?>>? PickOpen { get; set; }

    [ObservableProperty] private bool busy;
    [ObservableProperty] private string busyText = "";
    [ObservableProperty] private string? message;
    [ObservableProperty] private string? error;
    [ObservableProperty] private bool confirming;
    [ObservableProperty] private string confirmDetail = "";
    [ObservableProperty] private string confirmWarning = "";

    [RelayCommand]
    private async Task SaveBackup()
    {
        if (PickSave == null || Busy)
            return;
        Error = null;
        Message = null;
        Confirming = false;
        var suggested = "Your Local Lib backup " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".zip";
        var path = await PickSave(suggested);
        if (path == null)
            return;
        Busy = true;
        BusyText = "Saving the backup…";
        try
        {
            var report = await Task.Run(() => LibraryBackup.Save(_db, path, AppVersion()));
            Message = "Saved " + Describe(report.Counts, report.Covers) + " in " + Size(report.Bytes) + " to " + report.Path + ".";
        }
        catch (BackupException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            Error = "The backup could not be saved. " + ex.Message;
        }
        finally
        {
            Busy = false;
            BusyText = "";
        }
    }

    [RelayCommand]
    private async Task ChooseRestore()
    {
        if (PickOpen == null || Busy)
            return;
        Error = null;
        Message = null;
        Confirming = false;
        _restorePath = null;
        var path = await PickOpen();
        if (path == null)
            return;
        Busy = true;
        BusyText = "Reading the backup…";
        try
        {
            var check = await Task.Run(() => LibraryBackup.Inspect(path, _db.Path));
            _restorePath = path;
            Confirming = true;
            var when = check.CreatedUtc is DateTime utc
                ? utc.ToLocalTime().ToString("MMMM d, yyyy 'at' h:mm tt", CultureInfo.CurrentCulture)
                : "an unknown date";
            var version = string.IsNullOrWhiteSpace(check.AppVersion) ? AppBrand.Name : AppBrand.Name + " " + check.AppVersion;
            ConfirmDetail = "Saved " + when + " with " + version + ". It holds " + Describe(check.Counts, check.Covers) + ".";
            ConfirmWarning = _db.HasOwnBooksOrShelves()
                ? "Everything in the library on this computer is replaced, including its settings. The library you have now is saved first, so you can go back."
                : "The library on this computer is empty, so nothing is lost.";
            if (check.DatabaseOnly)
                ConfirmWarning += " This file has the catalog but no covers. Covers found online come back as you open or scan each title.";
        }
        catch (BackupException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            Error = "That file could not be read. " + ex.Message;
        }
        finally
        {
            Busy = false;
            BusyText = "";
        }
    }

    [RelayCommand]
    private async Task Restore()
    {
        if (_restorePath == null || Busy)
            return;
        Error = null;
        Message = null;
        Busy = true;
        BusyText = "Restoring the library…";
        try
        {
            var path = _restorePath;
            var result = await Task.Run(() => LibraryBackup.Restore(_db, path, _paths.Covers, _paths.Backups, AppVersion()));
            Confirming = false;
            _restorePath = null;
            _events.Replaced();
            var extra = result.SafetyCopy == null
                ? ""
                : " The library you had is saved as " + result.SafetyCopy + ".";
            Message = "Restored " + Describe(result.Counts, result.Covers) + "." + extra;
        }
        catch (BackupException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            Error = "The backup could not be restored. " + ex.Message;
        }
        finally
        {
            Busy = false;
            BusyText = "";
        }
    }

    [RelayCommand]
    private void CancelRestore()
    {
        Confirming = false;
        _restorePath = null;
        ConfirmDetail = "";
        ConfirmWarning = "";
    }

    private static string AppVersion() =>
        typeof(BackupViewModel).Assembly.GetName().Version?.ToString(3) ?? AppBrand.Version;

    private static string Describe(LibraryCounts counts, int covers)
    {
        var parts = new List<string>
        {
            Phrase(counts.Books, "title", "titles"),
            Phrase(counts.Copies, "copy", "copies"),
            Phrase(counts.Shelves, "shelf", "shelves"),
            Phrase(counts.Tags, "tag", "tags"),
            Phrase(counts.Ratings, "rating", "ratings"),
            Phrase(counts.Read, "marked done", "marked done"),
            Phrase(covers, "cover", "covers")
        };
        if (parts.Count == 0)
            return "an empty library";
        if (parts.Count == 1)
            return parts[0];
        return string.Join(", ", parts.Take(parts.Count - 1)) + ", and " + parts[^1];
    }

    private static string Phrase(int count, string one, string many) =>
        count.ToString("N0", CultureInfo.CurrentCulture) + " " + (count == 1 ? one : many);

    private static string Size(long bytes) =>
        bytes >= 1024 * 1024
            ? (bytes / (1024d * 1024d)).ToString("0.#", CultureInfo.CurrentCulture) + " MB"
            : Math.Max(1, bytes / 1024d).ToString("0.#", CultureInfo.CurrentCulture) + " KB";
}
