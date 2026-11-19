using System.Diagnostics;
using System.IO;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services;

public sealed record FixStepResult(string Display, bool Success, string Output);

/// <summary>Uma correção avulsa: comandos exatos (fixos), risco e se pede reinício.</summary>
public sealed record FixAction(string Key, string Title, string Description, TweakRisk Risk, bool NeedsReboot, IReadOnlyList<string> Commands);

/// <summary>
/// 9.6: correções com comando visível antes de executar, cancelamento entre etapas e resumo da saída.
/// O reset do Windows Update renomeia as pastas (nunca apaga) e sempre religa os serviços (try/finally).
/// </summary>
public sealed class FixesService(TweakContext ctx)
{
    public static readonly string[] UpdateServices = ["wuauserv", "bits", "cryptsvc", "msiserver"];

    public static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public static string ExplorerCacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Explorer");

    public static readonly IReadOnlyList<(string File, string[] Args)> NetworkReset =
    [
        ("netsh.exe", ["winsock", "reset"]),
        ("netsh.exe", ["int", "ip", "reset"]),
        ("ipconfig.exe", ["/flushdns"]),
        ("ipconfig.exe", ["/release"]),
        ("ipconfig.exe", ["/renew"])
    ];

    public static string Display(string file, IEnumerable<string> args) => $"{Path.GetFileNameWithoutExtension(file)} {string.Join(" ", args)}";

    /// <summary>Nome do backup renomeado, com data, ex.: SoftwareDistribution.bak-20261119-0130.</summary>
    public static string BackupName(string folder, DateTime when) => $"{folder}.bak-{when:yyyyMMdd-HHmm}";

    public static IReadOnlyList<FixAction> Actions { get; } =
    [
        new("network", "Resetar a rede", "Reinicia a pilha de rede do Windows (Winsock e TCP/IP), limpa o cache de DNS e renova o endereço IP. Use quando a internet some, cai sozinha ou só alguns sites não abrem.",
            TweakRisk.Medium, true, NetworkReset.Select(c => Display(c.File, c.Args)).ToList()),
        new("windows-update", "Resetar componentes do Windows Update", "Para os serviços do Windows Update, renomeia SoftwareDistribution e catroot2 (cópias de segurança, nada é apagado) e liga os serviços de novo. Use quando as atualizações travam ou dão erro sempre.",
            TweakRisk.Medium, true,
            [.. UpdateServices.Select(s => $"net stop {s}"), @"ren %WINDIR%\SoftwareDistribution SoftwareDistribution.bak-YYYYMMDD-HHMM", @"ren %WINDIR%\System32\catroot2 catroot2.bak-YYYYMMDD-HHMM", .. UpdateServices.Select(s => $"net start {s}")]),
        new("store-cache", "Limpar o cache da Microsoft Store", "Executa a ferramenta oficial wsreset, que limpa o cache da Store e a abre em seguida. Use quando downloads ou atualizações da Store não andam.",
            TweakRisk.Low, false, ["wsreset"]),
        new("icon-cache", "Reconstruir cache de ícones e miniaturas", "Fecha o Explorer, apaga os arquivos de cache iconcache*.db e thumbcache*.db do seu perfil (o Windows os recria) e abre o Explorer de novo. Use quando ícones aparecem errados ou em branco.",
            TweakRisk.Low, false, ["taskkill /f /im explorer.exe", @"del %LOCALAPPDATA%\Microsoft\Windows\Explorer\iconcache*.db", @"del %LOCALAPPDATA%\Microsoft\Windows\Explorer\thumbcache*.db", "explorer"])
    ];

    public async Task<IReadOnlyList<FixStepResult>> RunAsync(FixAction action, IProgress<string>? progress, CancellationToken ct) => action.Key switch
    {
        "network" => await RunCommandsAsync(NetworkReset, progress, ct).ConfigureAwait(false),
        "windows-update" => await ResetWindowsUpdateAsync(progress, ct).ConfigureAwait(false),
        "store-cache" => await RunCommandsAsync([("wsreset.exe", [])], progress, ct).ConfigureAwait(false),
        "icon-cache" => await RebuildIconCacheAsync(progress, ct).ConfigureAwait(false),
        _ => [new FixStepResult(action.Key, false, "Correção desconhecida.")]
    };

