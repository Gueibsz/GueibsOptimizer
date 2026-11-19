using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GueibsOptimizer.Core;

/// <summary>Estilos estendidos de janela (click-through do overlay).</summary>
public static class NativeWindow
{
    private const int GwlExStyle = -20;
    public const int WsExTransparent = 0x00000020;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExNoActivate = 0x08000000;
    public const int WsExLayered = 0x00080000;

    public static void SetClickThrough(Window window, bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int style = GetWindowLong(hwnd, GwlExStyle) | WsExToolWindow | WsExNoActivate | WsExLayered;
        style = clickThrough ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLong(hwnd, GwlExStyle, style);
    }

    private const int DwmUseImmersiveDarkMode = 20;

    /// <summary>Barra de título escura ou clara (Windows 10 20H1+/11). Falhas são ignoradas: é só aparência.</summary>
    public static void SetDarkTitleBar(Window window, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int value = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}

/// <summary>Atalho global via RegisterHotKey, ligado a uma janela do app.</summary>
public sealed class GlobalHotkey : IDisposable
{
    public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;

    private readonly IntPtr _hwnd;
    private readonly int _id;
    private readonly HwndSource? _source;
    private readonly Action _callback;

    public bool Registered { get; }

    public GlobalHotkey(Window window, int id, uint modifiers, uint virtualKey, Action callback)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _id = id;
        _callback = callback;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(Hook);
        Registered = RegisterHotKey(_hwnd, id, modifiers | ModNoRepeat, virtualKey);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == _id)
        {
            _callback();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterHotKey(_hwnd, _id);
        _source?.RemoveHook(Hook);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
