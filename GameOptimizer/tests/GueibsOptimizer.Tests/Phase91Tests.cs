using GueibsOptimizer.Models;
using GueibsOptimizer.Services;
using GueibsOptimizer.Services.Tweaks;
using Microsoft.Win32;
using Xunit;

namespace GueibsOptimizer.Tests;

public class Phase91Tests
{
    private static string One(string body) => "{ \"version\": 1, \"tweaks\": [ " + body + " ] }";

    private static string Def(string id = "test.sample", string extra = "", string risk = "Low", string level = "Baixo", string actions = null!) =>
        "{ \"id\": \"" + id + "\", \"category\": \"Privacy\", \"risk\": \"" + risk + "\", \"points\": 2, \"level\": \"" + level + "\", " +
        "\"name\": \"Ajuste de teste\", \"description\": \"Descrição de teste.\", " + extra +
        (actions ?? "\"registry\": [ { \"hive\": \"HKCU\", \"path\": \"Software\\\\X\", \"name\": \"V\", \"type\": \"DWord\", \"value\": 1, \"default\": null } ]") + " }";

    [Fact]
    public void ValidDefinitionParses()
    {
        var list = DeclarativeTweakLoader.Parse(One(Def()));
        var d = Assert.Single(list);
        Assert.Equal("test.sample", d.Id);
        Assert.Equal([10, 11], d.WindowsVersions);
    }

    [Theory]
    [InlineData("\"risk\": \"Enorme\"")]
    [InlineData("\"level\": \"Turbo\"")]
    [InlineData("\"category\": \"Nada\"")]
    public void InvalidEnumsAreRejected(string replacement)
    {
        var json = One(Def()).Replace("\"risk\": \"Low\"", replacement.StartsWith("\"risk") ? replacement : "\"risk\": \"Low\"")
            .Replace("\"level\": \"Baixo\"", replacement.StartsWith("\"level") ? replacement : "\"level\": \"Baixo\"")
            .Replace("\"category\": \"Privacy\"", replacement.StartsWith("\"category") ? replacement : "\"category\": \"Privacy\"");
        Assert.Throws<DeclarativeValidationException>(() => DeclarativeTweakLoader.Parse(json));
    }

    [Fact]
    public void DuplicateIdsAreRejected()
    {
        var ex = Assert.Throws<DeclarativeValidationException>(() => DeclarativeTweakLoader.Parse(One(Def() + "," + Def())));
        Assert.Contains(ex.Errors, e => e.Contains("duplicado"));
        // Também contra os ids dos ajustes nativos.
        Assert.Throws<DeclarativeValidationException>(() => DeclarativeTweakLoader.Parse(One(Def("power.plan")), ["power.plan"]));
    }

    [Fact]
    public void CommandsOutsideAllowlistAreRejected()
    {
        var ex = Assert.Throws<DeclarativeValidationException>(() =>
            DeclarativeTweakLoader.Parse(One(Def(actions: "\"commands\": [ \"format-c\" ]"))));
        Assert.Contains(ex.Errors, e => e.Contains("allowlist"));
        Assert.Single(DeclarativeTweakLoader.Parse(One(Def(actions: "\"commands\": [ \"hibernate-off\" ]"))));
    }

    [Theory]
    [InlineData("WinDefend")]
    [InlineData("wuauserv")]
    [InlineData("MpsSvc")]
    [InlineData("BITS")]
    public void ProtectedServicesAreRejected(string service)
    {
        Assert.Throws<DeclarativeValidationException>(() => DeclarativeTweakLoader.Parse(One(Def(actions:
            "\"services\": [ { \"name\": \"" + service + "\", \"startup\": \"Manual\", \"default\": \"Automatic\" } ]"))));
    }

    [Fact]
    public void HighRiskCannotBeInBoostAndBadPathsFail()
    {
        Assert.Throws<DeclarativeValidationException>(() => DeclarativeTweakLoader.Parse(One(Def(risk: "High", extra: "\"inBoost\": true, "))));
        Assert.Throws<DeclarativeValidationException>(() => DeclarativeTweakLoader.Parse(One(Def(actions:
            "\"registry\": [ { \"hive\": \"HKCR\", \"path\": \"..\\\\x\", \"name\": \"V\", \"type\": \"Binary\", \"value\": 1 } ]"))));
        Assert.Throws<DeclarativeValidationException>(() => DeclarativeTweakLoader.Parse(One(Def(actions: "\"registry\": []"))));
        Assert.Throws<DeclarativeValidationException>(() => DeclarativeTweakLoader.Parse(One(Def(actions:
            "\"scheduledTasks\": [ { \"path\": \"\\\\Terceiros\\\\Tarefa\", \"enabled\": false } ]"))));
    }

    [Fact]
    public void EmbeddedTweaksJsonIsValid()
    {
        using var key = new TestRegistryKey();
        // CreateDefault já carrega e valida o tweaks.json embutido junto com os nativos.
        var registry = TweakRegistry.CreateDefault(key.CreateContext(dryRun: true));
        Assert.NotEmpty(registry.All);
    }

