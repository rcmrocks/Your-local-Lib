using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YourLocalLib.Core;

namespace YourLocalLib.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly LibraryDatabase _db;
    private bool _loading;

    public SettingsViewModel(LibraryDatabase db) => _db = db;

    [ObservableProperty] private bool speech = true;
    [ObservableProperty] private double delay = 400;
    [ObservableProperty] private bool dark;
    [ObservableProperty] private string backupFolder = "";
    [ObservableProperty] private string googleBooksKey = "";
    [ObservableProperty] private string? message;

    public void Refresh()
    {
        _loading = true;
        Speech = _db.SpeechEnabled;
        Delay = _db.DebounceMs;
        Dark = _db.Theme == "dark";
        BackupFolder = _db.BackupFolder ?? "";
        GoogleBooksKey = _db.GoogleBooksKey ?? "";
        _loading = false;
    }

    partial void OnSpeechChanged(bool value)
    {
        if (!_loading)
            _db.SpeechEnabled = value;
    }

    partial void OnDarkChanged(bool value)
    {
        if (_loading)
            return;
        _db.Theme = value ? "dark" : "light";
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        if (Avalonia.Application.Current != null)
            Avalonia.Application.Current.RequestedThemeVariant = Dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
    }

    [RelayCommand]
    private void Save()
    {
        _db.DebounceMs = (int)Delay;
        _db.BackupFolder = string.IsNullOrWhiteSpace(BackupFolder) ? null : BackupFolder.Trim();
        _db.GoogleBooksKey = GoogleBooksKey;
        Message = "Saved. The next launch keeps ten database backups.";
    }
}
