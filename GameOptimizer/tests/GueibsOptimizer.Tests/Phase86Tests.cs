using GueibsOptimizer.Services;
using Xunit;

namespace GueibsOptimizer.Tests;

public class Phase86Tests
{
    private static MemoryModule Module(string locator, ulong gb) =>
        new(locator, "BANK 0", gb << 30, 3200, 2133, "Kingston", "KHX3200C16D4/8GX", 26);

    [Fact]
    public void SlotsFromMemoryArrays()
    {
        // Placa com 4 slots e 2 pentes de 8 GB.
        var s = PcDetailsLogic.ComputeSlots([(3, 4, 67108864UL)], [Module("DIMM_A1", 8), Module("DIMM_B1", 8)]);
        Assert.Equal(4, s.Total);
        Assert.Equal(2, s.Occupied);
        Assert.Equal(2, s.Free);
        Assert.Equal(4, s.Slots.Count);
        Assert.Equal("DIMM_A1", s.Slots[0].Locator);
        Assert.Contains("8 GB DDR4 2133 MT/s Kingston", s.Slots[0].Text);
        Assert.False(s.Slots[3].Occupied);
    }

    [Fact]
    public void SlotsIgnoreNonSystemArraysAndMissingTotal()
    {
        // Matriz de cache/vídeo (Use = 4) não conta; firmware sem total → usa o número de pentes.
        var s = PcDetailsLogic.ComputeSlots([(4, 2, 0UL), (3, 0, 0UL)], [Module("", 16)]);
        Assert.Equal(1, s.Total);
        Assert.Equal(0, s.Free);
        Assert.Equal("BANK 0", s.Slots[0].Locator);
        // Dois controladores de memória somam os slots.
        Assert.Equal(8, PcDetailsLogic.ComputeSlots([(3, 4, 0UL), (3, 4, 0UL)], []).Total);
    }

    [Theory]
    [InlineData(3, "", 1)]
    [InlineData(4, "", 2)]
    [InlineData(5, "", 3)]
    [InlineData(0, "L3 Cache", 3)]
    [InlineData(2, "desconhecido", 0)]
    public void CacheLevels(int wmi, string purpose, int expected) => Assert.Equal(expected, PcDetailsLogic.CacheLevel(wmi, purpose));

    [Fact]
    public void CachesAreGroupedPerLevel()
    {
        var c = PcDetailsLogic.GroupCaches([(1, 128UL), (1, 128UL), (2, 1024UL), (3, 6144UL), (0, 99UL)]);
        Assert.Equal(3, c.Count);
        Assert.Equal(256UL, c[0].SizeKb);
        Assert.Equal(2, c[0].Count);
        Assert.Equal("6 MB", PcDetailsLogic.CacheSize(c[2].SizeKb));
        Assert.Equal("256 KB", PcDetailsLogic.CacheSize(256));
    }

    [Theory]
    [InlineData("PRIME B250M-A", "B250")]
    [InlineData("ROG STRIX Z790-E GAMING WIFI", "Z790")]
    [InlineData("MAG X570S TOMAHAWK", "X570")]
    [InlineData("B550 AORUS ELITE", "B550")]
    [InlineData("0Y7WYT", null)]
    public void ChipsetFromBoardName(string board, string? expected) => Assert.Equal(expected, PcDetailsLogic.InferChipset(board, []));

    [Fact]
    public void ChipsetFromPciDevicesPrefersParentheses()
    {
        var pci = new[] { "Intel(R) Management Engine Interface", "Intel(R) 200 Series/Z370 Chipset Family LPC Controller (B250) - A2C8" };
        Assert.Equal("B250", PcDetailsLogic.InferChipset("0Y7WYT", pci));
        Assert.Null(PcDetailsLogic.InferChipset("", ["PCI Express Root Port"]));
    }

    private static PcDetails Sample()
    {
        var summary = new SystemSummary(new CpuInfo("Intel Core i5-7400", 4, 4, 3000),
            [new GpuInfo("NVIDIA GeForce RTX 2060 SUPER", "32.0.15.6094", "01/10/2024", 8UL << 30)],
            [Module("DIMM_A1", 8), Module("DIMM_B1", 8)], "ASUS PRIME", "AMI 1205", [], [], "Windows 11 Pro 23H2 (build 22631)", []);
        return new PcDetails(summary, "GenuineIntel", "LGA1151", 3000, PcDetailsLogic.VirtualizationText(true, false),
            [new CacheEntry(2, 1024, 1), new CacheEntry(3, 6144, 1)], "ASUSTeK", "PRIME B250M-A", "Rev X.0x", "BOARD-SN-123",
            "1205", "05/12/2019", "BIOS-SN-456", "Intel B250 (inferido pelo nome da placa/PCI)",
            PcDetailsLogic.ComputeSlots([(3, 4, 0UL)], summary.Memory),
            [new DiskDetail("Samsung SSD 970 <EVO>", "SSD NVMe", 500UL << 30, "Saudável", "DISK-SN-789")], ["MEM-SN-1", "MEM-SN-2"]);
    }

    [Fact]
    public void SectionsCoverEveryTab()
    {
        var keys = PcDetailsLogic.Sections(Sample()).Select(s => s.Key).ToArray();
        Assert.Equal(["cpu", "cache", "board", "memory", "slots", "gpu", "storage", "windows"], keys);
    }

    [Fact]
    public void ReportsOmitSerialsByDefault()
    {
        var sections = PcDetailsLogic.Sections(Sample());
        var now = new DateTime(2026, 11, 18, 22, 0, 0);
        foreach (var text in new[] { PcDetailsLogic.ToText(sections, false, now), PcDetailsLogic.ToHtml(sections, false, now), PcDetailsLogic.ToJson(sections, false, now) })
        {
            Assert.DoesNotContain("BOARD-SN-123", text);
            Assert.DoesNotContain("DISK-SN-789", text);
            Assert.DoesNotContain("MEM-SN-1", text);
            Assert.Contains("Intel Core i5-7400", text);
        }
        var full = PcDetailsLogic.ToText(sections, true, now);
        Assert.Contains("BOARD-SN-123", full);
        Assert.Contains("BIOS-SN-456", full);
        Assert.Contains("Slots no total: 4", full);
        Assert.Contains("Livres: 2", full);
    }

    [Fact]
    public void HtmlIsEscapedAndJsonIsValid()
    {
        var sections = PcDetailsLogic.Sections(Sample());
        var html = PcDetailsLogic.ToHtml(sections, false, DateTime.Now);
        Assert.Contains("Samsung SSD 970 &lt;EVO&gt;", html);
        Assert.DoesNotContain("<EVO>", html);
        using var doc = System.Text.Json.JsonDocument.Parse(PcDetailsLogic.ToJson(sections, true, DateTime.Now));
        Assert.True(doc.RootElement.GetProperty("serialsIncluded").GetBoolean());
        Assert.Equal(8, doc.RootElement.GetProperty("sections").GetArrayLength());
    }
}
