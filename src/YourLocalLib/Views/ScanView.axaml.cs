using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YourLocalLib.ViewModels;

namespace YourLocalLib.Views;

public partial class ScanView : UserControl
{
    public ScanView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(FocusScan);
        ScanRoot.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble);
        // A clicked button or checkbox keeps focus, and the scanner's first digit would go to it.
        ScanRoot.AddHandler(Button.ClickEvent, (_, _) => FocusScan(), RoutingStrategies.Bubble);
    }

    private ScanViewModel? Model => DataContext as ScanViewModel;

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // The release comes from a part inside the box, such as its text presenter, not the box itself.
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors().Any(v => v is TextBox or ComboBox))
            return;
        FocusScan();
    }

    private void ShelfPickerClosed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(FocusScan);

    public void FocusScan()
    {
        if (IsEffectivelyVisible)
            ScanBox.Focus();
    }

    private void AddDumpClick(object? sender, RoutedEventArgs e)
    {
        var model = Model;
        if (model == null)
            return;
        model.SubmitDump(DumpBox.Text ?? "");
        DumpBox.Text = "";
        FocusScan();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ScanViewModel model)
            model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ScanViewModel.Headline))
                {
                    ResultCard.Opacity = 0.35;
                    Dispatcher.UIThread.Post(() => ResultCard.Opacity = 1);
                }
            };
    }
}
