using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GueibsOptimizer.Core;

namespace GueibsOptimizer.Views;

/// <summary>
/// Cor do ícone em tempo de execução (sempre a cor de destaque): janelas (Window.Icon), bandeja e overlay.
/// Cores da paleta usam o .ico pronto (Assets/Icons/icon-&lt;cor&gt;.ico); HEX personalizado é desenhado na hora.
/// O ícone do .exe no Explorer é definido na compilação (Resources/app.ico) e não muda aqui.
/// </summary>
public sealed class IconService
{

    /// <summary>Nome do arquivo de cada cor da paleta (sem acento, igual ao tools/make-icon.ps1).</summary>
    public static readonly IReadOnlyDictionary<string, string> AssetSlugs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["#C52613"] = "carmim", ["#22D3EE"] = "ciano", ["#3B82F6"] = "azul", ["#6366F1"] = "indigo", ["#A855F7"] = "roxo", ["#EC4899"] = "rosa",
        ["#F97316"] = "laranja", ["#F59E0B"] = "ambar", ["#22C55E"] = "verde", ["#14B8A6"] = "verde-agua"
    };

    private ImageSource? _current;
    private bool _hooked;

    public string CurrentHex { get; private set; } = SettingsService.DefaultAccent;

    /// <summary>Disparado quando a cor muda (a bandeja usa para trocar o NotifyIcon).</summary>
    public event Action<string>? Changed;

    public static string? AssetSlugFor(string hex) =>
        SettingsService.NormalizeAccent(hex) is { } n && AssetSlugs.TryGetValue(n, out var slug) ? slug : null;


    public static Uri AssetUri(string slug) => new($"pack://application:,,,/Assets/Icons/icon-{slug}.ico", UriKind.Absolute);

    public ImageSource WindowIcon(string hex)
    {
        if (AssetSlugFor(hex) is { } slug)
        {
            try
            {
                var frame = BitmapFrame.Create(AssetUri(slug));
                frame.Freeze();
                return frame;
            }
            catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or System.IO.FileFormatException)
            {
                // recurso ausente: cai no desenho em tempo de execução
            }
        }
        ColorMath.TryParseHex(hex, out var c);
        return IconRenderer.CreateImageSource(c);
    }

    public System.Drawing.Icon TrayIcon(string hex)
    {
        ColorMath.TryParseHex(hex, out var c);
        return IconRenderer.CreateIcon(c, 32);
    }

    public void Apply(string hex)
    {
        CurrentHex = SettingsService.NormalizeAccent(hex) ?? SettingsService.DefaultAccent;
        _current = WindowIcon(CurrentHex);
        HookOnce();
        if (Application.Current is { } app)
            foreach (Window w in app.Windows) w.Icon = _current;
        Changed?.Invoke(CurrentHex);
    }

    private void HookOnce()
    {
        if (_hooked) return;
        _hooked = true;
        // Janelas abertas depois (Polling, Monitor, overlay) já nascem com o ícone na cor atual.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => { if (s is Window w && _current is not null) w.Icon = _current; }));
    }
}
