using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;
using Xunit;

namespace GueibsOptimizer.Tests;

/// <summary>Suporte a AMD (Ryzen e Radeon) no reconhecimento, nos sensores, no Check-up e nas dicas.</summary>
public class AmdSupportTests
{
    [Theory]
    [InlineData("AMD Ryzen 5 5600X 6-Core Processor", "Ryzen 5", "5600X", 5, false)]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", "Ryzen 7", "7800X3D", 7, true)]
    [InlineData("AMD Ryzen 9 9950X3D 16-Core Processor", "Ryzen 9", "9950X3D", 9, true)]
    [InlineData("AMD Ryzen 7 5700X3D 8-Core Processor", "Ryzen 7", "5700X3D", 5, true)]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor", "Ryzen 5", "3600", 3, false)]
    [InlineData("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", "Ryzen 7", "7840HS", 7, false)]
    [InlineData("AMD Ryzen 5 PRO 4650G with Radeon Graphics", "Ryzen 5", "4650G", 4, false)]
    public void RecognizesRyzenDesktopAndLaptop(string raw, string brand, string model, int series, bool x3d)
    {
        var cpu = HardwareNameParser.ParseCpu(raw);
        Assert.Equal(HardwareVendor.Amd, cpu.Vendor);
        Assert.Equal(brand, cpu.Brand);
        Assert.Equal(model, cpu.Model);
        Assert.Equal(series, cpu.Generation);
        Assert.Equal(x3d, cpu.IsX3D);
    }

    [Theory]
    [InlineData("AMD Ryzen AI 9 HX 370 w/ Radeon 890M", "Ryzen AI 9")]
    [InlineData("AMD Ryzen Z1 Extreme", "Ryzen")]
    [InlineData("AMD Ryzen Z2 Go", "Ryzen")]
    [InlineData("AMD Ryzen Threadripper 7980X 64-Cores", "Threadripper")]
    [InlineData("AMD Athlon Silver 3050U with Radeon Graphics", "Athlon")]
    public void RecognizesOtherAmdProcessors(string raw, string brand)
    {
        var cpu = HardwareNameParser.ParseCpu(raw);
        Assert.Equal(HardwareVendor.Amd, cpu.Vendor);
        Assert.Equal(brand, cpu.Brand);
        Assert.StartsWith("AMD", cpu.FriendlyName);
    }

    [Theory]
    [InlineData("AMD Radeon RX 9070 XT", "Radeon RX", false)]
    [InlineData("AMD Radeon RX 7900 XTX", "Radeon RX", false)]
    [InlineData("AMD Radeon RX 6600", "Radeon RX", false)]
    [InlineData("Radeon RX 580 Series", "Radeon RX", false)]
    [InlineData("Radeon RX Vega 64", "Radeon RX Vega", false)]
    [InlineData("AMD Radeon VII", "Radeon VII", false)]
    [InlineData("AMD Radeon Pro W7800", "Radeon Pro", false)]
    [InlineData("AMD Radeon(TM) Graphics", "Radeon Graphics", true)]
    [InlineData("AMD Radeon 780M Graphics", "Radeon Graphics", true)]
    [InlineData("AMD Radeon(TM) Vega 8 Graphics", "Radeon Graphics", true)]
    public void RecognizesRadeonDedicatedAndIntegrated(string raw, string brand, bool integrated)
    {
        var gpu = HardwareNameParser.ParseGpu(raw);
        Assert.Equal(HardwareVendor.Amd, gpu.Vendor);
        Assert.Equal(brand, gpu.Brand);
        Assert.Equal(integrated, gpu.IsIntegrated);
        if (!integrated) Assert.True(gpu.PerformanceIndex > 0);
    }

    [Fact]
    public void RadeonPerformanceOrderMakesSense()
    {
        double Index(string n) => HardwareNameParser.ParseGpu(n).PerformanceIndex;
        Assert.True(Index("AMD Radeon RX 9070 XT") > Index("AMD Radeon RX 7600"));
        Assert.True(Index("AMD Radeon RX 7900 XTX") > Index("AMD Radeon RX 7800 XT"));
        Assert.True(Index("AMD Radeon RX 6800 XT") > Index("AMD Radeon RX 6600"));
        Assert.Equal(PcTier.Topo, PcTierClassifier.GpuTier(HardwareNameParser.ParseGpu("AMD Radeon RX 7900 XTX")));
        Assert.Equal(PcTier.Entrada, PcTierClassifier.GpuTier(HardwareNameParser.ParseGpu("AMD Radeon(TM) Graphics")));
    }

