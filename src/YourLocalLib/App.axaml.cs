using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using YourLocalLib.Core;
using YourLocalLib.Services;
using YourLocalLib.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace YourLocalLib;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var root = AppDataRoot.Resolve();
        var database = LibraryDatabase.Open(Path.Combine(root, "library.db"));
        var paths = new AppPaths(root, Path.Combine(root, "covers"));
        var backupFolder = database.BackupFolder ?? paths.Backups;
        try
        {
            database.BackupTo(backupFolder);
        }
        catch
        {
            // A missing backup folder must not stop the catalog from opening.
        }

        RequestedThemeVariant = database.Theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var web = new WebSource();
        var services = new ServiceCollection();
        services.AddSingleton(database);
        services.AddSingleton(new BookFill(database, web, new OpenLibraryClient(web),
            new GoogleBooksClient(web, () => database.GoogleBooksKey), paths.Covers, 2, new MusicBrainzClient(web)));
        services.AddSingleton<IScanFeedback, WindowsScanFeedback>();
        services.AddSingleton<AppEvents>();
        services.AddSingleton(paths);
        services.AddSingleton<BackupViewModel>();
        services.AddSingleton<ScanViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<ShelvesViewModel>();
        services.AddSingleton<ImportViewModel>();
        services.AddSingleton<ReportsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ShellViewModel>();
        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow { DataContext = Services.GetRequiredService<ShellViewModel>() };

        base.OnFrameworkInitializationCompleted();
    }
}

public sealed record AppPaths(string Root, string Covers)
{
    public string Backups => Path.Combine(Root, "backups");
}

public sealed class AppEvents
{
    public event Action<string>? NavigationRequested;
    public event Action? CatalogChanged;
    public event Action? LibraryReplaced;
    public void Go(string section) => NavigationRequested?.Invoke(section);
    public void Changed() => CatalogChanged?.Invoke();
    public void Replaced() => LibraryReplaced?.Invoke();
}
