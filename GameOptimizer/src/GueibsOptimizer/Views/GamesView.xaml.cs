using System.Windows;
using System.Windows.Controls;
using GueibsOptimizer.ViewModels;

namespace GueibsOptimizer.Views;

public partial class GamesView : UserControl
{
    public GamesView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is GamesViewModel vm) await vm.EnsureLoadedAsync();
    }
}
