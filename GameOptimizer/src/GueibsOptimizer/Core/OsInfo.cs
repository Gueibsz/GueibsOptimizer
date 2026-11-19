using Microsoft.Win32;

namespace GueibsOptimizer.Core;

/// <summary>Requisito mínimo de versão do Windows de um ajuste.</summary>
public sealed record OsRequirement(int MinBuild, string Text);

public static class OsInfo
{
    private const string CurrentVersionKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>Build do Windows (ex.: 19045 = Win10 22H2, 22631 = Win11 23H2, 26100 = Win11 24H2).</summary>
    public static int Build => Environment.OSVersion.Version.Build;

    public static bool IsWindows11 => Build >= 22000;

    /// <summary>Revisão (UBR) do build, ex.: 22631.4460. 0 quando não dá para ler.</summary>
    public static int Ubr => Registry.GetValue(CurrentVersionKey, "UBR", null) is int u ? u : 0;

    /// <summary>Versão de recurso, ex.: "23H2". Vazio em versões antigas.</summary>
    public static string DisplayVersion =>
        Registry.GetValue(CurrentVersionKey, "DisplayVersion", null) as string
        ?? Registry.GetValue(CurrentVersionKey, "ReleaseId", null) as string
        ?? string.Empty;

    public static string NameFor(int build) => build >= 22000 ? "Windows 11" : build >= 10240 ? "Windows 10" : "Windows";

    /// <summary>Ex.: "Windows 11 23H2 · build 22631.4460".</summary>
    public static string Describe(int build, int ubr, string displayVersion) =>
        $"{NameFor(build)}{(string.IsNullOrWhiteSpace(displayVersion) ? string.Empty : " " + displayVersion)} · build {build}{(ubr > 0 ? $".{ubr}" : string.Empty)}";

    public static string Current => Describe(Build, Ubr, DisplayVersion);

    /// <summary>
    /// Ajustes que só existem a partir de uma versão. Fonte única: os ajustes usam <see cref="Supports(string)"/> no
    /// IsApplicable e a interface mostra o texto quando o ajuste não é aplicável.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, OsRequirement> Requirements = new Dictionary<string, OsRequirement>
    {
        ["windows.widgets-off"] = new(22000, "Só existe no Windows 11"),
        ["windows.classic-context-menu"] = new(22000, "Só existe no Windows 11 (o Windows 10 já usa o menu clássico)"),
        ["privacy.copilot-off"] = new(22621, "Exige Windows 11 22H2 ou mais novo"),
        ["privacy.recall-policy"] = new(26100, "Exige Windows 11 24H2 ou mais novo"),
        ["privacy.recall-feature"] = new(26100, "Exige Windows 11 24H2 ou mais novo"),
        ["gpu.hags"] = new(19041, "Exige Windows 10 2004 ou mais novo")
    };

    public static bool Supports(string tweakId, int? build = null) =>
        !Requirements.TryGetValue(tweakId, out var r) || (build ?? Build) >= r.MinBuild;

    /// <summary>Motivo de "não aplicável" ligado à versão, ou null se o ajuste não depende da versão (ou se ela atende).</summary>
    public static string? RequirementText(string tweakId, int? build = null) =>
        Requirements.TryGetValue(tweakId, out var r) && (build ?? Build) < r.MinBuild ? r.Text : null;
}
