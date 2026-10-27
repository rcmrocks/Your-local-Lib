using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace YourLocalLib.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    private readonly DispatcherTimer _catalogTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public ScanViewModel Scan { get; }
    public LibraryViewModel Library { get; }
    public ShelvesViewModel Shelves { get; }
    public ImportViewModel Import { get; }
    public ReportsViewModel Reports { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty] private string section = "scan";

    public ShellViewModel(ScanViewModel scan, LibraryViewModel library, ShelvesViewModel shelves, ImportViewModel import, ReportsViewModel reports, SettingsViewModel settings, AppEvents events)
    {
        Scan = scan;
        Library = library;
        Shelves = shelves;
        Import = import;
        Reports = reports;
        Settings = settings;
        events.NavigationRequested += Go;
        events.CatalogChanged += () =>
        {
            _catalogTimer.Stop();
            _catalogTimer.Start();
        };
        events.LibraryReplaced += () =>
        {
            Scan.Reset();
            Library.Reset();
            Shelves.Reset();
            Reports.Reset();
            Settings.Refresh();
            Settings.ApplyTheme();
        };
        _catalogTimer.Tick += (_, _) =>
        {
            _catalogTimer.Stop();
            if (!IsLibrary)
                Library.Refresh();
        };
        Scan.Refresh();
        Library.Refresh();
    }

    public bool IsScan => Section == "scan";
    public bool IsLibrary => Section == "library";
    public bool IsShelves => Section == "shelves";
    public bool IsImport => Section == "import";
    public bool IsReports => Section == "reports";
    public bool IsSettings => Section == "settings";

    partial void OnSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsScan));
        OnPropertyChanged(nameof(IsLibrary));
        OnPropertyChanged(nameof(IsShelves));
        OnPropertyChanged(nameof(IsImport));
        OnPropertyChanged(nameof(IsReports));
        OnPropertyChanged(nameof(IsSettings));
        RefreshCurrent();
    }

    [RelayCommand] private void ShowScan() => Section = "scan";
    [RelayCommand] private void ShowLibrary() => Section = "library";
    [RelayCommand] private void ShowShelves() => Section = "shelves";
    [RelayCommand] private void ShowImport() => Section = "import";
    [RelayCommand] private void ShowReports() => Section = "reports";
    [RelayCommand] private void ShowSettings() => Section = "settings";

    public void Go(string section) => Section = section;

    public void RefreshCurrent()
    {
        switch (Section)
        {
            case "scan": Scan.Refresh(); break;
            case "library": Library.Refresh(); break;
            case "shelves": Shelves.Refresh(); break;
            case "import": Import.Refresh(); break;
            case "reports": Reports.Refresh(); break;
            case "settings": Settings.Refresh(); break;
        }
    }
}
