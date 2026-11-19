namespace GueibsOptimizer.Models;

public enum HardwareVendor
{
    Unknown,
    Intel,
    Amd,
    Nvidia
}

public enum PcTier
{
    Entrada,
    Medio,
    Alto,
    Topo
}

/// <summary>Identificação do processador extraída do nome reportado pelo Windows.</summary>
public sealed record CpuIdentity(
    HardwareVendor Vendor,
    string Brand,          // "Core i5", "Core Ultra 7", "Ryzen 7", "Pentium"...
    string Model,          // "7400", "13700K", "5800X3D"
    int? Generation,       // Intel: 7, 13...; Core Ultra: série 1/2; AMD Ryzen: 5 (5000), 7 (7000)...
    int? BaseClockMhz,     // do "@ 3.00GHz" quando existe
    bool IsX3D,
    bool IsHybridCores,    // Intel 12ª+ / Core Ultra (núcleos P e E)
    string FriendlyName);

/// <summary>Identificação da placa de vídeo extraída do nome do adaptador.</summary>
public sealed record GpuIdentity(
    HardwareVendor Vendor,
    string Brand,          // "GeForce RTX", "GeForce GTX", "Radeon RX", "Arc", "UHD Graphics"...
    string Model,          // "2060 SUPER", "7900 XTX", "A770"
    bool IsIntegrated,
    bool SupportsReflex,
    double PerformanceIndex,
    string FriendlyName);

/// <summary>Resumo do PC usado pelo resto do app (recomendações, presets de jogos etc.).</summary>
public sealed record HardwareProfile(
    CpuIdentity Cpu,
    int Cores,
    int Threads,
    int? BoostClockMhz,
    GpuIdentity Gpu,
    ulong? VramBytes,
    string GpuDriver,
    bool HasHybridGraphics,
    bool IsLaptop,
    ulong RamBytes,
    int RamSpeedMts,
    PcTier Tier,
    IReadOnlyList<string> TierReasons,
    IReadOnlyList<string> Recommendations)
{
    public double RamGb => RamBytes / (1024.0 * 1024 * 1024);
    public double? VramGb => VramBytes is { } v ? v / (1024.0 * 1024 * 1024) : null;

    public string TierText => Tier switch
    {
        PcTier.Entrada => "Entrada",
        PcTier.Medio => "Médio",
        PcTier.Alto => "Alto",
        _ => "Topo"
    };

    public string Summary =>
        $"{Cpu.FriendlyName} ({Cores} núcleos / {Threads} threads) · {Gpu.FriendlyName}" +
        (VramGb is { } vram && !Gpu.IsIntegrated ? $" {vram:0} GB" : string.Empty) +
        $" · {RamGb:0} GB de RAM" + (RamSpeedMts > 0 ? $" a {RamSpeedMts} MT/s" : string.Empty) +
        (IsLaptop ? " · Notebook" : " · Desktop");
}
