using GueibsOptimizer.Core;

namespace GueibsOptimizer.Services;

/// <summary>
/// Até a versão 2.1 havia a opção "Iniciar com o Windows como administrador" (tarefa agendada de logon).
/// Desde a 2.2 o app só abre manualmente (sempre como administrador, pelo manifesto): esta classe apenas
/// encontra e apaga as tarefas que versões anteriores tenham deixado. Nunca cria tarefa de início.
/// </summary>
public sealed class AutoStartService(Logger logger, bool dryRun)
{
    public const string TaskName = "GueibsOptimizerAutoStart";
    /// <summary>Nome usado quando o app se chamava GameOptimizer (até a 2.0).</summary>
    public const string LegacyTaskName = "GameOptimizerAutoStart";

    public static IReadOnlyList<string> TaskNames { get; } = [TaskName, LegacyTaskName];

    public bool IsEnabled() => TaskNames.Any(n => TaskSchedulerCom.IsEnabled("\\", n) is not null);

    /// <summary>Apaga as tarefas de início que existirem. Retorna null se deu certo, ou a mensagem de erro.</summary>
    public string? Disable()
    {
        string? error = null;
        foreach (var name in TaskNames)
        {
            if (TaskSchedulerCom.IsEnabled("\\", name) is null) continue;
            if (dryRun)
            {
                logger.Simulated($"Apagar tarefa {name}");
                continue;
            }
            if (TaskSchedulerCom.DeleteTask("\\", name)) logger.Info($"Tarefa de início com o Windows removida ({name}); o app agora só abre manualmente.");
            else error = $"Não foi possível apagar a tarefa {name}.";
        }
        if (error is not null) logger.Warn(error);
        return error;
    }
}
