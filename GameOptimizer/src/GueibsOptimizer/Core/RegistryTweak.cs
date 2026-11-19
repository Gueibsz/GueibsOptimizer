using System.IO;
using System.Security;
using Microsoft.Win32;

namespace GueibsOptimizer.Core;

/// <summary>
/// Escrita de registro com backup do valor anterior (tipo incluído, e se chave/valor existiam).
/// No modo simulação apenas registra no log o que seria alterado.
/// </summary>
public sealed class RegistryTweak
{
    private readonly BackupStore _store;
    private readonly Logger _logger;

    public RegistryTweak(BackupStore store, Logger logger, bool dryRun)
    {
        _store = store;
        _logger = logger;
        DryRun = dryRun;
    }

    public bool DryRun { get; }

    public static string HiveName(RegHive hive) => hive == RegHive.LocalMachine ? "HKLM" : "HKCU";

    public static RegistryKey OpenBase(RegHive hive) => RegistryKey.OpenBaseKey(
        hive == RegHive.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
        RegistryView.Registry64);

    public bool KeyExists(RegHive hive, string keyPath)
    {
        try
        {
            using var baseKey = OpenBase(hive);
            using var key = baseKey.OpenSubKey(keyPath, writable: false);
            return key is not null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Lê o valor com o tipo; null se a chave/valor não existir ou não puder ser lido.</summary>
    public RegistryValueData? Read(RegHive hive, string keyPath, string valueName)
    {
        try
        {
            using var baseKey = OpenBase(hive);
            using var key = baseKey.OpenSubKey(keyPath, writable: false);
            if (key is null) return null;
            var raw = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (raw is null) return null;
            return RegistryValueData.FromObject(raw, key.GetValueKind(valueName));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public bool Matches(RegHive hive, string keyPath, string valueName, RegistryValueData expected) =>
        expected.SameValueAs(Read(hive, keyPath, valueName));

    public void Set(string tweakId, RegHive hive, string keyPath, string valueName, uint dword) =>
        Set(tweakId, hive, keyPath, valueName, RegistryValueData.DWord(dword));

    public void Set(string tweakId, RegHive hive, string keyPath, string valueName, object value, RegistryValueKind kind) =>
        Set(tweakId, hive, keyPath, valueName, RegistryValueData.FromObject(value, kind));

    public void Set(string tweakId, RegHive hive, string keyPath, string valueName, RegistryValueData value)
    {
        var current = Read(hive, keyPath, valueName);
        string location = $@"{HiveName(hive)}\{keyPath}\{valueName}";

        if (DryRun)
        {
            _logger.Simulated($"Registro {location}: {current?.Display() ?? "(não existe)"} → {value.Display()}");
            return;
        }

        _store.AddIfAbsent(new BackupEntry
        {
            TweakId = tweakId,
            Kind = BackupKind.Registry,
            Hive = hive,
            KeyPath = keyPath,
            ValueName = valueName,
            ValueExisted = current is not null,
            PreviousValue = current,
            CreatedKeyPath = FindFirstMissingKey(hive, keyPath),
            Description = $"{location} = {value.Display()}"
        });

        using var baseKey = OpenBase(hive);
        using var key = baseKey.CreateSubKey(keyPath, writable: true)
                        ?? throw new IOException($"Não foi possível abrir {HiveName(hive)}\\{keyPath}.");
        key.SetValue(valueName, value.ToRegistryObject(), value.Kind);
        _logger.Info($"Registro {location} definido como {value.Display()}");
    }

    /// <summary>Remove um valor guardando backup (usado quando o "padrão do Windows" é a ausência do valor).</summary>
    public void DeleteValue(string tweakId, RegHive hive, string keyPath, string valueName)
    {
        var current = Read(hive, keyPath, valueName);
        if (current is null) return;
        string location = $@"{HiveName(hive)}\{keyPath}\{valueName}";

        if (DryRun)
        {
            _logger.Simulated($"Registro {location}: remover valor {current.Display()}");
            return;
        }

        _store.AddIfAbsent(new BackupEntry
        {
            TweakId = tweakId,
            Kind = BackupKind.Registry,
            Hive = hive,
            KeyPath = keyPath,
            ValueName = valueName,
            ValueExisted = true,
            PreviousValue = current,
            Description = $"{location} removido"
        });

        using var baseKey = OpenBase(hive);
        using var key = baseKey.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
        _logger.Info($"Registro {location} removido");
    }

    /// <summary>Desfaz as alterações de registro de um ajuste, em ordem inversa. Retorna quantas entradas restaurou.</summary>
    public int Restore(string tweakId)
    {
        var entries = _store.ForTweak(tweakId)
            .Where(e => e.Kind == BackupKind.Registry)
            .OrderByDescending(e => e.TimestampUtc)
            .ToList();
        if (entries.Count == 0) return 0;

        var restored = new List<Guid>();
        foreach (var entry in entries)
        {
            try
            {
                RestoreEntry(entry);
                restored.Add(entry.Id);
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                _logger.Error($"Falha ao restaurar {entry.Description}", ex);
            }
        }

        if (!DryRun) _store.Remove(restored);
        return restored.Count;
    }

    /// <summary>Desfaz tudo, do ajuste mais recente para o mais antigo.</summary>
    public int RestoreAll()
    {
        var tweakIds = _store.All
            .Where(e => e.Kind == BackupKind.Registry)
            .GroupBy(e => e.TweakId)
            .OrderByDescending(g => g.Max(e => e.TimestampUtc))
            .Select(g => g.Key)
            .ToList();
        return tweakIds.Sum(Restore);
    }

    private void RestoreEntry(BackupEntry entry)
    {
        if (entry.KeyPath is null || entry.ValueName is null) return;
        string location = $@"{HiveName(entry.Hive)}\{entry.KeyPath}\{entry.ValueName}";

        if (DryRun)
        {
            _logger.Simulated($"Restaurar {location} → {entry.PreviousValueDisplay}");
            return;
        }

        using var baseKey = OpenBase(entry.Hive);
        if (entry.ValueExisted && entry.PreviousValue is not null)
        {
            using var key = baseKey.CreateSubKey(entry.KeyPath, writable: true);
            key?.SetValue(entry.ValueName, entry.PreviousValue.ToRegistryObject(), entry.PreviousValue.Kind);
        }
        else
        {
            using (var key = baseKey.OpenSubKey(entry.KeyPath, writable: true))
                key?.DeleteValue(entry.ValueName, throwOnMissingValue: false);

            if (entry.CreatedKeyPath is not null)
                DeleteEmptyKeysUpTo(baseKey, entry.KeyPath, entry.CreatedKeyPath);
        }
        _logger.Info($"Restaurado {location} → {entry.PreviousValueDisplay}");
    }

    /// <summary>Apaga chaves criadas pelo app, da folha até a raiz criada, apenas se estiverem vazias.</summary>
    private static void DeleteEmptyKeysUpTo(RegistryKey baseKey, string keyPath, string createdRoot)
    {
        var path = keyPath.TrimEnd('\\');
        var root = createdRoot.TrimEnd('\\');
        while (path.Length >= root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            using (var key = baseKey.OpenSubKey(path, writable: false))
            {
                if (key is null || key.ValueCount > 0 || key.SubKeyCount > 0) return;
            }
            baseKey.DeleteSubKey(path, throwOnMissingSubKey: false);
            int idx = path.LastIndexOf('\\');
            if (idx < 0) return;
            path = path[..idx];
        }
    }

    private string? FindFirstMissingKey(RegHive hive, string keyPath)
    {
        var parts = keyPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i <= parts.Length; i++)
        {
            var partial = string.Join('\\', parts.Take(i));
            if (!KeyExists(hive, partial)) return partial;
        }
        return null;
    }
}
