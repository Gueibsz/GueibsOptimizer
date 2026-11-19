using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services.Tweaks;

namespace GueibsOptimizer.Services.SelfTest;

public enum SelfTestStatus
{
    Passou,
    /// <summary>Funcionou, mas algo merece atenção (ex.: valor bruto equivalente, mas não idêntico; o autoteste restaurou o exato).</summary>
    Atencao,
    Falhou,
    Pulado,
    NaoAplicavel
}

public sealed class SelfTestResult
{
    public string Suite { get; set; } = string.Empty;
    public string Test { get; set; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SelfTestStatus Status { get; set; } = SelfTestStatus.Passou;
    public string Detail { get; set; } = string.Empty;
    public double Seconds { get; set; }
    public Dictionary<string, object?> Evidence { get; set; } = [];
    public List<string> Log { get; set; } = [];

    public void Fail(string message)
    {
        Status = SelfTestStatus.Falhou;
        Detail = (Detail + " " + message).Trim();
    }

    public void Warn(string message)
    {
        if (Status == SelfTestStatus.Passou) Status = SelfTestStatus.Atencao;
        Detail = (Detail + " " + message).Trim();
    }

    public void Note(string message) => Detail = (Detail + " " + message).Trim();
}

public sealed class SelfTestReport
{
    public string App { get; set; } = "Gueibs Optimizer";
    public string Version { get; set; } = string.Empty;
    public string Suite { get; set; } = string.Empty;
    public DateTime Started { get; set; }
    public DateTime Finished { get; set; }
    public string Windows { get; set; } = string.Empty;
    public string BackupFile { get; set; } = string.Empty;
    public bool? ConnectivityOk { get; set; }
    public Dictionary<string, int> Summary { get; set; } = [];
    public List<SelfTestResult> Results { get; set; } = [];
    public SortedDictionary<string, string>? Snapshot { get; set; }
    public List<SnapshotDiff>? BaselineDiff { get; set; }
}

/// <summary>
/// --selftest: aplica e reverte ajustes REAIS pelo próprio app, lendo o valor bruto antes, depois de aplicar e depois de
/// reverter. Usa um backup.json separado (na pasta do relatório) para não misturar com o histórico do usuário, e um diário
/// do valor original de cada local tocado para devolver o estado EXATO mesmo quando o desfazer do app volta ao "padrão".
/// Itens que mexem em rede, acesso remoto, hibernação ou exigem reinício ficam só em dry-run.
/// </summary>
public sealed class SelfTestRunner
{
    public static readonly string[] Suites =
    [
        "snapshot", "restorepoint", "tweaks", "power", "startup", "defender", "task", "windowsupdate", "maintenance",
        "cleanup", "hardware", "network", "winget", "boost", "ui", "logs", "rollback"
    ];

    /// <summary>Suítes que alteram o sistema: com perda de conexão no fim, o autoteste desfaz tudo na hora.</summary>
    private static readonly HashSet<string> Mutating = ["tweaks", "power", "startup", "defender", "task", "windowsupdate", "boost", "winget"];

    /// <summary>Serviços do acesso remoto e da rede: nunca tocados (regra 2 do PROMPT-TESTES).</summary>
    public static readonly HashSet<string> ForbiddenServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "TermService", "UmRdpService", "SessionEnv", "Dnscache", "Dhcp", "NlaSvc", "LanmanWorkstation", "Netman", "wlansvc", "Tcpip", "RemoteAccess"
    };

