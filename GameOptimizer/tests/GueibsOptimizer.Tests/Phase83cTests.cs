using GueibsOptimizer.Core;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.Tests;

/// <summary>8.3c: mensagens montadas pelo app (com valores) traduzidas por modelo {0}, e diálogos linha a linha.</summary>
[Collection("Localization")]
public class Phase83cTests
{
    private static T WithLanguage<T>(string lang, Func<T> action)
    {
        var l = LocalizationService.Instance;
        try { l.SetLanguage(lang); return action(); }
        finally { l.SetLanguage(LocalizationService.Portuguese); }
    }

    [Fact]
    public void TemplateRegexMatchesOnlyTheSameShape()
    {
        var re = LocalizationService.TemplateRegex("Liberados {0} ({1} processos).");
        var m = re.Match("Liberados 1,2 GB (37 processos).");
        Assert.True(m.Success);
        Assert.Equal("1,2 GB", m.Groups["p0"].Value);
        Assert.Equal("37", m.Groups["p1"].Value);
        Assert.DoesNotMatch(re, "Liberados 1,2 GB (37 arquivos).");
    }

    [Theory]
    [InlineData("en", "Pontuação 90 de 100", "Score 90 of 100")]
    [InlineData("es", "Pontuação 90 de 100", "Puntuación 90 de 100")]
    [InlineData("en", "Olá, Ana!", "Hi, Ana!")]
    [InlineData("en", "Nível Médio", "Medium level")]          // valor traduzível dentro do modelo
    [InlineData("en", "Simulação concluída: pontuação 70 → 90", "Simulation completed: score 70 → 90")]
    [InlineData("es", "Liberados 1,2 GB (37 processos). Disponível: 3 GB → 4 GB.", "Liberados 1,2 GB (37 procesos). Disponible: 3 GB → 4 GB.")]
    [InlineData("en", "Cyberpunk 2077", "Cyberpunk 2077")]       // dado do usuário passa sem mudança
    public void BuiltMessagesAreTranslated(string lang, string pt, string expected) =>
        Assert.Equal(expected, WithLanguage(lang, () => LocalizationService.Tr(pt)));

    [Fact]
    public void DashboardTextsFollowTheLanguage()
    {
        var line = DashboardText.SummaryLine(12, 16, 76, 469L << 20);
        Assert.Equal("Optimizations 12/16 · memory 76% in use · cleanup 469 MB", WithLanguage("en", () => LocalizationService.Tr(line)));
        Assert.Equal("Optimizations 12/16 · memory 76% in use · cleanup calculating...",
            WithLanguage("en", () => LocalizationService.Tr(DashboardText.SummaryLine(12, 16, 76, null))));
        Assert.Equal("Sistema optimizado", WithLanguage("es", () => LocalizationService.Tr(DashboardText.BadgeText(95))));
        Assert.Equal("1 point(s) · last on 18/11/2026 00:26",
            WithLanguage("en", () => LocalizationService.Tr(new RestorePointsInfo(1, new DateTime(2026, 11, 18, 0, 26, 0)).Text)));
    }

    [Fact]
    public void MultiLineDialogsAreTranslatedLineByLine()
    {
        var msg = "Erro X\n\nSem o ponto de restauração você ainda pode desfazer cada ajuste pela aba Restauração. Deseja continuar mesmo assim?";
        var en = WithLanguage("en", () => LocalizationService.Tr(msg));
        Assert.StartsWith("Erro X\n\n", en);
        Assert.EndsWith("Continue anyway?", en);
    }

    /// <summary>Run.Text liga em duas vias por padrão: sem Mode=OneWay o MultiBinding de tradução quebrava a tela ao abrir.</summary>
    [Fact]
    public void TranslationMultiBindingsAreOneWay()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "GueibsOptimizer.sln"))) dir = dir.Parent;
        var src = System.IO.Path.Combine(dir!.FullName, "src", "GueibsOptimizer");
        var bad = System.IO.Directory.EnumerateFiles(src, "*.xaml", System.IO.SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}"))
            .Where(f => System.IO.File.ReadAllText(f).Contains("<MultiBinding Converter=\"{StaticResource Tr}\">"))
            .Select(System.IO.Path.GetFileName).ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void PortugueseIsUnchanged() =>
        Assert.Equal("Pontuação 90 de 100", LocalizationService.Tr("Pontuação 90 de 100"));
}
