using Avalonia.Controls;
using Avalonia.Input;
using YourLocalLib.ViewModels;

namespace YourLocalLib.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    private void TagBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        if (DataContext is LibraryViewModel vm)
            vm.AddTagCommand.Execute(null);
        e.Handled = true;
    }
}