    private static HardwareSnapshot Snapshot(params HardwareReading[] readings) =>
        new(DateTime.Now, readings.GroupBy(r => (r.Hardware, r.Kind))
            .Select(g => new HardwareGroup(g.Key.Hardware, g.Key.Kind, g.ToList())).ToList());

    [Fact]
    public void RyzenWithIntegratedAndDedicatedRadeonShowsTheDedicatedCard()
    {
        const string cpu = "AMD Ryzen 7 7800X3D", igpu = "AMD Radeon(TM) Graphics", dgpu = "AMD Radeon RX 7800 XT";
        var s = HardwareSummary.From(Snapshot(
            new(cpu, HardwareKind.Cpu, "Core (Tctl/Tdie)", "Temperature", 71),
            new(cpu, HardwareKind.Cpu, "CCD1 (Tdie)", "Temperature", 69),
            new(cpu, HardwareKind.Cpu, "Package", "Power", 58),
            new(cpu, HardwareKind.Cpu, "Core #1", "Clock", 4950),
            new(cpu, HardwareKind.Cpu, "CPU Total", "Load", 33),
            // A integrada vem primeiro na lista do LibreHardwareMonitor.
            new(igpu, HardwareKind.Gpu, "GPU Core", "Temperature", 45),
            new(igpu, HardwareKind.Gpu, "GPU Core", "Load", 2),
            new(igpu, HardwareKind.Gpu, "D3D Dedicated Memory Total", "SmallData", 512),
            new(dgpu, HardwareKind.Gpu, "GPU Core", "Temperature", 62),
            new(dgpu, HardwareKind.Gpu, "GPU Hot Spot", "Temperature", 80),
            new(dgpu, HardwareKind.Gpu, "GPU Core", "Load", 97),
            new(dgpu, HardwareKind.Gpu, "GPU Core", "Clock", 2430),
            new(dgpu, HardwareKind.Gpu, "GPU Fan", "Fan", 1500),
            new(dgpu, HardwareKind.Gpu, "GPU Memory Total", "SmallData", 16368)));

        Assert.Equal(71, s.CpuTemp);
        Assert.Equal(58, s.CpuPower);
        Assert.Equal(4950, s.CpuClock);
        Assert.Equal(62, s.GpuTemp);
        Assert.Equal(97, s.GpuLoad);
        Assert.Equal(2430, s.GpuClock);
        Assert.Equal(16368, s.GpuMemTotalMb);
    }

    [Fact]
    public void OlderRyzenWithOffsetUsesTdieInsteadOfTctl()
    {
        const string cpu = "AMD Ryzen 7 2700X";
        var s = HardwareSummary.From(Snapshot(
            new(cpu, HardwareKind.Cpu, "Core (Tctl)", "Temperature", 75),
            new(cpu, HardwareKind.Cpu, "Core (Tdie)", "Temperature", 65)));
        Assert.Equal(65, s.CpuTemp);
    }

    [Fact]
    public void IntelPackageTemperatureStillWins()
    {
        const string cpu = "Intel Core i5-7400";
        var s = HardwareSummary.From(Snapshot(
            new(cpu, HardwareKind.Cpu, "CPU Core #1", "Temperature", 44),
            new(cpu, HardwareKind.Cpu, "CPU Package", "Temperature", 48)));
        Assert.Equal(48, s.CpuTemp);
    }

