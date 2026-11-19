using System.Management;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services;

/// <summary>
/// Reconhece CPU, GPU, RAM e o tipo de PC (notebook/desktop) e monta o <see cref="HardwareProfile"/>.
/// Usa WMI/CIM, a VRAM do registro (via <see cref="SystemInfoService"/>) e, quando disponível,
/// o clock máximo observado pelo LibreHardwareMonitor.
/// </summary>
public sealed class HardwareDetectionService(SystemInfoService info, HardwareMonitorService? monitor, Logger logger)
{
    private HardwareProfile? _cached;

    // Win32_SystemEnclosure.ChassisTypes de portáteis (SMBIOS): Portable, Laptop, Notebook, Hand Held,
    // Docking Station, Sub Notebook, Convertible, Detachable, Tablet...
    private static readonly int[] LaptopChassis = [8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32];

    public static bool IsLaptopChassis(IEnumerable<int> chassisTypes) => chassisTypes.Any(LaptopChassis.Contains);

    /// <summary>Adaptadores virtuais/remotos não contam como placa de vídeo do PC.</summary>
    public static bool IsVirtualAdapter(string name) =>
        new[] { "Basic Display", "Basic Render", "Remote", "Virtual", "Parsec", "Meta Virtual", "Citrix", "VMware", "Hyper-V", "Mirage", "DisplayLink" }
            .Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    public Task<HardwareProfile> DetectAsync(bool refresh = false) =>
        !refresh && _cached is not null ? Task.FromResult(_cached) : Task.Run(Detect);

    private HardwareProfile Detect()
    {
        var summary = info.Collect();
        var cpuName = summary.Cpu?.Name ?? string.Empty;
        var cpu = HardwareNameParser.ParseCpu(cpuName);
        if (cpu.BaseClockMhz is null && summary.Cpu is { MaxClockMhz: > 0 } wmiCpu)
            cpu = cpu with { BaseClockMhz = wmiCpu.MaxClockMhz };

        var gpus = summary.Gpus.Where(g => !IsVirtualAdapter(g.Name)).ToList();
        var parsed = gpus.Select(g => (Info: g, Id: HardwareNameParser.ParseGpu(g.Name))).ToList();
        var main = parsed
            .OrderBy(p => p.Id.IsIntegrated)
            .ThenByDescending(p => p.Id.PerformanceIndex)
            .ThenByDescending(p => p.Info.VramBytes ?? 0)
            .FirstOrDefault();
        var gpu = main.Id ?? HardwareNameParser.ParseGpu(string.Empty);
        bool hybrid = parsed.Any(p => p.Id.IsIntegrated) && parsed.Any(p => !p.Id.IsIntegrated);

        bool laptop = SafeIsLaptop();
        ulong ram = summary.TotalMemoryBytes;
        int ramSpeed = summary.Memory.Select(m => m.ConfiguredMts).DefaultIfEmpty(0).Max();
        int threads = summary.Cpu?.Threads ?? Environment.ProcessorCount;
        int cores = summary.Cpu?.Cores ?? threads;

        var (tier, reasons) = PcTierClassifier.Classify(gpu, threads, ram);
        var recs = PcTierClassifier.Recommendations(cpu, threads, gpu, main.Info?.VramBytes, laptop, hybrid, ram);

        _cached = new HardwareProfile(cpu, cores, threads, BoostFromSensors(), gpu, main.Info?.VramBytes,
            main.Info?.Driver ?? string.Empty, hybrid, laptop, ram, ramSpeed, tier, reasons, recs);
        logger.Info($"PC reconhecido: {_cached.Summary} — nível {_cached.TierText}.");
        return _cached;
    }

    /// <summary>Maior clock de núcleo visto pelo LibreHardwareMonitor (exige o driver PawnIO); senão null.</summary>
    private int? BoostFromSensors()
    {
        var latest = monitor?.Latest;
        if (latest is null) return null;
        var max = latest.All
            .Where(r => r.Kind == HardwareKind.Cpu && r.SensorType == "Clock" && r.Sensor.StartsWith("Core", StringComparison.OrdinalIgnoreCase) && r.Value is > 0)
            .Select(r => r.Value!.Value)
            .DefaultIfEmpty(0)
            .Max();
        return max > 0 ? (int)Math.Round(max) : null;
    }

    private bool SafeIsLaptop()
    {
        try
        {
            var types = new List<int>();
            using (var s = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
                foreach (ManagementObject mo in s.Get())
                    using (mo)
                        if (mo["ChassisTypes"] is ushort[] arr) types.AddRange(arr.Select(x => (int)x));
            if (IsLaptopChassis(types)) return true;

            using var b = new ManagementObjectSearcher("SELECT Name FROM Win32_Battery");
            using var batteries = b.Get();
            return batteries.Count > 0;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            logger.Warn($"Não foi possível identificar se é notebook: {ex.Message}");
            return false;
        }
    }
}
