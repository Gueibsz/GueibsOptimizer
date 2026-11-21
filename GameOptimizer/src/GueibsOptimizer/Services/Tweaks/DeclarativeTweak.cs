using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using Microsoft.Win32;

namespace GueibsOptimizer.Services.Tweaks;

// ---------------- Definições (Data/tweaks.json) ----------------

public sealed class DeclarativeFile
{
    public int Version { get; set; } = 1;
    public List<DeclarativeDefinition> Tweaks { get; set; } = [];
}

public sealed class DeclarativeDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Risk { get; set; } = string.Empty;
    public int Points { get; set; }
    /// <summary>Nível da Fase 8.4: Baixo, Medio, Alto, Maximo ou Extremo (Minimal/Standard/Advanced do WinUtil = Baixo/Médio/Alto).</summary>
    public string Level { get; set; } = string.Empty;
    public bool InBoost { get; set; }
    /// <summary>Versões do Windows em que existe: [10], [11] ou [10, 11].</summary>
    public List<int> WindowsVersions { get; set; } = [10, 11];
    public int MinBuild { get; set; }
    /// <summary>Só aplicável em PCs fora de domínio (ex.: serviços de gerenciamento corporativo).</summary>
    public bool NotOnDomain { get; set; }

    /// <summary>Textos em pt-BR; são as chaves de idioma (traduções em Data/i18n).</summary>
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Notice { get; set; }
    public DeclarativeInfo? Info { get; set; }

    public List<DeclarativeRegistry> Registry { get; set; } = [];
    public List<DeclarativeService> Services { get; set; } = [];
    public List<DeclarativeTask> ScheduledTasks { get; set; } = [];
    public List<string> Commands { get; set; } = [];
}

