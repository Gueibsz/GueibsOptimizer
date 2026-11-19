using System.Windows;
using System.Windows.Controls;
using GueibsOptimizer.ViewModels;

namespace GueibsOptimizer.Views;

public partial class OverlayTestsView : UserControl
{
    public OverlayTestsView() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e) => (DataContext as OverlayTestsViewModel)?.OnShown();
}
