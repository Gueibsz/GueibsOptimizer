using System.Windows;
using System.Windows.Controls;
using GueibsOptimizer.ViewModels;

namespace GueibsOptimizer.Views;

public partial class CategoryTweaksView : UserControl
{
    public CategoryTweaksView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is CategoryTweaksViewModel vm) await vm.RefreshAsync();
    }
}
