namespace GueibsOptimizer.Core;

public enum AppTheme
{
    Dark,
    Light,
    System
}

/// <summary>
/// Acesso às preferências (%AppData%\GueibsOptimizer\settings.json). Valida o que foi lido:
/// tema desconhecido ou HEX inválido voltam ao padrão, sem quebrar o app.
/// </summary>
public sealed class SettingsService
{
    public const string DefaultAccent = "#C52613";

    /// <summary>Padrão da versão 1.0. Quem nunca trocou a cor passa para o novo padrão.</summary>
    public const string LegacyDefaultAccent = "#22D3EE";

    private readonly string? _path;

    public SettingsService(string? path = null)
    {
        _path = path;
        Current = AppSettings.Load(path);
        Sanitize(Current);
    }

    public AppSettings Current { get; }

    public event Action? Changed;

    public void Save()
    {
        Current.Save(_path);
        Changed?.Invoke();
    }

    /// <summary>Corrige valores inválidos vindos do arquivo (editado à mão ou de versão antiga).</summary>
    public static void Sanitize(AppSettings s)
    {
        if (!Enum.IsDefined(s.Theme)) s.Theme = AppTheme.Dark;
        s.AccentColor = NormalizeAccent(s.AccentColor) ?? DefaultAccent;
        if (s.AccentColor == LegacyDefaultAccent && !s.AccentChosenByUser) s.AccentColor = DefaultAccent;
    }

    /// <summary>"#RRGGBB" em maiúsculas, ou null se o texto não for uma cor válida.</summary>
    public static string? NormalizeAccent(string? hex) =>
        ColorMath.TryParseHex(hex, out var c) ? ColorMath.ToHex(c) : null;
}
