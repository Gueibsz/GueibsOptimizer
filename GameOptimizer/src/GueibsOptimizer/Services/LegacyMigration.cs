using System.IO;
using System.Text.Json;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services.Tweaks;

namespace GueibsOptimizer.Services;

public enum DataMigrationOutcome { NothingToDo, Moved, Copied, BothExist, Failed }

/// <summary>
/// Até a versão 2.0 o app se chamava GameOptimizer. Esta classe leva o que a versão antiga deixou para os nomes novos:
/// a pasta de dados (backup dos valores originais, configurações, perfis, logs), as tarefas agendadas e o nome do plano
/// de energia. Só roda fora do modo simulação; nada é apagado antes de existir a versão nova.
/// </summary>
public static class LegacyMigration
{
    public const string LegacyFolderName = "GameOptimizer";
    public const string LegacyAutoStartTask = AutoStartService.LegacyTaskName;
    public const string LegacyWeeklyFolder = @"\GameOptimizer";
    public const string LegacyWeeklyTask = "RevisaoSemanal";

    public static string LegacyDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyFolderName);

    /// <summary>
    /// Move a pasta antiga para a nova. Se a nova já existir, não mexe em nada (não sobrescreve). Se algum arquivo estiver
    /// em uso (versão antiga aberta), copia o que der e mantém a pasta antiga.
    /// </summary>
    public static DataMigrationOutcome MigrateDataFolder(string oldDir, string newDir)
    {
        try
        {
            if (!Directory.Exists(oldDir)) return DataMigrationOutcome.NothingToDo;
            if (Directory.Exists(newDir)) return DataMigrationOutcome.BothExist;

            DataMigrationOutcome outcome;
            try
            {
                Directory.Move(oldDir, newDir);
                outcome = DataMigrationOutcome.Moved;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CopyTree(oldDir, newDir);
                outcome = DataMigrationOutcome.Copied;
            }
            FixSettingsPaths(Path.Combine(newDir, "settings.json"), oldDir, newDir);
            return outcome;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DataMigrationOutcome.Failed;
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            try { File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // arquivo em uso (ex.: log aberto)
        }
        foreach (var dir in Directory.EnumerateDirectories(from))
            CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    /// <summary>Caminhos salvos dentro da pasta antiga (ex.: PresentMon baixado em tools) passam a apontar para a nova.</summary>
    public static void FixSettingsPaths(string settingsFile, string oldDir, string newDir)
    {
        if (!File.Exists(settingsFile)) return;
        string text = File.ReadAllText(settingsFile);
        string oldJson = JsonEncodedText.Encode(oldDir).ToString();
        string newJson = JsonEncodedText.Encode(newDir).ToString();
        if (!text.Contains(oldJson, StringComparison.OrdinalIgnoreCase)) return;
        File.WriteAllText(settingsFile, text.Replace(oldJson, newJson, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Recria as tarefas agendadas com o nome novo (apontando para este exe), apaga as antigas e renomeia o plano.</summary>
    public static async Task MigrateSystemAsync(Shell shell, Logger logger, string exePath)
    {
        try
        {
            // Início com o Windows não existe mais (2.2): a tarefa antiga é apagada, não recriada.
            new AutoStartService(logger, dryRun: false).Disable();

            if (TaskSchedulerCom.IsEnabled(LegacyWeeklyFolder, LegacyWeeklyTask) is not null)
            {
                if (await new WeeklyReviewService(shell, logger).EnableAsync().ConfigureAwait(false) && TaskSchedulerCom.DeleteTask(LegacyWeeklyFolder, LegacyWeeklyTask))
                    logger.Info($"Revisão semanal movida para {WeeklyReviewService.TaskName}.");
            }

            var list = await shell.RunAsync("powercfg.exe", ["/list"]).ConfigureAwait(false);
            if (list.Success && PowerPlanTweak.FindLegacyPlan(PowerCfgParser.ParseList(list.StdOut)) is { } legacy)
            {
                var r = await shell.RunMutatingAsync("Renomear plano", "powercfg.exe",
                    ["/changename", legacy, PowerPlanTweak.PlanName, "Criado pelo Gueibs Optimizer"]).ConfigureAwait(false);
                if (r.Success) logger.Info($"Plano \"{PowerPlanTweak.LegacyPlanName}\" renomeado para \"{PowerPlanTweak.PlanName}\".");
            }
        }
        catch (Exception ex)
        {
            logger.Error("Falha ao migrar itens da versão antiga (GameOptimizer)", ex);
        }
    }

    /// <summary>Desinstalação: remove também as tarefas com os nomes antigos, se ainda existirem.</summary>
    public static void RemoveLegacyTasks()
    {
        if (TaskSchedulerCom.IsEnabled("\\", LegacyAutoStartTask) is not null) TaskSchedulerCom.DeleteTask("\\", LegacyAutoStartTask);
        if (TaskSchedulerCom.IsEnabled(LegacyWeeklyFolder, LegacyWeeklyTask) is not null) TaskSchedulerCom.DeleteTask(LegacyWeeklyFolder, LegacyWeeklyTask);
    }
}
