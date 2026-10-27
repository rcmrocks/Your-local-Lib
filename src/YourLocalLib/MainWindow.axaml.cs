using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YourLocalLib.Core;
using YourLocalLib.ViewModels;
using YourLocalLib.Views;
using Microsoft.Extensions.DependencyInjection;

namespace YourLocalLib;

public partial class MainWindow : Window
{
    private const string PatreonUrl = "https://patreon.com/Rcmrocks?utm_medium=unknown&utm_source=join_link&utm_campaign=creatorshare_creator&utm_content=copyLink";
    private readonly StringBuilder _burst = new();
    private DateTime _lastInput;
    private bool _capturing;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKey, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnPreviewText, RoutingStrategies.Tunnel);
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private void OnOpened(object? sender, EventArgs e)
    {
        var shell = Shell;
        if (shell == null)
            return;
        var db = App.Services.GetRequiredService<LibraryDatabase>();
        if (db.WindowWidth >= MinWidth)
            Width = db.WindowWidth;
        if (db.WindowHeight >= MinHeight)
            Height = db.WindowHeight;
        shell.Import.PickCsv = PickCsv;
        shell.Import.Backup.PickSave = PickBackupSave;
        shell.Import.Backup.PickOpen = PickBackupOpen;
        shell.Library.PickCover = PickCover;
        shell.Library.CopyText = text => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        shell.Shelves.PickPdf = PickPdf;
        shell.Reports.PickSave = PickSave;
        shell.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ShellViewModel.Section) && shell.IsScan)
                Dispatcher.UIThread.Post(FocusScan, DispatcherPriority.Background);
        };
    }

    private async void OnSupportClick(object? sender, RoutedEventArgs e)
    {
        FocusScan();
        try
        {
            await Launcher.LaunchUriAsync(new Uri(PatreonUrl));
        }
        catch (Exception)
        {
            // With no default browser there is nowhere to open the page.
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (WindowState != WindowState.Normal)
            return;
        var db = App.Services.GetRequiredService<LibraryDatabase>();
        db.WindowWidth = Width;
        db.WindowHeight = Height;
    }

    private async Task<string?> PickCsv()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Libib collection export",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*.*" } }
            }
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private async Task<string?> PickCover()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Cover",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Image") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*.*" } }
            }
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private async Task<string?> PickPdf()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save shelf labels",
            SuggestedFileName = "shelf-labels.pdf",
            DefaultExtension = "pdf",
            FileTypeChoices = new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } }
        });
        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickBackupSave(string name)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save a library backup",
            SuggestedFileName = name,
            SuggestedStartLocation = await RemovableDriveAsync(),
            DefaultExtension = "zip",
            FileTypeChoices = new[] { new FilePickerFileType("Library backup") { Patterns = new[] { "*.zip" } } }
        });
        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickBackupOpen()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Restore a library backup",
            AllowMultiple = false,
            SuggestedStartLocation = await RemovableDriveAsync(),
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Library backup") { Patterns = new[] { "*.zip", "*.db" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*.*" } }
            }
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private async Task<IStorageFolder?> RemovableDriveAsync()
    {
        try
        {
            var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.DriveType == DriveType.Removable);
            if (drive == null)
                return null;
            return await StorageProvider.TryGetFolderFromPathAsync(drive.RootDirectory.FullName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<string?> PickSave(string name, string kind)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save " + kind,
            SuggestedFileName = name,
            DefaultExtension = "csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } }
        });
        return file?.TryGetLocalPath();
    }

    private void OnPreviewText(object? sender, TextInputEventArgs e)
    {
        if (Shell is not { IsScan: true } || string.IsNullOrEmpty(e.Text))
            return;
        var now = DateTime.UtcNow;
        var fast = (now - _lastInput).TotalMilliseconds < 40;
        _lastInput = now;
        var scan = Find<TextBox>("ScanBox");
        var dump = Find<TextBox>("DumpBox");
        var focused = FocusManager?.GetFocusedElement();
        if (focused == scan || focused == dump)
        {
            _capturing = false;
            return;
        }
        if (!fast && !_capturing)
            return;
        if (focused is TextBox box && box.Text is { Length: > 0 } text && !_capturing)
        {
            _burst.Append(text[^1]);
            box.Text = text[..^1];
            box.CaretIndex = box.Text.Length;
        }
        _capturing = true;
        _burst.Append(e.Text);
        e.Handled = true;
    }

    private void OnPreviewKey(object? sender, KeyEventArgs e)
    {
        var shell = Shell;
        if (shell == null)
            return;
        var scan = shell.Scan;
        var focusedBox = FocusManager?.GetFocusedElement() as TextBox;
        var scanBox = Find<TextBox>("ScanBox");
        var otherBox = focusedBox != null && focusedBox != scanBox;
        // An empty scan box has nothing of its own to undo, so Ctrl+Z there undoes the last scan.
        var typing = otherBox || !string.IsNullOrEmpty(focusedBox?.Text);
        var decision = KeyRouter.Decide(e.Key.ToString(), e.KeyModifiers.HasFlag(KeyModifiers.Control), typing, scan.HasPrompt, shell.IsScan);
        switch (decision)
        {
            case KeyDecision.UndoScan:
                scan.UndoCommand.Execute(null);
                e.Handled = true;
                return;
            case KeyDecision.OpenSearch:
                shell.ShowLibraryCommand.Execute(null);
                e.Handled = true;
                Dispatcher.UIThread.Post(() => Find<TextBox>("LibrarySearch")?.Focus(), DispatcherPriority.Background);
                return;
            case KeyDecision.DismissPrompt:
                scan.DeclineCommand.Execute(null);
                e.Handled = true;
                return;
            case KeyDecision.ClearBuffer:
                if (otherBox)
                    return;
                if (scanBox != null)
                    scanBox.Text = "";
                scan.ClearBuffer();
                e.Handled = true;
                FocusScan();
                return;
            case KeyDecision.AcceptPrompt:
                if (otherBox)
                    return;
                scan.AcceptCommand.Execute(null);
                e.Handled = true;
                FocusScan();
                return;
            case KeyDecision.DeclinePrompt:
                if (otherBox)
                    return;
                scan.DeclineCommand.Execute(null);
                e.Handled = true;
                FocusScan();
                return;
        }

        if (!shell.IsScan)
            return;

        if (e.Key == Key.Enter && _capturing && _burst.Length > 0)
        {
            var value = _burst.ToString();
            _burst.Clear();
            _capturing = false;
            e.Handled = true;
            scan.SubmitScan(value);
            FocusScan();
            return;
        }

        if (e.Key == Key.Enter && focusedBox == scanBox && scanBox != null)
        {
            var value = scanBox.Text ?? "";
            scanBox.Text = "";
            e.Handled = true;
            scan.SubmitScan(value);
            FocusScan();
        }
    }

    private void FocusScan()
    {
        if (Shell is { IsScan: true })
            this.GetVisualDescendants().OfType<ScanView>().FirstOrDefault()?.FocusScan();
    }

    private T? Find<T>(string name) where T : Control =>
        this.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);
}
