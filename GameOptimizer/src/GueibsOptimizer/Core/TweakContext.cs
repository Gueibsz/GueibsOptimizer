namespace GueibsOptimizer.Core;

/// <summary>Dependências compartilhadas pelos ajustes. DryRun propaga o modo simulação.</summary>
public sealed class TweakContext
{
    public TweakContext(Logger logger, BackupStore backup, bool dryRun)
    {
        Logger = logger;
        Backup = backup;
        DryRun = dryRun;
        Registry = new RegistryTweak(backup, logger, dryRun);
        Shell = new Shell(logger, dryRun);
    }

    public Logger Logger { get; }
    public BackupStore Backup { get; }
    public RegistryTweak Registry { get; }
    public Shell Shell { get; }
    public bool DryRun { get; }

    /// <summary>Guarda um estado não-registro (ex.: GUID do plano de energia anterior), sem sobrescrever o original.</summary>
    public void SaveCustomBackup(string tweakId, string key, string value, string description)
    {
        if (DryRun)
        {
            Logger.Simulated($"Backup de '{key}' = {value}");
            return;
        }
        Backup.AddIfAbsent(new BackupEntry
        {
            TweakId = tweakId,
            Kind = BackupKind.Custom,
            CustomKey = key,
            CustomValue = value,
            Description = description
        });
    }
}
