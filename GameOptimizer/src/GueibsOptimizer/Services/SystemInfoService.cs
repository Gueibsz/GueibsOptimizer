using System.IO;
using System.Management;
using System.Text;
using GueibsOptimizer.Core;
using Microsoft.Win32;

namespace GueibsOptimizer.Services;

public sealed record CpuInfo(string Name, int Cores, int Threads, int MaxClockMhz);
public sealed record GpuInfo(string Name, string Driver, string DriverDate, ulong? VramBytes)
{
    /// <summary>Adaptador virtual (Parsec, área de trabalho remota, VM...): vai para o fim da lista e é marcado.</summary>
    public bool IsVirtual => HardwareDetectionService.IsVirtualAdapter(Name);
    public string DisplayName => IsVirtual ? $"{Name} (adaptador virtual)" : Name;
}
public sealed record MemoryModule(string Locator, string Bank, ulong CapacityBytes, int SpeedMts, int ConfiguredMts, string Manufacturer, string PartNumber, int SmbiosType);
public sealed record DiskInfo(string Model, string Kind, ulong SizeBytes);
public sealed record VolumeInfo(string Name, string Label, ulong TotalBytes, ulong FreeBytes);

public sealed record SystemSummary(
    CpuInfo? Cpu,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<MemoryModule> Memory,
    string Motherboard,
    string Bios,
    IReadOnlyList<DiskInfo> Disks,
    IReadOnlyList<VolumeInfo> Volumes,
    string Windows,
    IReadOnlyList<MonitorMode> Monitors)
{
    public ulong TotalMemoryBytes => Memory.Aggregate(0UL, (sum, m) => sum + m.CapacityBytes);

    public static string Gb(ulong bytes) => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB";

    public static string MemoryTypeText(int smbiosType) => smbiosType switch
    {
        24 => "DDR3",
        26 => "DDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        30 => "LPDDR4",
        _ => "RAM"
    };

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Resumo do PC (Gueibs Optimizer) ===");
        if (Cpu is { } c) sb.AppendLine($"CPU: {c.Name} — {c.Cores} núcleos / {c.Threads} threads, até {c.MaxClockMhz} MHz");
        foreach (var g in Gpus)
            sb.AppendLine($"GPU: {g.DisplayName} — VRAM {(g.VramBytes is { } v ? Gb(v) : "desconhecida")}, driver {g.Driver} ({g.DriverDate})");
        if (Memory.Count > 0)
        {
            var first = Memory[0];
            sb.AppendLine($"RAM: {Gb(TotalMemoryBytes)} {MemoryTypeText(first.SmbiosType)} em {Memory.Count} pente(s), {first.ConfiguredMts} MT/s configurado ({first.SpeedMts} nominal)");
            foreach (var m in Memory)
                sb.AppendLine($"  • {m.Locator}: {Gb(m.CapacityBytes)} {m.Manufacturer} {m.PartNumber}".TrimEnd());
        }
        sb.AppendLine($"Placa-mãe: {Motherboard}");
        sb.AppendLine($"BIOS: {Bios}");
        foreach (var d in Disks) sb.AppendLine($"Disco: {d.Model} ({d.Kind}, {Gb(d.SizeBytes)})");
        foreach (var v in Volumes) sb.AppendLine($"Unidade {v.Name} {v.Label}: {Gb(v.FreeBytes)} livres de {Gb(v.TotalBytes)}");
        sb.AppendLine($"Windows: {Windows}");
        foreach (var m in Monitors)
            sb.AppendLine($"Monitor{(m.Primary ? " (principal)" : "")}: {m.Width}x{m.Height} @ {m.CurrentHz} Hz (máx. {m.MaxHzAtResolution} Hz)");
        return sb.ToString();
    }
}

