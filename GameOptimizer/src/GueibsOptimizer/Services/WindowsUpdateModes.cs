using System.Globalization;
using System.Management;
using GueibsOptimizer.Core;

namespace GueibsOptimizer.Services;

public enum UpdateMode
{
    Default,
    SecurityOnly,
    Paused
}

public sealed record UpdateModeState(UpdateMode Mode, DateTime? PausedUntilUtc, int DeferDays)
{
    public string Text => Mode switch
    {
        UpdateMode.Paused when PausedUntilUtc is { } d && d > DateTime.UtcNow.AddDays(WindowsUpdateModeService.MaxPauseDays + 1) =>
            $"Pausado até {d.ToLocalTime():dd/MM/yyyy} (pausa longa feita fora do app; recomendado: Padrão ou Somente segurança)",
        UpdateMode.Paused when PausedUntilUtc is { } d => $"Pausado até {d.ToLocalTime():dd/MM/yyyy}",
        UpdateMode.SecurityOnly => $"Somente segurança (atualizações de recursos adiadas {DeferDays} dias)",
        _ => "Padrão do Windows"
    };
}

/// <summary>
/// 9.7: três modos reversíveis do Windows Update. NUNCA desativa atualizações de forma permanente:
/// "Somente segurança" só adia atualizações de recursos (as de qualidade/segurança continuam) e a pausa tem limite de 35 dias,
/// o mesmo do app Configurações.
/// </summary>
public sealed class WindowsUpdateModeService(TweakContext ctx)
{
    public const string BackupId = "windowsupdate.mode";
    public const int MaxPauseDays = 35;
    public const int SecurityDeferDays = 365;

    public const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    public const string UxKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

    public static readonly string[] PolicyValues = ["DeferFeatureUpdates", "DeferFeatureUpdatesPeriodInDays"];
    public static readonly string[] PauseValues =
        ["PauseUpdatesExpiryTime", "PauseFeatureUpdatesStartTime", "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesStartTime", "PauseQualityUpdatesEndTime"];

    public static string FormatUtc(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static DateTime? ParseUtc(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public static int ClampPauseDays(int days) => Math.Clamp(days, 1, MaxPauseDays);

    /// <summary>Valores que cada modo grava (lógica pura, testada).</summary>
    public static IReadOnlyList<(string Key, string Name, RegistryValueData Value)> ValuesFor(UpdateMode mode, DateTime nowUtc, int pauseDays) => mode switch
    {
        UpdateMode.SecurityOnly =>
        [
            (PolicyKey, "DeferFeatureUpdates", RegistryValueData.DWord(1)),
            (PolicyKey, "DeferFeatureUpdatesPeriodInDays", RegistryValueData.DWord(SecurityDeferDays))
        ],
        UpdateMode.Paused => PauseEntries(nowUtc, nowUtc.AddDays(ClampPauseDays(pauseDays))),
        _ => []
    };

    private static IReadOnlyList<(string, string, RegistryValueData)> PauseEntries(DateTime start, DateTime end)
    {
        string s = FormatUtc(start), e = FormatUtc(end);
        return
        [
            (UxKey, "PauseUpdatesExpiryTime", RegistryValueData.String(e)),
            (UxKey, "PauseFeatureUpdatesStartTime", RegistryValueData.String(s)),
            (UxKey, "PauseFeatureUpdatesEndTime", RegistryValueData.String(e)),
            (UxKey, "PauseQualityUpdatesStartTime", RegistryValueData.String(s)),
            (UxKey, "PauseQualityUpdatesEndTime", RegistryValueData.String(e))
        ];
    }

    /// <summary>Estado real, lido do registro.</summary>
    public UpdateModeState ReadState(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var pause = ParseUtc(ctx.Registry.Read(RegHive.LocalMachine, UxKey, "PauseUpdatesExpiryTime")?.Text);
        if (pause is { } p && p > now) return new UpdateModeState(UpdateMode.Paused, p, 0);
        bool defer = ctx.Registry.Read(RegHive.LocalMachine, PolicyKey, "DeferFeatureUpdates")?.Number == 1;
        int days = (int)(ctx.Registry.Read(RegHive.LocalMachine, PolicyKey, "DeferFeatureUpdatesPeriodInDays")?.Number ?? 0);
        return defer && days > 0 ? new UpdateModeState(UpdateMode.SecurityOnly, null, days) : new UpdateModeState(UpdateMode.Default, null, 0);
    }

    /// <summary>Aplica um modo. Limpa os valores do app dos outros modos antes; tudo com backup para o desfazer.</summary>
    public void Apply(UpdateMode mode, int pauseDays = 7, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        foreach (var name in PolicyValues) DeleteIfPresent(PolicyKey, name);
        foreach (var name in PauseValues) DeleteIfPresent(UxKey, name);
        foreach (var (key, name, value) in ValuesFor(mode, now, pauseDays))
            ctx.Registry.Set(BackupId, RegHive.LocalMachine, key, name, value);
        ctx.Logger.Info($"Windows Update: modo {mode}{(mode == UpdateMode.Paused ? $" ({ClampPauseDays(pauseDays)} dias)" : string.Empty)}.");
    }

    private void DeleteIfPresent(string key, string name)
    {
        if (ctx.Registry.Read(RegHive.LocalMachine, key, name) is not null)
            ctx.Registry.DeleteValue(BackupId, RegHive.LocalMachine, key, name);
    }

    /// <summary>Desfaz tudo o que o app mudou (volta aos valores de antes).</summary>
    public int Restore() => ctx.Registry.Restore(BackupId);

    /// <summary>Última atualização instalada (Win32_QuickFixEngineering = Get-HotFix).</summary>
    public (string Id, DateTime? Date)? ReadLastUpdate()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering");
            var list = new List<(string, DateTime?)>();
            foreach (ManagementObject mo in s.Get())
                using (mo) list.Add((mo["HotFixID"]?.ToString() ?? "?", ParseHotfixDate(mo["InstalledOn"]?.ToString())));
            return list.Count == 0 ? null : list.OrderByDescending(x => x.Item2 ?? DateTime.MinValue).First();
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            ctx.Logger.Warn($"Windows Update: não foi possível ler o histórico: {ex.Message}");
            return null;
        }
    }

    /// <summary>InstalledOn vem como "M/d/yyyy" (formato dos EUA) ou, às vezes, como data hexadecimal.</summary>
    public static DateTime? ParseHotfixDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (DateTime.TryParseExact(text, ["M/d/yyyy", "MM/dd/yyyy", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ft) && ft > 0)
            try { return DateTime.FromFileTimeUtc(ft).ToLocalTime().Date; } catch (ArgumentOutOfRangeException) { }
        return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out d) ? d : null;
    }
}
