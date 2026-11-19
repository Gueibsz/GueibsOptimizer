using System.Management;
using GueibsOptimizer.Core;

namespace GueibsOptimizer.Services;

public enum FeatureState
{
    Enabled,
    Disabled,
    /// <summary>Não existe nesta edição/versão (ex.: Hyper-V e Sandbox no Windows Home).</summary>
    Unavailable,
    Unknown
}

/// <summary>Recurso opcional conhecido (lista fechada; o nome vem daqui, nunca do usuário).</summary>
public sealed record FeatureDefinition(string FeatureName, string Title, string Description, string Warning);

public sealed record FeatureResult(bool Success, bool NeedsReboot, bool Simulated, string Message);

/// <summary>9.5: estado real via Win32_OptionalFeature e ativar/desativar via DISM, com desfazer (IUndoProvider).</summary>
public sealed class WindowsFeatureService(TweakContext ctx) : IUndoProvider
{
    public const string UndoPrefix = "feature:";

    public static readonly IReadOnlyList<FeatureDefinition> Known =
    [
        new("NetFx3", ".NET Framework 3.5 (inclui 2.0 e 3.0)", "Necessário para jogos e programas antigos que pedem o .NET 3.5.", string.Empty),
        new("Microsoft-Hyper-V-All", "Hyper-V", "Máquinas virtuais da Microsoft (Windows Pro/Enterprise).",
            "Com o Hyper-V ativo o Windows roda sobre um hipervisor: pode reduzir um pouco o desempenho em jogos e alguns anti-cheats não funcionam. Ative só se precisar de máquinas virtuais."),
        new("VirtualMachinePlatform", "Plataforma de Máquina Virtual", "Base usada pelo WSL 2 e pelo Android/outros apps virtualizados.",
            "Ativa o hipervisor do Windows: pode reduzir um pouco o desempenho em jogos e conflitar com alguns anti-cheats."),
        new("Microsoft-Windows-Subsystem-Linux", "Subsistema do Windows para Linux (WSL)", "Roda ferramentas Linux no Windows.", string.Empty),
        new("Containers-DisposableClientVM", "Área Restrita do Windows (Sandbox)", "Ambiente descartável para testar programas com segurança (Windows Pro/Enterprise).",
            "Usa o hipervisor do Windows: pode reduzir um pouco o desempenho em jogos e conflitar com alguns anti-cheats."),
        new("Recall", "Recall", "Recurso de IA que guarda capturas da tela (Windows 11 24H2 em PCs Copilot+).", "Veja também o ajuste \"Bloquear capturas do Recall\"."),
        new("WindowsMediaPlayer", "Windows Media Player legado", "Player antigo do Windows (o novo Media Player não é afetado).", string.Empty),
        new("TelnetClient", "Cliente Telnet", "Ferramenta de linha de comando para testar conexões de rede.", string.Empty)
    ];

    public static FeatureState MapInstallState(int? installState) => installState switch
    {
        1 => FeatureState.Enabled,
        2 => FeatureState.Disabled,
        3 => FeatureState.Unavailable,
        null => FeatureState.Unavailable,
        _ => FeatureState.Unknown
    };

    public static string StateText(FeatureState s) => s switch
    {
        FeatureState.Enabled => "Ativado",
        FeatureState.Disabled => "Desativado",
        FeatureState.Unavailable => "Indisponível nesta edição do Windows",
        _ => "Estado desconhecido"
    };

    /// <summary>Comando exato (argumentos fixos; o nome vem da lista fechada).</summary>
    public static IReadOnlyList<string> DismArguments(string featureName, bool enable) => enable
        ? ["/Online", "/Enable-Feature", $"/FeatureName:{featureName}", "/All", "/NoRestart"]
        : ["/Online", "/Disable-Feature", $"/FeatureName:{featureName}", "/NoRestart"];

    public static string CommandText(string featureName, bool enable) => "DISM " + string.Join(" ", DismArguments(featureName, enable));

    /// <summary>DISM: 0 = OK, 3010 = OK mas pede reinício.</summary>
    public static FeatureResult Interpret(int exitCode, bool simulated, bool enable) => (exitCode, simulated) switch
    {
        (_, true) => new(true, false, true, "Simulação: nada foi alterado."),
        (0, _) => new(true, false, false, enable ? "Recurso ativado." : "Recurso desativado."),
        (3010, _) => new(true, true, false, (enable ? "Recurso ativado." : "Recurso desativado.") + " Reinicie o PC para concluir."),
        _ => new(false, false, false, $"O DISM falhou (código {exitCode}). Veja o log em Atividade.")
    };

    public Task<IReadOnlyDictionary<string, FeatureState>> ReadStatesAsync() => Task.Run<IReadOnlyDictionary<string, FeatureState>>(ReadStates);

    public IReadOnlyDictionary<string, FeatureState> ReadStates()
    {
        var result = Known.ToDictionary(k => k.FeatureName, _ => FeatureState.Unavailable, StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, InstallState FROM Win32_OptionalFeature");
            foreach (ManagementObject mo in s.Get())
                using (mo)
                    if (mo["Name"]?.ToString() is { } name && result.ContainsKey(name))
                        result[name] = MapInstallState(mo["InstallState"] is null ? null : Convert.ToInt32(mo["InstallState"]));
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            ctx.Logger.Warn($"Recursos do Windows: não foi possível ler o estado: {ex.Message}");
            foreach (var k in result.Keys.ToList()) result[k] = FeatureState.Unknown;
        }
        return result;
    }

    public async Task<FeatureResult> SetAsync(FeatureDefinition feature, bool enable, bool recordUndo = true)
    {
        if (!Known.Contains(feature)) return new(false, false, false, "Recurso desconhecido.");
        var r = await ctx.Shell.RunMutatingAsync($"{(enable ? "Ativar" : "Desativar")} recurso {feature.FeatureName}", "DISM.exe",
            DismArguments(feature.FeatureName, enable), TimeSpan.FromMinutes(30)).ConfigureAwait(false);
        var result = Interpret(r.TimedOut ? -1 : r.ExitCode, r.Simulated, enable);
        if (result.Success && !result.Simulated && recordUndo && ctx.Backup.ForTweak(UndoPrefix + feature.FeatureName).Count == 0)
            ctx.SaveCustomBackup(UndoPrefix + feature.FeatureName, "previous-enabled", (!enable).ToString(), $"Recurso {feature.Title} {(enable ? "ativado" : "desativado")}");
        ctx.Logger.Info($"{CommandText(feature.FeatureName, enable)}: {result.Message}");
        return result;
    }

    public bool CanUndo(string id) => id.StartsWith(UndoPrefix, StringComparison.Ordinal);

    public string DisplayName(string id) =>
        Known.FirstOrDefault(k => UndoPrefix + k.FeatureName == id)?.Title is { } t ? $"Recurso do Windows: {t}" : id;

    public async Task UndoAsync(string id)
    {
        var feature = Known.FirstOrDefault(k => UndoPrefix + k.FeatureName == id);
        var previous = ctx.Backup.ForTweak(id).FirstOrDefault(b => b.CustomKey == "previous-enabled")?.CustomValue;
        if (feature is null || previous is null) return;
        var r = await SetAsync(feature, enable: previous == bool.TrueString, recordUndo: false).ConfigureAwait(false);
        if (r.Success && !ctx.DryRun) ctx.Backup.RemoveTweak(id);
    }
}
