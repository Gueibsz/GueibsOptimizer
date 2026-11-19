using GueibsOptimizer.Models;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.Tests;

public class CpuNameParserTests
{
    [Theory]
    [InlineData("Intel(R) Core(TM) i5-7400 CPU @ 3.00GHz", "Core i5", "7400", 7, 3000, false)]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700K", "Core i7", "13700K", 13, null, true)]
    [InlineData("Intel(R) Core(TM) i9-14900KF", "Core i9", "14900KF", 14, null, true)]
    [InlineData("Intel(R) Core(TM) i3-10100F CPU @ 3.60GHz", "Core i3", "10100F", 10, 3600, false)]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", "Core Ultra 7", "155H", 1, null, true)]
    public void ParsesIntel(string raw, string brand, string model, int gen, int? clock, bool hybrid)
    {
        var cpu = HardwareNameParser.ParseCpu(raw);
        Assert.Equal(HardwareVendor.Intel, cpu.Vendor);
        Assert.Equal(brand, cpu.Brand);
        Assert.Equal(model, cpu.Model);
        Assert.Equal(gen, cpu.Generation);
        Assert.Equal(clock, cpu.BaseClockMhz);
        Assert.Equal(hybrid, cpu.IsHybridCores);
    }

    [Theory]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", "Ryzen 7", "5800X3D", 5, true)]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor", "Ryzen 9", "7950X3D", 7, true)]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor", "Ryzen 5", "3600", 3, false)]
    [InlineData("AMD Ryzen 5 PRO 4650G with Radeon Graphics", "Ryzen 5", "4650G", 4, false)]
    public void ParsesRyzen(string raw, string brand, string model, int series, bool x3d)
    {
        var cpu = HardwareNameParser.ParseCpu(raw);
        Assert.Equal(HardwareVendor.Amd, cpu.Vendor);
        Assert.Equal(brand, cpu.Brand);
        Assert.Equal(model, cpu.Model);
        Assert.Equal(series, cpu.Generation);
        Assert.Equal(x3d, cpu.IsX3D);
    }

    [Fact]
    public void ParsesRyzenAi()
    {
        var cpu = HardwareNameParser.ParseCpu("AMD Ryzen AI 9 HX 370 w/ Radeon 890M");
        Assert.Equal("Ryzen AI 9", cpu.Brand);
        Assert.Equal("HX 370", cpu.Model);
    }

    [Fact]
    public void UnknownNamesDoNotThrow()
    {
        Assert.Equal(HardwareVendor.Intel, HardwareNameParser.ParseCpu("Intel(R) Pentium(R) Gold G6400 CPU @ 4.00GHz").Vendor);
        Assert.Equal("Processador desconhecido", HardwareNameParser.ParseCpu("").FriendlyName);
        Assert.Equal(HardwareVendor.Unknown, HardwareNameParser.ParseCpu("Qualquer coisa").Vendor);
    }
}

public class GpuNameParserTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 2060 SUPER", "GeForce RTX", "2060 SUPER", 3.5, true)]
    [InlineData("NVIDIA GeForce GTX 1660 Ti", "GeForce GTX", "1660 Ti", 3.0, true)]
    [InlineData("NVIDIA GeForce RTX 4090", "GeForce RTX", "4090", 8.0, true)]
    [InlineData("NVIDIA GeForce RTX 4060 Ti", "GeForce RTX", "4060 Ti", 5.5, true)]
    [InlineData("NVIDIA GeForce GTX 1050", "GeForce GTX", "1050", 1.0, true)]
    [InlineData("NVIDIA GeForce GTX 970", "GeForce GTX", "970", 2.0, true)]
    public void ParsesNvidia(string raw, string brand, string model, double index, bool reflex)
    {
        var gpu = HardwareNameParser.ParseGpu(raw);
        Assert.Equal(HardwareVendor.Nvidia, gpu.Vendor);
        Assert.Equal(brand, gpu.Brand);
        Assert.Equal(model, gpu.Model);
        Assert.Equal(index, gpu.PerformanceIndex, 2);
        Assert.Equal(reflex, gpu.SupportsReflex);
        Assert.False(gpu.IsIntegrated);
    }

    [Theory]
    [InlineData("AMD Radeon RX 7900 XTX", "7900 XTX", 8.5)]
    [InlineData("AMD Radeon RX 6600", "6600", 3.5)]
    [InlineData("AMD Radeon RX 6500 XT", "6500 XT", 2.5)]
    [InlineData("AMD Radeon RX 9070 XT", "9070 XT", 7.0)]
    [InlineData("Radeon RX 580 Series", "580", 1.0)]
    public void ParsesRadeon(string raw, string model, double index)
    {
        var gpu = HardwareNameParser.ParseGpu(raw);
        Assert.Equal(HardwareVendor.Amd, gpu.Vendor);
        Assert.Equal(model, gpu.Model);
        Assert.Equal(index, gpu.PerformanceIndex, 2);
    }

    [Theory]
    [InlineData("Intel(R) UHD Graphics 630", HardwareVendor.Intel)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", HardwareVendor.Intel)]
    [InlineData("AMD Radeon(TM) Graphics", HardwareVendor.Amd)]
    [InlineData("AMD Radeon 780M Graphics", HardwareVendor.Amd)]
    public void DetectsIntegrated(string raw, HardwareVendor vendor)
    {
        var gpu = HardwareNameParser.ParseGpu(raw);
        Assert.True(gpu.IsIntegrated);
        Assert.Equal(vendor, gpu.Vendor);
    }

    [Fact]
    public void ParsesIntelArc()
    {
        var gpu = HardwareNameParser.ParseGpu("Intel(R) Arc(TM) A770 Graphics");
        Assert.False(gpu.IsIntegrated);
        Assert.Equal("A770", gpu.Model);
        Assert.Equal(4, gpu.PerformanceIndex, 2);
    }

    [Theory]
    [InlineData("Parsec Virtual Display Adapter", true)]
    [InlineData("Microsoft Basic Display Adapter", true)]
    [InlineData("NVIDIA GeForce RTX 3060", false)]
    public void FiltersVirtualAdapters(string name, bool isVirtual) =>
        Assert.Equal(isVirtual, HardwareDetectionService.IsVirtualAdapter(name));
}

