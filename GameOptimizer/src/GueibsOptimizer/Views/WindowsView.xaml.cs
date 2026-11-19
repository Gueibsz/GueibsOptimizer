using System.Windows;
using System.Windows.Controls;
using GueibsOptimizer.ViewModels;

namespace GueibsOptimizer.Views;

public partial class WindowsView : UserControl
{
    public WindowsView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is WindowsViewModel vm) await vm.EnsureLoadedAsync();
    }

    /// <summary>Mantém a saída da ferramenta rolada até a última linha.</summary>
    private void OnToolOutputChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box) box.ScrollToEnd();
    }
}