    /// <summary>Só dry-run: DNS/IPv6/rede, hibernação, serviços em massa e OneDrive (arquivos do usuário).</summary>
    public static readonly HashSet<string> DryRunOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "net.prefer-ipv4", "net.disable-ipv6", "power.hibernate-off", "power.hibernate-on", ServicesManual.TweakId, "debloat.onedrive-sync-off"
    };

    /// <summary>Exigem reinício para valer (regra 3): o ciclo aplicar/reverter sem reiniciar não prova nada.</summary>
    public static readonly HashSet<string> NeedsReboot = new(StringComparer.OrdinalIgnoreCase)
    {
        "gpu.hags", "privacy.recall-feature", "pref.detailed-bsod", "windows.rtc-utc", "net.prefer-ipv4", "net.disable-ipv6"
    };

    public static readonly HashSet<string> PowerTweaks = new(StringComparer.OrdinalIgnoreCase)
    {
        "power.plan", PowerPlanAdvancedTweak.TweakId, PowerPlanMaximumTweak.TweakId
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Logger _logger;
    private readonly CommandLineOptions _options;
    private readonly string _reportPath;
    private readonly string _workDir;
    private readonly BackupStore _store;
    private readonly TweakContext _ctx;
    private readonly TweakRegistry _registry;
    private readonly List<string> _log = [];
    private readonly SelfTestReport _report = new();
    private string _suite = string.Empty;

    public SelfTestRunner(Logger logger, CommandLineOptions options)
    {
        _logger = logger;
        _options = options;
        _suite = options.Suite ?? string.Empty;
        _reportPath = Path.GetFullPath(options.ReportPath ?? Path.Combine(AppPaths.LogDirectory, $"selftest-{_suite}-{DateTime.Now:yyyyMMdd-HHmmss}.json"));
        _workDir = Path.GetDirectoryName(_reportPath)!;
        Directory.CreateDirectory(_workDir);
        _store = new BackupStore(Path.Combine(_workDir, "selftest-backup.json"));
        _ctx = new TweakContext(logger, _store, options.DryRun);
        _registry = TweakRegistry.CreateDefault(_ctx);
        logger.EntryLogged += e => { lock (_log) _log.Add(e.ToString()); };
    }

    public async Task<(int Exit, string Summary)> RunAsync()
    {
        if (!Suites.Contains(_suite))
            return (2, $"Suíte desconhecida '{_suite}'. Use --suite com: {string.Join(", ", Suites)}.");
        if (!AdminCheck.IsAdministrator()) return (2, "O autoteste precisa ser executado como administrador.");
        if (_suite != "ui" && Mutex.TryOpenExisting(@"Local\GueibsOptimizer.SingleInstance", out var open))
        {
            open.Dispose();
            return (2, "Feche o Gueibs Optimizer antes de rodar o autoteste.");
        }
        if (Mutating.Contains(_suite) && _suite != "winget" && _store.All.Count > 0)
            return (2, $"O backup do autoteste ({_store.FilePath}) tem {_store.All.Count} entrada(s) pendente(s). Rode --suite rollback antes.");

        _report.Version = typeof(SelfTestRunner).Assembly.GetName().Version?.ToString(3) ?? "?";
        _report.Suite = _suite;
        _report.Started = DateTime.Now;
        _report.Windows = $"{(OsInfo.IsWindows11 ? "Windows 11" : "Windows 10")} build {OsInfo.Build}";
        _report.BackupFile = _store.FilePath;
        _logger.Info($"Autoteste iniciado: suíte {_suite}{(_options.DryRun ? " (simulação)" : string.Empty)}.");

        try
        {
            await (_suite switch
            {
                "snapshot" => SnapshotSuiteAsync(),
                "restorepoint" => RestorePointSuiteAsync(),
                "tweaks" => TweaksSuiteAsync(),
                "power" => PowerSuiteAsync(),
                "startup" => StartupSuiteAsync(),
                "defender" => DefenderSuiteAsync(),
                "task" => TaskSuiteAsync(),
                "windowsupdate" => WindowsUpdateSuiteAsync(),
                "maintenance" => MaintenanceSuiteAsync(),
                "cleanup" => CleanupSuiteAsync(),
                "hardware" => HardwareSuiteAsync(),
                "network" => NetworkSuiteAsync(),
                "winget" => WingetSuiteAsync(),
                "boost" => BoostSuiteAsync(),
                "ui" => UiSuiteAsync(),
                "logs" => LogsSuiteAsync(),
                _ => RollbackSuiteAsync()
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _report.Results.Add(new SelfTestResult { Suite = _suite, Test = "(suíte)", Status = SelfTestStatus.Falhou, Detail = $"Exceção: {ex.GetType().Name}: {ex.Message}" });
        }

        // Regra 5: conectividade depois de cada suíte; sem rede, desfaz tudo o que o autoteste guardou.
        _report.ConnectivityOk = await PingOkAsync().ConfigureAwait(false);
        if (_report.ConnectivityOk == false && Mutating.Contains(_suite))
        {
            await Test("emergencia.rollback", async r =>
            {
                r.Fail("Sem conexão (ping 1.1.1.1) depois da suíte: rollback imediato.");
                await RollbackCoreAsync(r).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        if (_options.BaselinePath is { } baselinePath && _suite != "snapshot")
            await CompareWithBaselineAsync(baselinePath).ConfigureAwait(false);

        _report.Finished = DateTime.Now;
        _report.Summary = _report.Results.GroupBy(r => r.Status.ToString()).ToDictionary(g => g.Key, g => g.Count());
        await File.WriteAllTextAsync(_reportPath, JsonSerializer.Serialize(_report, Json)).ConfigureAwait(false);
        _logger.Info($"Autoteste {_suite} concluído: {string.Join(", ", _report.Summary.Select(kv => $"{kv.Key}={kv.Value}"))}.");

        var sb = new StringBuilder();
        sb.AppendLine($"Autoteste {_suite}: {string.Join(", ", _report.Summary.Select(kv => $"{kv.Key}={kv.Value}"))}; conexão {(_report.ConnectivityOk == true ? "ok" : "FALHOU")}.");
        foreach (var r in _report.Results.Where(r => r.Status is SelfTestStatus.Falhou or SelfTestStatus.Atencao))
            sb.AppendLine($"  [{r.Status}] {r.Test}: {r.Detail}");
        sb.AppendLine($"Relatório: {_reportPath}");
        bool failed = _report.Results.Any(r => r.Status == SelfTestStatus.Falhou) || _report.ConnectivityOk == false;
        return (failed ? 1 : 0, sb.ToString());
    }

    // ================= Infraestrutura =================

    private async Task Test(string name, Func<SelfTestResult, Task> body, TimeSpan? timeout = null)
    {
        var r = new SelfTestResult { Suite = _suite, Test = name };
        lock (_log) _log.Clear();
        var sw = Stopwatch.StartNew();
        try
        {
            var task = body(r);
            var finished = await Task.WhenAny(task, Task.Delay(timeout ?? TimeSpan.FromMinutes(10))).ConfigureAwait(false);
            if (finished != task) r.Fail($"Tempo esgotado ({(timeout ?? TimeSpan.FromMinutes(10)).TotalMinutes:0} min).");
            else await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            r.Fail($"Exceção: {ex.GetType().Name}: {ex.Message}");
        }
        r.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
        lock (_log) r.Log = _log.Take(300).ToList();
        _report.Results.Add(r);
    }

    private void Skip(string name, SelfTestStatus status, string reason) =>
        _report.Results.Add(new SelfTestResult { Suite = _suite, Test = name, Status = status, Detail = reason });

    private void SaveText(string fileName, string content)
    {
        try { File.WriteAllText(Path.Combine(_workDir, fileName), content, new UTF8Encoding(false)); }
        catch (IOException) { }
    }

    private static async Task<bool> PingOkAsync()
    {
        using var ping = new Ping();
        int ok = 0;
        for (int i = 0; i < 3; i++)
        {
            try { if ((await ping.SendPingAsync("1.1.1.1", 2000).ConfigureAwait(false)).Status == IPStatus.Success) ok++; }
            catch (PingException) { }
        }
        return ok > 0;
    }

    private async Task CompareWithBaselineAsync(string baselinePath)
    {
        await Test("baseline.comparacao", async r =>
        {
            var baseline = LoadSnapshot(baselinePath);
            if (baseline is null) { r.Fail($"Não foi possível ler o baseline {baselinePath}."); return; }
            var now = await StateSnapshot.TakeAsync(_ctx, _registry).ConfigureAwait(false);
            var diff = StateSnapshot.Diff(baseline, now).ToList();
            _report.BaselineDiff = diff;
            var real = diff.Where(d => !d.Volatile).ToList();
            r.Evidence["diferencas"] = real.Count;
            r.Evidence["diferencas_volateis"] = diff.Count - real.Count;
            if (real.Count > 0) r.Fail($"{real.Count} diferença(s) não voláteis em relação ao baseline (ver BaselineDiff).");
        }).ConfigureAwait(false);
    }

    public static SortedDictionary<string, string>? LoadSnapshot(string path)
    {
        try
        {
            var report = JsonSerializer.Deserialize<SelfTestReport>(File.ReadAllText(path));
            return report?.Snapshot;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    // ================= Snapshot / ponto de restauração =================

    private async Task SnapshotSuiteAsync()
    {
        await Test("snapshot", async r =>
        {
            var snap = await StateSnapshot.TakeAsync(_ctx, _registry).ConfigureAwait(false);
            _report.Snapshot = snap;
            r.Evidence["entradas"] = snap.Count;
            r.Evidence["registro"] = snap.Keys.Count(k => k.StartsWith("reg:", StringComparison.Ordinal));
            r.Evidence["servicos"] = snap.Keys.Count(k => k.StartsWith("svc:", StringComparison.Ordinal));
            r.Evidence["tarefas"] = snap.Keys.Count(k => k.StartsWith("task:", StringComparison.Ordinal));
            r.Evidence["planos"] = snap.Where(kv => kv.Key.StartsWith("power:scheme:", StringComparison.Ordinal)).Select(kv => $"{kv.Key[13..]} {kv.Value}").ToList();
            r.Evidence["appx"] = snap.GetValueOrDefault("appx:count");
            r.Evidence["defender"] = snap.GetValueOrDefault("defender:exclusions");
            r.Evidence["pontos_restauracao"] = snap.GetValueOrDefault("restorepoints:count");
            r.Evidence["windows_update"] = snap.GetValueOrDefault("wu:state");

            var (_, listRaw) = await StateSnapshot.ListSchemesAsync(_ctx.Shell).ConfigureAwait(false);
            SaveText($"{Path.GetFileNameWithoutExtension(_reportPath)}-powercfg-list.txt", listRaw);
            var active = PowerCfgParser.ParseList(listRaw).FirstOrDefault(s => s.Active);
            if (active is not null)
                SaveText($"{Path.GetFileNameWithoutExtension(_reportPath)}-powercfg-qh-ativo.txt", (await StateSnapshot.QueryAsync(_ctx.Shell, active.Guid).ConfigureAwait(false)).Raw);

            if (_options.BaselinePath is { } b && LoadSnapshot(b) is { } baseline)
            {
                var diff = StateSnapshot.Diff(baseline, snap).ToList();
                _report.BaselineDiff = diff;
                var real = diff.Where(d => !d.Volatile).ToList();
                r.Evidence["diferencas"] = real.Count;
                r.Evidence["diferencas_volateis"] = diff.Count - real.Count;
                if (real.Count > 0) r.Fail($"{real.Count} diferença(s) não voláteis em relação ao baseline.");
            }
        }).ConfigureAwait(false);
    }

    private async Task RestorePointSuiteAsync()
    {
        await Test("restorepoint.criar", async r =>
        {
            var before = await RestorePointReader.ReadAsync().ConfigureAwait(false);
            var result = await new RestorePointService(_ctx.Shell, _logger).CreateAsync("Gueibs Optimizer - autoteste").ConfigureAwait(false);
            var after = await RestorePointReader.ReadAsync().ConfigureAwait(false);
            r.Evidence["antes"] = before.Text;
            r.Evidence["depois"] = after.Text;
            r.Evidence["mensagem"] = result.Message;
            if (!result.Success) r.Fail(result.Message);
            else if (after.Count <= before.Count && after.Last == before.Last) r.Warn("O app informou sucesso, mas nenhum ponto novo apareceu no WMI.");
        }).ConfigureAwait(false);
    }

    // ================= Diário de valores brutos =================

    private sealed class Original
    {
        public required RegHive Hive { get; init; }
        public required string Path { get; init; }
        public required string Name { get; init; }
        public RegistryValueData? Value { get; init; }
        public string? CreatedKeyPath { get; init; }
    }

    /// <summary>Guarda o valor ORIGINAL (antes de qualquer escrita) de cada local tocado, observando o backup.json do autoteste.</summary>
    private sealed class RawJournal : IDisposable
    {
        private readonly BackupStore _store;
        private readonly HashSet<Guid> _seen = [];
        public Dictionary<string, Original> Registry { get; } = new(StringComparer.OrdinalIgnoreCase);

        public RawJournal(BackupStore store)
        {
            _store = store;
            foreach (var e in store.All) _seen.Add(e.Id);
            store.Changed += OnChanged;
        }

        public void Declare(RegistryTweak reg, RegHive hive, string path, string name) =>
            Registry.TryAdd(StateSnapshot.RegKey(hive, path, name), new Original { Hive = hive, Path = path, Name = name, Value = reg.Read(hive, path, name) });

        private void OnChanged()
        {
            foreach (var e in _store.All)
            {
                if (!_seen.Add(e.Id) || e.Kind != BackupKind.Registry || e.KeyPath is null || e.ValueName is null) continue;
                string key = StateSnapshot.RegKey(e.Hive, e.KeyPath, e.ValueName);
                // Já declarado antes (lido antes de tudo): só completa a chave criada, se houver.
                if (Registry.TryGetValue(key, out var known))
                {
                    if (known.CreatedKeyPath is null && e.CreatedKeyPath is not null)
                        Registry[key] = new Original { Hive = known.Hive, Path = known.Path, Name = known.Name, Value = known.Value, CreatedKeyPath = e.CreatedKeyPath };
                    continue;
                }
                Registry[key] = new Original
                {
                    Hive = e.Hive, Path = e.KeyPath, Name = e.ValueName,
                    Value = e.ValueExisted ? e.PreviousValue : null, CreatedKeyPath = e.CreatedKeyPath
                };
            }
        }

        public void Dispose() => _store.Changed -= OnChanged;
    }

    private static IEnumerable<(RegHive Hive, string Path, string Name)> DeclaredLocations(ITweak t)
    {
        if (t is RegistrySettingsTweak rs)
            foreach (var s in rs.RegistryLocations) yield return (s.Hive, s.KeyPath, s.ValueName);
        if (t is DeclarativeTweak d)
        {
            foreach (var r in d.Definition.Registry)
                yield return (r.Hive == "HKLM" ? RegHive.LocalMachine : RegHive.CurrentUser, r.Path, r.Name);
            foreach (var s in d.Definition.Services)
            {
                yield return (RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\" + s.Name, "Start");
                yield return (RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\" + s.Name, "DelayedAutostart");
            }
        }
    }

    private static IReadOnlyList<string> TasksOf(ITweak t) => t switch
    {
        DeclarativeTweak d => d.Definition.ScheduledTasks.Select(x => x.Path).ToList(),
        CompatibilityTasksTweak => CompatibilityTasksTweak.TaskList.Select(x => $@"{x.Folder}\{x.Name}").ToList(),
        _ => []
    };

    private static IReadOnlyList<string> ServicesOf(ITweak t) => t switch
    {
        DeclarativeTweak d => d.Definition.Services.Select(s => s.Name).ToList(),
        TelemetryServicesTweak => CompatibilityTasksTweak.TelemetryServiceNames,
        _ => []
    };

    private static (string Folder, string Name) SplitTask(string full)
    {
        int i = full.LastIndexOf('\\');
        return (i <= 0 ? "\\" : full[..i], full[(i + 1)..]);
    }

    /// <summary>Estado bruto antes do ciclo: registro (declarado + diário), tarefas e serviços.</summary>
    private sealed class RawState
    {
        public required RawJournal Journal { get; init; }
        public Dictionary<string, bool?> Tasks { get; } = [];
        public Dictionary<string, string> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private RawState Capture(IEnumerable<ITweak> tweaks)
    {
        var state = new RawState { Journal = new RawJournal(_store) };
        var services = StateSnapshot.ReadServiceStates();
        foreach (var t in tweaks)
        {
            foreach (var (h, p, n) in DeclaredLocations(t)) state.Journal.Declare(_ctx.Registry, h, p, n);
            foreach (var task in TasksOf(t))
            {
                var (f, n) = SplitTask(task);
                state.Tasks.TryAdd(task, TaskSchedulerCom.IsEnabled(f, n));
            }
            foreach (var s in ServicesOf(t))
                if (services.TryGetValue(s, out var st)) state.Services.TryAdd(s, st);
        }
        return state;
    }

    private Dictionary<string, string> ReadCurrent(RawState state)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, o) in state.Journal.Registry) map[key] = StateSnapshot.Format(_ctx.Registry.Read(o.Hive, o.Path, o.Name));
        foreach (var task in state.Tasks.Keys)
        {
            var (f, n) = SplitTask(task);
            map["task:" + task] = Describe(TaskSchedulerCom.IsEnabled(f, n));
        }
        var services = StateSnapshot.ReadServiceStates();
        foreach (var s in state.Services.Keys) map["svcstatus:" + s] = services.GetValueOrDefault(s, "?");
        return map;
    }

    private static string Describe(bool? enabled) => enabled switch { true => "habilitada", false => "desabilitada", _ => "(não existe)" };

    private static Dictionary<string, string> OriginalMap(RawState state)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, o) in state.Journal.Registry) map[key] = StateSnapshot.Format(o.Value);
        foreach (var (task, enabled) in state.Tasks) map["task:" + task] = Describe(enabled);
        foreach (var (s, st) in state.Services) map["svcstatus:" + s] = st;
        return map;
    }

    private static List<string> Differences(Dictionary<string, string> original, Dictionary<string, string> current) =>
        original.Where(kv => current.GetValueOrDefault(kv.Key) != kv.Value)
                .Select(kv => $"{kv.Key}: {kv.Value} → {current.GetValueOrDefault(kv.Key) ?? "?"}")
                .ToList();

    /// <summary>Volta cada local ao valor original EXATO (registro pelo próprio RegistryTweak.Restore; tarefas; serviços).</summary>
    private async Task<int> RestoreExactAsync(RawState state)
    {
        const string FixId = "selftest.fix";
        int fixes = 0;
        foreach (var (_, o) in state.Journal.Registry)
        {
            var current = _ctx.Registry.Read(o.Hive, o.Path, o.Name);
            bool keyLeft = o.CreatedKeyPath is not null && _ctx.Registry.KeyExists(o.Hive, o.CreatedKeyPath);
            if (StateSnapshot.Format(current) == StateSnapshot.Format(o.Value) && !keyLeft) continue;
            _store.AddIfAbsent(new BackupEntry
            {
                TweakId = FixId, Kind = BackupKind.Registry, Hive = o.Hive, KeyPath = o.Path, ValueName = o.Name,
                ValueExisted = o.Value is not null, PreviousValue = o.Value, CreatedKeyPath = o.CreatedKeyPath,
                Description = $"autoteste: restaurar {o.Path}\\{o.Name}"
            });
            fixes++;
        }
        if (fixes > 0) _ctx.Registry.Restore(FixId);
        _store.RemoveTweak(FixId);

        foreach (var (task, enabled) in state.Tasks)
        {
            if (enabled is not { } want) continue;
            var (f, n) = SplitTask(task);
            if (TaskSchedulerCom.IsEnabled(f, n) is { } now && now != want)
            {
                TaskSchedulerCom.SetEnabled(f, n, want);
                fixes++;
            }
        }

        var services = StateSnapshot.ReadServiceStates();
        foreach (var (name, wanted) in state.Services)
        {
            if (ForbiddenServices.Contains(name) || services.GetValueOrDefault(name) == wanted) continue;
            if (wanted == "Running") await _ctx.Shell.RunMutatingAsync($"autoteste: iniciar {name}", "sc.exe", ["start", name]).ConfigureAwait(false);
            else if (wanted == "Stopped") await _ctx.Shell.RunMutatingAsync($"autoteste: parar {name}", "sc.exe", ["stop", name]).ConfigureAwait(false);
            fixes++;
        }
        return fixes;
    }

    // ================= Suíte tweaks =================

    public static (SelfTestStatus Status, string Reason)? SkipReason(ITweak t, bool forBoost = false)
    {
        if (!forBoost && PowerTweaks.Contains(t.Id)) return (SelfTestStatus.Pulado, "testado na suíte power");
        if (t.Risk == TweakRisk.High) return (SelfTestStatus.Pulado, "risco Alto (regra 4)");
        if (DryRunOnly.Contains(t.Id)) return (SelfTestStatus.Pulado, "somente dry-run (rede, hibernação, serviços em massa ou OneDrive)");
        if (NeedsReboot.Contains(t.Id) || MentionsReboot(t)) return (SelfTestStatus.Pulado, "exige reinício");
        if (ServicesOf(t).Any(ForbiddenServices.Contains)) return (SelfTestStatus.Pulado, "toca serviço protegido (acesso remoto/rede)");
        if (DeclaredLocations(t).Any(l => ForbiddenServices.Any(s => l.Path.Contains(@"Services\" + s, StringComparison.OrdinalIgnoreCase))))
            return (SelfTestStatus.Pulado, "toca chave de serviço protegido (acesso remoto/rede)");
        if (t is DeclarativeTweak d && d.Definition.Commands.Count > 0) return (SelfTestStatus.Pulado, "comando de sistema: somente dry-run");
        return null;
    }

    private static bool MentionsReboot(ITweak t)
    {
        string text;
        try { text = (t.Notice ?? string.Empty) + " " + t.Info.Notes; }
        catch (Exception) { text = t.Notice ?? string.Empty; }
        return text.Contains("Requer reinício", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Exige reiniciar", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("próximo reinício", StringComparison.OrdinalIgnoreCase);
    }

    private async Task TweaksSuiteAsync()
    {
        foreach (var t in _registry.All)
        {
            if (SkipReason(t) is { } skip) { Skip(t.Id, skip.Status, skip.Reason); continue; }
            bool applicable;
            try { applicable = t.IsApplicable(); }
            catch (Exception ex) { Skip(t.Id, SelfTestStatus.Falhou, $"IsApplicable lançou {ex.GetType().Name}: {ex.Message}"); continue; }
            if (!applicable)
            {
                Skip(t.Id, SelfTestStatus.NaoAplicavel, OsInfo.RequirementText(t.Id) ?? "não aplicável nesta máquina/versão do Windows");
                continue;
            }
            await Test(t.Id, r => CycleAsync(t, r)).ConfigureAwait(false);
        }
    }

    private async Task CycleAsync(ITweak t, SelfTestResult r)
    {
        var state = Capture([t]);
        using var _ = state.Journal;
        r.Evidence["nome"] = t.Name;
        r.Evidence["risco"] = t.Risk.ToString();
        bool initial = t.IsApplied();
        r.Evidence["inicial_aplicado"] = initial;
        try
        {
            if (!initial)
            {
                await t.ApplyAsync().ConfigureAwait(false);
                bool applied = t.IsApplied();
                var afterApply = ReadCurrent(state);
                var changed = Differences(OriginalMap(state), afterApply);
                r.Evidence["apos_aplicar_aplicado"] = applied;
                r.Evidence["valores_apos_aplicar"] = changed;
                if (!applied) r.Fail("IsApplied continua falso depois de aplicar.");
                if (changed.Count == 0) r.Fail("Nenhum valor bruto mudou ao aplicar.");

                await t.RevertAsync().ConfigureAwait(false);
                bool still = t.IsApplied();
                r.Evidence["apos_reverter_aplicado"] = still;
                if (still) r.Fail("IsApplied continua verdadeiro depois de reverter.");
            }
            else
            {
                r.Evidence["modo"] = "inverso: já estava aplicado (reverter → conferir → aplicar de novo)";
                await t.RevertAsync().ConfigureAwait(false);
                bool still = t.IsApplied();
                var afterRevert = ReadCurrent(state);
                var changed = Differences(OriginalMap(state), afterRevert);
                r.Evidence["apos_reverter_aplicado"] = still;
                r.Evidence["valores_apos_reverter"] = changed;
                if (still) r.Fail("IsApplied continua verdadeiro depois de reverter.");
                if (changed.Count == 0) r.Fail("Nenhum valor bruto mudou ao reverter.");

                await t.ApplyAsync().ConfigureAwait(false);
                bool applied = t.IsApplied();
                r.Evidence["apos_reaplicar_aplicado"] = applied;
                if (!applied) r.Fail("IsApplied continua falso depois de aplicar de novo.");
            }
        }
        finally
        {
            var final = ReadCurrent(state);
            var leftovers = Differences(OriginalMap(state), final);
            bool exact = leftovers.Count == 0;
            r.Evidence["locais_verificados"] = OriginalMap(state).Count;
            if (leftovers.Count > 0)
            {
                r.Evidence["diferencas_no_fim"] = leftovers;
                int fixes = await RestoreExactAsync(state).ConfigureAwait(false);
                var again = Differences(OriginalMap(state), ReadCurrent(state));
                exact = again.Count == 0;
                r.Evidence["restaurado_pelo_autoteste"] = fixes;
                if (again.Count > 0)
                {
                    r.Evidence["diferencas_apos_restaurar"] = again;
                    r.Fail("O estado bruto NÃO voltou ao original, nem depois da restauração do autoteste.");
                }
                else if (initial)
                    r.Warn("Modo inverso: o desfazer sem backup volta ao padrão do Windows e o reaplicar grava o valor explicitamente; o resultado é equivalente, não idêntico. O autoteste restaurou o valor exato.");
                else
                    r.Fail("O desfazer do app não devolveu o valor bruto idêntico (o autoteste restaurou o exato).");
            }
            // Estado bruto igual ao original: as entradas que sobraram (ex.: backup do "aplicar de novo" no modo inverso, cujo
            // valor anterior é o padrão do Windows) não podem ficar, senão um rollback depois levaria o PC para o padrão.
            if (exact && _store.ForTweak(t.Id).Count > 0) _store.RemoveTweak(t.Id);
        }
    }

    // ================= Suíte power =================

    private async Task PowerSuiteAsync()
    {
        var plan = (PowerPlanTweak)_registry.Find("power.plan")!;
        var advanced = _registry.Find(PowerPlanAdvancedTweak.TweakId)!;
        var maximum = _registry.Find(PowerPlanMaximumTweak.TweakId)!;
        var shell = _ctx.Shell;

        var (schemes0, list0) = await StateSnapshot.ListSchemesAsync(shell).ConfigureAwait(false);
        SaveText("power-list-antes.txt", list0);
        var active0 = schemes0.FirstOrDefault(s => s.Active);
        if (active0 is null) { Skip("power.ciclo", SelfTestStatus.Falhou, "Não foi possível ler o plano ativo (powercfg /list)."); return; }
        var values0 = await StateSnapshot.QueryAsync(shell, active0.Guid).ConfigureAwait(false);
        SaveText("power-qh-ativo-antes.txt", values0.Raw);
        var ours0 = schemes0.Where(s => s.Name.Equals(PowerPlanTweak.PlanName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (ours0.Count > 1) { Skip("power.ciclo", SelfTestStatus.Falhou, $"Já existem {ours0.Count} planos \"{PowerPlanTweak.PlanName}\" antes do teste."); return; }

        string? renamed = null;
        const string TempName = "Gueibs Optimizer Performance (original, autoteste)";
        await Test("power.ciclo", async r =>
        {
            r.Evidence["plano_ativo_inicial"] = $"{active0.Name} ({active0.Guid})";
            r.Evidence["planos_iniciais"] = schemes0.Select(s => $"{s.Guid} {s.Name}{(s.Active ? " *" : "")}").ToList();
            try
            {
                // Para testar criação, "nunca duplicar" e exclusão sem perder o plano já existente, ele é renomeado
                // durante o teste (powercfg /changename sem descrição só muda o nome) e volta ao nome original no fim.
                if (ours0.Count == 1)
                {
                    renamed = ours0[0].Guid;
                    var ren = await shell.RunMutatingAsync("autoteste: renomear plano existente", "powercfg.exe", ["/changename", renamed, TempName]).ConfigureAwait(false);
                    if (!ren.Success) { r.Fail("Não foi possível renomear o plano existente; teste abortado sem alterações."); renamed = null; return; }
                    r.Note($"O plano existente {renamed} foi renomeado temporariamente.");
                }

                await plan.ApplyAsync().ConfigureAwait(false);
                var (s1, l1) = await StateSnapshot.ListSchemesAsync(shell).ConfigureAwait(false);
                SaveText("power-list-apos-aplicar.txt", l1);
                var created = s1.Where(s => s.Name.Equals(PowerPlanTweak.PlanName, StringComparison.OrdinalIgnoreCase)).ToList();
                r.Evidence["planos_apos_aplicar"] = s1.Select(s => $"{s.Guid} {s.Name}{(s.Active ? " *" : "")}").ToList();
                if (created.Count != 1) r.Fail($"Esperado 1 plano \"{PowerPlanTweak.PlanName}\", encontrados {created.Count}.");
                var mine = created.FirstOrDefault();
                if (mine is null) return;
                if (!mine.Active) r.Fail("O plano criado não ficou ativo.");
                if (!plan.IsApplied()) r.Fail("power.plan: IsApplied falso depois de aplicar.");

                await plan.ApplyAsync().ConfigureAwait(false);
                var (s2, _) = await StateSnapshot.ListSchemesAsync(shell).ConfigureAwait(false);
                int count2 = s2.Count(s => s.Name.Equals(PowerPlanTweak.PlanName, StringComparison.OrdinalIgnoreCase));
                r.Evidence["planos_com_o_nome_apos_2a_aplicacao"] = count2;
                if (count2 != 1) r.Fail($"Segunda aplicação: {count2} planos com o nome (esperado 1).");

                await advanced.ApplyAsync().ConfigureAwait(false);
                await maximum.ApplyAsync().ConfigureAwait(false);
                var q = await StateSnapshot.QueryAsync(shell, mine.Guid).ConfigureAwait(false);
                SaveText("power-qh-nosso-aplicado.txt", q.Raw);
                var checks = new (string Label, string Setting, uint Want)[]
                {
                    ("Desligar tela", "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e", 0),
                    ("Suspender", "29f6c1db-86da-48c5-9fdb-f2b67b1f44da", 0),
                    ("Hibernar após", "9d7815a6-7ee4-497e-8888-515a05f02364", 0),
                    ("Desligar disco", "6738e2c4-e8a5-4a42-b16a-e040e769756e", 0),
                    ("Processador mínimo", "893dee8e-2bef-41e0-89c6-b55d0929964c", 100),
                    ("Processador máximo", "bc5038f7-23e0-4960-96da-33abaf5935ec", 100),
                    ("USB suspensão seletiva", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", 0),
                    ("PCIe ASPM", "ee12f906-d277-404b-b6da-e5fa1a576df5", 0),
                    ("Wi-Fi economia", "12bbebe6-58d6-4636-95bb-3217ef867c1a", 0)
                };
                var lines = new List<string>();
                foreach (var (label, setting, want) in checks)
                {
                    if (!q.Values.TryGetValue(setting, out var v)) { lines.Add($"{label}: não existe nesta máquina"); continue; }
                    bool dcMatters = !PowerTierTweak.HasBattery.Value || label is "Desligar tela" or "Suspender" or "Hibernar após" or "Desligar disco";
                    bool ok = v.Ac == want && (!dcMatters || v.Dc == want);
                    lines.Add($"{label}: AC={v.Ac} DC={v.Dc} (esperado {want}) {(ok ? "ok" : "ERRADO")}");
                    if (!ok) r.Fail($"{label} não ficou em {want}.");
                }
                r.Evidence["valores_do_plano"] = lines;
                if (!advanced.IsApplied()) r.Fail("power.plan-advanced: IsApplied falso depois de aplicar.");
                if (!maximum.IsApplied()) r.Fail("power.plan-maximum: IsApplied falso depois de aplicar.");
                r.Evidence["c_states_intocados"] = q.Values.TryGetValue(PowerPlanSettings.ProcessorIdleDisable, out var idle) ? $"AC={idle.Ac} DC={idle.Dc}" : "configuração oculta/ausente";

                await maximum.RevertAsync().ConfigureAwait(false);
                await advanced.RevertAsync().ConfigureAwait(false);
                await plan.RevertAsync().ConfigureAwait(false);
                var (s3, l3) = await StateSnapshot.ListSchemesAsync(shell).ConfigureAwait(false);
                SaveText("power-list-apos-reverter.txt", l3);
                r.Evidence["planos_apos_reverter"] = s3.Select(s => $"{s.Guid} {s.Name}{(s.Active ? " *" : "")}").ToList();
                if (s3.Any(s => s.Guid == mine.Guid)) r.Fail("O plano criado pelo app não foi apagado ao reverter.");
                var active3 = s3.FirstOrDefault(s => s.Active);
                if (active3?.Guid != active0.Guid) r.Fail($"Depois de reverter, o ativo é {active3?.Guid} (esperado {active0.Guid}).");
            }
            finally
            {
                if (renamed is not null)
                {
                    var back = await shell.RunMutatingAsync("autoteste: devolver o nome do plano", "powercfg.exe", ["/changename", renamed, PowerPlanTweak.PlanName]).ConfigureAwait(false);
                    if (!back.Success) r.Fail($"ATENÇÃO: não foi possível devolver o nome do plano {renamed}.");
                }
                var (sEnd, lEnd) = await StateSnapshot.ListSchemesAsync(shell).ConfigureAwait(false);
                if (sEnd.FirstOrDefault(s => s.Active)?.Guid != active0.Guid)
                {
                    await shell.RunMutatingAsync("autoteste: reativar o plano original", "powercfg.exe", ["/setactive", active0.Guid]).ConfigureAwait(false);
                    r.Fail("O plano original precisou ser reativado pelo autoteste.");
                    (sEnd, lEnd) = await StateSnapshot.ListSchemesAsync(shell).ConfigureAwait(false);
                }
                SaveText("power-list-fim.txt", lEnd);
                var valuesEnd = await StateSnapshot.QueryAsync(shell, active0.Guid).ConfigureAwait(false);
                SaveText("power-qh-ativo-fim.txt", valuesEnd.Raw);
                var schemeDiff = schemes0.Select(s => $"{s.Guid}|{s.Name}|{s.Active}").Except(sEnd.Select(s => $"{s.Guid}|{s.Name}|{s.Active}"))
                    .Concat(sEnd.Select(s => $"{s.Guid}|{s.Name}|{s.Active}").Except(schemes0.Select(s => $"{s.Guid}|{s.Name}|{s.Active}"))).ToList();
                var valueDiff = values0.Values.Keys.Union(valuesEnd.Values.Keys)
                    .Where(k => values0.Values.GetValueOrDefault(k) != valuesEnd.Values.GetValueOrDefault(k))
                    .Select(k => $"{k}: {values0.Values.GetValueOrDefault(k)} → {valuesEnd.Values.GetValueOrDefault(k)}").ToList();
                r.Evidence["diferenca_planos_inicio_fim"] = schemeDiff;
                r.Evidence["diferenca_valores_plano_ativo"] = valueDiff;
                if (schemeDiff.Count > 0) r.Fail("A lista de planos no fim não é igual à do início.");
                if (valueDiff.Count > 0) r.Fail($"{valueDiff.Count} valor(es) do plano ativo diferentes do início.");
                foreach (var id in PowerTweaks) _store.RemoveTweak(id);
            }
        }).ConfigureAwait(false);
    }

    // ================= Suíte startup =================

    private async Task StartupSuiteAsync()
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "GueibsOptimizerSelfTest";
        const string Id = "selftest.startup";
        var service = new StartupService(_ctx);
        await Test("startup.ciclo", async r =>
        {
            var before = service.List().Where(i => i.Key != Name).Select(i => $"{i.Source}|{i.Key}|{i.Enabled}").OrderBy(x => x).ToList();
            r.Evidence["entradas_reais_antes"] = before.Count;
            if (_ctx.Registry.Read(RegHive.CurrentUser, RunKey, Name) is not null) { r.Fail("A entrada de teste já existia; abortado."); return; }
            try
            {
                _ctx.Registry.Set(Id, RegHive.CurrentUser, RunKey, Name, RegistryValueData.String(@"""C:\Windows\System32\cmd.exe"" /c exit"));
                var item = service.List().FirstOrDefault(i => i.Source == StartupSource.RunUser && i.Key == Name);
                if (item is null) { r.Fail("A entrada criada não apareceu na lista de inicialização."); return; }
                r.Evidence["listada"] = $"{item.Name} | {item.Command} | ativada={item.Enabled}";
                if (!item.Enabled) r.Fail("A entrada nova deveria aparecer ativada.");

                await service.DisableAsync(item).ConfigureAwait(false);
                var approved = _ctx.Registry.Read(RegHive.CurrentUser, StartupApproved.Base + @"\Run", Name);
                r.Evidence["startupapproved_desativado"] = StateSnapshot.Format(approved);
                var disabled = service.List().First(i => i.Key == Name);
                if (disabled.Enabled) r.Fail("Depois de desativar, a lista ainda mostra ativada.");
                if (_ctx.Registry.Read(RegHive.CurrentUser, RunKey, Name) is null) r.Fail("Desativar apagou o valor do Run (deveria só marcar no StartupApproved).");

                await service.EnableAsync(item).ConfigureAwait(false);
                var reenabled = service.List().First(i => i.Key == Name);
                r.Evidence["startupapproved_reativado"] = StateSnapshot.Format(_ctx.Registry.Read(RegHive.CurrentUser, StartupApproved.Base + @"\Run", Name));
                if (!reenabled.Enabled) r.Fail("Depois de reativar, a lista mostra desativada.");
            }
            finally
            {
                _ctx.Registry.Restore(Id);
                _store.RemoveTweak(Id);
                _ctx.Registry.Restore($"startup:{StartupSource.RunUser}:{Name}");
                bool runLeft = _ctx.Registry.Read(RegHive.CurrentUser, RunKey, Name) is not null;
                bool approvedLeft = _ctx.Registry.Read(RegHive.CurrentUser, StartupApproved.Base + @"\Run", Name) is not null;
                r.Evidence["sobrou_no_run"] = runLeft;
                r.Evidence["sobrou_no_startupapproved"] = approvedLeft;
                if (runLeft || approvedLeft) r.Fail("A entrada de teste não foi totalmente removida.");
                var after = service.List().Select(i => $"{i.Source}|{i.Key}|{i.Enabled}").OrderBy(x => x).ToList();
                if (!before.SequenceEqual(after)) r.Fail("As entradas reais de inicialização mudaram durante o teste.");
            }
        }).ConfigureAwait(false);
    }

    // ================= Suíte defender =================

    private async Task DefenderSuiteAsync()
    {
        var defender = new DefenderService(_ctx);
        var status = await defender.GetStatusAsync().ConfigureAwait(false);
        if (!status.Available || status.ManagedByPolicyOrThirdParty)
        {
            Skip("defender.exclusao", SelfTestStatus.NaoAplicavel, status.Message);
            return;
        }
        await Test("defender.exclusao", async r =>
        {
            r.Evidence["pasta"] = DefenderService.AppFolder;
            if (status.ExclusionPresent) { r.Note("A exclusão já existia; só leitura, nada foi alterado."); return; }
            var add = await defender.AddExclusionAsync().ConfigureAwait(false);
            if (add is not null) { r.Fail(add); return; }
            var mid = await defender.GetStatusAsync().ConfigureAwait(false);
            r.Evidence["presente_apos_adicionar"] = mid.ExclusionPresent;
            if (!mid.ExclusionPresent) r.Fail("Get-MpPreference não mostra a exclusão depois de adicionar.");
            var remove = await defender.RemoveExclusionAsync().ConfigureAwait(false);
            if (remove is not null) r.Fail(remove);
            var end = await defender.GetStatusAsync().ConfigureAwait(false);
            r.Evidence["presente_apos_remover"] = end.ExclusionPresent;
            if (end.ExclusionPresent) r.Fail("A exclusão continua depois de remover.");
        }).ConfigureAwait(false);
    }

    // ================= Suíte task =================

    private async Task TaskSuiteAsync()
    {
        var weekly = new WeeklyReviewService(_ctx.Shell, _logger);
        bool existed = TaskSchedulerCom.IsEnabled(@"\GueibsOptimizer", "RevisaoSemanal") is not null;
        await Test("task.criar-remover", async r =>
        {
            if (existed) { r.Status = SelfTestStatus.Pulado; r.Note("A tarefa já existia antes do teste: não foi alterada."); return; }
            if (!await weekly.EnableAsync().ConfigureAwait(false)) { r.Fail("schtasks /Create falhou."); return; }
            try
            {
                var state = TaskSchedulerCom.IsEnabled(@"\GueibsOptimizer", "RevisaoSemanal");
                r.Evidence["habilitada_apos_criar"] = state;
                if (state != true) r.Fail("A tarefa não aparece habilitada no Agendador.");
                var xml = await _ctx.Shell.RunAsync("schtasks.exe", ["/Query", "/TN", WeeklyReviewService.TaskName, "/XML"]).ConfigureAwait(false);
                bool hasArgs = xml.StdOut.Contains("--weekly", StringComparison.Ordinal);
                bool hasExe = xml.StdOut.Contains(WeeklyReviewService.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                r.Evidence["acao_tem_--weekly"] = hasArgs;
                r.Evidence["acao_aponta_para_o_exe"] = hasExe;
                if (!hasArgs || !hasExe) r.Fail("A ação da tarefa não é \"GueibsOptimizer.exe --weekly\".");
            }
            finally
            {
                await weekly.DisableAsync().ConfigureAwait(false);
                var gone = TaskSchedulerCom.IsEnabled(@"\GueibsOptimizer", "RevisaoSemanal") is null;
                r.Evidence["removida"] = gone;
                if (!gone) r.Fail("A tarefa não foi removida.");
            }
        }).ConfigureAwait(false);

        await Test("task.weekly-execucao", async r =>
        {
            // --weekly real limparia também C:\Windows\Temp e o cache de shaders; o roteiro só permite limpar %TEMP%.
            var exe = Environment.ProcessPath!;
            var started = DateTime.Now.AddSeconds(-1);
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add("--weekly");
            psi.ArgumentList.Add("--dry-run");
            using var p = Process.Start(psi)!;
            bool exited = await Task.Run(() => p.WaitForExit(90_000)).ConfigureAwait(false);
            if (!exited) { try { p.Kill(); } catch { } r.Fail("--weekly não terminou em 90 s."); return; }
            r.Evidence["codigo_saida"] = p.ExitCode;
            if (p.ExitCode != 0) r.Fail($"--weekly saiu com código {p.ExitCode}.");
            var line = ReadLogSince(started).LastOrDefault(l => l.Contains("Revisão semanal:", StringComparison.Ordinal));
            r.Evidence["linha_do_log"] = line;
            if (line is null) r.Fail("O log não registrou o relatório da revisão semanal.");
            r.Note("Executado com --dry-run (decisão: o --weekly real limpa mais do que %TEMP%).");
        }).ConfigureAwait(false);
    }

    private static List<string> ReadLogSince(DateTime since)
    {
        var path = Path.Combine(AppPaths.LogDirectory, "gueibsoptimizer.log");
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var lines = new List<string>();
            while (reader.ReadLine() is { } l)
                if (l.Length >= 19 && DateTime.TryParse(l[..19], out var t) && t >= since) lines.Add(l);
            return lines;
        }
        catch (IOException) { return []; }
    }

    // ================= Suíte windowsupdate =================

    private async Task WindowsUpdateSuiteAsync()
    {
        var wu = new WindowsUpdateModeService(_ctx);
        string Raw()
        {
            var parts = WindowsUpdateModeService.PolicyValues.Select(n => $"{n}={StateSnapshot.Format(_ctx.Registry.Read(RegHive.LocalMachine, WindowsUpdateModeService.PolicyKey, n))}")
                .Concat(WindowsUpdateModeService.PauseValues.Select(n => $"{n}={StateSnapshot.Format(_ctx.Registry.Read(RegHive.LocalMachine, WindowsUpdateModeService.UxKey, n))}"));
            return string.Join("; ", parts);
        }
        var baselineRaw = Raw();
        var baselineState = wu.ReadState();
        // Pausar primeiro: a pausa longa que já existe é trocada por outra pausa (o PC nunca fica sem pausa nesse passo).
        foreach (var mode in new[] { UpdateMode.Paused, UpdateMode.SecurityOnly, UpdateMode.Default })
        {
            await Test($"windowsupdate.{mode}", r =>
            {
                r.Evidence["estado_inicial"] = baselineState.Text;
                r.Evidence["bruto_inicial"] = baselineRaw;
                try
                {
                    wu.Apply(mode, 7);
                    var state = wu.ReadState();
                    r.Evidence["estado_aplicado"] = state.Text;
                    r.Evidence["bruto_aplicado"] = Raw();
                    if (state.Mode != mode) r.Fail($"Modo lido {state.Mode}, esperado {mode}.");
                    if (mode == UpdateMode.Paused && state.PausedUntilUtc is { } until && Math.Abs((until - DateTime.UtcNow.AddDays(7)).TotalHours) > 2)
                        r.Fail($"Pausa até {until:u}, esperado ~7 dias.");
                    if (mode == UpdateMode.SecurityOnly && state.DeferDays != WindowsUpdateModeService.SecurityDeferDays)
                        r.Fail($"Adiamento {state.DeferDays} dias, esperado {WindowsUpdateModeService.SecurityDeferDays}.");
                }
                finally
                {
                    wu.Restore();
                    _store.RemoveTweak(WindowsUpdateModeService.BackupId);
                    var raw = Raw();
                    r.Evidence["bruto_apos_restaurar"] = raw;
                    r.Evidence["estado_apos_restaurar"] = wu.ReadState().Text;
                    if (raw != baselineRaw) r.Fail("Os valores brutos não voltaram idênticos ao estado inicial.");
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }

    // ================= Suíte maintenance =================

    private async Task MaintenanceSuiteAsync()
    {
        var runner = new IntegrityToolRunner(_logger, dryRun: false);
        var commands = new[]
        {
            new IntegrityCommand(IntegrityTool.Dism, "DISM (só verificar)", "DISM.exe", ["/Online", "/Cleanup-Image", "/CheckHealth"], false),
            IntegrityCommands.For(IntegrityTool.ChkdskScan),
            new IntegrityCommand(IntegrityTool.Sfc, "SFC (só verificar)", "sfc.exe", ["/verifyonly"], true)
        };
        foreach (var cmd in commands)
        {
            await Test($"maintenance.{Path.GetFileNameWithoutExtension(cmd.FileName).ToLowerInvariant()}", async r =>
            {
                string executed = Path.GetFileNameWithoutExtension(cmd.FileName) + " " + string.Join(" ", cmd.Arguments);
                r.Evidence["comando_mostrado"] = cmd.Display;
                r.Evidence["comando_executado"] = executed;
                if (!cmd.Display.Equals(executed, StringComparison.OrdinalIgnoreCase)) r.Fail("O comando mostrado é diferente do executado.");
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var result = await runner.RunAsync(cmd, null, cts.Token).ConfigureAwait(false);
                r.Evidence["codigo_saida"] = result.ExitCode;
                r.Evidence["interpretacao"] = $"{result.Outcome}: {result.Summary}";
                var tail = IntegrityCommands.Normalize(result.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(8).ToList();
                r.Evidence["saida_final"] = tail;
                if (result.Outcome == IntegrityOutcome.Cancelled) r.Fail("Tempo esgotado (10 min).");
                else if (result.Outcome == IntegrityOutcome.Failed) r.Warn($"A ferramenta não concluiu: {result.Summary}");
            }, TimeSpan.FromMinutes(11)).ConfigureAwait(false);
        }
    }

    // ================= Suíte cleanup =================

    private async Task CleanupSuiteAsync()
    {
        var temp = Path.GetTempPath();
        await Test("cleanup.estimativa", r =>
        {
            foreach (var c in CleanupService.Categories().Where(c => c.Kind != CleanupKind.RecycleBin))
            {
                var e = CleanupService.Estimate(c);
                r.Evidence[c.Kind.ToString()] = $"{MemoryCleanResult.FormatBytes((ulong)e.Bytes)} em {e.Files} arquivo(s)";
            }
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        await Test("cleanup.temp-real", r =>
        {
            var category = CleanupService.Categories().First(c => c.Kind == CleanupKind.UserTemp);
            var before = MeasureDirectory(temp);
            var estimate = CleanupService.Estimate(category);
            var result = CleanupService.CleanDirectory(temp, dryRun: false, _logger);
            var after = MeasureDirectory(temp);
            long measured = before - after;
            r.Evidence["pasta"] = temp;
            r.Evidence["estimativa_do_app"] = estimate.Bytes;
            r.Evidence["medido_antes"] = before;
            r.Evidence["medido_depois"] = after;
            r.Evidence["liberado_informado"] = result.FreedBytes;
            r.Evidence["liberado_medido"] = measured;
            r.Evidence["arquivos_apagados"] = result.DeletedFiles;
            r.Evidence["arquivos_em_uso_ignorados"] = result.SkippedFiles;
            long tolerance = Math.Max(5L * 1024 * 1024, measured / 20);
            if (Math.Abs(result.FreedBytes - measured) > tolerance)
                r.Warn($"Liberado informado ({result.FreedBytes:N0} B) difere do medido ({measured:N0} B) além da tolerância (outros programas escrevem no %TEMP%).");
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        await Test("cleanup.ram", async r =>
        {
            var (avail0, total) = MemoryCleanerService.ReadMemory();
            var result = await new MemoryCleanerService(_logger, dryRun: false).CleanAsync().ConfigureAwait(false);
            var (avail1, _) = MemoryCleanerService.ReadMemory();
            r.Evidence["total"] = MemoryCleanResult.FormatBytes(total);
            r.Evidence["disponivel_antes_medido"] = MemoryCleanResult.FormatBytes(avail0);
            r.Evidence["disponivel_depois_medido"] = MemoryCleanResult.FormatBytes(avail1);
            r.Evidence["app_antes"] = MemoryCleanResult.FormatBytes(result.AvailableBefore);
            r.Evidence["app_depois"] = MemoryCleanResult.FormatBytes(result.AvailableAfter);
            r.Evidence["app_liberado"] = MemoryCleanResult.FormatBytes(result.Freed);
            r.Evidence["processos"] = result.ProcessesTrimmed;
            if (result.Simulated) r.Fail("A limpeza de RAM rodou simulada.");
            if (result.ProcessesTrimmed == 0) r.Fail("Nenhum processo foi aparado.");
            if (result.Total != total) r.Fail("O total de RAM informado difere do medido.");
            long gap = (long)avail1 - (long)result.AvailableAfter;
            if (Math.Abs(gap) > 512L * 1024 * 1024) r.Warn("A memória disponível medida logo depois difere mais de 512 MB da informada pelo app.");
        }).ConfigureAwait(false);
    }

    private static long MeasureDirectory(string root)
    {
        long total = 0;
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                    try { total += new FileInfo(f).Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                foreach (var d in Directory.EnumerateDirectories(dir))
                    if ((new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) == 0 && !CleanupService.IsProtected(root, d)) stack.Push(d);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }

    // ================= Suíte hardware =================

    private static List<Dictionary<string, object?>> Wmi(string cls, params string[] props)
    {
        var list = new List<Dictionary<string, object?>>();
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT {string.Join(",", props)} FROM {cls}");
            foreach (ManagementObject mo in s.Get())
                using (mo) list.Add(props.ToDictionary(p => p, p => (object?)mo[p]));
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException) { }
        return list;
    }

    private async Task HardwareSuiteAsync()
    {
        var info = new SystemInfoService(_logger);
        var summary = await info.CollectAsync().ConfigureAwait(false);

        await Test("hardware.comparacao-wmi", r =>
        {
            var cpu = Wmi("Win32_Processor", "Name", "NumberOfCores", "NumberOfLogicalProcessors").FirstOrDefault();
            string wmiCpu = cpu?["Name"]?.ToString()?.Trim() ?? "?";
            r.Evidence["cpu"] = $"app: {summary.Cpu?.Name} {summary.Cpu?.Cores}C/{summary.Cpu?.Threads}T | wmi: {wmiCpu} {cpu?["NumberOfCores"]}C/{cpu?["NumberOfLogicalProcessors"]}T";
            if (summary.Cpu is null || !summary.Cpu.Name.Trim().Equals(wmiCpu, StringComparison.OrdinalIgnoreCase)) r.Fail("Nome da CPU diferente do WMI.");
            if (summary.Cpu is { } c && (c.Cores != Convert.ToInt32(cpu?["NumberOfCores"]) || c.Threads != Convert.ToInt32(cpu?["NumberOfLogicalProcessors"]))) r.Fail("Núcleos/threads diferentes do WMI.");

            var sticks = Wmi("Win32_PhysicalMemory", "Capacity");
            ulong wmiRam = sticks.Aggregate(0UL, (s, m) => s + Convert.ToUInt64(m["Capacity"]));
            var cs = Wmi("Win32_ComputerSystem", "TotalPhysicalMemory").FirstOrDefault();
            r.Evidence["ram"] = $"app: {SystemSummary.Gb(summary.TotalMemoryBytes)} em {summary.Memory.Count} pente(s) | wmi: {SystemSummary.Gb(wmiRam)} em {sticks.Count} pente(s), visível ao SO {SystemSummary.Gb(Convert.ToUInt64(cs?["TotalPhysicalMemory"] ?? 0UL))}";
            if (summary.TotalMemoryBytes != wmiRam || summary.Memory.Count != sticks.Count) r.Fail("RAM total ou número de pentes diferente do WMI.");

            var gpus = Wmi("Win32_VideoController", "Name", "CurrentRefreshRate");
            r.Evidence["gpus"] = $"app: {string.Join(", ", summary.Gpus.Select(g => $"{g.Name} VRAM {(g.VramBytes is { } v ? SystemSummary.Gb(v) : "?")}"))} | wmi: {string.Join(", ", gpus.Select(g => g["Name"]))}";
            foreach (var g in summary.Gpus)
                if (!gpus.Any(w => string.Equals(w["Name"]?.ToString()?.Trim(), g.Name.Trim(), StringComparison.OrdinalIgnoreCase))) r.Fail($"GPU {g.Name} não aparece no WMI.");

            var board = Wmi("Win32_BaseBoard", "Manufacturer", "Product").FirstOrDefault();
            string wmiBoard = $"{board?["Manufacturer"]} {board?["Product"]}".Trim();
            r.Evidence["placa_mae"] = $"app: {summary.Motherboard} | wmi: {wmiBoard}";
            if (!summary.Motherboard.Equals(wmiBoard, StringComparison.OrdinalIgnoreCase)) r.Fail("Placa-mãe diferente do WMI.");

            var disks = Wmi("Win32_DiskDrive", "Model", "Size");
            r.Evidence["discos"] = $"app: {string.Join(", ", summary.Disks.Select(d => $"{d.Model} {d.Kind} {SystemSummary.Gb(d.SizeBytes)}"))} | wmi: {string.Join(", ", disks.Select(d => $"{d["Model"]} {SystemSummary.Gb(Convert.ToUInt64(d["Size"] ?? 0UL))}"))}";
            if (summary.Disks.Count != disks.Count) r.Warn("Número de discos diferente do Win32_DiskDrive (o app usa MSFT_PhysicalDisk).");

            var hz = gpus.Select(g => Convert.ToInt32(g["CurrentRefreshRate"] ?? 0)).Where(h => h > 0).ToList();
            r.Evidence["refresh"] = $"app: {string.Join(", ", summary.Monitors.Select(m => $"{m.CurrentHz} Hz (máx {m.MaxHzAtResolution})"))} | wmi: {string.Join(", ", hz)} Hz";
            // Win32_VideoController.CurrentRefreshRate é inteiro truncado (239,96 Hz vira 239); o app arredonda (240). ±1 Hz é a mesma taxa.
            if (summary.Monitors.Count > 0 && hz.Count > 0 && !summary.Monitors.Any(m => hz.Any(h => Math.Abs(h - m.CurrentHz) <= 1))) r.Fail("Taxa de atualização diferente do WMI.");
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        await Test("hardware.sensores", async r =>
        {
            using var monitor = new HardwareMonitorService(_logger);
            monitor.Acquire();
            HardwareSnapshot? snap = null;
            for (int i = 0; i < 20 && (snap = monitor.Latest) is null; i++) await Task.Delay(500).ConfigureAwait(false);
            await Task.Delay(2000).ConfigureAwait(false);
            snap = monitor.Latest ?? snap;
            monitor.Release();
            if (snap is null) { r.Fail("O LibreHardwareMonitor não devolveu leituras em 12 s."); return; }
            var h = HardwareSummary.From(snap);
            r.Evidence["leituras"] = snap.All.Count();
            r.Evidence["resumo"] = h;
            static bool In(float? v, float lo, float hi) => v is null || (v >= lo && v <= hi);
            if (!In(h.CpuTemp, 5, 110)) r.Fail($"Temperatura da CPU implausível: {h.CpuTemp}");
            if (!In(h.GpuTemp, 5, 110)) r.Fail($"Temperatura da GPU implausível: {h.GpuTemp}");
            if (!In(h.CpuLoad, 0, 100) || !In(h.GpuLoad, 0, 100) || !In(h.RamLoad, 0, 100)) r.Fail("Carga fora de 0–100%.");
            if (h.RamUsedGb is { } u && h.RamAvailableGb is { } a)
            {
                double totalGb = summary.TotalMemoryBytes / (1024.0 * 1024 * 1024);
                if (Math.Abs(u + a - totalGb) > totalGb * 0.1) r.Fail($"RAM usada+disponível ({u + a:0.0} GB) não bate com a física ({totalGb:0.0} GB).");
            }
            if (h.CpuTemp is null) r.Warn("Sem temperatura da CPU (sensor ausente).");
            if (h.GpuMemTotalMb is { } vram && summary.Gpus.FirstOrDefault(g => g.VramBytes is not null)?.VramBytes is { } appVram &&
                Math.Abs(vram - appVram / (1024.0 * 1024)) > 512) r.Warn($"VRAM do sensor ({vram:0} MB) difere da do registro ({appVram / (1024 * 1024)} MB).");
        }).ConfigureAwait(false);

        await Test("hardware.checkup", async r =>
        {
            var items = await new CheckupService(_registry, info, _ctx.Shell, _logger).RunAsync().ConfigureAwait(false);
            r.Evidence["itens"] = items.Select(i => $"[{i.Status}] {i.Title}: {i.Detail}").ToList();
            if (items.Count == 0) r.Fail("O Check-up não devolveu itens.");
        }).ConfigureAwait(false);

        await Test("hardware.exportacao-sem-seriais", async r =>
        {
            var details = await info.CollectDetailsAsync(summary).ConfigureAwait(false);
            var sections = PcDetailsLogic.Sections(details);
            var now = DateTime.Now;
            var txt = PcDetailsLogic.ToText(sections, false, now);
            var html = PcDetailsLogic.ToHtml(sections, false, now);
            var json = PcDetailsLogic.ToJson(sections, false, now);
            SaveText("meu-pc.txt", txt);
            SaveText("meu-pc.html", html);
            SaveText("meu-pc.json", json);
            var serials = Wmi("Win32_BaseBoard", "SerialNumber").Concat(Wmi("Win32_BIOS", "SerialNumber"))
                .Concat(Wmi("Win32_DiskDrive", "SerialNumber")).Concat(Wmi("Win32_PhysicalMemory", "SerialNumber"))
                .Select(d => d["SerialNumber"]?.ToString()?.Trim() ?? string.Empty)
                .Concat(Wmi("Win32_ComputerSystemProduct", "IdentifyingNumber", "UUID").SelectMany(d => new[] { d["IdentifyingNumber"]?.ToString() ?? "", d["UUID"]?.ToString() ?? "" }))
                .Select(s => s.Trim())
                .Where(s => s.Length >= 5 && !s.Contains("O.E.M", StringComparison.OrdinalIgnoreCase) && !s.Contains("Default string", StringComparison.OrdinalIgnoreCase)
                            && s.Trim('0', '-', 'F', 'f').Length > 0)
                .Distinct().ToList();
            r.Evidence["seriais_conhecidos"] = serials.Count;
            var leaked = serials.Where(s => txt.Contains(s, StringComparison.OrdinalIgnoreCase) || html.Contains(s, StringComparison.OrdinalIgnoreCase) || json.Contains(s, StringComparison.OrdinalIgnoreCase)).ToList();
            r.Evidence["seriais_vazados"] = leaked.Count;
            if (leaked.Count > 0) r.Fail($"{leaked.Count} número(s) de série aparecem na exportação padrão.");
            var withSerials = PcDetailsLogic.ToText(sections, true, now);
            r.Evidence["com_opcao_seriais_aparecem"] = serials.Count(s => withSerials.Contains(s, StringComparison.OrdinalIgnoreCase));
        }).ConfigureAwait(false);
    }

    // ================= Suíte network =================

    private async Task NetworkSuiteAsync()
    {
        var net = new NetworkTestService();
        foreach (var target in new[] { "1.1.1.1", "8.8.8.8", "9.9.9.9" })
        {
            await Test($"network.ping-{target}", async r =>
            {
                var s = await net.PingAsync(target, 20).ConfigureAwait(false);
                double loss = s.Sent == 0 ? 100 : 100.0 * (s.Sent - s.Received) / s.Sent;
                r.Evidence["resultado"] = $"enviados {s.Sent}, recebidos {s.Received}, perda {loss:0.#}%, mín {s.MinMs:0.#} / méd {s.AvgMs:0.#} / máx {s.MaxMs:0.#} ms, jitter {s.JitterMs:0.#} ms";
                if (s.Received == 0) r.Fail("Nenhuma resposta.");
                if (s.Received > 0 && !(s.AvgMs > 0 && s.MinMs <= s.AvgMs && s.AvgMs <= s.MaxMs)) r.Fail("Estatísticas incoerentes (mín ≤ méd ≤ máx, méd > 0).");
                if (loss is < 0 or > 100 || s.JitterMs < 0) r.Fail("Perda ou jitter fora do intervalo.");
            }).ConfigureAwait(false);
        }
        await Test("network.velocidade", async r =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var progress = new List<string>();
            var s = await net.SpeedTestAsync(new Progress<SpeedProgress>(p => { lock (progress) if (progress.Count < 50) progress.Add($"{p.Phase} {p.Percent:0}% {p.CurrentMbps:0.0} Mbps"); }), cts.Token).ConfigureAwait(false);
            r.Evidence["download_mbps"] = Math.Round(s.DownloadMbps, 1);
            r.Evidence["upload_mbps"] = Math.Round(s.UploadMbps, 1);
            lock (progress) r.Evidence["progresso_amostra"] = progress.Take(10).ToList();
            if (s.DownloadMbps <= 0) r.Fail("Download = 0.");
            if (s.UploadMbps <= 0) r.Fail("Upload = 0.");
        }, TimeSpan.FromMinutes(6)).ConfigureAwait(false);
    }

    // ================= Suíte winget =================

    private async Task WingetSuiteAsync()
    {
        var winget = new WingetService(_ctx.Shell, _logger);
        if (!await winget.IsAvailableAsync().ConfigureAwait(false)) { Skip("winget", SelfTestStatus.NaoAplicavel, "winget não encontrado."); return; }

        await Test("winget.catalogo", async r =>
        {
            var apps = CatalogService.Apps;
            var invalid = new List<string>();
            var md = new StringBuilder();
            md.AppendLine($"# Validação do catálogo ({DateTime.Now:yyyy-MM-dd HH:mm})");
            md.AppendLine();
            md.AppendLine("Cada ID foi consultado com `winget show --id <id> -e` (timeout de 60 s por ID, pausa de 300 ms).");
            md.AppendLine();
            md.AppendLine("| # | ID | Nome | Resultado |");
            md.AppendLine("|---|---|---|---|");
            int n = 0;
            foreach (var app in apps)
            {
                n++;
                var res = await _ctx.Shell.RunAsync("winget.exe", ["show", "--id", app.WingetId, "-e", "--accept-source-agreements", "--disable-interactivity"],
                    TimeSpan.FromSeconds(60), new UTF8Encoding(false)).ConfigureAwait(false);
                string verdict = res.ExitCode == 0 ? "encontrado"
                    : res.TimedOut ? "tempo esgotado"
                    : $"NÃO encontrado (0x{unchecked((uint)res.ExitCode):X8})";
                if (res.ExitCode != 0) invalid.Add($"{app.WingetId} ({verdict})");
                md.AppendLine($"| {n} | `{app.WingetId}` | {app.Name.Replace("|", "/")} | {verdict} |");
                await Task.Delay(300).ConfigureAwait(false);
            }
            md.AppendLine();
            md.AppendLine($"Total: {apps.Count}; inválidos: {invalid.Count}.");
            SaveText("catalog-validation.md", md.ToString());
            r.Evidence["total"] = apps.Count;
            r.Evidence["invalidos"] = invalid;
            if (invalid.Count > 0) r.Fail($"{invalid.Count} ID(s) não encontrados.");
        }, TimeSpan.FromMinutes(90)).ConfigureAwait(false);

        await Test("winget.7zip", async r =>
        {
            const string Id = "7zip.7zip";
            async Task<bool> Installed()
            {
                var l = await _ctx.Shell.RunAsync("winget.exe", ["list", "--id", Id, "-e", "--accept-source-agreements", "--disable-interactivity"],
                    TimeSpan.FromSeconds(90), new UTF8Encoding(false)).ConfigureAwait(false);
                return l.ExitCode == 0 && l.StdOut.Contains(Id, StringComparison.OrdinalIgnoreCase);
            }
            if (await Installed().ConfigureAwait(false)) { r.Status = SelfTestStatus.Pulado; r.Note("O 7-Zip já estava instalado: nada foi feito."); return; }
            var install = await winget.InstallAsync(Id).ConfigureAwait(false);
            r.Evidence["instalacao"] = $"{install.OutcomeText}: {install.Message} (código {install.ExitCode})";
            if (install.Outcome is not (InstallOutcome.Installed or InstallOutcome.RebootRequired)) { r.Fail("A instalação não foi concluída."); return; }
            bool present = await Installed().ConfigureAwait(false);
            r.Evidence["listado_apos_instalar"] = present;
            if (!present) r.Fail("winget list não mostra o 7-Zip depois de instalar.");
            var un = await winget.UninstallAsync(Id).ConfigureAwait(false);
            r.Evidence["desinstalacao"] = $"código {un.ExitCode}";
            bool gone = !await Installed().ConfigureAwait(false);
            r.Evidence["removido"] = gone;
            if (!gone) r.Fail("O 7-Zip continua instalado depois de desinstalar.");
        }, TimeSpan.FromMinutes(15)).ConfigureAwait(false);
    }

    // ================= Suíte boost =================

    private async Task BoostSuiteAsync()
    {
        var candidates = _registry.All.Where(t => TweakText.IsRecommended(t) && SkipReason(t, forBoost: true) is null).ToList();
        var excluded = _registry.All.Where(t => TweakText.IsRecommended(t) && SkipReason(t, forBoost: true) is { }).Select(t => $"{t.Id}: {SkipReason(t, true)!.Value.Reason}").ToList();

        await Test("boost.completo", async r =>
        {
            r.Evidence["fora_do_teste"] = excluded;
            var snapBefore = await StateSnapshot.TakeAsync(_ctx, _registry).ConfigureAwait(false);
            var (schemes0, _) = await StateSnapshot.ListSchemesAsync(_ctx.Shell).ConfigureAwait(false);
            var active0 = schemes0.FirstOrDefault(s => s.Active);
            var state = Capture(candidates);
            using var journalScope = state.Journal;
            var perf = new SystemSnapshotService(_logger, () => new StartupService(_ctx).List().Count(i => i.Enabled));
            var m0 = await perf.MeasureAsync().ConfigureAwait(false);
            var ids = candidates.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var boost = new BoostService(_registry, new ScoreService(_logger), new RestorePointService(_ctx.Shell, _logger), _logger, dryRun: false);
            // Sem ponto de restauração (limite de 24 h do Windows) o teste segue: o backup e o diário do autoteste cobrem o desfazer.
            var summary = await boost.RunAsync(_ => Task.FromResult(true), null, default, t => ids.Contains(t.Id)).ConfigureAwait(false);
            var m1 = await perf.MeasureAsync().ConfigureAwait(false);
            r.Evidence["pontuacao"] = $"{summary.ScoreBefore} → {summary.ScoreAfter}";
            r.Evidence["ponto_restauracao"] = summary.RestorePoint?.Message;
            r.Evidence["etapas"] = summary.Steps.Select(s => $"[{s.Status}] {s.Tweak.Id}: {s.Message}").ToList();
            r.Evidence["comparativo"] = SnapshotComparison.Rows(m0, m1).Select(x => $"{x.Label}: {x.Before} → {x.AfterText}").ToList();
            if (summary.FailedCount > 0) r.Fail($"{summary.FailedCount} ajuste(s) falharam no Boost.");
            if (summary.AppliedCount > 0 && summary.ScoreAfter <= summary.ScoreBefore) r.Fail("A pontuação não subiu apesar de ajustes aplicados.");

            // Rollback completo pelo próprio app (aba Restauração > Desfazer tudo).
            var errors = await new RestorationService(_registry, _store, _ctx.Registry, _logger).UndoAllAsync().ConfigureAwait(false);
            r.Evidence["erros_do_desfazer_tudo"] = errors;
            if (errors.Count > 0) r.Fail($"Desfazer tudo: {errors.Count} erro(s).");
            var scoreAfterUndo = new ScoreService(_logger).Evaluate(_registry.All).Score;
            r.Evidence["pontuacao_apos_desfazer"] = scoreAfterUndo;

            var (schemes1, _) = await StateSnapshot.ListSchemesAsync(_ctx.Shell).ConfigureAwait(false);
            if (active0 is not null && schemes1.FirstOrDefault(s => s.Active)?.Guid != active0.Guid)
            {
                r.Warn($"Depois do desfazer o plano ativo era {schemes1.FirstOrDefault(s => s.Active)?.Name}; o original ({active0.Name}) já estava ativo antes do Boost e o app volta ao Equilibrado quando não tem o plano anterior guardado. O autoteste reativou o original.");
                await _ctx.Shell.RunMutatingAsync("autoteste: reativar o plano original", "powercfg.exe", ["/setactive", active0.Guid]).ConfigureAwait(false);
            }
            var leftovers = Differences(OriginalMap(state), ReadCurrent(state));
            if (leftovers.Count > 0)
            {
                r.Evidence["diferencas_brutas_apos_desfazer"] = leftovers;
                int fixes = await RestoreExactAsync(state).ConfigureAwait(false);
                r.Evidence["restaurado_pelo_autoteste"] = fixes;
                r.Warn($"{leftovers.Count} valor(es) não voltaram idênticos com o Desfazer tudo; o autoteste restaurou o exato.");
            }
            var snapAfter = await StateSnapshot.TakeAsync(_ctx, _registry).ConfigureAwait(false);
            var diff = StateSnapshot.Diff(snapBefore, snapAfter).Where(d => !d.Volatile).ToList();
            r.Evidence["diferencas_vs_antes_do_boost"] = diff.Select(d => $"{d.Key}: {d.Before} → {d.After}").ToList();
            r.Evidence["entradas_restantes_no_backup"] = _store.All.Select(e => $"{e.TweakId}: {e.Description}").ToList();
            if (diff.Count > 0) r.Fail($"{diff.Count} diferença(s) em relação ao estado antes do Boost.");
            else foreach (var id in _store.All.Select(e => e.TweakId).Distinct().ToList()) _store.RemoveTweak(id);
        }, TimeSpan.FromMinutes(15)).ConfigureAwait(false);
    }

    // ================= Suítes ui, logs e rollback =================

    private async Task UiSuiteAsync()
    {
        await Test("ui.abrir-30s", async r =>
        {
            var started = DateTime.Now.AddSeconds(-1);
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            psi.ArgumentList.Add("--dry-run");
            using var p = Process.Start(psi)!;
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            p.Refresh();
            bool alive = !p.HasExited;
            r.Evidence["aberto_apos_30s"] = alive;
            r.Evidence["janela"] = alive ? p.MainWindowTitle : $"saiu com código {p.ExitCode}";
            if (!alive) r.Fail("O app fechou sozinho antes de 30 s.");
            else
            {
                p.CloseMainWindow();
                if (!await Task.Run(() => p.WaitForExit(15_000)).ConfigureAwait(false)) { p.Kill(); r.Warn("Não fechou pela janela em 15 s (encerrado)."); }
            }
            var errors = ReadLogSince(started).Where(l => l.Contains("[ERRO]", StringComparison.Ordinal)).ToList();
            r.Evidence["erros_no_log"] = errors;
            if (errors.Any(IsUnhandled)) r.Fail("Exceção não tratada no log durante a execução.");
            else if (errors.Count > 0) r.Warn($"{errors.Count} erro(s) tratados no log.");
        }, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
    }

    private static bool IsUnhandled(string line) =>
        line.Contains("Exceção fatal", StringComparison.Ordinal) ||
        line.Contains("Erro inesperado na interface", StringComparison.Ordinal) ||
        line.Contains("Exceção não observada", StringComparison.Ordinal);

    private Task LogsSuiteAsync() => Test("logs.excecoes", r =>
    {
        var files = Directory.Exists(AppPaths.LogDirectory) ? Directory.GetFiles(AppPaths.LogDirectory, "gueibsoptimizer.log*") : [];
        var unhandled = new List<string>();
        var errorLines = new List<string>();
        var context = new List<string>();
        foreach (var f in files)
        {
            try
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                var recent = new Queue<string>();
                while (reader.ReadLine() is { } l)
                {
                    if (l.Contains("[ERRO]", StringComparison.Ordinal))
                    {
                        errorLines.Add(l);
                        if (IsUnhandled(l))
                        {
                            unhandled.Add($"{Path.GetFileName(f)}: {l}");
                            context.Add($"--- contexto de {l[..Math.Min(19, l.Length)]} ---");
                            context.AddRange(recent);
                            context.Add(l);
                        }
                    }
                    recent.Enqueue(l);
                    if (recent.Count > 6) recent.Dequeue();
                }
            }
            catch (IOException) { }
        }
        r.Evidence["arquivos"] = files.Select(Path.GetFileName).ToList();
        r.Evidence["linhas_de_erro"] = errorLines.Count;
        r.Evidence["todas_as_linhas_de_erro"] = errorLines.TakeLast(30).ToList();
        r.Evidence["excecoes_nao_tratadas"] = unhandled.TakeLast(30).ToList();
        r.Evidence["contexto"] = context.TakeLast(60).ToList();
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        int recentUnhandled = unhandled.Count(u => u.Contains(today, StringComparison.Ordinal));
        if (recentUnhandled > 0) r.Fail($"{recentUnhandled} exceção(ões) não tratada(s) registradas hoje.");
        else if (unhandled.Count > 0) r.Warn($"{unhandled.Count} exceção(ões) não tratada(s) antigas no log (nenhuma hoje).");
        return Task.CompletedTask;
    });

    private Task RollbackSuiteAsync() => Test("rollback", RollbackCoreAsync);

    /// <summary>Desfaz tudo o que está no backup do autoteste (registro, tarefas, plano anterior).</summary>
    private async Task RollbackCoreAsync(SelfTestResult r)
    {
        r.Evidence["entradas_no_backup"] = _store.All.Count;
        var previous = _store.GetCustom("power.plan", "previous-scheme");
        foreach (var e in _store.All.Where(e => e.CustomKey?.StartsWith("task:", StringComparison.Ordinal) == true))
        {
            var (f, n) = SplitTask(e.CustomKey![5..]);
            TaskSchedulerCom.SetEnabled(f, n, e.CustomValue == "habilitada");
        }
        int restored = _ctx.Registry.RestoreAll();
        r.Evidence["valores_restaurados"] = restored;
        if (previous is not null)
            await _ctx.Shell.RunMutatingAsync("autoteste: reativar plano anterior", "powercfg.exe", ["/setactive", previous]).ConfigureAwait(false);
        foreach (var id in _store.All.Select(e => e.TweakId).Distinct().ToList()) _store.RemoveTweak(id);
    }
}
