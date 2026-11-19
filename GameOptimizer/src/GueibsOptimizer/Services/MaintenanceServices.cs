using System.Diagnostics;
using System.IO;
using System.Text;
using GueibsOptimizer.Core;

namespace GueibsOptimizer.Services;

/// <summary>Abre um console visível com DISM /RestoreHealth seguido de sfc /scannow.</summary>
public sealed class RepairService(Logger logger, bool dryRun)
{
    public const string Script =
        "title Reparo do Windows - Gueibs Optimizer && " +
        "echo Etapa 1 de 2: DISM verifica e repara a imagem do Windows (pode levar de 5 a 20 minutos). && " +
        "echo Nao feche esta janela. && echo. && " +
        "DISM /Online /Cleanup-Image /RestoreHealth && " +
        "echo. && echo Etapa 2 de 2: SFC verifica os arquivos de sistema. && echo. && " +
        "sfc /scannow && " +
        "echo. && echo Concluido. Se algo foi reparado, reinicie o computador. Pode fechar esta janela.";

    public bool Start()
    {
        if (dryRun)
        {
            logger.Simulated("Abrir console de reparo: DISM /Online /Cleanup-Image /RestoreHealth e sfc /scannow");
            return true;
        }
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = true };
            psi.ArgumentList.Add("/k");
            psi.ArgumentList.Add(Script);
            Process.Start(psi)?.Dispose();
            logger.Info("Console de reparo (DISM + SFC) aberto.");
            return true;
        }
        catch (Exception ex)
        {
            logger.Error("Não foi possível abrir o console de reparo", ex);
            return false;
        }
    }
}

/// <summary>Tarefa agendada semanal que roda "GueibsOptimizer.exe --weekly".</summary>
public sealed class WeeklyReviewService(Shell shell, Logger logger)
{
    public const string TaskName = @"GueibsOptimizer\RevisaoSemanal";

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppPaths.AppDirectory, "GueibsOptimizer.exe");

    public static IReadOnlyList<string> CreateArguments(string exePath) =>
    [
        "/Create", "/TN", TaskName,
        "/TR", $"\"{exePath}\" --weekly",
        "/SC", "WEEKLY", "/D", "SUN", "/ST", "20:00",
        "/RL", "HIGHEST", "/F"
    ];

    public async Task<bool> IsEnabledAsync()
    {
        var r = await shell.RunAsync("schtasks.exe", ["/Query", "/TN", TaskName]).ConfigureAwait(false);
        return r.Success;
    }

    public async Task<bool> EnableAsync()
    {
        var r = await shell.RunMutatingAsync("Criar revisão semanal", "schtasks.exe", CreateArguments(ExecutablePath)).ConfigureAwait(false);
        if (!r.Success) logger.Error($"Falha ao criar a revisão semanal: {r.StdErr.Trim()}");
        else if (!r.Simulated) logger.Info("Revisão semanal ativada (domingo, 20:00).");
        return r.Success;
    }

    public async Task<bool> DisableAsync()
    {
        // Sem tarefa, não há o que apagar (evita erro do schtasks na desinstalação).
        if (!await IsEnabledAsync().ConfigureAwait(false)) return true;
        var r = await shell.RunMutatingAsync("Remover revisão semanal", "schtasks.exe", ["/Delete", "/TN", TaskName, "/F"]).ConfigureAwait(false);
        if (r.Success && !r.Simulated) logger.Info("Revisão semanal desativada.");
        return r.Success;
    }

    /// <summary>Execução silenciosa (--weekly): limpeza segura + RAM + relatório curto.</summary>
    public static async Task<string> RunReviewAsync(CleanupService cleanup, MemoryCleanerService memory, Logger logger)
    {
        var safe = CleanupService.Categories()
            .Where(c => c.Kind is CleanupKind.UserTemp or CleanupKind.WindowsTemp or CleanupKind.ShaderCache)
            .ToList();
        var clean = await cleanup.CleanAsync(safe).ConfigureAwait(false);
        var ram = await memory.CleanAsync().ConfigureAwait(false);

        var sb = new StringBuilder();
        // Em simulação nada é apagado: o texto diz o que "seria" feito.
        sb.Append(clean.Simulated
            ? $"Revisão semanal (simulação): {MemoryCleanResult.FormatBytes((ulong)clean.FreedBytes)} de arquivos temporários seriam removidos"
            : $"Revisão semanal: {MemoryCleanResult.FormatBytes((ulong)clean.FreedBytes)} de arquivos temporários removidos");
        sb.Append($" e {MemoryCleanResult.FormatBytes(ram.Freed)} de RAM liberados.");
        var report = sb.ToString();
        logger.Info(report);
        return report;
    }
}
