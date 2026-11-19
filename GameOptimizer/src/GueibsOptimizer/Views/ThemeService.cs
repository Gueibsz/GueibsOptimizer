using System.Windows;
using System.Windows.Media;
using GueibsOptimizer.Core;
using Microsoft.Win32;

namespace GueibsOptimizer.Views;

public sealed record AccentOption(string Name, string Hex);

/// <summary>
/// Aplica tema (Escuro/Claro/Seguir o Windows) e cor de destaque em tempo real: troca a paleta
/// (dicionário de índice 0) e redefine os pincéis Accent* nos recursos do app. Como todas as telas
/// usam DynamicResource, a mudança é instantânea, sem reiniciar.
/// </summary>
public sealed class ThemeService
{
    public static readonly IReadOnlyList<AccentOption> Palette =
    [
        new("Carmim", "#C52613"),
        new("Ciano", "#22D3EE"),
        new("Azul", "#3B82F6"),
        new("Índigo", "#6366F1"),
        new("Roxo", "#A855F7"),
        new("Rosa", "#EC4899"),
        new("Laranja", "#F97316"),
        new("Âmbar", "#F59E0B"),
        new("Verde", "#22C55E"),
        new("Verde-água", "#14B8A6")
    ];

    private static readonly Uri DarkUri = new("Resources/Palette.Dark.xaml", UriKind.Relative);
    private static readonly Uri LightUri = new("Resources/Palette.Light.xaml", UriKind.Relative);

    private AppTheme _mode = AppTheme.Dark;
    private string _accent = SettingsService.DefaultAccent;
    private bool _hooked;

    /// <summary>True quando o tema efetivo é escuro (usado pela barra de título das janelas).</summary>
    public static bool IsDark { get; private set; } = true;

    public AccentPalette? CurrentAccent { get; private set; }
    public Color Background { get; private set; }

    public event Action? Applied;

    public void Apply(AppTheme mode, string accentHex)
    {
        _mode = mode;
        _accent = SettingsService.NormalizeAccent(accentHex) ?? SettingsService.DefaultAccent;
        HookOnce();

        var app = Application.Current;
        if (app is null) return;

        bool dark = mode switch
        {
            AppTheme.Light => false,
            AppTheme.System => !SystemUsesLightTheme(),
            _ => true
        };
        IsDark = dark;

        var palette = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = palette; else merged.Insert(0, palette);

        Background = (palette["BgBrush"] as SolidColorBrush)?.Color ?? Colors.Black;
        ColorMath.TryParseHex(_accent, out var accent);
        var p = AccentPalette.From(accent, Background, dark);
        CurrentAccent = p;

        Set(app, "AccentBrush", p.Accent);
        Set(app, "AccentHoverBrush", p.Hover);
        Set(app, "AccentSoftBrush", p.Soft);
        Set(app, "AccentTextBrush", p.AccentText);
        Set(app, "OnAccentBrush", p.OnAccent);

        foreach (Window w in app.Windows) NativeWindow.SetDarkTitleBar(w, dark);
        Applied?.Invoke();
    }

    private static void Set(Application app, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        app.Resources[key] = brush;
    }

    /// <summary>AppsUseLightTheme = 1 → Windows em modo claro para apps.</summary>
    public static bool SystemUsesLightTheme()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 0) is int v && v == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException)
        {
            return false;
        }
    }

    private void HookOnce()
    {
        if (_hooked) return;
        _hooked = true;
        // Barra de título no tema certo para toda janela nova (Polling, Monitor, overlay...).
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => { if (s is Window w) NativeWindow.SetDarkTitleBar(w, IsDark); }));
        // "Seguir o Windows": reaplica quando o usuário troca o tema do sistema.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (_mode == AppTheme.System && e.Category == UserPreferenceCategory.General)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(_mode, _accent));
        };
    }
}
