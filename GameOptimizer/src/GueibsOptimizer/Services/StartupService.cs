using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using Microsoft.Win32;

namespace GueibsOptimizer.Services;

/// <summary>Formato binário do StartupApproved (o mesmo que o Gerenciador de Tarefas usa).</summary>
public static class StartupApproved
{
    public const string Base = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>Byte 0 ímpar (0x03, 0x07...) = desativado. Ausente = ativado.</summary>
    public static bool IsDisabled(byte[]? data) => data is { Length: > 0 } && (data[0] & 1) == 1;

    public static byte[] Disabled(DateTime whenUtc)
    {
        var data = new byte[12];
        data[0] = 0x03;
        BitConverter.GetBytes(whenUtc.ToFileTimeUtc()).CopyTo(data, 4);
        return data;
    }

    public static byte[] Enabled() => [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    public static (RegHive Hive, string Key)? Location(StartupSource source) => source switch
    {
        StartupSource.RunUser => (RegHive.CurrentUser, Base + @"\Run"),
        StartupSource.RunMachine => (RegHive.LocalMachine, Base + @"\Run"),
        StartupSource.RunMachine32 => (RegHive.LocalMachine, Base + @"\Run32"),
        StartupSource.FolderUser => (RegHive.CurrentUser, Base + @"\StartupFolder"),
        StartupSource.FolderCommon => (RegHive.LocalMachine, Base + @"\StartupFolder"),
        _ => null
    };
}

public static class CommandLineTools
{
    /// <summary>Extrai o caminho do executável de uma linha de comando (aspas ou até ".exe").</summary>
    public static string? ExtractExecutable(string command)
    {
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.Length == 0) return null;
        if (text[0] == '"')
        {
            int end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return text[..(exe + 4)];
        int space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }
}

/// <summary>
/// Lista programas que iniciam com o Windows e os desativa de forma 100% reversível:
/// entradas Run/pasta via StartupApproved (nada é apagado) e tarefas via Enabled=false.
/// </summary>
public sealed class StartupService(TweakContext ctx) : IUndoProvider
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKey32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

    public Task<IReadOnlyList<StartupItem>> ListAsync() => Task.Run<IReadOnlyList<StartupItem>>(List);

    public IReadOnlyList<StartupItem> List()
    {
        var items = new List<StartupItem>();
        AddRun(items, StartupSource.RunUser, RegHive.CurrentUser, RunKey);
        AddRun(items, StartupSource.RunMachine, RegHive.LocalMachine, RunKey);
        AddRun(items, StartupSource.RunMachine32, RegHive.LocalMachine, RunKey32);
        AddFolder(items, StartupSource.FolderUser, Environment.GetFolderPath(Environment.SpecialFolder.Startup));
        AddFolder(items, StartupSource.FolderCommon, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));