public sealed class DeclarativeInfo
{
    public string Long { get; set; } = string.Empty;
    public string Benefit { get; set; } = string.Empty;
    public string Risk { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public sealed class DeclarativeRegistry
{
    public string Hive { get; set; } = "HKCU";
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>DWord, QWord, String ou ExpandString.</summary>
    public string Type { get; set; } = "DWord";
    public JsonElement Value { get; set; }
    /// <summary>Padrão do Windows; ausente/null = o valor não existe por padrão.</summary>
    public JsonElement? Default { get; set; }
}

public sealed class DeclarativeService
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Automatic, AutomaticDelayed, Manual ou Disabled.</summary>
    public string Startup { get; set; } = "Manual";
    public string Default { get; set; } = "Automatic";
}

public sealed class DeclarativeTask
{
    /// <summary>Caminho completo, ex.: \Microsoft\Windows\Feedback\Siuf\DmClient.</summary>
    public string Path { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}

/// <summary>
/// Comandos permitidos. O JSON só referencia o id; argumentos fixos vivem aqui (nunca texto vindo de arquivo).
/// </summary>
public sealed record AllowedCommand(string Id, string FileName, string[] ApplyArgs, string[] RevertArgs,
    Func<RegistryTweak, bool?> IsApplied, string Display);

public static class CommandAllowlist
{
    public static readonly IReadOnlyDictionary<string, AllowedCommand> All = new Dictionary<string, AllowedCommand>(StringComparer.OrdinalIgnoreCase)
    {
        // Hibernação: o estado real é lido de HKLM\SYSTEM\CurrentControlSet\Control\Power\HibernateEnabled.
        ["hibernate-off"] = new("hibernate-off", "powercfg.exe", ["/hibernate", "off"], ["/hibernate", "on"],
            reg => reg.Read(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled") is { } v ? v.Number == 0 : null,
            "powercfg /hibernate off"),
        ["hibernate-on"] = new("hibernate-on", "powercfg.exe", ["/hibernate", "on"], ["/hibernate", "off"],
            reg => reg.Read(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled") is { } v ? v.Number == 1 : null,
            "powercfg /hibernate on")
    };
}

public sealed class DeclarativeValidationException(IReadOnlyList<string> errors)
    : Exception("tweaks.json inválido: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Lê e valida Data/tweaks.json. Qualquer erro de schema impede o carregamento (nada é aplicado "pela metade").</summary>
public static partial class DeclarativeTweakLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static readonly string[] Levels = ["Baixo", "Medio", "Alto", "Maximo", "Extremo"];
    public static readonly string[] StartupTypes = ["Automatic", "AutomaticDelayed", "Manual", "Disabled"];
    public static readonly string[] RegistryTypes = ["DWord", "QWord", "String", "ExpandString"];

    [GeneratedRegex(@"^[a-z0-9]+(\.[a-z0-9-]+)+$")]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$")]
    private static partial Regex ServiceNameRegex();

    public static IReadOnlyList<DeclarativeDefinition> Parse(string json, IEnumerable<string>? existingIds = null)
    {
        DeclarativeFile? file;
        try { file = JsonSerializer.Deserialize<DeclarativeFile>(json, Options); }
        catch (JsonException ex) { throw new DeclarativeValidationException([$"JSON inválido: {ex.Message}"]); }
        if (file is null) throw new DeclarativeValidationException(["arquivo vazio"]);

        var errors = new List<string>();
        var ids = new HashSet<string>(existingIds ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var d in file.Tweaks) Validate(d, ids, errors);
        if (errors.Count > 0) throw new DeclarativeValidationException(errors);
        return file.Tweaks;
    }

    private static void Validate(DeclarativeDefinition d, HashSet<string> ids, List<string> errors)
    {
        string who = string.IsNullOrWhiteSpace(d.Id) ? "(sem id)" : d.Id;
        void Err(string m) => errors.Add($"{who}: {m}");

        if (!IdRegex().IsMatch(d.Id)) Err("id inválido (use letras minúsculas, ex.: privacy.activity-history)");
        else if (!ids.Add(d.Id)) Err("id duplicado");
        if (!Enum.TryParse<TweakCategory>(d.Category, true, out _)) Err($"categoria inválida '{d.Category}'");
        if (!Enum.TryParse<TweakRisk>(d.Risk, true, out var risk)) Err($"risco inválido '{d.Risk}'");
        if (!Levels.Contains(d.Level, StringComparer.OrdinalIgnoreCase)) Err($"nível inválido '{d.Level}'");
        if (d.Points < 0 || d.Points > 20) Err("pontos fora de 0 a 20");
        if (d.InBoost && risk == TweakRisk.High) Err("ajuste de risco Alto não pode entrar no Boost");
        // Extremo: sempre risco Alto, fora do Boost, sem pontos (a pontuação 0-100 é dos níveis seguros) e com aviso explícito.
        if (string.Equals(d.Level, "Extremo", StringComparison.OrdinalIgnoreCase) &&
            (risk != TweakRisk.High || d.InBoost || d.Points != 0 || string.IsNullOrWhiteSpace(d.Info?.Risk)))
            Err("nível Extremo exige risco High, inBoost false, points 0 e info.risk preenchido");
        if (d.WindowsVersions.Count == 0 || d.WindowsVersions.Any(v => v is not (10 or 11))) Err("windowsVersions deve conter 10 e/ou 11");
        if (string.IsNullOrWhiteSpace(d.Name) || string.IsNullOrWhiteSpace(d.Description)) Err("nome e descrição são obrigatórios");
        if (d.Registry.Count + d.Services.Count + d.ScheduledTasks.Count + d.Commands.Count == 0) Err("nenhuma ação");

        foreach (var r in d.Registry)
        {
            if (r.Hive is not ("HKLM" or "HKCU")) Err($"hive inválida '{r.Hive}'");
            if (string.IsNullOrWhiteSpace(r.Path) || r.Path.StartsWith('\\') || r.Path.Contains("..")) Err($"caminho de registro inválido '{r.Path}'");
            if (!RegistryTypes.Contains(r.Type)) Err($"tipo de registro inválido '{r.Type}'");
            else if (TryValue(r.Type, r.Value) is null) Err($"valor inválido para {r.Path}\\{r.Name}");
            if (r.Default is { ValueKind: not JsonValueKind.Null } def && TryValue(r.Type, def) is null) Err($"padrão inválido para {r.Path}\\{r.Name}");
        }
        foreach (var s in d.Services)
        {
            if (!ServiceNameRegex().IsMatch(s.Name)) Err($"nome de serviço inválido '{s.Name}'");
            if (!StartupTypes.Contains(s.Startup) || !StartupTypes.Contains(s.Default)) Err($"tipo de inicialização inválido em {s.Name}");
            if (ProtectedServices.Contains(s.Name)) Err($"o serviço {s.Name} é protegido e nunca pode ser alterado");
        }
        foreach (var t in d.ScheduledTasks)
            if (!t.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) || t.Path.EndsWith('\\')) Err($"tarefa inválida '{t.Path}' (só tarefas da Microsoft)");
        foreach (var c in d.Commands)
            if (!CommandAllowlist.All.ContainsKey(c)) Err($"comando fora da allowlist '{c}'");
    }

    /// <summary>Serviços que o app nunca altera (segurança, Windows Update, rede essencial, áudio, impressão).</summary>
    public static readonly HashSet<string> ProtectedServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "WinDefend", "WdNisSvc", "SecurityHealthService", "Sense", "MpsSvc", "BFE", "wuauserv", "UsoSvc", "WaaSMedicSvc", "BITS",
        "CryptSvc", "TrustedInstaller", "Dhcp", "Dnscache", "NlaSvc", "nsi", "netprofm", "Audiosrv", "AudioEndpointBuilder",
        "Spooler", "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "EventLog", "Winmgmt", "PlugPlay", "Power", "ProfSvc", "Schedule",
        "AppIDSvc", "wscsvc", "EFS", "KeyIso", "VaultSvc", "gpsvc"
    };

