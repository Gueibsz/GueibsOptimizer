using GueibsOptimizer.Core;
using GueibsOptimizer.Services.Games;

namespace GueibsOptimizer.Tests;

/// <summary>8.3c lote 2: Meus Jogos (presets, opções por jogo e mensagens) em en e es.</summary>
[Collection("Localization")]
public class Phase83cGamesTests
{
    public static IEnumerable<string> GameKeys()
    {
        foreach (var p in GamePresetService.All)
        {
            foreach (var s in p.General) yield return s;
            foreach (var list in p.Tiers.Values) foreach (var s in list) yield return s;
            if (p.ConfigNote is { } note) yield return note;
            if (p.ConfigFile is null) continue;
            foreach (var s in p.ConfigFile.Settings.Where(s => !s.Hidden))
            {
                yield return s.Label;
                foreach (var v in s.Values.Values) yield return v;
            }
        }
        foreach (var o in GameProfileService.Options) { yield return o.Name; yield return o.Description; }
    }

    [Theory]
    [InlineData(LocalizationService.English)]
    [InlineData(LocalizationService.Spanish)]
    public void PresetsAndGameOptionsAreTranslated(string language)
    {
        var table = LocalizationService.LoadEmbedded(language);
        var missing = GameKeys().Distinct().Where(k => !table.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)).ToList();
        Assert.True(missing.Count == 0, $"{language}: {missing.Count} sem tradução:\n" + string.Join("\n", missing.Take(20)));
    }

    [Theory]
    [InlineData("en", "O jogo não está otimizado: 4 de 10 opções diferentes do recomendado para o nível Médio.",
        "The game is not optimized: 4 of 10 options differ from the recommendation for the Medium level.")]
    [InlineData("es", "27 jogo(s) encontrado(s).", "27 juego(s) encontrado(s).")]
    [InlineData("en", "• Sombras: Média → Alta", "• Shadows: Medium → High")]
    [InlineData("en", "Windows: 1 de 4 ajustes", "Windows: 1 of 4 tweaks")]
    public void GameMessagesAreTranslated(string lang, string pt, string expected)
    {
        var l = LocalizationService.Instance;
        try
        {
            l.SetLanguage(lang);
            Assert.Equal(expected, LocalizationService.Tr(pt));
        }
        finally
        {
            l.SetLanguage(LocalizationService.Portuguese);
        }
    }
}

/// <summary>8.3c lote 3: Hardware, Check-up, Overlay e testes.</summary>
[Collection("Localization")]
public class Phase83cHardwareTests
{
    [Theory]
    [InlineData("en", "CPU: Intel Core i5-7400 — 4 núcleos / 4 threads, até 3000 MHz", "CPU: Intel Core i5-7400 — 4 cores / 4 threads, up to 3000 MHz")]
    [InlineData("en", "Provável dual channel: 2 pentes iguais em canais diferentes. É uma estimativa: o Windows não informa o modo real.",
        "Likely dual channel: 2 identical sticks on different channels. This is an estimate: Windows does not report the real mode.")]
    [InlineData("es", "1.1.1.1: média 12 ms · mín 10 · máx 15 · jitter 1,2 ms · perda 0%", "1.1.1.1: media 12 ms · mín 10 · máx 15 · jitter 1,2 ms · pérdida 0%")]
    [InlineData("en", "Clock: 1905 MHz · Ventoinha: 0 RPM · VRAM: 812 MB / 8192 MB", "Clock: 1905 MHz · Fan: 0 RPM · VRAM: 812 MB / 8192 MB")]
    [InlineData("en", "Atenção", "Attention")]
    public void HardwareMessagesAreTranslated(string lang, string pt, string expected)
    {
        var l = GueibsOptimizer.Core.LocalizationService.Instance;
        try
        {
            l.SetLanguage(lang);
            Assert.Equal(expected, GueibsOptimizer.Core.LocalizationService.Tr(pt));
        }
        finally
        {
            l.SetLanguage(GueibsOptimizer.Core.LocalizationService.Portuguese);
        }
    }
}

