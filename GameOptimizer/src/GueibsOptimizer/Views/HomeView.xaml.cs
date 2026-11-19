using System.Windows;
using System.Windows.Controls;
using GueibsOptimizer.ViewModels;

namespace GueibsOptimizer.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => (DataContext as HomeViewModel)?.StopLive();
    }

    private bool _loadedOnce;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not HomeViewModel vm) return;
        vm.StartLive(); // memória e CPU só enquanto o Painel está visível
        if (_loadedOnce) return;
        _loadedOnce = true;
        await vm.LoadDashboardAsync();
    }
}
