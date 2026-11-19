using System.IO;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services.Tweaks;
using Microsoft.Win32;

namespace GueibsOptimizer.Services.SelfTest;

public sealed record SnapshotDiff(string Key, string? Before, string? After, bool Volatile);

/// <summary>
/// Foto SOMENTE LEITURA do estado que o app pode alterar: registro (chaves dos ajustes e subárvores relacionadas),
/// serviços, planos de energia, tarefas, inicialização, Defender, Appx, pontos de restauração e Windows Update.
/// Fica num dicionário plano "tipo:caminho" → valor para a comparação ser simples e exata.
/// </summary>
public static class StateSnapshot
{
    private static readonly (RegHive Hive, string Path, bool Recursive)[] Trees =
    [
        (RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", false),
        (RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", false),
        (RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved", true),
        (RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", false),
        (RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", false),
        (RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", false),
        (RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", false),
        (RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", false),
        (RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false),
        (RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", false),
        (RegHive.CurrentUser, @"Software\Microsoft\GameBar", false),
        (RegHive.CurrentUser, @"System\GameConfigStore", false),
        (RegHive.CurrentUser, @"Control Panel\Mouse", false),
        (RegHive.CurrentUser, @"Control Panel\Keyboard", false),
        (RegHive.CurrentUser, @"Control Panel\Accessibility", true),
        (RegHive.CurrentUser, @"Control Panel\Desktop", false),
        (RegHive.CurrentUser, @"Software\Policies", true),
        (RegHive.CurrentUser, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}", true),
        (RegHive.CurrentUser, @"Environment", false),
        (RegHive.LocalMachine, @"SOFTWARE\Policies", true),
        (RegHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", true),
        (RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", false),
        (RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", false),
        (RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\CrashControl", false),
        (RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation", false),
        (RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", false),
        (RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", false),
        (RegHive.LocalMachine, @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", false),
        (RegHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", false),
        (RegHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false),
        (RegHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved", true)
    ];

    public static string Format(RegistryValueData? v) => v is null
        ? "(não existe)"
        : $"{v.Kind}:{v.Text ?? v.Number?.ToString() ?? v.Base64 ?? (v.Lines is null ? string.Empty : string.Join("|", v.Lines))}";

    public static string RegKey(RegHive hive, string path, string name) =>
        $@"reg:{RegistryTweak.HiveName(hive)}\{path}\{name}".ToLowerInvariant();

    /// <summary>Lê a foto completa (registro, serviços, energia, tarefas, inicialização, Defender, Appx, restauração, WU).</summary>
    public static async Task<SortedDictionary<string, string>> TakeAsync(TweakContext ctx, TweakRegistry registry)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (hive, path, recursive) in Trees) AddTree(ctx, map, hive, path, recursive, depth: 0);
        foreach (var (hive, path, name) in TweakLocations(registry))
            map[RegKey(hive, path, name)] = Format(ctx.Registry.Read(hive, path, name));

        AddServices(ctx, map);
        await AddPowerAsync(ctx, map).ConfigureAwait(false);
        await AddTasksAsync(ctx, map).ConfigureAwait(false);
        foreach (var item in new StartupService(ctx).List())
            map[$"startup:{item.Source}:{item.Key}".ToLowerInvariant()] = item.Enabled ? "ativado" : "desativado";

        var defender = await ctx.Shell.RunPowerShellAsync(
            "$p = Get-MpPreference -ErrorAction SilentlyContinue; if ($p) { 'EXCL=' + (@($p.ExclusionPath) -join ';') } else { 'SEM-DEFENDER' }",
            TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        map["defender:exclusions"] = defender.Success ? defender.StdOut.Trim() : $"erro {defender.ExitCode}";

        var appx = await ctx.Shell.RunPowerShellAsync("@(Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue).Count",
            TimeSpan.FromSeconds(120)).ConfigureAwait(false);
        map["appx:count"] = appx.Success ? appx.StdOut.Trim() : $"erro {appx.ExitCode}";

        var points = await RestorePointReader.ReadAsync().ConfigureAwait(false);
        map["restorepoints:count"] = points.Error ?? points.Count.ToString();
        map["restorepoints:last"] = points.Last?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
        map["wu:state"] = new WindowsUpdateModeService(ctx).ReadState().Mode.ToString();
        return map;
    }

    /// <summary>Todos os valores de registro declarados pelos ajustes (nativos e do tweaks.json).</summary>
    public static IEnumerable<(RegHive Hive, string Path, string Name)> TweakLocations(TweakRegistry registry)
    {
        foreach (var t in registry.All)
        {
            if (t is RegistrySettingsTweak rs)
                foreach (var s in SafeLocations(rs)) yield return (s.Hive, s.KeyPath, s.ValueName);
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
    }

    private static IReadOnlyList<RegSetting> SafeLocations(RegistrySettingsTweak t)
    {
        try { return t.RegistryLocations; }
        catch (Exception) { return []; }
    }

    private static void AddTree(TweakContext ctx, SortedDictionary<string, string> map, RegHive hive, string path, bool recursive, int depth)
    {
        try
        {
            using var baseKey = RegistryTweak.OpenBase(hive);
            using var key = baseKey.OpenSubKey(path, writable: false);
            if (key is null) { map[$@"key:{RegistryTweak.HiveName(hive)}\{path}".ToLowerInvariant()] = "(não existe)"; return; }
            map[$@"key:{RegistryTweak.HiveName(hive)}\{path}".ToLowerInvariant()] = "existe";
            foreach (var name in key.GetValueNames())
                map[RegKey(hive, path, name.Length == 0 ? "(padrão)" : name)] = Format(ctx.Registry.Read(hive, path, name));
            if (!recursive || depth >= 6) return;
            foreach (var sub in key.GetSubKeyNames())
                AddTree(ctx, map, hive, path + "\\" + sub, recursive, depth + 1);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            map[$@"key:{RegistryTweak.HiveName(hive)}\{path}".ToLowerInvariant()] = "(sem acesso)";
        }
    }

    private static void AddServices(TweakContext ctx, SortedDictionary<string, string> map)
    {
        foreach (var (name, state) in ReadServiceStates())
        {
            string root = @"SYSTEM\CurrentControlSet\Services\" + name;
            var start = ctx.Registry.Read(RegHive.LocalMachine, root, "Start")?.Number;
            var delayed = ctx.Registry.Read(RegHive.LocalMachine, root, "DelayedAutostart")?.Number;
            map[$"svc:{name}".ToLowerInvariant()] = $"Start={start?.ToString() ?? "-"};Delayed={delayed?.ToString() ?? "-"}";
            map[$"svcstatus:{name}".ToLowerInvariant()] = state;
        }
    }

    /// <summary>Nome e estado (Running/Stopped...) de cada serviço, via Win32_Service.</summary>
    public static IReadOnlyDictionary<string, string> ReadServiceStates()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name, State FROM Win32_Service");
            foreach (System.Management.ManagementObject mo in searcher.Get())
                using (mo)
                    if (mo["Name"] is string n) result[n] = mo["State"]?.ToString() ?? "?";
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or System.Runtime.InteropServices.COMException) { }
        return result;
    }

    public static async Task<(IReadOnlyList<PowerScheme> Schemes, string ListRaw)> ListSchemesAsync(Shell shell)
    {
        var r = await shell.RunAsync("powercfg.exe", ["/list"]).ConfigureAwait(false);
        return (r.Success ? PowerCfgParser.ParseList(r.StdOut) : [], r.StdOut);
    }

    public static async Task<(IReadOnlyDictionary<string, (uint Ac, uint Dc)> Values, string Raw)> QueryAsync(Shell shell, string scheme)
    {
        var r = await shell.RunAsync("powercfg.exe", ["/qh", scheme], TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return (r.Success ? PowerPlanSettings.ParseQuery(r.StdOut) : new Dictionary<string, (uint, uint)>(), r.StdOut);
    }

    private static async Task AddPowerAsync(TweakContext ctx, SortedDictionary<string, string> map)
    {
        var (schemes, _) = await ListSchemesAsync(ctx.Shell).ConfigureAwait(false);
        foreach (var s in schemes) map[$"power:scheme:{s.Guid}"] = s.Name + (s.Active ? " *" : string.Empty);
        foreach (var s in schemes.Where(s => s.Active || s.Name.Equals(PowerPlanTweak.PlanName, StringComparison.OrdinalIgnoreCase)))
        {
            var (values, _) = await QueryAsync(ctx.Shell, s.Guid).ConfigureAwait(false);
            foreach (var (setting, v) in values) map[$"power:value:{s.Guid}:{setting}"] = $"AC={v.Ac};DC={v.Dc}";
        }
    }

    private static async Task AddTasksAsync(TweakContext ctx, SortedDictionary<string, string> map)
    {
        var r = await ctx.Shell.RunAsync("schtasks.exe", ["/query", "/fo", "csv", "/nh"], TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (!r.Success) { map["task:(erro)"] = $"código {r.ExitCode}"; return; }
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cols = line.Split("\",\"");
            if (cols.Length < 3) continue;
            string name = cols[0].Trim('"');
            if (!name.StartsWith('\\')) continue;
            string status = cols[^1].Trim('"');
            bool disabled = status.StartsWith("Disabled", StringComparison.OrdinalIgnoreCase) ||
                            status.StartsWith("Desabilitad", StringComparison.OrdinalIgnoreCase) ||
                            status.StartsWith("Deshabilitad", StringComparison.OrdinalIgnoreCase);
            map[$"task:{name}".ToLowerInvariant()] = disabled ? "desabilitada" : "habilitada";
        }
    }

    /// <summary>Diferenças entre duas fotos. Estado de execução de serviços e pontos de restauração mudam sozinhos (voláteis).</summary>
    public static IReadOnlyList<SnapshotDiff> Diff(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        var keys = before.Keys.Union(after.Keys).OrderBy(k => k, StringComparer.Ordinal);
        var list = new List<SnapshotDiff>();
        foreach (var k in keys)
        {
            before.TryGetValue(k, out var b);
            after.TryGetValue(k, out var a);
            if (b == a) continue;
            list.Add(new SnapshotDiff(k, b, a, IsVolatile(k)));
        }
        return list;
    }

    public static bool IsVolatile(string key) =>
        key.StartsWith("svcstatus:", StringComparison.Ordinal) ||
        key.StartsWith("restorepoints:", StringComparison.Ordinal) ||
        key.Contains(@"\contentdeliverymanager\", StringComparison.Ordinal) && key.Contains("timestamp", StringComparison.Ordinal);
}