    private async Task<List<FixStepResult>> RunCommandsAsync(IEnumerable<(string File, string[] Args)> commands, IProgress<string>? progress, CancellationToken ct)
    {
        var results = new List<FixStepResult>();
        foreach (var (file, args) in commands)
        {
            ct.ThrowIfCancellationRequested();
            string display = Display(file, args);
            progress?.Report($"> {display}");
            var r = await ctx.Shell.RunMutatingAsync(display, file, args, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
            var output = r.Simulated ? "[simulação]" : Summarize(r.StdOut + "\n" + r.StdErr);
            if (output.Length > 0) progress?.Report(output);
            results.Add(new FixStepResult(display, r.Success, output));
        }
        return results;
    }

    /// <summary>Últimas linhas não vazias da saída (o resumo mostrado na tela).</summary>
    public static string Summarize(string output, int lines = 4) =>
        string.Join(Environment.NewLine, output.Replace("\r", string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(lines));

    private async Task<List<FixStepResult>> ResetWindowsUpdateAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var results = new List<FixStepResult>();
        try
        {
            results.AddRange(await RunCommandsAsync(UpdateServices.Select(s => ("net.exe", new[] { "stop", s })), progress, ct).ConfigureAwait(false));
            foreach (var folder in new[] { Path.Combine(WindowsDir, "SoftwareDistribution"), Path.Combine(WindowsDir, "System32", "catroot2") })
            {
                ct.ThrowIfCancellationRequested();
                string target = Path.Combine(Path.GetDirectoryName(folder)!, BackupName(Path.GetFileName(folder), DateTime.Now));
                string display = $"ren {folder} {Path.GetFileName(target)}";
                progress?.Report($"> {display}");
                if (ctx.DryRun)
                {
                    ctx.Logger.Simulated(display);
                    results.Add(new FixStepResult(display, true, "[simulação]"));
                    continue;
                }
                try
                {
                    if (Directory.Exists(folder)) Directory.Move(folder, target);
                    ctx.Logger.Info($"Windows Update: {folder} renomeada para {target}.");
                    results.Add(new FixStepResult(display, true, "renomeada"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    results.Add(new FixStepResult(display, false, ex.Message));
                    progress?.Report(ex.Message);
                }
            }
        }
        finally
        {
            // Sempre religa os serviços, mesmo após cancelamento ou erro.
            results.AddRange(await RunCommandsAsync(UpdateServices.Reverse().Select(s => ("net.exe", new[] { "start", s })), progress, CancellationToken.None).ConfigureAwait(false));
        }
        return results;
    }

    private async Task<List<FixStepResult>> RebuildIconCacheAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var results = new List<FixStepResult>();
        try
        {
            results.AddRange(await RunCommandsAsync([("taskkill.exe", ["/f", "/im", "explorer.exe"])], progress, ct).ConfigureAwait(false));
            await Task.Delay(1000, ct).ConfigureAwait(false);
            int deleted = 0, skipped = 0;
            foreach (var pattern in new[] { "iconcache*.db", "thumbcache*.db" })
            {
                if (!Directory.Exists(ExplorerCacheDir)) break;
                foreach (var file in Directory.EnumerateFiles(ExplorerCacheDir, pattern))
                {
                    if (ctx.DryRun) { ctx.Logger.Simulated($"Apagar {file}"); deleted++; continue; }
                    try { File.Delete(file); deleted++; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
                }
            }
            string summary = $"{deleted} arquivo(s) de cache apagados, {skipped} em uso ignorados.";
            progress?.Report(summary);
            results.Add(new FixStepResult("del iconcache*.db / thumbcache*.db", true, summary));
        }
        finally
        {
            if (!ctx.DryRun && Process.GetProcessesByName("explorer").Length == 0)
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose(); }
                catch (Exception ex) { ctx.Logger.Error("Não foi possível reabrir o Explorer", ex); }
            }
            results.Add(new FixStepResult("explorer", true, ctx.DryRun ? "[simulação]" : "Explorer aberto."));
        }
        return results;
    }

    /// <summary>Resumo final: quantas etapas deram certo e se precisa reiniciar.</summary>
    public static string FinalSummary(FixAction action, IReadOnlyList<FixStepResult> results, bool cancelled)
    {
        int ok = results.Count(r => r.Success);
        string text = cancelled ? $"Cancelado: {ok} de {results.Count} etapa(s) concluídas." : $"{ok} de {results.Count} etapa(s) concluídas.";
        if (!cancelled && action.NeedsReboot && ok > 0) text += " Reinicie o PC para concluir.";
        return text;
    }
    /// <summary>Seção do --report: o que cada correção executaria (nada é executado).</summary>
    public static Task WriteReportAsync(System.IO.StringWriter w)
    {
        w.WriteLine();
        w.WriteLine("== Correções (executariam, sob confirmação) ==");
        foreach (var a in Actions)
        {
            w.WriteLine($"{a.Title} (risco {TweakText.RiskText(a.Risk)}{(a.NeedsReboot ? ", pede reinício" : string.Empty)}):");
            foreach (var c in a.Commands) w.WriteLine($"  - {c}");
        }
        return Task.CompletedTask;
    }
}