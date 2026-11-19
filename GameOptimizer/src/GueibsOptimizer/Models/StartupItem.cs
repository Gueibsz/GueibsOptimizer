namespace GueibsOptimizer.Models;

public enum StartupSource
{
    RunUser,
    RunMachine,
    RunMachine32,
    FolderUser,
    FolderCommon,
    ScheduledTask
}

public sealed record StartupItem(
    StartupSource Source,
    string Key,
    string Name,
    string Command,
    string Location,
    string? Publisher,
    bool Enabled)
{
    /// <summary>Id usado no backup/histórico (prefixo "startup:").</summary>
    public string BackupId => StartupIds.Make(Source, Key);

    public string SourceText => Source switch
    {
        StartupSource.RunUser => "Registro (usuário)",
        StartupSource.RunMachine => "Registro (todos os usuários)",
        StartupSource.RunMachine32 => "Registro 32 bits (todos)",
        StartupSource.FolderUser => "Pasta Inicializar (usuário)",
        StartupSource.FolderCommon => "Pasta Inicializar (todos)",
        _ => "Tarefa agendada (logon)"
    };

    public string PublisherText => string.IsNullOrEmpty(Publisher) ? "Sem assinatura digital" : Publisher;
}

public static class StartupIds
{
    public const string Prefix = "startup:";

    public static string Make(StartupSource source, string key) => $"{Prefix}{source}|{key}";

    public static bool TryParse(string id, out StartupSource source, out string key)
    {
        source = default;
        key = string.Empty;
        if (!id.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var rest = id[Prefix.Length..];
        int bar = rest.IndexOf('|');
        if (bar <= 0 || !Enum.TryParse(rest[..bar], out source)) return false;
        key = rest[(bar + 1)..];
        return key.Length > 0;
    }
}