/// <summary>8.3c lote 4a: Instalar Apps, Formatação, Perfis, Debloat, Defender, Limpeza, Boost e App.</summary>
[Collection("Localization")]
public class Phase83cAppsTests
{
    [Theory]
    [InlineData(GueibsOptimizer.Core.LocalizationService.English)]
    [InlineData(GueibsOptimizer.Core.LocalizationService.Spanish)]
    public void CategoriesProfilesAndCleanupAreTranslated(string language)
    {
        var table = GueibsOptimizer.Core.LocalizationService.LoadEmbedded(language);
        var keys = GueibsOptimizer.Services.CatalogParser.Categories
            .Concat(GueibsOptimizer.Services.CleanupService.Categories().SelectMany(c => new[] { c.Name, c.Description }))
            .Concat(["Competitivo", "Equilibrado", "Economia"]);
        var missing = keys.Where(k => !table.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)).ToList();
        Assert.True(missing.Count is 0, $"{language}: " + string.Join(" | ", missing));
    }

    [Theory]
    [InlineData(GueibsOptimizer.Core.LocalizationService.English)]
    [InlineData(GueibsOptimizer.Core.LocalizationService.Spanish)]
    public void CatalogDescriptionsAndDebloatReasonsAreTranslated(string language)
    {
        var table = GueibsOptimizer.Core.LocalizationService.LoadEmbedded(language);
        var keys = GueibsOptimizer.Services.CatalogService.Apps.Select(a => a.Description)
            .Concat(GueibsOptimizer.Services.DebloatService.Catalog.Select(e => e.Reason))
            .Where(k => !string.IsNullOrWhiteSpace(k)).Distinct();
        var missing = keys.Where(k => !table.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)).ToList();
        Assert.True(missing.Count is 0, $"{language}: {missing.Count} faltando: " + string.Join(" | ", missing.Take(10)));
    }

    [Theory]
    [InlineData("en", "Hub de Comentários", "Feedback Hub")]
    [InlineData("es", "Bloco de Notas", "Bloc de notas")]
    [InlineData("en", "Navegador da Microsoft baseado em Chromium.", "Microsoft's Chromium-based browser.")]
    [InlineData("es", "Para reinstalar: winget install --id 9NBLGGH4R32N --source msstore (ou pela aba Restauração).", "Para reinstalar: winget install --id 9NBLGGH4R32N --source msstore (o desde la pestaña Restauración).")]
    public void DataTextsAreTranslated(string lang, string pt, string expected)
    {
        var l = GueibsOptimizer.Core.LocalizationService.Instance;
        try
        {
            l.SetLanguage(lang);
            Assert.Equal(expected, GueibsOptimizer.Core.LocalizationService.Tr(pt));
        }
        finally
        {
            l.SetLanguage(GueibsOptimizer.Core.LocalizationService.Portuguese);
        }
    }

    [Theory]
    [InlineData("en", "Relatório: 3 instalados, 1 já existiam, 0 pedem reinício, 0 falharam.", "Report: 3 installed, 1 already existed, 0 need a restart, 0 failed.")]
    [InlineData("en", "Instalando Discord (2 de 5)...", "Installing Discord (2 of 5)...")]
    [InlineData("en", "Etapa atual: 3. Instalar programas", "Current step: 3. Install programs")]
    [InlineData("es", "12 seguros, 8 opcionais, 30 protegidos e 4 não classificados.", "12 seguras, 8 opcionales, 30 protegidas y 4 sin clasificar.")]
    public void AppMessagesAreTranslated(string lang, string pt, string expected)
    {
        var l = GueibsOptimizer.Core.LocalizationService.Instance;
        try
        {
            l.SetLanguage(lang);
            Assert.Equal(expected, GueibsOptimizer.Core.LocalizationService.Tr(pt));
        }
        finally
        {
            l.SetLanguage(GueibsOptimizer.Core.LocalizationService.Portuguese);
        }
    }
}
