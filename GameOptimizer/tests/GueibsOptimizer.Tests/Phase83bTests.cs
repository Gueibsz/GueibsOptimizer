using System.IO;
using System.Reflection;
using System.Text.Json;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.Tests;

/// <summary>8.3b: nomes, descrições, avisos e textos de info dos ajustes, e os textos de ajuda (UiHelp), em en e es.</summary>
[Collection("Localization")]
public class Phase83bTests
{
    public static IReadOnlyList<string> TweakKeys()
    {
        using var key = new TestRegistryKey();
        var registry = TweakRegistry.CreateDefault(key.CreateContext(dryRun: true));
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        void Add(string? s) { if (!string.IsNullOrWhiteSpace(s)) keys.Add(s); }
        foreach (var t in registry.All)
        {
            Add(t.Name);
            Add(t.Description);
            if (t.Id != "gpu.hags") Add(t.Notice); // o aviso do HAGS é montado com o nome da GPU: as partes são verificadas abaixo
            foreach (var s in t.Info.Sections) { Add(s.Header); Add(s.Text); }
        }
        foreach (var r in Enum.GetValues<TweakRisk>()) Add(TweakText.RiskText(r));
        foreach (var c in Enum.GetValues<TweakCategory>()) Add(TweakText.CategoryText(c));
        foreach (var s in new[] { "Risco", "pts", "Não aplicável", "Aplicado", "Não aplicado", "Mais informações",
                     "Requer reinício para ter efeito.", "GPU detectada:", "GPU não identificada", "desconhecido" }) Add(s);
        foreach (var f in typeof(UiHelp).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (f.FieldType == typeof(string)) Add((string?)f.GetValue(null));
        return keys.ToList();
    }

    [Theory]
    [InlineData(LocalizationService.English)]
    [InlineData(LocalizationService.Spanish)]
    public void EveryTweakAndHelpTextIsTranslated(string language)
    {
        var keys = TweakKeys();
        if (Environment.GetEnvironmentVariable("GO_DUMP_KEYS") == "1")
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "claude", "tweak-keys.json"), JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var table = LocalizationService.LoadEmbedded(language);
        // Texto de várias linhas é traduzido linha a linha pelo LocalizationService: basta cada linha ter tradução.
        bool Has(string k) => table.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v);
        var missing = keys.Where(k => !Has(k) && !(k.Contains('\n') && k.Split('\n').Where(l => l.Length > 0).All(Has))).ToList();
        Assert.True(missing.Count == 0, $"{language}: {missing.Count} sem tradução:\n" + string.Join("\n", missing.Take(15)));
    }

    [Fact]
    public void TweakCardTextsFollowTheLanguage()
    {
        using var key = new TestRegistryKey();
        var registry = TweakRegistry.CreateDefault(key.CreateContext(dryRun: true));
        var tweak = registry.All.First(t => t.Id == "gaming.game-mode");
        var vm = new GueibsOptimizer.ViewModels.TweakItemViewModel(tweak, new NullDialogs(), key.Logger);
        var l = LocalizationService.Instance;
        try
        {
            int changes = 0;
            vm.PropertyChanged += (_, _) => changes++;
            l.SetLanguage("en");
            Assert.NotEqual(tweak.Name, vm.Name);
            Assert.StartsWith("Risk", vm.RiskText);
            Assert.True(changes > 0);
        }
        finally
        {
            l.SetLanguage(LocalizationService.Portuguese);
        }
        Assert.Equal(tweak.Name, vm.Name);
    }

    private sealed class NullDialogs : IDialogService
    {
        public bool Confirm(string title, string message) => false;
        public void Info(string title, string message) { }
        public void Error(string title, string message) { }
        public string? PickOpenFile(string filter) => null;
        public string? PickSaveFile(string filter, string defaultName) => null;
        public void CopyText(string text) { }
    }
}