public class PcTierTests
{
    private static readonly ulong Gb16 = 16UL << 30, Gb8 = 8UL << 30, Gb32 = 32UL << 30;

    [Theory]
    [InlineData("NVIDIA GeForce RTX 2060 SUPER", 4, 16, PcTier.Medio)]
    [InlineData("NVIDIA GeForce RTX 3060", 12, 16, PcTier.Medio)]
    [InlineData("NVIDIA GeForce RTX 4060 Ti", 12, 16, PcTier.Alto)]
    [InlineData("NVIDIA GeForce RTX 4090", 32, 32, PcTier.Topo)]
    [InlineData("AMD Radeon RX 7900 XTX", 16, 32, PcTier.Topo)]
    [InlineData("AMD Radeon RX 6600", 12, 16, PcTier.Medio)]
    [InlineData("NVIDIA GeForce GTX 1050", 8, 16, PcTier.Entrada)]
    [InlineData("Intel(R) UHD Graphics 630", 12, 16, PcTier.Entrada)]
    public void ClassifiesCommonSetups(string gpuName, int threads, int ramGb, PcTier expected)
    {
        var (tier, reasons) = PcTierClassifier.Classify(HardwareNameParser.ParseGpu(gpuName), threads, (ulong)ramGb << 30);
        Assert.Equal(expected, tier);
        Assert.NotEmpty(reasons);
    }

    [Fact]
    public void WeakCpuOrRamCapsAStrongGpu()
    {
        var gpu = HardwareNameParser.ParseGpu("NVIDIA GeForce RTX 4090");
        Assert.Equal(PcTier.Medio, PcTierClassifier.Classify(gpu, 4, Gb32).Tier);
        Assert.Equal(PcTier.Alto, PcTierClassifier.Classify(gpu, 8, Gb32).Tier);
        Assert.Equal(PcTier.Medio, PcTierClassifier.Classify(gpu, 16, Gb8).Tier);
        Assert.Equal(PcTier.Entrada, PcTierClassifier.Classify(gpu, 16, 4UL << 30).Tier);
        var (_, reasons) = PcTierClassifier.Classify(gpu, 4, Gb16);
        Assert.Contains(reasons, r => r.Contains("4 threads"));
    }

    [Theory]
    [InlineData(new[] { 3 }, false)]       // desktop
    [InlineData(new[] { 10 }, true)]       // notebook
    [InlineData(new[] { 31 }, true)]       // conversível
    [InlineData(new int[0], false)]
    public void DetectsLaptopChassis(int[] types, bool laptop) => Assert.Equal(laptop, HardwareDetectionService.IsLaptopChassis(types));

    [Fact]
    public void RecommendationsAreConditional()
    {
        var x3d = HardwareNameParser.ParseCpu("AMD Ryzen 7 7800X3D 8-Core Processor");
        var rtx = HardwareNameParser.ParseGpu("NVIDIA GeForce RTX 4070");
        var recs = PcTierClassifier.Recommendations(x3d, 16, rtx, 12UL << 30, isLaptop: true, hybridGraphics: true, Gb32);
        Assert.Contains(recs, r => r.Contains("X3D"));
        Assert.Contains(recs, r => r.Contains("Reflex"));
        Assert.Contains(recs, r => r.Contains("híbrido"));
        Assert.DoesNotContain(recs, r => r.Contains("16 GB"));

        var intel = HardwareNameParser.ParseCpu("Intel(R) Core(TM) i5-7400 CPU @ 3.00GHz");
        var amdGpu = HardwareNameParser.ParseGpu("AMD Radeon RX 6500 XT");
        var recs2 = PcTierClassifier.Recommendations(intel, 4, amdGpu, 4UL << 30, false, false, Gb8);
        Assert.DoesNotContain(recs2, r => r.Contains("X3D") || r.Contains("Reflex"));
        Assert.Contains(recs2, r => r.Contains("Anti-Lag"));
        Assert.Contains(recs2, r => r.Contains("VRAM"));
        Assert.Contains(recs2, r => r.Contains("16 GB"));
    }

    [Fact]
    public void DualCcdX3DGetsSpecificAdvice()
    {
        var cpu = HardwareNameParser.ParseCpu("AMD Ryzen 9 7950X3D 16-Core Processor");
        var recs = PcTierClassifier.Recommendations(cpu, 32, HardwareNameParser.ParseGpu("NVIDIA GeForce RTX 4090"), 24UL << 30, false, false, Gb32);
        Assert.Contains(recs, r => r.Contains("dois CCDs"));
    }
}
