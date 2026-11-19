using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GueibsOptimizer.Core;
using GueibsOptimizer.ViewModels;

namespace GueibsOptimizer.Views;

/// <summary>
/// Overlay sem injeção: é só uma janela transparente, sempre no topo e click-through.
/// Funciona sobre jogos em "tela cheia sem bordas"; em tela cheia exclusiva o Windows não a exibe.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly OverlayViewModel _vm;

    public OverlayWindow(OverlayViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Left = vm.Settings.OverlayLeft;
        Top = vm.Settings.OverlayTop;
        SourceInitialized += (_, _) => NativeWindow.SetClickThrough(this, clickThrough: true);
    }

    public void SetEditMode(bool editing)
    {
        _vm.IsEditing = editing;
        NativeWindow.SetClickThrough(this, !editing);
        Root.BorderBrush = editing ? (Brush)FindResource("WarningBrush") : Brushes.Transparent;
        if (!editing)
        {
            _vm.Settings.OverlayLeft = Left;
            _vm.Settings.OverlayTop = Top;
            _vm.Settings.Save();
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.IsEditing && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