/// <summary>Coleta de informações do PC via WMI/CIM e registro. Cada parte falha de forma isolada.</summary>
public sealed partial class SystemInfoService(Logger logger)
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public Task<SystemSummary> CollectAsync() => Task.Run(Collect);

    public SystemSummary Collect() => new(
        Safe(ReadCpu, null),
        Safe(ReadGpus, []),
        Safe(ReadMemory, []),
        Safe(() => Query("Win32_BaseBoard", "Manufacturer", "Product").Select(r => $"{r[0]} {r[1]}".Trim()).FirstOrDefault() ?? "—", "—"),
        Safe(() => Query("Win32_BIOS", "Manufacturer", "SMBIOSBIOSVersion", "ReleaseDate")
            .Select(r => $"{r[0]} {r[1]} ({FormatWmiDate(r[2])})".Trim()).FirstOrDefault() ?? "—", "—"),
        Safe(ReadDisks, []),
        Safe(ReadVolumes, []),
        Safe(ReadWindows, "—"),
        Safe(DisplayInfo.Monitors, []));

    public IReadOnlyList<MemoryModule> ReadMemory() =>
        Query("Win32_PhysicalMemory", "DeviceLocator", "BankLabel", "Capacity", "Speed", "ConfiguredClockSpeed", "Manufacturer", "PartNumber", "SMBIOSMemoryType")
            .Select(r => new MemoryModule(Str(r[0]), Str(r[1]), ToULong(r[2]), ToInt(r[3]), ToInt(r[4]), Str(r[5]), Str(r[6]), ToInt(r[7])))
            .ToList();

    public string ReadCpuName() => ReadCpu()?.Name ?? string.Empty;

    /// <summary>
    /// Nomes dos dispositivos do sistema com driver fornecido pela AMD (pacote AMD Chipset Software: PSP, GPIO, SMBus, PPM...).
    /// Drivers genéricos que vêm com o Windows têm a Microsoft como fornecedora e não entram.
    /// </summary>
    public IReadOnlyList<string> ReadAmdSystemDrivers() =>
        Query("Win32_PnPSignedDriver", "DeviceName", "DeviceClass", "DriverProviderName")
            .Where(r => Str(r[1]).Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) && IsAmdProvider(Str(r[2])))
            .Select(r => Str(r[0]))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool IsAmdProvider(string provider) =>
        provider.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) ||
        provider.Trim().Equals("AMD", StringComparison.OrdinalIgnoreCase);

    private static CpuInfo? ReadCpu() =>
        Query("Win32_Processor", "Name", "NumberOfCores", "NumberOfLogicalProcessors", "MaxClockSpeed")
            .Select(r => new CpuInfo(Str(r[0]), ToInt(r[1]), ToInt(r[2]), ToInt(r[3])))
            .FirstOrDefault();

    public IReadOnlyList<GpuInfo> ReadGpus()
    {
        var vram = ReadVramByAdapter();
        return Query("Win32_VideoController", "Name", "DriverVersion", "DriverDate")
            .Where(r => !Str(r[0]).Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
            .Select(r => new GpuInfo(Str(r[0]), Str(r[1]), FormatWmiDate(r[2]),
                vram.TryGetValue(Str(r[0]), out var bytes) ? bytes : null))
            .OrderBy(g => g.IsVirtual) // placas reais primeiro
            .ToList();
    }

    /// <summary>VRAM pelo registro do driver: o AdapterRAM do WMI satura em 4 GB.</summary>
    private Dictionary<string, ulong> ReadVramByAdapter()
    {
        var result = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (classKey is null) return result;
            foreach (var sub in classKey.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                try
                {
                    using var key = classKey.OpenSubKey(sub);
                    if (key?.GetValue("DriverDesc") is not string desc) continue;
                    var bytes = ParseVram(key.GetValue("HardwareInformation.qwMemorySize"), key.GetValue("HardwareInformation.MemorySize"));
                    if (bytes is { } b && (!result.TryGetValue(desc, out var old) || b > old)) result[desc] = b;
                }
                catch (System.Security.SecurityException) { }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.Warn($"Não foi possível ler a VRAM pelo registro: {ex.Message}");
        }
        return result;
    }

    /// <summary>qwMemorySize (QWORD) tem prioridade; o valor legado pode ser DWORD ou binário de 4/8 bytes.</summary>
    public static ulong? ParseVram(object? qwMemorySize, object? legacyMemorySize)
    {
        static ulong? Convert(object? v) => v switch
        {
            long l when l > 0 => (ulong)l,
            int i when i != 0 => unchecked((uint)i),
            byte[] { Length: >= 8 } b => BitConverter.ToUInt64(b, 0),
            byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b, 0),
            _ => null
        };
        return Convert(qwMemorySize) ?? Convert(legacyMemorySize);
    }

    private static IReadOnlyList<DiskInfo> ReadDisks()
    {
        var list = new List<DiskInfo>();
        using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
            "SELECT FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk");
        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
                list.Add(new DiskInfo(Str(mo["FriendlyName"]), DiskKind(ToInt(mo["MediaType"]), ToInt(mo["BusType"])), ToULong(mo["Size"])));
        }
        return list;
    }

    /// <summary>MediaType: 3 = HDD, 4 = SSD. BusType 17 = NVMe.</summary>
    public static string DiskKind(int mediaType, int busType) => (mediaType, busType) switch
    {
        (_, 17) => "SSD NVMe",
        (4, _) => "SSD",
        (3, _) => "HDD",
        _ => "desconhecido"
    };

    private static IReadOnlyList<VolumeInfo> ReadVolumes() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new VolumeInfo(d.Name, d.VolumeLabel, (ulong)d.TotalSize, (ulong)d.AvailableFreeSpace))
            .ToList();

    private static string ReadWindows()
    {
        var os = Query("Win32_OperatingSystem", "Caption", "Version", "BuildNumber").FirstOrDefault();
        string display = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null) as string ?? string.Empty;
        string ubr = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", null) is int u ? $".{u}" : string.Empty;
        return os is null ? Environment.OSVersion.VersionString : $"{os[0]} {display} (build {os[2]}{ubr})".Replace("  ", " ");
    }

    private static List<object?[]> Query(string wmiClass, params string[] properties)
    {
        var rows = new List<object?[]>();
        using var searcher = new ManagementObjectSearcher($"SELECT {string.Join(", ", properties)} FROM {wmiClass}");
        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo) rows.Add(properties.Select(p => mo[p]).ToArray());
        }
        return rows;
    }

    private T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or IOException)
        {
            logger.Warn($"Informação de sistema indisponível: {ex.Message}");
            return fallback;
        }
    }

    private static string Str(object? v) => v?.ToString()?.Trim() ?? string.Empty;
    private static int ToInt(object? v) { try { return v is null ? 0 : System.Convert.ToInt32(v); } catch { return 0; } }
    private static ulong ToULong(object? v) { try { return v is null ? 0 : System.Convert.ToUInt64(v); } catch { return 0; } }

    private static string FormatWmiDate(object? v)
    {
        var s = v?.ToString();
        if (string.IsNullOrEmpty(s) || s.Length < 8) return "—";
        try { return ManagementDateTimeConverter.ToDateTime(s).ToString("dd/MM/yyyy"); }
        catch (ArgumentOutOfRangeException) { return s[..8]; }
    }
}
