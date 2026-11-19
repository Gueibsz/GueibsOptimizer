using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GueibsOptimizer.Core;

public enum BackupKind
{
    Registry,
    /// <summary>Estado salvo por um ajuste não baseado em registro (plano de energia, serviço, etc.).</summary>
    Custom
}

public sealed class BackupEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TweakId { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BackupKind Kind { get; set; }

    /// <summary>Descrição legível do que foi alterado (exibida na aba Restauração).</summary>
    public string Description { get; set; } = string.Empty;

    // --- Registro ---
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RegHive Hive { get; set; }
    public string? KeyPath { get; set; }
    public string? ValueName { get; set; }
    public bool ValueExisted { get; set; }
    /// <summary>Primeira chave (mais alta na árvore) criada pelo app; null se a chave já existia.</summary>
    public string? CreatedKeyPath { get; set; }
    public RegistryValueData? PreviousValue { get; set; }

    // --- Custom ---
    public string? CustomKey { get; set; }
    public string? CustomValue { get; set; }

    [JsonIgnore]
    public string PreviousValueDisplay => Kind switch
    {
        BackupKind.Registry => ValueExisted ? PreviousValue?.Display() ?? "?" : "(não existia)",
        _ => CustomValue ?? "(vazio)"
    };
}

/// <summary>
/// Persistência do histórico de valores anteriores em backup.json.
/// Guarda apenas o valor ORIGINAL: reaplicar um ajuste não sobrescreve o backup.
/// </summary>
public sealed class BackupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;
    private readonly object _sync = new();
    private List<BackupEntry> _entries;

    public BackupStore(string filePath)
    {
        _filePath = filePath;
        _entries = Load(filePath);
    }

    public string FilePath => _filePath;

    public event Action? Changed;

    public IReadOnlyList<BackupEntry> All
    {
        get { lock (_sync) return _entries.ToList(); }
    }

    public IReadOnlyList<BackupEntry> ForTweak(string tweakId)
    {
        lock (_sync) return _entries.Where(e => e.TweakId == tweakId).ToList();
    }

    public bool HasRegistryBackup(string tweakId, RegHive hive, string keyPath, string valueName)
    {
        lock (_sync)
            return _entries.Any(e => e.Kind == BackupKind.Registry && e.TweakId == tweakId && e.Hive == hive &&
                                     string.Equals(e.KeyPath, keyPath, StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals(e.ValueName, valueName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adiciona a entrada se ainda não houver backup equivalente. Retorna true se adicionou.</summary>
    public bool AddIfAbsent(BackupEntry entry)
    {
        lock (_sync)
        {
            bool exists = entry.Kind switch
            {
                BackupKind.Registry => _entries.Any(e => e.Kind == BackupKind.Registry && e.TweakId == entry.TweakId &&
                    e.Hive == entry.Hive &&
                    string.Equals(e.KeyPath, entry.KeyPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.ValueName, entry.ValueName, StringComparison.OrdinalIgnoreCase)),
                _ => _entries.Any(e => e.Kind == BackupKind.Custom && e.TweakId == entry.TweakId &&
                    string.Equals(e.CustomKey, entry.CustomKey, StringComparison.OrdinalIgnoreCase))
            };
            if (exists) return false;
            _entries.Add(entry);
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    public string? GetCustom(string tweakId, string key)
    {
        lock (_sync)
            return _entries.LastOrDefault(e => e.Kind == BackupKind.Custom && e.TweakId == tweakId &&
                                               string.Equals(e.CustomKey, key, StringComparison.OrdinalIgnoreCase))?.CustomValue;
    }

    public void Remove(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        if (set.Count == 0) return;
        lock (_sync)
        {
            _entries.RemoveAll(e => set.Contains(e.Id));
            Save();
        }
        Changed?.Invoke();
    }

    public void RemoveTweak(string tweakId) => Remove(ForTweak(tweakId).Select(e => e.Id));

    /// <summary>Relê o arquivo (usado nos testes para provar que o tipo sobrevive à serialização).</summary>
    public void Reload()
    {
        lock (_sync) _entries = Load(_filePath);
        Changed?.Invoke();
    }

    private static List<BackupEntry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<BackupEntry>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Arquivo corrompido: preserva uma cópia para análise e começa vazio.
            try { File.Copy(path, path + ".corrompido", overwrite: true); } catch { /* sem ação */ }
            return [];
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = _filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries, JsonOptions));
        File.Move(temp, _filePath, overwrite: true);
    }
}
