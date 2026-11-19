using System.IO;
using System.Text.Json;

namespace GueibsOptimizer.Core;

/// <summary>Preferências do usuário em %AppData%\GueibsOptimizer\settings.json.</summary>
public sealed class AppSettings
{
    public bool GameBoosterEnabled { get; set; }

    /// <summary>Nome da saudação do Painel ("Olá, {nome}!"). Vazio = nome do usuário do Windows.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Idioma da interface: pt-BR (padrão), en ou es.</summary>
    public string Language { get; set; } = LocalizationService.Portuguese;

    /// <summary>Data da última revisão de integridade (CHKDSK/DISM/SFC) concluída. O Painel avisa após 7 dias.</summary>
    public DateTime? LastIntegrityReview { get; set; }

    /// <summary>Criar ponto de restauração antes do Boost, níveis, perfis, Debloat, assistente e correções (padrão: sim).</summary>
    public bool CreateRestorePoints { get; set; } = true;

    /// <summary>Menu lateral recolhido (só ícones).</summary>
    public bool NavCollapsed { get; set; }

    // --- Aparência ---
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>Cor de destaque em "#RRGGBB" (validada ao carregar; inválida volta ao padrão).</summary>
    public string AccentColor { get; set; } = SettingsService.DefaultAccent;

    /// <summary>True depois que o usuário escolhe uma cor (não migra a escolha dele para um novo padrão).</summary>
    public bool AccentChosenByUser { get; set; }

    /// <summary>A oferta de exclusão do Defender é feita uma única vez, no primeiro uso.</summary>
    public bool DefenderOffered { get; set; }

    // --- Overlay e FPS ---
    public string? PresentMonPath { get; set; }
    public double OverlayLeft { get; set; } = 20;
    public double OverlayTop { get; set; } = 20;
    public bool OverlayShowFps { get; set; } = true;
    public bool OverlayShowLows { get; set; } = true;
    public bool OverlayShowCpu { get; set; } = true;
    public bool OverlayShowGpu { get; set; } = true;
    public bool OverlayShowTemps { get; set; } = true;

    // --- Rede ---
    public string PingTargets { get; set; } = "1.1.1.1, 8.8.8.8";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "settings.json");

    public static AppSettings Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // preferências não são críticas
        }
    }
}
