using System.IO;
using System.Text.Json;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using Microsoft.Win32;

namespace GueibsOptimizer.Services.Games;

/// <summary>
/// Encontra jogos instalados: Steam (libraryfolders.vdf + appmanifest), Epic (manifests .item),
/// outros launchers pelo registro de desinstalação, e jogos adicionados à mão (salvos em games-library.json).
/// Só lê arquivos e registro; nunca altera nada dos jogos.
/// </summary>
public sealed class GameLibraryService(Logger logger, string? manualListPath = null)
{
    private readonly string _manualPath = manualListPath ?? Path.Combine(AppPaths.DataDirectory, "games-library.json");

    private static readonly string[] LauncherPublishers = ["Riot Games", "Ubisoft", "Electronic Arts", "Blizzard", "Rockstar Games", "Activision"];
    private static readonly string[] NotGames =
        ["Launcher", "Battle.net", "EA app", "Ubisoft Connect", "Riot Client", "Riot Vanguard", "Social Club", "Origin", "Uplay", "Redistributable"];

    public Task<IReadOnlyList<GameEntry>> ScanAsync() => Task.Run<IReadOnlyList<GameEntry>>(Scan);

    public IReadOnlyList<GameEntry> Scan()
    {
        var games = new List<GameEntry>();
        Try("Steam", () => games.AddRange(ScanSteam()));
        Try("Epic", () => games.AddRange(ScanEpic()));
        Try("launchers", () => games.AddRange(ScanUninstallRegistry()));
        Try("lista manual", () => games.AddRange(LoadManual()));
        return games
            .GroupBy(g => g.Key)
            .Select(g => g.OrderBy(x => x.Source).First())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void Try(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException)
        {
            logger.Warn($"Meus Jogos: não foi possível ler {what}: {ex.Message}");
        }
    }

    public static string? SteamRoot()
    {
        var path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                   ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path.Replace('/', '\\'));
    }

    private static IEnumerable<GameEntry> ScanSteam()
    {
        var root = SteamRoot();
        if (root is null) yield break;
        var libraries = new List<string> { root };
        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf)) libraries.AddRange(VdfParser.LibraryPaths(File.ReadAllText(vdf)));

        foreach (var lib in libraries.Select(l => Path.GetFullPath(l)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var m = VdfParser.AppManifest(File.ReadAllText(acf));
                if (m is null) continue;
                // Ferramentas da Steam (redistribuíveis, Proton etc.) não são jogos.
                if (m.Value.Name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase) || m.Value.AppId == "228980") continue;
                var folder = Path.Combine(apps, "common", m.Value.InstallDir);
                if (!Directory.Exists(folder)) continue;
                yield return new GameEntry(m.Value.Name, GameSource.Steam, folder, MainExecutablePicker.PickInFolder(folder, m.Value.Name), m.Value.AppId);
            }
        }
    }

    private static IEnumerable<GameEntry> ScanEpic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            var m = EpicManifestParser.Parse(File.ReadAllText(item));
            if (m is null || m.LaunchExecutable is null || !Directory.Exists(m.InstallLocation)) continue;
            var location = Path.GetFullPath(m.InstallLocation);
            var declared = Path.GetFullPath(Path.Combine(location, m.LaunchExecutable));
            // O LaunchExecutable às vezes é um lançador (ex.: FortniteBootstrapper); o processo do jogo é outro.
            var picked = MainExecutablePicker.PickInFolder(location, m.DisplayName);
            var exe = File.Exists(declared) && !MainExecutablePicker.IsExcluded(declared) ? declared : picked ?? (File.Exists(declared) ? declared : null);
            if (picked is not null && picked.Contains("-Shipping", StringComparison.OrdinalIgnoreCase)) exe = picked;
            yield return new GameEntry(m.DisplayName, GameSource.Epic, location, exe);
        }
    }

    private static IEnumerable<GameEntry> ScanUninstallRegistry()
    {
        var roots = new (RegistryKey Hive, string Path)[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
        };
        foreach (var (hive, path) in roots)
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(sub);
                var publisher = app?.GetValue("Publisher") as string ?? string.Empty;
                var name = app?.GetValue("DisplayName") as string ?? string.Empty;
                var location = app?.GetValue("InstallLocation") as string ?? string.Empty;
                if (!LauncherPublishers.Any(p => publisher.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
                if (name.Length == 0 || NotGames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))) continue;
                location = location.Trim().Trim('"');
                if (location.Length > 0) location = Path.GetFullPath(location);
                if (location.Length == 0 || !Directory.Exists(location)) continue;
                yield return new GameEntry(name, GameSource.Launcher, location, MainExecutablePicker.PickInFolder(location, name));
            }
        }
    }

    // --- Jogos adicionados pelo usuário ---

    private sealed record ManualGame(string Name, string Folder, string? Executable);

    public IReadOnlyList<GameEntry> LoadManual()
    {
        if (!File.Exists(_manualPath)) return [];
        var list = JsonSerializer.Deserialize<List<ManualGame>>(File.ReadAllText(_manualPath)) ?? [];
        return list.Where(g => Directory.Exists(g.Folder))
                   .Select(g => new GameEntry(g.Name, GameSource.Manual, g.Folder, g.Executable))
                   .ToList();
    }

    /// <summary>Adiciona a partir de uma pasta (procura o .exe principal) ou de um .exe escolhido.</summary>
    public GameEntry AddManual(string path)
    {
        GameEntry entry;
        if (File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var folder = Path.GetDirectoryName(path)!;
            entry = new GameEntry(Path.GetFileNameWithoutExtension(path), GameSource.Manual, folder, path);
        }
        else if (Directory.Exists(path))
        {
            entry = new GameEntry(new DirectoryInfo(path).Name, GameSource.Manual, path, MainExecutablePicker.PickInFolder(path));
        }
        else
        {
            throw new FileNotFoundException("Pasta ou executável não encontrado.", path);
        }

        var list = File.Exists(_manualPath)
            ? JsonSerializer.Deserialize<List<ManualGame>>(File.ReadAllText(_manualPath)) ?? []
            : [];
        list.RemoveAll(g => string.Equals(g.Folder, entry.Folder, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(g.Executable, entry.Executable, StringComparison.OrdinalIgnoreCase));
        list.Add(new ManualGame(entry.Name, entry.Folder, entry.Executable));
        Directory.CreateDirectory(Path.GetDirectoryName(_manualPath)!);
        File.WriteAllText(_manualPath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        logger.Info($"Meus Jogos: adicionado {entry.Name} ({entry.Executable ?? entry.Folder}).");
        return entry;
    }
}
