using System.IO;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.Tests;

public class ProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"go-profiles-{Guid.NewGuid():N}");
    private readonly TweakRegistry _registry = new([new FakeTweak("a", 50), new FakeTweak("b", 30), new FakeTweak("c", 20, TweakRisk.High)]);
    private readonly ProfileService _service;

    public ProfileTests() => _service = new ProfileService(_registry, new Logger(null), _dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private const string Valid = """{"format":"GueibsOptimizer.Profile","version":1,"name":"Meu","description":"d","tweaks":["a","B","x-desconhecido","a"]}""";

    [Fact]
    public void ValidProfileKeepsOnlyKnownIdsWithoutDuplicates()
    {
        var v = _service.ValidateJson(Valid);
        Assert.True(v.IsValid);
        Assert.Equal(["a", "b"], v.Profile!.Tweaks);
        Assert.Equal(["x-desconhecido"], v.IgnoredIds);
    }

    [Theory]
    [InlineData("não é json")]
    [InlineData("[]")]
    [InlineData("""{"format":"Outro","version":1,"name":"x","tweaks":[]}""")]
    [InlineData("""{"format":"GueibsOptimizer.Profile","version":99,"name":"x","tweaks":[]}""")]
    [InlineData("""{"format":"GueibsOptimizer.Profile","version":1,"name":"","tweaks":[]}""")]
    [InlineData("""{"format":"GueibsOptimizer.Profile","version":1,"name":"x"}""")]
    [InlineData("""{"format":"GueibsOptimizer.Profile","version":1,"name":"x","tweaks":[{"cmd":"format c:"}]}""")]
    [InlineData("""{"format":"GueibsOptimizer.Profile","version":1,"name":"x","tweaks":"a"}""")]
    public void InvalidProfilesAreRejected(string json) => Assert.False(_service.ValidateJson(json).IsValid);

    [Fact]
    public void ExtraFieldsLikeCommandsAreNeverUsed()
    {
        var v = _service.ValidateJson("""{"format":"GueibsOptimizer.Profile","version":1,"name":"x","tweaks":["a"],"run":"powershell -c evil"}""");
        Assert.True(v.IsValid);
        Assert.Equal(["a"], v.Profile!.Tweaks);
    }

    [Fact]
    public void OversizedFileIsRejected()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "grande.gopro");
        File.WriteAllText(path, new string(' ', (int)ProfileService.MaxFileBytes + 10));
        Assert.False(_service.Validate(path).IsValid);
    }

    [Fact]
    public void SaveListDuplicateExportImportDelete()
    {
        var states = new[]
        {
            new TweakState(_registry.Find("a")!, true, true),
            new TweakState(_registry.Find("b")!, true, false)
        };
        var saved = _service.SaveCurrent("Meu perfil", "teste", states);
        Assert.Equal(["a"], saved.Tweaks);

        var copy = _service.Duplicate(saved);
        Assert.Equal("Meu perfil (cópia)", copy.Name);

        var exported = Path.Combine(_dir, "export.json");
        _service.Export(saved, exported);
        var imported = _service.Import(_service.Validate(exported));
        Assert.Equal("Meu perfil 2", imported.Name);

        var list = _service.List();
        Assert.Equal(3 + 3, list.Count); // 3 padrão + 3 do usuário

        _service.Delete(copy);
        Assert.DoesNotContain(_service.List(), p => p.Name == copy.Name);
        Assert.Throws<InvalidOperationException>(() => _service.Delete(_service.BuiltIn()[0]));
    }

    [Fact]
    public void BuiltInProfilesUseOnlyKnownIdsAndCompetitiveSkipsHighRisk()
    {
        var builtIn = _service.BuiltIn();
        Assert.Equal(["Competitivo", "Equilibrado", "Economia"], builtIn.Select(p => p.Name));
        Assert.All(builtIn.SelectMany(p => p.Tweaks), id => Assert.True(_registry.IsKnown(id)));
        Assert.DoesNotContain("c", builtIn[0].Tweaks);
    }

    [Fact]
    public void RealBuiltInProfilesReferenceRealTweaks()
    {
        using var key = new TestRegistryKey();
        var registry = TweakRegistry.CreateDefault(key.CreateContext(dryRun: true));
        var builtIn = new ProfileService(registry, new Logger(null), _dir).BuiltIn();
        Assert.All(builtIn, p => Assert.NotEmpty(p.Tweaks));
        Assert.DoesNotContain("power.plan", builtIn[2].Tweaks);
    }

    [Fact]
    public void PlanAppliesMissingAndRevertsExtra()
    {
        var profile = new Profile { Name = "x", Tweaks = ["a"] };
        var plan = ProfileService.Plan(profile,
        [
            new TweakState(_registry.Find("a")!, true, false),
            new TweakState(_registry.Find("b")!, true, true),
            new TweakState(_registry.Find("c")!, false, false)
        ]);
        Assert.Equal("a", Assert.Single(plan.ToApply).Id);
        Assert.Equal("b", Assert.Single(plan.ToRevert).Id);
        Assert.Contains("Aplicar", plan.Describe());
    }

    [Fact]
    public async Task ExecuteContinuesAfterFailure()
    {
        var broken = new FakeTweak("x", 1, failOnApply: true);
        var ok = new FakeTweak("y", 1);
        var errors = await ProfileService.ExecuteAsync(new ProfilePlan([broken, ok], [], []), new Logger(null));
        Assert.Single(errors);
        Assert.True(ok.Applied);
    }
}

