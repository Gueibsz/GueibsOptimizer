using System.Windows;
using System.Windows.Controls;
using GueibsOptimizer.ViewModels;

namespace GueibsOptimizer.Views;

public partial class WindowsUpdateView : UserControl
{
    public WindowsUpdateView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is WindowsUpdateViewModel vm) await vm.RefreshAsync();
    }
}
