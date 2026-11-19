using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services;

/// <summary>Predefinições do assistente pós-formatação (8.10). Só IDs que existem no catálogo validado.</summary>
public static class WizardPresets
{
    public const string NoDebloat = "Nenhum";

    /// <summary>Seleções prontas de programas para o passo 1.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> AppPresets = new Dictionary<string, string[]>
    {
        ["Essenciais para jogos"] = CatalogService.GamingEssentials,
        ["Jogos e comunicação"] = ["Valve.Steam", "EpicGames.EpicGamesLauncher", "Discord.Discord", "Google.Chrome", "7zip.7zip", "VideoLAN.VLC", "OBSProject.OBSStudio"],
        ["Mínimo"] = ["Mozilla.Firefox", "7zip.7zip", "VideoLAN.VLC"]
    };

    /// <summary>Runtimes que a maioria dos jogos exige (passo de extras): VC++ 2015-2022, .NET Desktop 8 e DirectX legado.</summary>
    public static readonly string[] Runtimes =
    [
        "Microsoft.VCRedist.2015+.x64", "Microsoft.VCRedist.2015+.x86", "Microsoft.DotNet.DesktopRuntime.8", "Microsoft.DirectX"
    ];

    /// <summary>Predefinições de Debloat oferecidas no assistente. "Agressivo" fica só na tela de Debloat, com revisão item a item.</summary>
    public static readonly string[] DebloatChoices = [NoDebloat, nameof(DebloatPreset.Leve), nameof(DebloatPreset.Equilibrado)];

    public static DebloatPreset? ParseDebloat(string? choice) =>
        choice == nameof(DebloatPreset.Leve) ? DebloatPreset.Leve
        : choice == nameof(DebloatPreset.Equilibrado) ? DebloatPreset.Equilibrado
        : null;

    /// <summary>Ordem das etapas executáveis (o valor 6 = extras foi acrescentado depois e roda antes do resumo).</summary>
    public static readonly int[] StepOrder =
    [
        WizardState.StepRestorePoint, WizardState.StepInstall, WizardState.StepOptimize, WizardState.StepExtras, WizardState.StepSummary
    ];

    public static int Next(int step)
    {
        int i = Array.IndexOf(StepOrder, step);
        return i < 0 || i == StepOrder.Length - 1 ? WizardState.StepSummary : StepOrder[i + 1];
    }
}