public class HardwareTests
{
    private static HardwareReading R(HardwareKind kind, string sensor, string type, float? value) => new("hw", kind, sensor, type, value);

    [Fact]
    public void SummaryPicksPreferredSensors()
    {
        var snapshot = new HardwareSnapshot(DateTime.Now,
        [
            new HardwareGroup("CPU", HardwareKind.Cpu,
            [
                R(HardwareKind.Cpu, "Core #1", "Temperature", 50),
                R(HardwareKind.Cpu, "CPU Package", "Temperature", 62),
                R(HardwareKind.Cpu, "CPU Total", "Load", 33),
                R(HardwareKind.Cpu, "Core #1", "Clock", 4500),
                R(HardwareKind.Cpu, "Core #2", "Clock", 4700),
                R(HardwareKind.Cpu, "Bus Speed", "Clock", 100)
            ]),
            new HardwareGroup("GPU", HardwareKind.Gpu,
            [
                R(HardwareKind.Gpu, "GPU Core", "Temperature", 70),
                R(HardwareKind.Gpu, "GPU Memory Used", "SmallData", 2048),
                R(HardwareKind.Gpu, "GPU Memory Total", "SmallData", 8192)
            ]),
            new HardwareGroup("RAM", HardwareKind.Memory, [R(HardwareKind.Memory, "Memory", "Load", 55)])
        ]);

        var s = HardwareSummary.From(snapshot);
        Assert.Equal(62, s.CpuTemp);
        Assert.Equal(33, s.CpuLoad);
        Assert.Equal(4700, s.CpuClock);
        Assert.Equal(70, s.GpuTemp);
        Assert.Equal(8192, s.GpuMemTotalMb);
        Assert.Equal(55, s.RamLoad);
        Assert.Null(s.CpuPower);
    }

    [Fact]
    public void RamIgnoresVirtualMemoryEvenWhenListedFirst()
    {
        static HardwareReading M(string hw, string sensor, string type, float v) => new(hw, HardwareKind.Memory, sensor, type, v);
        var snapshot = new HardwareSnapshot(DateTime.Now,
        [
            new HardwareGroup("Virtual Memory", HardwareKind.Memory,
            [
                M("Virtual Memory", "Memory", "Load", 50.2f),
                M("Virtual Memory", "Memory Used", "Data", 9.8f),
                M("Virtual Memory", "Memory Available", "Data", 9.7f)
            ]),
            new HardwareGroup("Total Memory", HardwareKind.Memory,
            [
                M("Total Memory", "Memory", "Load", 46f),
                M("Total Memory", "Memory Used", "Data", 7.3f),
                M("Total Memory", "Memory Available", "Data", 8.6f)
            ])
        ]);

        var s = HardwareSummary.From(snapshot);
        Assert.Equal(46f, s.RamLoad);
        Assert.Equal(7.3f, s.RamUsedGb);
        Assert.Equal(8.6f, s.RamAvailableGb);
    }

    [Fact]
    public void MissingSensorsShowDash() =>
        Assert.Equal("—", R(HardwareKind.Cpu, "x", "Temperature", null).ValueText);

    [Fact]
    public void FormatsUnits()
    {
        Assert.Equal("62.5 °C", R(HardwareKind.Cpu, "x", "Temperature", 62.5f).ValueText.Replace(',', '.'));
        Assert.Equal("4700 MHz", R(HardwareKind.Cpu, "x", "Clock", 4700.4f).ValueText);
    }

    [Theory]
    [InlineData(12884901888L, null, 12884901888UL)]
    [InlineData(null, 0x40000000, 1073741824UL)]
    public void ParsesVramFromRegistry(long? qw, int? legacy, ulong expected) =>
        Assert.Equal(expected, SystemInfoService.ParseVram(qw, legacy));

    [Fact]
    public void ParsesVramBinaryAndRejectsMissing()
    {
        Assert.Equal(4294967296UL, SystemInfoService.ParseVram(null, BitConverter.GetBytes(4294967296UL)));
        Assert.Null(SystemInfoService.ParseVram(null, null));
    }

    [Theory]
    [InlineData(4, 17, "SSD NVMe")]
    [InlineData(4, 11, "SSD")]
    [InlineData(3, 11, "HDD")]
    [InlineData(0, 7, "desconhecido")]
    public void ClassifiesDisks(int media, int bus, string expected) => Assert.Equal(expected, SystemInfoService.DiskKind(media, bus));

