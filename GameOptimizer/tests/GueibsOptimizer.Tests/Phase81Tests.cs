using System.IO;
using GueibsOptimizer.Services;
using GueibsOptimizer.Views.Controls;

namespace GueibsOptimizer.Tests;

public class Phase81Tests
{
    [Theory]
    [InlineData(100, "Sistema otimizado")]
    [InlineData(90, "Sistema otimizado")]
    [InlineData(89, "Quase lá")]
    [InlineData(60, "Quase lá")]
    [InlineData(59, "Precisa de atenção")]
    [InlineData(0, "Precisa de atenção")]
    public void BadgeFollowsScoreBands(int score, string expected) => Assert.Equal(expected, DashboardText.BadgeText(score));

    [Theory]
    [InlineData(null, "jogador", "Olá, jogador!")]
    [InlineData("  Ana  ", "jogador", "Olá, Ana!")]
    [InlineData("", "", "Olá!")]
    public void GreetingUsesDisplayNameOrWindowsUser(string? name, string user, string expected) =>
        Assert.Equal(expected, DashboardText.Greeting(name, user));

    [Fact]
    public void SummaryLineShowsMeasuredValues()
    {
        Assert.Equal("Otimizações 12/16 · memória 43% em uso · limpeza 1.5 GB",
            DashboardText.SummaryLine(12, 16, 43, 1_610_612_736).Replace(',', '.'));
        Assert.EndsWith("limpeza calculando...", DashboardText.SummaryLine(0, 16, 0, null));
        Assert.Equal("300 MB", DashboardText.FormatGb(300L << 20));
    }

    [Fact]
    public void MemoryPercentIsUsedShare()
    {
        Assert.Equal(25, DashboardText.MemoryPercent(available: 12, total: 16));
        Assert.Equal(0, DashboardText.MemoryPercent(0, 0));
    }

    [Fact]
    public void RestorePointsSummaryFromWmiDates()
    {
        var info = RestorePointsInfo.FromDmtf(["20261110080000.000000-180", "20261118093000.000000-180", "lixo"]);
        Assert.Equal(2, info.Count);
        Assert.Equal(new DateTime(2026, 11, 18), info.Last!.Value.Date);
        Assert.Contains("2 ponto(s)", info.Text);
        Assert.Equal("Nenhum ponto de restauração encontrado.", RestorePointsInfo.FromDmtf([]).Text);
        Assert.Equal("erro", new RestorePointsInfo(0, null, "erro").Text);
    }

    [Fact]
    public void CpuUsageFromSystemTimes()
    {
        // Kernel inclui o ocioso: 100 de kernel+usuário, 25 ocioso → 75% de uso.
        Assert.Equal(75, CpuUsageSampler.Compute((100, 200, 300), (125, 260, 340))!.Value, 3);
        Assert.Null(CpuUsageSampler.Compute((1, 1, 1), (1, 1, 1)));
        Assert.Equal(0, CpuUsageSampler.Compute((0, 0, 0), (50, 50, 0))!.Value, 3);
        Assert.True(new CpuUsageSampler().Sample() is null); // primeira leitura não tem intervalo
    }

    [Theory]
    [InlineData(0, 180)]
    [InlineData(50, 90)]
    [InlineData(100, 0)]
    [InlineData(150, 0)]
    public void GaugeAngle(double value, double angle) => Assert.Equal(angle, SemiGauge.AngleFor(value), 6);

    [Fact]
    public void HomeViewHasLiveCardsAndHonestNotice()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GueibsOptimizer.sln"))) dir = dir.Parent;
        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "src", "GueibsOptimizer", "Views", "HomeView.xaml"));
        foreach (var expected in new[] { "c:SemiGauge", "Pontos de restauração", "Arquivos para limpar", "Memória RAM",
                     "Desempenho em tempo real", "Boost agora", "Check-up", "Windows recém-formatado", "depende do hardware", "c:InfoButton" })
            Assert.Contains(expected, xaml);
        Assert.DoesNotContain("FPS a mais", xaml);
    }
}
