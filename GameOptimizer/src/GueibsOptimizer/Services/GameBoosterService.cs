using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GueibsOptimizer.Core;

namespace GueibsOptimizer.Services;

/// <summary>
/// A cada 5 s procura processos de jogos (lista editável em games.json) e define prioridade Alta.
/// Nunca usa Realtime. Ao parar o serviço, devolve a prioridade original dos jogos ainda abertos.
/// </summary>
public sealed class GameBoosterService : IDisposable
{
    public static readonly string[] DefaultGames =
    [
        "cs2.exe", "valorant-win64-shipping.exe", "fortniteclient-win64-shipping.exe", "r5apex.exe",
        "league of legends.exe", "dota2.exe", "overwatch.exe", "rocketleague.exe", "gta5.exe", "gta5_enhanced.exe",
        "rdr2.exe", "eldenring.exe", "cod.exe", "cs2-win64-shipping.exe", "pubg-win64-shipping.exe",
        "tslgame.exe", "fifa23.exe", "fc24.exe", "fc25.exe", "witcher3.exe", "cyberpunk2077.exe",
        "minecraft.windows.exe", "javaw.exe", "robloxplayerbeta.exe", "deadlock.exe", "marvel-win64-shipping.exe",
        "bf2042.exe", "destiny2.exe", "warframe.x64.exe", "rainbowsix.exe", "eft.exe", "escapefromtarkov.exe"
    ];

    private readonly Logger _logger;
    private readonly bool _dryRun;
    private readonly string _listPath;
    private readonly Dictionary<int, ProcessPriorityClass> _boosted = [];
    private readonly object _sync = new();
    private Timer? _timer;

    public GameBoosterService(Logger logger, bool dryRun, string? listPath = null)
    {
        _logger = logger;
        _dryRun = dryRun;
        _listPath = listPath ?? Path.Combine(AppPaths.DataDirectory, "games.json");
    }

    public string ListPath => _listPath;
    public bool IsRunning => _timer is not null;

    public event Action<string>? GameBoosted;

    /// <summary>Normaliza a lista: minúsculas, sem duplicatas, com ".exe".</summary>
    public static IReadOnlySet<string> NormalizeList(IEnumerable<string> names) =>
        names.Select(n => n.Trim().ToLowerInvariant())
             .Where(n => n.Length > 0)
             .Select(n => n.EndsWith(".exe", StringComparison.Ordinal) ? n : n + ".exe")
             .ToHashSet();

    public static bool ShouldBoost(ProcessPriorityClass current) =>
        current is ProcessPriorityClass.Normal or ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle or ProcessPriorityClass.AboveNormal;

    public IReadOnlySet<string> LoadList()
    {
        try
        {
            if (!File.Exists(_listPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_listPath)!);
                File.WriteAllText(_listPath, JsonSerializer.Serialize(DefaultGames, new JsonSerializerOptions { WriteIndented = true }));
            }
            var names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_listPath)) ?? [];
            return NormalizeList(names);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.Warn($"Lista de jogos inválida ({_listPath}); usando a padrão. {ex.Message}");
            return NormalizeList(DefaultGames);
        }
    }

    /// <summary>Adiciona um .exe à lista. Retorna false se já estava (ou no modo simulação).</summary>
    public bool AddToList(string exeName) => EditList(exeName, add: true);

    /// <summary>Remove um .exe da lista. Retorna false se não estava (ou no modo simulação).</summary>
    public bool RemoveFromList(string exeName) => EditList(exeName, add: false);

    private bool EditList(string exeName, bool add)
    {
        var name = NormalizeList([exeName]).Single();
        var list = LoadList().ToList();
        bool present = list.Contains(name);
        if (add == present) return false;
        if (_dryRun)
        {
            _logger.Simulated($"Lista do Game Booster: {(add ? "adicionar" : "remover")} {name}");
            return false;
        }
        if (add) list.Add(name); else list.Remove(name);
        try
        {
            File.WriteAllText(_listPath, JsonSerializer.Serialize(list.Order().ToArray(), new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Não foi possível salvar a lista de jogos: {ex.Message}");
            return false;
        }
    }

    public void Start()
    {
        if (_timer is not null) return;
        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        _logger.Info("Game Booster ativado.");
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        lock (_sync)
        {
            foreach (var (pid, original) in _boosted)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (!_dryRun) p.PriorityClass = original;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // processo já fechou
                }
            }
            _boosted.Clear();
        }
        _logger.Info("Game Booster desativado.");
    }

    private void Tick()
    {
        var games = LoadList();
        var alive = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!games.Contains(process.ProcessName.ToLowerInvariant() + ".exe")) continue;
                    alive.Add(process.Id);
                    ProcessPriorityClass current;
                    lock (_sync)
                    {
                        if (_boosted.ContainsKey(process.Id)) continue;
                        current = process.PriorityClass;
                        if (!ShouldBoost(current)) continue;
                        _boosted[process.Id] = current;
                        if (_dryRun)
                            _logger.Simulated($"Prioridade Alta para {process.ProcessName} (PID {process.Id})");
                        else
                            process.PriorityClass = ProcessPriorityClass.High;
                    }
                    _logger.Info($"Game Booster: {process.ProcessName} com prioridade Alta (antes: {current}).");
                    GameBoosted?.Invoke(process.ProcessName);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // sem acesso (anti-cheat/processo protegido) ou processo encerrou: ignora
                }
            }
        }
        // Jogo fechou: não há o que restaurar, só esquecer o PID.
        lock (_sync)
            foreach (var pid in _boosted.Keys.Where(pid => !alive.Contains(pid)).ToList())
                _boosted.Remove(pid);
    }

    public void Dispose() => Stop();
}
