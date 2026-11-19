using System.Drawing;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace GueibsOptimizer.Views;

/// <summary>Ícone opcional na bandeja com "Abrir", "Boost" e "Sair". A cor acompanha o <see cref="IconService"/>.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly IconService _icons;
    private Icon _image;

    public TrayIcon(Window window, Action boost, bool dryRun, IconService icons)
    {
        _icons = icons;
        _image = icons.TrayIcon(icons.CurrentHex);
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_, _) => Show(window));
        menu.Items.Add(dryRun ? "⚡ Boost (simulação)" : "⚡ Boost", null, (_, _) =>
        {
            Show(window);
            boost();
        });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => window.Close());

        _icon = new WinForms.NotifyIcon
        {
            Icon = _image,
            Text = dryRun ? "Gueibs Optimizer (simulação)" : "Gueibs Optimizer",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => Show(window);
        _icons.Changed += OnIconColorChanged;
    }

    private void OnIconColorChanged(string hex)
    {
        var old = _image;
        _image = _icons.TrayIcon(hex);
        _icon.Icon = _image;
        old.Dispose();
    }

    private static void Show(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    }

    public void Dispose()
    {
        _icons.Changed -= OnIconColorChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
