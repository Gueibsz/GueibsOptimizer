using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using GueibsOptimizer.Core;
using GueibsOptimizer.ViewModels;

namespace GueibsOptimizer.Tests;

/// <summary>Idiomas: toda chave usada na interface existe em en e es, sem tradução vazia; troca ao vivo e fallback.</summary>
[Collection("Localization")]
public class Phase83Tests
{
    private static string Src(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GueibsOptimizer.sln"))) dir = dir.Parent;
        return Path.Combine([dir!.FullName, "src", "GueibsOptimizer", .. parts]);
    }

    /// <summary>Chaves {l:T '...'} de todos os XAML, já sem escapes (\' e \\) e entidades XML.</summary>
    public static IReadOnlyList<string> XamlKeys()
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        var re = new Regex(@"\{l:T '((?:[^'\\]|\\.)*)'\}");
        foreach (var file in Directory.EnumerateFiles(Src(), "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in re.Matches(File.ReadAllText(file)))
                keys.Add(WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, @"\\(.)", "$1")));
        }
        return keys.ToList();
    }

    public static IEnumerable<string> CodeKeys() =>
        NavLayout.Groups.Select(g => g.Group).Concat(NavLayout.Groups.SelectMany(g => g.Titles))
            .Concat(["Idioma"]);

    [Theory]
    [InlineData(LocalizationService.English)]
    [InlineData(LocalizationService.Spanish)]
    public void EveryUiKeyIsTranslatedAndNotEmpty(string language)
    {
        var table = LocalizationService.LoadEmbedded(language);
        var keys = XamlKeys();
        Assert.True(keys.Count > 200, $"poucas chaves encontradas: {keys.Count}");
        var missing = keys.Concat(CodeKeys()).Where(k => !table.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)).Distinct().ToList();
        Assert.True(missing.Count == 0, $"{language}: sem tradução:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void EnglishAndSpanishHaveTheSameKeys()
    {
        var en = LocalizationService.LoadEmbedded(LocalizationService.English).Keys.ToHashSet();
        var es = LocalizationService.LoadEmbedded(LocalizationService.Spanish).Keys.ToHashSet();
        Assert.Empty(en.Except(es));
        Assert.Empty(es.Except(en));
        Assert.All(LocalizationService.LoadEmbedded(LocalizationService.English).Values.Concat(LocalizationService.LoadEmbedded(LocalizationService.Spanish).Values),
            v => Assert.False(string.IsNullOrWhiteSpace(v)));
    }

    [Fact]
    public void NoFixedTextLeftInXamlAttributes()
    {
        var attr = new Regex(@"(?<![\w:.])(Text|Content|Title|Header|ToolTip|ShortTip)=""([^""{][^""]*)""");
        var leftovers = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Src("Views"), "*.xaml", SearchOption.AllDirectories).Append(Src("App.xaml")))
            foreach (Match m in attr.Matches(File.ReadAllText(file)))
                if (Regex.IsMatch(m.Groups[2].Value, "[A-Za-zÀ-ú]{2}") && !m.Groups[2].Value.StartsWith("&#x", StringComparison.Ordinal))
                    leftovers.Add($"{Path.GetFileName(file)}: {m.Value}");
        Assert.True(leftovers.Count == 0, string.Join("\n", leftovers));
    }

    [Fact]
    public void SwitchesLiveAndFallsBackToPortuguese()
    {
        var l = LocalizationService.Instance;
        try
        {
            var text = new LocalizedText("Salvar");
            int changes = 0;
            text.PropertyChanged += (_, _) => changes++;

            l.SetLanguage("en");
            Assert.Equal("Save", text.Value);
            Assert.Equal("Save", LocalizationService.Tr("Salvar"));
            Assert.Equal("Texto que não existe", l.T("Texto que não existe"));
            l.SetLanguage("es");
            Assert.Equal("Guardar", text.Value);
            Assert.True(changes >= 2);

            l.SetLanguage("klingon");
            Assert.Equal(LocalizationService.Portuguese, l.Language);
            Assert.Equal("Salvar", text.Value);
        }
        finally
        {
            l.SetLanguage(LocalizationService.Portuguese);
        }
    }

    [Fact]
    public void LanguageIsPersistedInSettings()
    {
        Assert.Equal("pt-BR", new AppSettings().Language);
        Assert.Equal("en", LocalizationService.Normalize("EN"));
        Assert.Equal("Español", LocalizationService.DisplayName("es"));
        Assert.Contains("LanguageOptions", File.ReadAllText(Src("Views", "SettingsView.xaml")));
    }
}
