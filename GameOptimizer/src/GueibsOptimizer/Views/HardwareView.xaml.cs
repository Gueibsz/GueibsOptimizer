using System.Windows;
using System.Windows.Controls;
using GueibsOptimizer.ViewModels;

namespace GueibsOptimizer.Views;

public partial class HardwareView : UserControl
{
    public HardwareView() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e) => (DataContext as HardwareViewModel)?.Activate();

    private void OnUnloaded(object sender, RoutedEventArgs e) => (DataContext as HardwareViewModel)?.Deactivate();
}