    private static DeclarativeTweak Build(TestRegistryKey key, string type, string value, string? def)
    {
        string path = key.Path.Replace("\\", "\\\\");
        var json = One(Def(actions: "\"registry\": [ { \"hive\": \"HKCU\", \"path\": \"" + path + "\\\\Sub\", \"name\": \"Valor\", \"type\": \"" + type +
                                    "\", \"value\": " + value + ", \"default\": " + (def ?? "null") + " } ]"));
        return new DeclarativeTweak(DeclarativeTweakLoader.Parse(json)[0], key.CreateContext());
    }

    [Fact]
    public async Task ApplyAndRevertRestoresMissingValue()
    {
        using var key = new TestRegistryKey();
        var t = Build(key, "DWord", "1", null);
        Assert.True(t.IsApplicable());
        Assert.False(t.IsApplied());
        await t.ApplyAsync();
        Assert.True(t.IsApplied());
        Assert.Equal(1, key.ReadRaw("Sub", "Valor"));
        Assert.Equal(RegistryValueKind.DWord, key.Kind("Sub", "Valor"));
        await t.RevertAsync();
        Assert.Null(key.ReadRaw("Sub", "Valor")); // "não existia" volta a não existir
        Assert.False(t.IsApplied());
    }

    [Fact]
    public async Task ApplyAndRevertRestoresPreviousValueAndType()
    {
        using var key = new TestRegistryKey();
        key.Write("Sub", "Valor", "antigo", RegistryValueKind.String);
        var t = Build(key, "String", "\"novo\"", "\"padrao\"");
        await t.ApplyAsync();
        Assert.Equal("novo", key.ReadRaw("Sub", "Valor"));
        await t.RevertAsync();
        Assert.Equal("antigo", key.ReadRaw("Sub", "Valor"));
        Assert.Equal(RegistryValueKind.String, key.Kind("Sub", "Valor"));
    }

    [Fact]
    public async Task RevertWithoutBackupGoesToWindowsDefault()
    {
        using var key = new TestRegistryKey();
        key.Write("Sub", "Valor", 1, RegistryValueKind.DWord); // aplicado "fora do app"
        var t = Build(key, "DWord", "1", "0");
        Assert.True(t.IsApplied());
        await t.RevertAsync();
        Assert.Equal(0, key.ReadRaw("Sub", "Valor"));
    }

    [Fact]
    public async Task DryRunChangesNothing()
    {
        using var key = new TestRegistryKey();
        string path = key.Path.Replace("\\", "\\\\");
        var json = One(Def(actions: "\"registry\": [ { \"hive\": \"HKCU\", \"path\": \"" + path + "\\\\Sub\", \"name\": \"Valor\", \"type\": \"DWord\", \"value\": 1 } ]"));
        var t = new DeclarativeTweak(DeclarativeTweakLoader.Parse(json)[0], key.CreateContext(dryRun: true));
        await t.ApplyAsync();
        Assert.Null(key.ReadRaw("Sub", "Valor"));
    }

    [Fact]
    public void MissingServiceOrTaskIsNotApplicable()
    {
        using var key = new TestRegistryKey();
        var json = One(Def(actions:
            "\"services\": [ { \"name\": \"ServicoQueNaoExiste123\", \"startup\": \"Manual\", \"default\": \"Automatic\" } ], " +
            "\"scheduledTasks\": [ { \"path\": \"\\\\Microsoft\\\\Windows\\\\NaoExiste\\\\Tarefa123\", \"enabled\": false } ]"));
        var t = new DeclarativeTweak(DeclarativeTweakLoader.Parse(json)[0], key.CreateContext(dryRun: true));
        Assert.False(t.IsApplicable());
        Assert.True(t.IsApplied()); // nada a fazer: nunca erro
    }

    [Fact]
    public void WindowsVersionGate()
    {
        using var key = new TestRegistryKey();
        int other = GueibsOptimizer.Core.OsInfo.IsWindows11 ? 10 : 11;
        var t = new DeclarativeTweak(DeclarativeTweakLoader.Parse(One(Def(extra: $"\"windowsVersions\": [ {other} ], ")))[0], key.CreateContext(dryRun: true));
        Assert.False(t.IsApplicable());
    }

    [Theory]
    [InlineData("Baixo", PerformanceLevel.Baixo, true)]
    [InlineData("Alto", PerformanceLevel.Medio, false)]
    [InlineData("Alto", PerformanceLevel.Alto, true)]
    [InlineData("Maximo", PerformanceLevel.Maximo, true)]
    public void ExplicitLevelIsUsed(string level, PerformanceLevel query, bool expected)
    {
        using var key = new TestRegistryKey();
        var t = new DeclarativeTweak(DeclarativeTweakLoader.Parse(One(Def(level: level, extra: "\"inBoost\": true, ")))[0], key.CreateContext(dryRun: true));
        Assert.Equal(expected, PerformanceLevels.Includes(query, t));
    }

    [Fact]
    public void ServiceStartupMapping()
    {
        Assert.Equal((2u, (uint?)0), DeclarativeTweak.StartupValues("Automatic"));
        Assert.Equal((2u, (uint?)1), DeclarativeTweak.StartupValues("AutomaticDelayed"));
        Assert.Equal((3u, (uint?)null), DeclarativeTweak.StartupValues("Manual"));
        Assert.Equal((4u, (uint?)null), DeclarativeTweak.StartupValues("Disabled"));
    }
}