    public static RegistryValueData? TryValue(string type, JsonElement e)
    {
        try
        {
            return type switch
            {
                "DWord" when e.ValueKind == JsonValueKind.Number && e.TryGetUInt32(out var u) => RegistryValueData.DWord(u),
                "QWord" when e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var l) => RegistryValueData.FromObject(l, RegistryValueKind.QWord),
                "String" when e.ValueKind == JsonValueKind.String => RegistryValueData.String(e.GetString()!),
                "ExpandString" when e.ValueKind == JsonValueKind.String => RegistryValueData.FromObject(e.GetString()!, RegistryValueKind.ExpandString),
                _ => null
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException) { return null; }
    }

    /// <summary>Carrega o Data/tweaks.json embutido. Ids já usados pelos ajustes nativos contam como duplicados.</summary>
    public static IReadOnlyList<DeclarativeTweak> LoadDefault(TweakContext ctx, IEnumerable<string> nativeIds)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Data.tweaks.json", StringComparison.OrdinalIgnoreCase));
        if (name is null) return [];
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), nativeIds).Select(d => new DeclarativeTweak(d, ctx)).ToList();
    }
}

// ---------------- O ajuste ----------------

/// <summary>
/// Ajuste definido em JSON. Registro e serviços passam pelo RegistryTweak (backup com tipo e "não existia");
/// serviços são alterados pelo valor Start da chave do serviço; tarefas pelo Agendador; comandos só da allowlist.
/// Chave/serviço/tarefa inexistente = ação ignorada; sem nenhuma ação aplicável = "não aplicável".
/// </summary>
public sealed class DeclarativeTweak : ITweak, ILeveledTweak
{
    private const string ServicesRoot = @"SYSTEM\CurrentControlSet\Services\";
    private readonly DeclarativeDefinition _d;
    private readonly TweakContext _ctx;

    public DeclarativeTweak(DeclarativeDefinition definition, TweakContext ctx)
    {
        _d = definition;
        _ctx = ctx;
        Category = Enum.Parse<TweakCategory>(definition.Category, true);
        Risk = Enum.Parse<TweakRisk>(definition.Risk, true);
        Level = Enum.Parse<PerformanceLevel>(definition.Level, true);
    }

    public DeclarativeDefinition Definition => _d;
    public string Id => _d.Id;
    public string Name => _d.Name;
    public string Description => _d.Description;
    public TweakCategory Category { get; }
    public TweakRisk Risk { get; }
    public int Points => _d.Points;
    public string? Notice => _d.Notice;
    public bool InBoost => _d.InBoost;
    public PerformanceLevel Level { get; }

    public TweakInfo Info => new(
        _d.Info?.Long is { Length: > 0 } l ? l : _d.Description,
        string.Join("\n", ActionLines()),
        _d.Info?.Benefit ?? string.Empty,
        _d.Info?.Risk ?? string.Empty,
        "Botão Desfazer no cartão ou na aba Restauração: volta cada valor ao que estava antes (inclusive \"não existia\"). Se aplicado fora do app, volta ao padrão do Windows.",
        _d.Info?.Notes ?? string.Empty);

    public IEnumerable<string> ActionLines()
    {
        foreach (var r in _d.Registry)
            yield return $"• {r.Hive}\\{r.Path}\\{r.Name} = {DeclarativeTweakLoader.TryValue(r.Type, r.Value)?.Display()}";
        foreach (var s in _d.Services) yield return $"• Serviço {s.Name}: inicialização {StartupText(s.Startup)}";
        foreach (var t in _d.ScheduledTasks) yield return $"• Tarefa {t.Path}: {(t.Enabled ? "habilitada" : "desabilitada")}";
        foreach (var c in _d.Commands) yield return $"• Comando: {CommandAllowlist.All[c].Display}";
    }

    public static string StartupText(string startup) => startup switch
    {
        "Automatic" => "Automático",
        "AutomaticDelayed" => "Automático (atraso)",
        "Manual" => "Manual",
        _ => "Desativado"
    };

    // ---------- Versão e aplicabilidade ----------

