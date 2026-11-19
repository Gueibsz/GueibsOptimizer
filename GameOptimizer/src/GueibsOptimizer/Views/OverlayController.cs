using System.Windows;
using GueibsOptimizer.Core;
using GueibsOptimizer.ViewModels;

namespace GueibsOptimizer.Views;

public interface IOverlayController
{
    bool IsVisible { get; }
    bool HotkeyRegistered { get; }
    void Toggle();
    void SetEditMode(bool editing);
    void RefreshItems();
}

/// <summary>Cria/mostra/oculta o overlay e registra o atalho global Ctrl+Shift+F10.</summary>
public sealed class OverlayController : IOverlayController, IDisposable
{
    private const uint VkF10 = 0x79;
    private readonly OverlayViewModel _vm;
    private OverlayWindow? _window;
    private GlobalHotkey? _hotkey;

    public OverlayController(OverlayViewModel vm, Window owner)
    {
        _vm = vm;
        _hotkey = new GlobalHotkey(owner, 0x4F47, GlobalHotkey.ModControl | GlobalHotkey.ModShift, VkF10, Toggle);
    }

    public bool IsVisible => _window is { IsVisible: true };
    public bool HotkeyRegistered => _hotkey?.Registered == true;

    public event Action? VisibilityChanged;

    public void Toggle()
    {
        if (IsVisible)
        {
            _window!.Hide();
            _vm.Stop();
        }
        else
        {
            _window ??= new OverlayWindow(_vm);
            _vm.Start();
            _window.Show();
        }
        VisibilityChanged?.Invoke();
    }

    public void SetEditMode(bool editing)
    {
        if (!IsVisible && editing) Toggle();
        _window?.SetEditMode(editing);
    }

    public void RefreshItems() => _vm.ApplySettings();

    public void Dispose()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        if (IsVisible) _vm.Stop();
        _window?.Close();
    }
}
