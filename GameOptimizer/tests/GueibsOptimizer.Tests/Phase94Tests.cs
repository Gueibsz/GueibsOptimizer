using System.Net;
using System.Net.Sockets;
using GueibsOptimizer.Services;
using GueibsOptimizer.Services.Tweaks;
using GueibsOptimizer.ViewModels;
using Xunit;

namespace GueibsOptimizer.Tests;

public class Phase94Tests
{
    [Fact]
    public void ProvidersHaveValidOfficialAddresses()
    {
        Assert.Equal(["Automático (DHCP)", "Cloudflare", "Google", "Quad9", "AdGuard DNS"], DnsService.Providers.Select(p => p.Name));
        foreach (var p in DnsService.Providers.Where(p => !p.IsAutomatic))
        {
            Assert.All(p.IPv4, ip => Assert.Equal(AddressFamily.InterNetwork, IPAddress.Parse(ip).AddressFamily));
            Assert.All(p.IPv6, ip => Assert.Equal(AddressFamily.InterNetworkV6, IPAddress.Parse(ip).AddressFamily));
            Assert.Equal(2, p.IPv4.Length);
            Assert.Equal(2, p.IPv6.Length);
        }
        Assert.Contains("1.1.1.1", DnsService.Providers[1].IPv4);
        Assert.Contains("9.9.9.9", DnsService.Providers[3].IPv4);
    }

    [Theory]
    [InlineData("1.1.1.1, 8.8.8.8", 2, 0)]
    [InlineData("2606:4700:4700::1111;1.0.0.1", 2, 0)]
    [InlineData("1.1.1", 0, 1)]
    [InlineData("1", 0, 1)]
    [InlineData("127.0.0.1", 0, 1)]
    [InlineData("0.0.0.0", 0, 1)]
    [InlineData("8.8.8.8; rm -rf; 'x'", 1, 3)]
    [InlineData("", 0, 0)]
    public void CustomAddressesAreValidated(string text, int valid, int invalid)
    {
        var (v, i) = DnsService.ParseAddresses(text);
        Assert.Equal(valid, v.Length);
        Assert.Equal(invalid, i.Length);
    }

    [Fact]
    public void ScriptUsesOnlyIndexAndQuotedAddresses()
    {
        Assert.Equal("Set-DnsClientServerAddress -InterfaceIndex 12 -ServerAddresses @('1.1.1.1','2606:4700:4700::1111') -ErrorAction Stop; Clear-DnsClientCache",
            DnsService.BuildScript(12, ["1.1.1.1", "2606:4700:4700::1111"]));
        Assert.Equal("Set-DnsClientServerAddress -InterfaceIndex 7 -ResetServerAddresses -ErrorAction Stop; Clear-DnsClientCache",
            DnsService.BuildScript(7, []));
    }

    [Fact]
    public void DnsQueryIsWellFormed()
    {
        var q = DnsService.BuildQuery(0x1234, "www.microsoft.com");
        Assert.Equal(0x12, q[0]);
        Assert.Equal(0x34, q[1]);
        Assert.Equal(0x01, q[2]); // RD
        Assert.Equal(1, q[5]);    // 1 pergunta
        Assert.Equal(3, q[12]);   // "www"
        Assert.Equal([0x00, 0x00, 0x01, 0x00, 0x01], q[^5..]); // fim do nome + tipo A + classe IN
        Assert.True(DnsService.IsAnswerTo([0x12, 0x34, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0], 0x1234));
        Assert.False(DnsService.IsAnswerTo([0x12, 0x35, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0], 0x1234));
        Assert.False(DnsService.IsAnswerTo([0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0], 0x1234)); // é pergunta, não resposta
    }

    [Fact]
    public void MedianOfMeasuredTimes()
    {
        Assert.Null(DnsService.Median([]));
        Assert.Equal(20, DnsService.Median([30, 10, 20]));
        Assert.Equal(15, DnsService.Median([10, 20]));
    }

    [Fact]
    public void Ipv6TweaksAreSeparateMediumAndOutOfBoost()
    {
        using var key = new TestRegistryKey();
        var reg = TweakRegistry.CreateDefault(key.CreateContext(dryRun: true));
        var net = reg.All.Where(DnsViewModel.IsNetwork).Cast<DeclarativeTweak>().ToList();
        Assert.Equal(["net.disable-ipv6", "net.prefer-ipv4"], net.Select(t => t.Id).Order());
        Assert.All(net, t => { Assert.False(t.InBoost); Assert.Equal(GueibsOptimizer.Models.TweakRisk.Medium, t.Risk); Assert.NotNull(t.Notice); });
        Assert.Equal(32, reg.Find("net.prefer-ipv4") is DeclarativeTweak p ? p.Definition.Registry[0].Value.GetInt32() : 0);
    }

    [Fact]
    public void CustomProviderValidationInViewModel()
    {
        var vm = new DnsViewModel(null!, null!, new StubDialogs(), new GueibsOptimizer.Core.Logger(null)) { SelectedProvider = DnsViewModel.CustomName, CustomServers = "1.1.1.1, abc" };
        Assert.NotNull(vm.ChosenServers().Error);
        vm.CustomServers = "1.1.1.1";
        Assert.Equal(["1.1.1.1"], vm.ChosenServers().Servers);
        vm.SelectedProvider = "Automático (DHCP)";
        Assert.Empty(vm.ChosenServers().Servers);
    }

    private sealed class StubDialogs : IDialogService
    {
        public bool Confirm(string title, string message) => true;
        public void Info(string title, string message) { }
        public void Error(string title, string message) { }
        public string? PickOpenFile(string filter) => null;
        public string? PickSaveFile(string filter, string defaultName) => null;
        public void CopyText(string text) { }
    }
}