    private bool VersionMatches()
    {
        int major = OsInfo.IsWindows11 ? 11 : 10;
        return _d.WindowsVersions.Contains(major) && OsInfo.Build >= _d.MinBuild;
    }

    private static RegHive Hive(DeclarativeRegistry r) => r.Hive == "HKLM" ? RegHive.LocalMachine : RegHive.CurrentUser;

    private bool ServiceExists(DeclarativeService s) => _ctx.Registry.KeyExists(RegHive.LocalMachine, ServicesRoot + s.Name);

    private static (string Folder, string Name) SplitTask(string path)
    {
        int i = path.LastIndexOf('\\');
        return (i <= 0 ? "\\" : path[..i], path[(i + 1)..]);
    }

    private bool TaskExists(DeclarativeTask t)
    {
        var (folder, name) = SplitTask(t.Path);
        return TaskSchedulerCom.IsEnabled(folder, name) is not null;
    }

    /// <summary>PC ligado a um domínio (Win32_ComputerSystem.PartOfDomain), lido uma vez.</summary>
    public static readonly Lazy<bool> IsDomainJoined = new(() =>
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT PartOfDomain FROM Win32_ComputerSystem");
            foreach (System.Management.ManagementObject mo in s.Get())
                using (mo) return mo["PartOfDomain"] is true;
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or System.Runtime.InteropServices.COMException) { }
        return false;
    });

    public bool IsApplicable()
    {
        if (!VersionMatches()) return false;
        if (_d.NotOnDomain && IsDomainJoined.Value) return false;
        if (_d.Registry.Count > 0 || _d.Commands.Count > 0) return true;
        return _d.Services.Any(ServiceExists) || _d.ScheduledTasks.Any(TaskExists);
    }

    // ---------- Estado real ----------

    public bool IsApplied()
    {
        foreach (var r in _d.Registry)
        {
            var desired = DeclarativeTweakLoader.TryValue(r.Type, r.Value)!;
            var current = _ctx.Registry.Read(Hive(r), r.Path, r.Name);
            var def = r.Default is { ValueKind: not JsonValueKind.Null } d ? DeclarativeTweakLoader.TryValue(r.Type, d) : null;
            bool ok = current is null ? def is not null && desired.SameValueAs(def) : desired.SameValueAs(current);
            if (!ok) return false;
        }
        foreach (var s in _d.Services.Where(ServiceExists))
            if (ReadStartup(s.Name) != s.Startup) return false;
        foreach (var t in _d.ScheduledTasks)
        {
            var (folder, name) = SplitTask(t.Path);
            if (TaskSchedulerCom.IsEnabled(folder, name) is { } enabled && enabled != t.Enabled) return false;
        }
        foreach (var c in _d.Commands)
            if (CommandAllowlist.All[c].IsApplied(_ctx.Registry) == false) return false;
        return true;
    }

    /// <summary>Tipo de inicialização lido do registro (Start + DelayedAutostart).</summary>
    public string? ReadStartup(string service)
    {
        var start = _ctx.Registry.Read(RegHive.LocalMachine, ServicesRoot + service, "Start")?.Number;
        var delayed = _ctx.Registry.Read(RegHive.LocalMachine, ServicesRoot + service, "DelayedAutostart")?.Number == 1;
        return start switch
        {
            2 => delayed ? "AutomaticDelayed" : "Automatic",
            3 => "Manual",
            4 => "Disabled",
            null => null,
            _ => "Other"
        };
    }

    public static (uint Start, uint? Delayed) StartupValues(string startup) => startup switch
    {
        "Automatic" => (2, 0),
        "AutomaticDelayed" => (2, 1),
        "Manual" => (3, null),
        _ => (4, null)
    };

    // ---------- Aplicar ----------

    public async Task ApplyAsync()
    {
        foreach (var r in _d.Registry)
        {
            var desired = DeclarativeTweakLoader.TryValue(r.Type, r.Value)!;
            if (!desired.SameValueAs(_ctx.Registry.Read(Hive(r), r.Path, r.Name)))
                _ctx.Registry.Set(Id, Hive(r), r.Path, r.Name, desired);
        }

        foreach (var s in _d.Services.Where(ServiceExists))
        {
            if (ReadStartup(s.Name) == s.Startup) continue;
            var (start, delayed) = StartupValues(s.Startup);
            _ctx.Registry.Set(Id, RegHive.LocalMachine, ServicesRoot + s.Name, "Start", RegistryValueData.DWord(start));
            if (delayed is { } dl) _ctx.Registry.Set(Id, RegHive.LocalMachine, ServicesRoot + s.Name, "DelayedAutostart", RegistryValueData.DWord(dl));
        }

        foreach (var t in _d.ScheduledTasks)
        {
            var (folder, name) = SplitTask(t.Path);
            if (TaskSchedulerCom.IsEnabled(folder, name) is not { } enabled || enabled == t.Enabled) continue;
            if (_ctx.DryRun) { _ctx.Logger.Simulated($"{(t.Enabled ? "Habilitar" : "Desabilitar")} tarefa agendada {t.Path}"); continue; }
            _ctx.SaveCustomBackup(Id, "task:" + t.Path, enabled ? "habilitada" : "desabilitada", $"Tarefa {t.Path}");
            if (!TaskSchedulerCom.SetEnabled(folder, name, t.Enabled))
                throw new InvalidOperationException($"Não foi possível alterar a tarefa {t.Path}.");
        }

        foreach (var id in _d.Commands)
        {
            var c = CommandAllowlist.All[id];
            if (c.IsApplied(_ctx.Registry) == true) continue;
            var r = await _ctx.Shell.RunMutatingAsync($"{Name}: {c.Display}", c.FileName, c.ApplyArgs).ConfigureAwait(false);
            if (!r.Success) throw new InvalidOperationException($"O comando {c.Display} falhou (código {r.ExitCode}).");
            if (!r.Simulated) _ctx.SaveCustomBackup(Id, "cmd:" + id, "aplicado", c.Display);
        }
    }

    // ---------- Desfazer ----------

    public async Task RevertAsync()
    {
        var backups = _ctx.Backup.ForTweak(Id);

        // Tarefas e comandos com backup próprio.
        foreach (var b in backups.Where(b => b.CustomKey is not null))
        {
            if (b.CustomKey!.StartsWith("task:", StringComparison.Ordinal))
            {
                var (folder, name) = SplitTask(b.CustomKey["task:".Length..]);
                if (_ctx.DryRun) _ctx.Logger.Simulated($"Voltar tarefa {b.CustomKey[5..]} para {b.CustomValue}");
                else TaskSchedulerCom.SetEnabled(folder, name, b.CustomValue == "habilitada");
            }
            else if (b.CustomKey.StartsWith("cmd:", StringComparison.Ordinal) && CommandAllowlist.All.TryGetValue(b.CustomKey[4..], out var c))
            {
                await _ctx.Shell.RunMutatingAsync($"{Name}: desfazer {c.Display}", c.FileName, c.RevertArgs).ConfigureAwait(false);
            }
        }

        // Registro e serviços: o backup guarda o valor anterior (ou "não existia").
        if (_ctx.Registry.Restore(Id) > 0 || backups.Count > 0)
        {
            if (!_ctx.DryRun) _ctx.Backup.RemoveTweak(Id);
            return;
        }

        // Sem backup (aplicado fora do app): volta ao padrão do Windows.
        _ctx.Logger.Info($"{Name}: sem backup registrado; voltando ao padrão do Windows.");
        foreach (var r in _d.Registry)
        {
            var def = r.Default is { ValueKind: not JsonValueKind.Null } d ? DeclarativeTweakLoader.TryValue(r.Type, d) : null;
            if (def is null) _ctx.Registry.DeleteValue(Id, Hive(r), r.Path, r.Name);
            else _ctx.Registry.Set(Id, Hive(r), r.Path, r.Name, def);
        }
        foreach (var s in _d.Services.Where(ServiceExists))
        {
            var (start, delayed) = StartupValues(s.Default);
            _ctx.Registry.Set(Id, RegHive.LocalMachine, ServicesRoot + s.Name, "Start", RegistryValueData.DWord(start));
            if (delayed is { } dl) _ctx.Registry.Set(Id, RegHive.LocalMachine, ServicesRoot + s.Name, "DelayedAutostart", RegistryValueData.DWord(dl));
        }
        foreach (var t in _d.ScheduledTasks)
        {
            var (folder, name) = SplitTask(t.Path);
            if (TaskSchedulerCom.IsEnabled(folder, name) is null) continue;
            if (_ctx.DryRun) _ctx.Logger.Simulated($"Voltar tarefa {t.Path} ao padrão");
            else TaskSchedulerCom.SetEnabled(folder, name, !t.Enabled);
        }
        foreach (var id in _d.Commands)
        {
            var c = CommandAllowlist.All[id];
            if (c.IsApplied(_ctx.Registry) == true)
                await _ctx.Shell.RunMutatingAsync($"{Name}: desfazer {c.Display}", c.FileName, c.RevertArgs).ConfigureAwait(false);
        }
        if (!_ctx.DryRun) _ctx.Backup.RemoveTweak(Id);
    }
}
