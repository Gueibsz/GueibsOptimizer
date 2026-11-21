using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using LibreHardwareMonitor.Hardware;

namespace GueibsOptimizer.Services;

/// <summary>Valores principais extraídos do snapshot (lógica pura, testada).</summary>
public sealed record HardwareSummary(
    float? CpuTemp, float? CpuLoad, float? CpuPower, float? CpuClock,
    float? GpuTemp, float? GpuLoad, float? GpuClock, float? GpuFan, float? GpuMemUsedMb, float? GpuMemTotalMb,
    float? RamLoad, float? RamUsedGb, float? RamAvailableGb)
{
    public static HardwareSummary From(HardwareSnapshot s)
    {
        var cpu = s.All.Where(r => r.Kind == HardwareKind.Cpu).ToList();
        var gpu = MainGpu(s.All.Where(r => r.Kind == HardwareKind.Gpu));
        // O LibreHardwareMonitor expõe "Total Memory" e "Virtual Memory" com os mesmos nomes de sensor
        // ("Memory Used"/"Memory Available"); a memória virtual (RAM + arquivo de paginação) não é a RAM física.
        var mem = s.All.Where(r => r.Kind == HardwareKind.Memory
                                   && !r.Hardware.Contains("Virtual", StringComparison.OrdinalIgnoreCase)).ToList();

        float? cpuClock = cpu.Where(r => r.SensorType == "Clock" && r.Sensor.StartsWith("Core", StringComparison.OrdinalIgnoreCase) && r.Value is not null)
                             .Select(r => r.Value).DefaultIfEmpty(null).Max();

        return new HardwareSummary(
            // Intel: "CPU Package". AMD Ryzen: "Core (Tctl/Tdie)"; nos Ryzen com deslocamento (1000/2000 X) o Tdie é o real e o Tctl vem somado.
            Pick(cpu, "Temperature", "CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "CCDs Average (Tdie)", "CCD1 (Tdie)", "Core (Tctl)",
                "Core Average", "Core Max"),
            Pick(cpu, "Load", "CPU Total"),
            Pick(cpu, "Power", "CPU Package", "Package"),
            cpuClock,
            Pick(gpu, "Temperature", "GPU Core", "GPU Hot Spot"),
            Pick(gpu, "Load", "GPU Core", "D3D 3D"),
            Pick(gpu, "Clock", "GPU Core"),
            Pick(gpu, "Fan", "GPU Fan", "GPU Fan 1"),
            Pick(gpu, "SmallData", "GPU Memory Used", "D3D Dedicated Memory Used"),
            Pick(gpu, "SmallData", "GPU Memory Total", "D3D Dedicated Memory Total"),
            Pick(mem, "Load", "Memory"),
            Pick(mem, "Data", "Memory Used"),
            Pick(mem, "Data", "Memory Available"));
    }

    /// <summary>
    /// Sensores só da placa de vídeo principal. Ryzen 7000/9000 e Intel com vídeo integrado têm duas GPUs no
    /// LibreHardwareMonitor; sem isso o cartão poderia mostrar a integrada. Ordem: dedicada, real (não virtual), mais VRAM.
    /// </summary>
    public static List<HardwareReading> MainGpu(IEnumerable<HardwareReading> gpuReadings)
    {
        var groups = gpuReadings.GroupBy(r => r.Hardware).ToList();
        if (groups.Count <= 1) return groups.SelectMany(g => g).ToList();
        static float Vram(IEnumerable<HardwareReading> g) => g
            .Where(r => r.SensorType == "SmallData" && r.Value is not null &&
                        (r.Sensor.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase) ||
                         r.Sensor.Equals("D3D Dedicated Memory Total", StringComparison.OrdinalIgnoreCase)))
            .Select(r => r.Value!.Value).DefaultIfEmpty(0).Max();
        return groups
            .OrderBy(g => HardwareNameParser.ParseGpu(g.Key).IsIntegrated)
            .ThenBy(g => HardwareDetectionService.IsVirtualAdapter(g.Key))
            .ThenByDescending(g => Vram(g))
            .First().ToList();
    }

    /// <summary>Primeiro sensor do tipo cujo nome bate com a preferência; senão o primeiro do tipo.</summary>
    private static float? Pick(List<HardwareReading> readings, string type, params string[] preferred)
    {
        var ofType = readings.Where(r => r.SensorType == type && r.Value is not null).ToList();
        foreach (var name in preferred)
        {
            var hit = ofType.FirstOrDefault(r => r.Sensor.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit.Value;
        }
        return ofType.FirstOrDefault()?.Value;
    }
}

