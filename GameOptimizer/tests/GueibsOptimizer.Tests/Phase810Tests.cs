using System.IO;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;
using Xunit;

namespace GueibsOptimizer.Tests;

public class Phase810Tests
{
    [Fact]
    public void PresetsAndRuntimesUseOnlyCatalogIds()
    {
        var ids = CatalogService.Apps.Select(a => a.WingetId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, apps) in WizardPresets.AppPresets)
            Assert.All(apps, id => Assert.True(ids.Contains(id), $"{name}: {id} fora do catálogo"));
        Assert.All(WizardPresets.Runtimes, id => Assert.Contains(id, ids));
        Assert.Contains("Microsoft.VCRedist.2015+.x64", WizardPresets.Runtimes);
        Assert.Contains("Microsoft.DotNet.DesktopRuntime.8", WizardPresets.Runtimes);
        Assert.Contains("Microsoft.DirectX", WizardPresets.Runtimes);
    }

    [Fact]
    public void DebloatChoicesNeverIncludeAggressive()
    {
        Assert.DoesNotContain(nameof(DebloatPreset.Agressivo), WizardPresets.DebloatChoices);
        Assert.Null(WizardPresets.ParseDebloat(WizardPresets.NoDebloat));
        Assert.Null(WizardPresets.ParseDebloat(nameof(DebloatPreset.Agressivo)));
        Assert.Equal(DebloatPreset.Leve, WizardPresets.ParseDebloat("Leve"));
        Assert.Equal(DebloatPreset.Equilibrado, WizardPresets.ParseDebloat("Equilibrado"));
    }

    [Fact]
    public void ExtrasRunBeforeSummary()
    {
        Assert.Equal(WizardState.StepInstall, WizardPresets.Next(WizardState.StepRestorePoint));
        Assert.Equal(WizardState.StepExtras, WizardPresets.Next(WizardState.StepOptimize));
        Assert.Equal(WizardState.StepSummary, WizardPresets.Next(WizardState.StepExtras));
    }

    [Fact]
    public void StateWithExtrasIsResumable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"go-wizard-{Guid.NewGuid():N}.json");
        try
        {
            new WizardState { Step = WizardState.StepExtras, DebloatPreset = "Equilibrado", InstallRuntimes = false, DebloatDone = true, Apps = ["Valve.Steam"] }.Save(path);
            var s = WizardState.Load(path);
            Assert.NotNull(s);
            Assert.Equal(WizardState.StepExtras, s!.Step);
            Assert.Equal("Equilibrado", s.DebloatPreset);
            Assert.False(s.InstallRuntimes);
            Assert.True(s.ApplyPowerPlan);
            Assert.True(s.DebloatDone);
            Assert.Equal("5. Extras: runtimes e plano de energia", s.StepTitle);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OldStateFilesStillLoadWithSafeDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"go-wizard-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ \"Step\": 4, \"ProfileName\": \"Competitivo\", \"Apps\": [] }");
            var s = WizardState.Load(path)!;
            Assert.Equal("Leve", s.DebloatPreset);
            Assert.True(s.InstallRuntimes);
            Assert.False(s.DebloatDone);
        }
        finally { File.Delete(path); }
    }
}
