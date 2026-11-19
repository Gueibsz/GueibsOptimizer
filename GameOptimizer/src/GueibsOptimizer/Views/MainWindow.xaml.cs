using System.Windows;
using GueibsOptimizer.Core;
using GueibsOptimizer.ViewModels;

namespace GueibsOptimizer.Views;

public partial class MainWindow : Window
{
    private bool _justChecked;

    public MainWindow()
    {
        InitializeComponent();
        // Barra de título acompanha o tema (o ThemeService atualiza de novo quando o tema muda).
        SourceInitialized += (_, _) => NativeWindow.SetDarkTitleBar(this, ThemeService.IsDark);
    }

    // Responde a clique, teclado e automação de UI (Command só dispararia no clique).
    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { DataContext: NavItem item } && DataContext is MainViewModel vm)
        {
            _justChecked = true;
            vm.SelectNavCommand.Execute(item);
        }
    }

    // Itens que abrem uma janela (ex.: teste do mouse) podem ser clicados de novo mesmo já selecionados.
    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (!_justChecked && sender is System.Windows.Controls.RadioButton { DataContext: NavItem { OnOpen: { } open } }) open();
        _justChecked = false;
    }
}
