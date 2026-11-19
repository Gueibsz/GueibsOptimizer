using GueibsOptimizer.Services;
using GueibsOptimizer.Services.Tweaks;
using GueibsOptimizer.ViewModels;
using Xunit;

namespace GueibsOptimizer.Tests;

public class Phase99Tests
{
    private static TweakRegistry Reg(TestRegistryKey k) => TweakRegistry.CreateDefault(k.CreateContext(dryRun: true));

    [Fact]
    public void NewTweaksAreOutOfBoostAndHaveNotices()
    {
        using var key = new TestRegistryKey();
        var reg = Reg(key);
        foreach (var id in new[] { "debloat.onedrive-sync-off", "windows.visual-effects-performance", "windows.rtc-utc", "services.intel-lms-off" })
        {
            var t = Assert.IsType<DeclarativeTweak>(reg.Find(id));
            Assert.False(t.InBoost);
            Assert.NotNull(t.Notice);
        }
    }

    [Fact]
    public void VisualEffectsKeepFontSmoothing()
    {
        using var key = new TestRegistryKey();
        var t = (DeclarativeTweak)Reg(key).Find("windows.visual-effects-performance")!;
        Assert.DoesNotContain(t.Definition.Registry, r => r.Name.Contains("FontSmoothing"));
        Assert.Contains(t.Definition.Registry, r => r.Name == "VisualFXSetting" && r.Value.GetInt32() == 3);
    }

    [Fact]
    public void IntelLmsOnlyOutsideDomain()
    {
        using var key = new TestRegistryKey();
        var t = (DeclarativeTweak)Reg(key).Find("services.intel-lms-off")!;
        Assert.True(t.Definition.NotOnDomain);
        Assert.Equal("LMS", Assert.Single(t.Definition.Services).Name);
        if (DeclarativeTweak.IsDomainJoined.Value) Assert.False(t.IsApplicable());
    }

    [Fact]
    public void OneDriveAndShutUpUseValidatedWingetIdsOnly()
    {
        Assert.Equal(["uninstall", "--id", "Microsoft.OneDrive", "-e", "--silent", "--accept-source-agreements"], ExtraActionsService.UninstallArgs(ExtraActionsService.OneDriveId));
        Assert.Equal("OO-Software.ShutUp10", ExtraActionsService.ShutUpId);
        Assert.StartsWith("https://www.oo-software.com/", ExtraActionsService.ShutUpUrl);
        Assert.Contains("debloat.onedrive-sync-off", DebloatViewModel.ExtraTweakIds);
        Assert.Contains("windows.classic-context-menu", DebloatViewModel.ExtraTweakIds); // já existia (7.7)
    }

    [Fact]
    public async Task DryRunUninstallChangesNothing()
    {
        using var key = new TestRegistryKey();
        var svc = new ExtraActionsService(key.CreateContext(dryRun: true));
        Assert.Null(await svc.UninstallOneDriveAsync());
        Assert.Empty(key.Store.ForTweak(ExtraActionsService.OneDriveUndoId));
    }

    [Fact]
    public void NoForbiddenActionsInDeclarativeFile()
    {
        // Regra 7 da Fase 9: nada de remover o Edge, desativar UAC/Defender/Windows Update de vez nem bloquear hosts.
        using var key = new TestRegistryKey();
        var all = Reg(key).All.OfType<DeclarativeTweak>().SelectMany(t => t.Definition.Registry.Select(r => r.Name)).ToList();
        Assert.DoesNotContain("EnableLUA", all);
        Assert.DoesNotContain("DisableAntiSpyware", all);
        Assert.DoesNotContain("NoAutoUpdate", all);
    }
}
