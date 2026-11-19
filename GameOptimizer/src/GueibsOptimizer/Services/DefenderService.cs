using System.IO;
using GueibsOptimizer.Core;

namespace GueibsOptimizer.Services;

public sealed record DefenderStatus(bool Available, bool ManagedByPolicyOrThirdParty, bool ExclusionPresent, string Message)
{
    /// <summary>Somente leitura (8.9): estado lido do Get-MpComputerStatus; null quando não informado.</summary>
    public bool? RealTimeProtection { get; init; }
    public DateTime? SignaturesUpdated { get; init; }
    public string SignatureVersion { get; init; } = string.Empty;
    public bool? TamperProtected { get; init; }
    public string Mode { get; init; } = string.Empty;
}

/// <summary>
/// Única mudança permitida no Defender: adicionar/remover a PASTA DO PRÓPRIO APP como exclusão.
/// Nunca desliga proteção, nunca exclui a raiz de um disco nem pastas do sistema.
/// </summary>
public sealed class DefenderService(TweakContext ctx) : IUndoProvider
{
    public const string UndoId = "defender.app-exclusion";

    public static string AppFolder => Path.GetFullPath(AppPaths.AppDirectory).TrimEnd('\\');

    /// <summary>Só aceita uma pasta específica: nada de raiz de disco, Windows, Arquivos de Programas ou perfil inteiro.</summary>
    public static bool IsSafeExclusion(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        // "D:" ou "pasta\sub" seriam resolvidos a partir da pasta atual (e "D:" viraria a pasta atual
        // do drive D, não a raiz). Só caminhos absolutos completos são aceitos.
        if (!Path.IsPathFullyQualified(path)) return false;
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }

        var root = Path.GetPathRoot(full)?.TrimEnd('\\');
        if (string.IsNullOrEmpty(root) || full.Equals(root, StringComparison.OrdinalIgnoreCase)) return false;
        if (full.Contains('*') || full.Contains('?')) return false;

        string[] forbidden =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Path.GetTempPath()
        ];
        return !forbidden.Any(f => !string.IsNullOrEmpty(f) && full.Equals(f.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    public async Task<DefenderStatus> GetStatusAsync()
    {
        string script = $$"""
            $s = Get-MpComputerStatus -ErrorAction SilentlyContinue
            if (-not $s) { 'NODEFENDER'; exit 0 }
            $p = Get-MpPreference -ErrorAction SilentlyContinue
            $policy = Test-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender\Exclusions'
            $excl = @($p.ExclusionPath) -contains {{Shell.PsQuote(AppFolder)}}
            "MODE=$($s.AMRunningMode)"
            "AV=$($s.AntivirusEnabled)"
            "POLICY=$policy"
            "RTP=$($s.RealTimeProtectionEnabled)"
            "SIGDATE=$(if ($s.AntivirusSignatureLastUpdated) { $s.AntivirusSignatureLastUpdated.ToString('yyyy-MM-dd HH:mm') })"
            "SIGVER=$($s.AntivirusSignatureVersion)"
            "TAMPER=$($s.IsTamperProtected)"
            "EXCL=$excl"
            """;
        var r = await ctx.Shell.RunPowerShellAsync(script, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return ParseStatus(r.Success ? r.StdOut : "NODEFENDER");
    }

    public static DefenderStatus ParseStatus(string output)
    {
        if (output.Contains("NODEFENDER", StringComparison.Ordinal))
            return new DefenderStatus(false, true, false, "O Microsoft Defender não está disponível (outro antivírus pode estar ativo). Configure a exclusão no seu antivírus, se quiser.");

        var values = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);

        string mode = values.GetValueOrDefault("MODE") ?? string.Empty;
        bool av = values.GetValueOrDefault("AV")?.Equals("True", StringComparison.OrdinalIgnoreCase) == true;
        bool policy = values.GetValueOrDefault("POLICY")?.Equals("True", StringComparison.OrdinalIgnoreCase) == true;
        bool excl = values.GetValueOrDefault("EXCL")?.Equals("True", StringComparison.OrdinalIgnoreCase) == true;

        bool managed = policy || !av || mode.Contains("Passive", StringComparison.OrdinalIgnoreCase) || mode.Contains("EDR", StringComparison.OrdinalIgnoreCase);
        string message = managed
            ? "O Defender está em modo passivo, desativado por outro antivírus ou gerenciado por política. A exclusão pode não ter efeito; configure no antivírus em uso."
            : excl ? "A pasta do Gueibs Optimizer já é uma exclusão do Defender." : "A pasta do Gueibs Optimizer não está nas exclusões do Defender.";
        static bool? Flag(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Equals("True", StringComparison.OrdinalIgnoreCase);
        DateTime? sig = DateTime.TryParseExact(values.GetValueOrDefault("SIGDATE"), "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;
        return new DefenderStatus(true, managed, excl, message)
        {
            RealTimeProtection = Flag(values.GetValueOrDefault("RTP")),
            SignaturesUpdated = sig,
            SignatureVersion = values.GetValueOrDefault("SIGVER") ?? string.Empty,
            TamperProtected = Flag(values.GetValueOrDefault("TAMPER")),
            Mode = mode
        };
    }

    public async Task<string?> AddExclusionAsync()
    {
        var folder = AppFolder;
        if (!IsSafeExclusion(folder)) return "A pasta do app não é segura para exclusão (raiz de disco ou pasta do sistema). Mova o app para uma pasta própria.";

        var r = await ctx.Shell.RunPowerShellMutatingAsync("Adicionar exclusão do Defender",
            $"Add-MpPreference -ExclusionPath {Shell.PsQuote(folder)} -ErrorAction Stop", TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (!r.Success) return $"O Defender recusou a exclusão: {r.StdErr.Trim()}";
        if (r.Simulated) return null;
        ctx.SaveCustomBackup(UndoId, "exclusion-path", folder, $"Exclusão do Defender adicionada: {folder}");
        ctx.Logger.Info($"Exclusão do Defender adicionada para {folder}");
        return null;
    }

    public async Task<string?> RemoveExclusionAsync()
    {
        var folder = ctx.Backup.GetCustom(UndoId, "exclusion-path") ?? AppFolder;
        if (!IsSafeExclusion(folder)) return "Caminho de exclusão inválido.";
        var r = await ctx.Shell.RunPowerShellMutatingAsync("Remover exclusão do Defender",
            $"Remove-MpPreference -ExclusionPath {Shell.PsQuote(folder)} -ErrorAction Stop", TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (!r.Success) return $"Não foi possível remover a exclusão: {r.StdErr.Trim()}";
        if (r.Simulated) return null;
        ctx.Backup.RemoveTweak(UndoId);
        ctx.Logger.Info($"Exclusão do Defender removida para {folder}");
        return null;
    }

    public bool CanUndo(string id) => id == UndoId;
    public string DisplayName(string id) => "Exclusão do Defender (pasta do app)";

    public async Task UndoAsync(string id)
    {
        var error = await RemoveExclusionAsync().ConfigureAwait(false);
        if (error is not null) throw new InvalidOperationException(error);
    }
}
