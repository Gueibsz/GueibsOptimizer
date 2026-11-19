using System.Windows;
using System.Windows.Controls;
using GueibsOptimizer.ViewModels;

namespace GueibsOptimizer.Views;

public partial class DebloatView : UserControl
{
    public DebloatView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is DebloatViewModel vm) await vm.EnsureLoadedAsync();
    }
}