        // Tarefas do próprio Windows (\Microsoft\...) ficam de fora para evitar desativar componentes do sistema.
        foreach (var task in TaskSchedulerCom.ListLogonTasks()
                     .Where(t => !t.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)))
        {
            string full = task.Path == "\\" ? "\\" + task.Name : $@"{task.Path}\{task.Name}";
            items.Add(new StartupItem(StartupSource.ScheduledTask, full, task.Name, task.Command, full,
                PublisherOf(task.Command), task.Enabled));
        }
        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task DisableAsync(StartupItem item)
    {
        if (item.Source == StartupSource.ScheduledTask)
        {
            if (ctx.DryRun) { ctx.Logger.Simulated($"Desativar tarefa de logon {item.Key}"); return; }
            ctx.SaveCustomBackup(item.BackupId, "task-enabled", "true", $"Inicialização: tarefa {item.Key} desativada");
            var (folder, name) = SplitTask(item.Key);
            if (!TaskSchedulerCom.SetEnabled(folder, name, false))
                throw new InvalidOperationException($"Não foi possível desativar a tarefa {item.Key}.");
            ctx.Logger.Info($"Inicialização: {item.Name} desativado.");
            return;
        }

        var (hive, key) = StartupApproved.Location(item.Source)!.Value;
        ctx.Registry.Set(item.BackupId, hive, key, item.Key,
            RegistryValueData.FromObject(StartupApproved.Disabled(DateTime.UtcNow), RegistryValueKind.Binary));
        ctx.Logger.Info($"Inicialização: {item.Name} desativado.");
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task EnableAsync(StartupItem item) => UndoAsync(item.BackupId);

    // --- IUndoProvider: permite "Desfazer" na aba Restauração ---
    public bool CanUndo(string id) => StartupIds.TryParse(id, out _, out _);

    public string DisplayName(string id) =>
        StartupIds.TryParse(id, out _, out var key) ? $"Inicialização: {Path.GetFileName(key)}" : id;

    public Task UndoAsync(string id)
    {
        if (!StartupIds.TryParse(id, out var source, out var key))
            throw new ArgumentException("Id de inicialização inválido.", nameof(id));

        if (source == StartupSource.ScheduledTask)
        {
            var (folder, name) = SplitTask(key);
            if (ctx.DryRun) { ctx.Logger.Simulated($"Reativar tarefa de logon {key}"); return Task.CompletedTask; }
            if (!TaskSchedulerCom.SetEnabled(folder, name, true))
                throw new InvalidOperationException($"Não foi possível reativar a tarefa {key}.");
        }
        else if (ctx.Registry.Restore(id) == 0)
        {
            // Desativado fora do app: grava o estado "ativado".
            var (hive, approvedKey) = StartupApproved.Location(source)!.Value;
            ctx.Registry.Set(id, hive, approvedKey, key,
                RegistryValueData.FromObject(StartupApproved.Enabled(), RegistryValueKind.Binary));
        }
        if (!ctx.DryRun) ctx.Backup.RemoveTweak(id);
        ctx.Logger.Info($"Inicialização: {Path.GetFileName(key)} reativado.");
        return Task.CompletedTask;
    }

    private void AddRun(List<StartupItem> items, StartupSource source, RegHive hive, string keyPath)
    {
        try
        {
            using var baseKey = RegistryTweak.OpenBase(hive);
            using var key = baseKey.OpenSubKey(keyPath);
            if (key is null) return;
            var (approvedHive, approvedKey) = StartupApproved.Location(source)!.Value;
            foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
            {
                var command = key.GetValue(name)?.ToString() ?? string.Empty;
                var approved = ctx.Registry.Read(approvedHive, approvedKey, name);
                bool enabled = !StartupApproved.IsDisabled(approved?.Base64 is { } b ? Convert.FromBase64String(b) : null);
                items.Add(new StartupItem(source, name, name, command,
                    $@"{RegistryTweak.HiveName(hive)}\{keyPath}", PublisherOf(command), enabled));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            ctx.Logger.Warn($"Não foi possível ler {keyPath}: {ex.Message}");
        }
    }

    private void AddFolder(List<StartupItem> items, StartupSource source, string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        var (approvedHive, approvedKey) = StartupApproved.Location(source)!.Value;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                string command = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(file) ?? file : file;
                var approved = ctx.Registry.Read(approvedHive, approvedKey, fileName);
                bool enabled = !StartupApproved.IsDisabled(approved?.Base64 is { } b ? Convert.FromBase64String(b) : null);
                items.Add(new StartupItem(source, fileName, Path.GetFileNameWithoutExtension(file), command, folder,
                    PublisherOf(command), enabled));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Logger.Warn($"Não foi possível ler {folder}: {ex.Message}");
        }
    }

    private static (string Folder, string Name) SplitTask(string full)
    {
        int idx = full.LastIndexOf('\\');
        return (idx <= 0 ? "\\" : full[..idx], full[(idx + 1)..]);
    }

    private static string? ShortcutTarget(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(lnk);
            string target = shortcut.TargetPath;
            string args = shortcut.Arguments;
            return string.IsNullOrEmpty(target) ? null : $"\"{target}\" {args}".Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Nome do assinante do Authenticode (sem validar a cadeia); null se não assinado.</summary>
    public static string? PublisherOf(string command)
    {
        var exe = CommandLineTools.ExtractExecutable(command);
        if (exe is null || !File.Exists(exe)) return null;
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(exe));
            var name = cert.GetNameInfo(X509NameType.SimpleName, false);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