    [Fact]
    public void SummaryTextContainsMainParts()
    {
        var summary = new SystemSummary(new CpuInfo("Ryzen", 8, 16, 4500), [new GpuInfo("RTX", "1.0", "01/01/2026", 8UL << 30)],
            [new MemoryModule("DIMM_A2", "BANK 0", 16UL << 30, 3600, 3600, "Corsair", "X", 26)], "ASUS B550", "AMI 1.0",
            [new DiskInfo("Samsung 980", "SSD NVMe", 1000UL << 30)], [], "Windows 11 Pro", []);
        var text = summary.ToText();
        Assert.Contains("8 núcleos / 16 threads", text);
        Assert.Contains("VRAM 8 GB", text);
        Assert.Contains("DDR4", text);
    }
}

public class CheckupTests
{
    private static MemoryModule M(string locator, string bank, ulong gb, int speed = 3200, int configured = 3200, int type = 26) =>
        new(locator, bank, gb << 30, speed, configured, "x", "y", type);

    [Fact]
    public void SingleStickIsSingleChannel() =>
        Assert.Equal(CheckStatus.Attention, CheckupRules.DualChannel([M("DIMM_A2", "BANK 0", 16)]).Status);

    [Fact]
    public void TwoEqualSticksInDifferentChannelsIsProbableDual()
    {
        var r = CheckupRules.DualChannel([M("DIMM_A2", "BANK 0", 16), M("DIMM_B2", "BANK 2", 16)]);
        Assert.Equal(CheckStatus.Ok, r.Status);
        Assert.Contains("estimativa", r.Detail);
    }

    [Fact]
    public void SameChannelIsFlagged() =>
        Assert.Equal(CheckStatus.Attention,
            CheckupRules.DualChannel([M("ChannelA-DIMM0", "BANK 0", 8), M("ChannelA-DIMM1", "BANK 1", 8)]).Status);

    [Fact]
    public void DifferentSizesAreFlexMode() =>
        Assert.Contains("flex", CheckupRules.DualChannel([M("DIMM_A2", "", 16), M("DIMM_B2", "", 8)]).Detail);

    [Fact]
    public void XmpOffWhenConfiguredBelowNominal() =>
        Assert.Equal(CheckStatus.Attention, CheckupRules.Xmp([M("A", "", 16, speed: 3600, configured: 2133)]).Status);

    [Fact]
    public void XmpOnWhenAboveJedec() =>
        Assert.Equal(CheckStatus.Ok, CheckupRules.Xmp([M("A", "", 16, speed: 3600, configured: 3600)]).Status);

    [Fact]
    public void JedecSpeedIsFlaggedAsEstimate()
    {
        var r = CheckupRules.Xmp([M("A", "", 16, speed: 4800, configured: 4800, type: 34)]);
        Assert.Equal(CheckStatus.Attention, r.Status);
    }

    [Fact]
    public void ParsesPcieOutputAndFlagsIdleGen()
    {
        var links = CheckupService.ParsePcie("NVIDIA GeForce RTX 2060 SUPER|1|3|16|16\r\nlixo\n");
        var link = Assert.Single(links);
        Assert.Equal(1, link.CurrentGen);
        Assert.Equal(3, link.MaxGen);
        var r = CheckupRules.Pcie(links);
        Assert.Equal(CheckStatus.Attention, r.Status);
        Assert.Contains("repouso", r.Detail);
    }

    [Fact]
    public void ReducedWidthIsFlagged() =>
        Assert.Contains("largura", CheckupRules.Pcie([new PcieLink("GPU", 4, 4, 8, 16)]).Detail);

    [Fact]
    public void FullLinkIsOk() => Assert.Equal(CheckStatus.Ok, CheckupRules.Pcie([new PcieLink("GPU", 4, 4, 16, 16)]).Status);

    [Fact]
    public void RefreshBelowMaxIsFlagged()
    {
        Assert.Equal(CheckStatus.Attention, CheckupRules.RefreshRate([new MonitorMode("\\\\.\\DISPLAY1", 1920, 1080, 60, 144, true)]).Status);
        Assert.Equal(CheckStatus.Ok, CheckupRules.RefreshRate([new MonitorMode("\\\\.\\DISPLAY1", 1920, 1080, 144, 144, true)]).Status);
    }

    [Fact]
    public void TweakChecksHandleNotApplicableAndErrors()
    {
        Assert.Equal(CheckStatus.Ok, CheckupRules.FromTweak("x", new FakeTweak("a", 1, applicable: false), "ok", "fix").Status);
        Assert.Equal(CheckStatus.Attention, CheckupRules.FromTweak("x", new FakeTweak("a", 1), "ok", "fix").Status);
        Assert.Equal(CheckStatus.Error, CheckupRules.FromTweak("x", null, "ok", "fix").Status);
    }
}
