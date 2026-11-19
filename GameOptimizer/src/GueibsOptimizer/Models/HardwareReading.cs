namespace GueibsOptimizer.Models;

public enum HardwareKind
{
    Cpu,
    Gpu,
    Memory,
    Storage,
    Motherboard,
    Other
}

/// <summary>Uma leitura de sensor, independente da biblioteca usada para obtê-la.</summary>
public sealed record HardwareReading(string Hardware, HardwareKind Kind, string Sensor, string SensorType, float? Value)
{
    public string Unit => SensorType switch
    {
        "Temperature" => "°C",
        "Clock" => "MHz",
        "Load" or "Control" or "Level" => "%",
        "Power" => "W",
        "Fan" => "RPM",
        "Voltage" => "V",
        "Data" => "GB",
        "SmallData" => "MB",
        "Throughput" => "MB/s",
        "Frequency" => "Hz",
        _ => string.Empty
    };

    public string ValueText => Value is not { } v
        ? "—"
        : SensorType switch
        {
            "Throughput" => $"{v / (1024 * 1024):0.0} {Unit}",
            "Voltage" => $"{v:0.000} {Unit}",
            "Clock" or "Fan" or "SmallData" => $"{v:0} {Unit}",
            _ => $"{v:0.#} {Unit}"
        };
}

public sealed record HardwareGroup(string Name, HardwareKind Kind, IReadOnlyList<HardwareReading> Readings);

public sealed record HardwareSnapshot(DateTime Time, IReadOnlyList<HardwareGroup> Groups)
{
    public IEnumerable<HardwareReading> All => Groups.SelectMany(g => g.Readings);
}
