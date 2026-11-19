using System.IO;
using System.Text.Json;
using GueibsOptimizer.Core;

namespace GueibsOptimizer.Services;

/// <summary>Estado da formatação guiada, salvo após cada passo para poder retomar se o app for fechado.</summary>
public sealed class WizardState
{
    public const int StepChoose = 1, StepRestorePoint = 2, StepInstall = 3, StepOptimize = 4, StepSummary = 5, StepExtras = 6;

    public int Step { get; set; } = StepChoose;
    public string? ProfileName { get; set; }
    public List<string> Apps { get; set; } = [];
    public List<string> InstalledApps { get; set; } = [];
    public List<string> Log { get; set; } = [];
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public bool Finished { get; set; }

    // --- 8.10: Debloat com predefinição e extras (runtimes + plano de energia) ---
    public string DebloatPreset { get; set; } = "Leve";
    public bool InstallRuntimes { get; set; } = true;
    public bool ApplyPowerPlan { get; set; } = true;
    public bool DebloatDone { get; set; }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "wizard.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static WizardState? Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return null;
            var s = JsonSerializer.Deserialize<WizardState>(File.ReadAllText(path), Options);
            if (s is null || s.Step < StepChoose || s.Step > StepExtras) return null;
            return s;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
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
        }
    }

    public static void Delete(string? path = null)
    {
        try { File.Delete(path ?? DefaultPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Apps que ainda faltam instalar (para retomar o passo 3 sem repetir).</summary>
    public IReadOnlyList<string> PendingApps => Apps.Where(a => !InstalledApps.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();

    public string StepTitle => Step switch
    {
        StepChoose => "1. Escolher perfil e programas",
        StepRestorePoint => "2. Criar ponto de restauração",
        StepInstall => "3. Instalar programas",
        StepOptimize => "4. Boost, privacidade e Debloat",
        StepExtras => "5. Extras: runtimes e plano de energia",
        _ => "6. Resumo e reinício"
    };
}
