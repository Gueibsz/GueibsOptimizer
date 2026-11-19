using System.Windows;
using System.Windows.Controls;
using GueibsOptimizer.ViewModels;

namespace GueibsOptimizer.Views;

public partial class PreferencesView : UserControl
{
    public PreferencesView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is PreferencesViewModel vm) await vm.RefreshAsync();
    }
}