    [Fact]
    public void GpuDriverCheckPointsToTheRightOfficialPage()
    {
        var today = new DateTime(2026, 11, 21);
        var old = CheckupRules.GpuDriver(
            [new GpuInfo("Parsec Virtual Display Adapter", "1.0", "01/01/2020", null),
             new GpuInfo("AMD Radeon(TM) Graphics", "31.0.1", "01/10/2026", null),
             new GpuInfo("AMD Radeon RX 7800 XT", "32.0.12011.1036", "15/01/2026", 16UL << 30)], today);
        Assert.Equal(CheckStatus.Attention, old.Status);
        Assert.Contains("AMD Radeon RX 7800 XT", old.Detail);
        Assert.Contains(CheckupRules.AmdDriversUrl, old.HowToFix);

        var fresh = CheckupRules.GpuDriver([new GpuInfo("NVIDIA GeForce RTX 2060 SUPER", "32.0.15.7680", "10/10/2026", null)], today);
        Assert.Equal(CheckStatus.Ok, fresh.Status);

        var intel = CheckupRules.GpuDriver([new GpuInfo("Intel(R) UHD Graphics 630", "31.0.101", "—", null)], today);
        Assert.Equal(CheckStatus.Attention, intel.Status);
        Assert.Contains(CheckupRules.IntelDriversUrl, intel.HowToFix);

        Assert.Equal(CheckStatus.Attention, CheckupRules.GpuDriver([], today).Status);
    }

    [Fact]
    public void AmdChipsetCheckOnlyForAmdProcessors()
    {
        Assert.Null(CheckupRules.AmdChipset(HardwareNameParser.ParseCpu("Intel(R) Core(TM) i5-7400 CPU @ 3.00GHz"), []));

        var x3d = HardwareNameParser.ParseCpu("AMD Ryzen 9 7950X3D 16-Core Processor");
        var missing = CheckupRules.AmdChipset(x3d, []);
        Assert.NotNull(missing);
        Assert.Equal(CheckStatus.Attention, missing!.Status);
        Assert.Contains("X3D", missing.Detail);
        Assert.Contains(CheckupRules.AmdDriversUrl, missing.HowToFix);

        var present = CheckupRules.AmdChipset(x3d, ["AMD PSP 11.0 Device"]);
        Assert.Equal(CheckStatus.Ok, present!.Status);

        Assert.True(SystemInfoService.IsAmdProvider("Advanced Micro Devices, Inc."));
        Assert.True(SystemInfoService.IsAmdProvider("AMD"));
        Assert.False(SystemInfoService.IsAmdProvider("Microsoft"));
    }

    [Fact]
    public void AmdRecommendations()
    {
        var recs = PcTierClassifier.Recommendations(HardwareNameParser.ParseCpu("AMD Ryzen 5 7600"), 12,
            HardwareNameParser.ParseGpu("AMD Radeon RX 7700 XT"), 12UL << 30, false, true, 32UL << 30);
        Assert.Contains(recs, r => r.StartsWith("AMD Ryzen:", StringComparison.Ordinal));
        Assert.Contains(recs, r => r.Contains("Anti-Lag", StringComparison.Ordinal));

        var apu = PcTierClassifier.Recommendations(HardwareNameParser.ParseCpu("AMD Ryzen 7 8700G w/ Radeon 780M Graphics"), 16,
            HardwareNameParser.ParseGpu("AMD Radeon 780M Graphics"), null, false, false, 32UL << 30);
        Assert.Contains(apu, r => r.StartsWith("Vídeo integrado Radeon", StringComparison.Ordinal));

        var intel = PcTierClassifier.Recommendations(HardwareNameParser.ParseCpu("Intel Core i5-7400"), 4,
            HardwareNameParser.ParseGpu("NVIDIA GeForce RTX 2060 SUPER"), 8UL << 30, false, false, 16UL << 30);
        Assert.DoesNotContain(intel, r => r.Contains("AMD", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    public void NewAmdTextsAreTranslated(string language)
    {
        var table = LocalizationService.Instance.Table(language);
        foreach (var pt in new[]
        {
            "Driver da placa de vídeo", "Driver de chipset AMD (estimativa)",
            "{0}: driver {1} de {2}.", "Baixe o driver mais recente na página oficial da {0}: {1}",
            "AMD Ryzen: a velocidade da memória pesa mais no Ryzen (Infinity Fabric). Ative o perfil EXPO/DOCP na BIOS e mantenha o driver de chipset AMD atualizado (veja o Check-up).",
        })
            Assert.True(table.ContainsKey(pt), $"{language}: {pt}");
    }
}
