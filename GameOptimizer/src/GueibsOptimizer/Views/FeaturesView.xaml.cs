using System.Windows;
using System.Windows.Controls;
using GueibsOptimizer.ViewModels;

namespace GueibsOptimizer.Views;

public partial class FeaturesView : UserControl
{
    public FeaturesView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is FeaturesViewModel vm) await vm.RefreshAsync();
    }
}