/// <summary>
/// Lê sensores com LibreHardwareMonitorLib a cada 1 s. Uso por contagem de referências:
/// só fica ligado enquanto alguma tela/overlay precisar. Sensores ausentes viram "—".
/// </summary>
public sealed class HardwareMonitorService : IDisposable
{
    private readonly Logger _logger;
    private readonly object _sync = new();
    private readonly object _computerLock = new();
    private Computer? _computer;
    private Timer? _timer;
    private int _users;
    private bool _busy;

    public HardwareMonitorService(Logger logger) => _logger = logger;

    public event Action<HardwareSnapshot>? SnapshotReady;

    public HardwareSnapshot? Latest { get; private set; }

    public void Acquire()
    {
        lock (_sync)
        {
            if (++_users > 1) return;
            _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
    }

    public void Release()
    {
        lock (_sync)
        {
            if (_users == 0 || --_users > 0) return;
            _timer?.Dispose();
            _timer = null;
            CloseComputer();
        }
    }

    private void Tick()
    {
        lock (_sync)
        {
            if (_busy || _users == 0) return;
            _busy = true;
        }
        try
        {
            lock (_computerLock) TickCore();
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao ler sensores de hardware", ex);
        }
        finally
        {
            lock (_sync) _busy = false;
        }
    }

    private void TickCore()
    {
        if (_users == 0) return;
        {
            if (_computer is null)
            {
                var computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true,
                    IsStorageEnabled = true,
                    IsMotherboardEnabled = true
                };
                computer.Open();
                _computer = computer;
                _logger.Info("Monitor de hardware iniciado.");
            }

            var groups = new List<HardwareGroup>();
            foreach (var hw in _computer.Hardware)
                Collect(hw, groups);

            var snapshot = new HardwareSnapshot(DateTime.Now, groups);
            Latest = snapshot;
            SnapshotReady?.Invoke(snapshot);
        }
    }

    private static void Collect(IHardware hw, List<HardwareGroup> groups)
    {
        try { hw.Update(); } catch { return; }
        var kind = Map(hw.HardwareType);
        var readings = hw.Sensors
            .Select(s => new HardwareReading(hw.Name, kind, s.Name, s.SensorType.ToString(), s.Value))
            .ToList();

        foreach (var sub in hw.SubHardware)
        {
            try { sub.Update(); } catch { continue; }
            readings.AddRange(sub.Sensors.Select(s => new HardwareReading(hw.Name, kind, s.Name, s.SensorType.ToString(), s.Value)));
        }
        if (readings.Count > 0) groups.Add(new HardwareGroup(hw.Name, kind, readings));
    }

    private static HardwareKind Map(HardwareType type) => type switch
    {
        HardwareType.Cpu => HardwareKind.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => HardwareKind.Gpu,
        HardwareType.Memory => HardwareKind.Memory,
        HardwareType.Storage => HardwareKind.Storage,
        HardwareType.Motherboard or HardwareType.SuperIO => HardwareKind.Motherboard,
        _ => HardwareKind.Other
    };

    private void CloseComputer()
    {
        lock (_computerLock)
        {
            try { _computer?.Close(); } catch { /* driver já liberado */ }
            _computer = null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _users = 0;
            _timer?.Dispose();
            _timer = null;
            CloseComputer();
        }
    }
}
