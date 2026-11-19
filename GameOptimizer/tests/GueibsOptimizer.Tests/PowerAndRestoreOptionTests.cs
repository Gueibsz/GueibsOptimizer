using GueibsOptimizer.Services;
using GueibsOptimizer.Services.Tweaks;
using Xunit;

namespace GueibsOptimizer.Tests;

public class PowerPlanTests
{
    // Trecho real do formato de "powercfg /qh" (pt-BR). Só GUIDs e números importam.
    private const string Query = """
        GUID do Esquema de Energia: 11111111-2222-3333-4444-555555555555  (Gueibs Optimizer Performance)
          GUID do Subgrupo: 7516b95f-f776-4464-8c53-06167f40cc99  (Vídeo)
            GUID da Configuração de Energia: 3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e  (Desligar vídeo após)
              Configuração Mínima Possível: 0x00000000
              Configuração Máxima Possível: 0xffffffff
              Incremento de Configurações Possíveis: 0x00000001
              Unidades de Configurações Possíveis: Segundos
            Índice de Configuração de Energia CA Atual: 0x00000384
            Índice de Configuração de Energia CC Atual: 0x0000012c

            GUID da Configuração de Energia: 8ec4b3a5-6868-48c2-be75-4f3044be88a7  (Tempo limite do console)
              Configuração Mínima Possível: 0x00000000
              Configuração Máxima Possível: 0xffffffff
              Incremento de Configurações Possíveis: 0x00000001
              Unidades de Configurações Possíveis: Segundos
            Índice de Configuração de Energia CA Atual: 0x0000003c
            Índice de Configuração de Energia CC Atual: 0x0000003c
          GUID do Subgrupo: 2a737441-1930-4402-8d77-b2bebba308a3  (Configurações USB)
            GUID da Configuração de Energia: 48e6b7a6-50f5-4782-a5d4-53bb8f07e226  (Suspensão seletiva)
              Índice de Configuração Possível: 000
              Nome Amigável da Configuração Possível: Desabilitado
              Índice de Configuração Possível: 001
              Nome Amigável da Configuração Possível: Habilitado
            Índice de Configuração de Energia CA Atual: 0x00000001
            Índice de Configuração de Energia CC Atual: 0x00000001
          GUID do Subgrupo: 54533251-82be-4824-96c1-47b60b740d00  (Gerenciamento de energia do processador)
            GUID da Configuração de Energia: 893dee8e-2bef-41e0-89c6-b55d0929964c  (Estado mínimo)
              Configuração Mínima Possível: 0x00000000
              Configuração Máxima Possível: 0x00000064
              Incremento de Configurações Possíveis: 0x00000001
              Unidades de Configurações Possíveis: %
            Índice de Configuração de Energia CA Atual: 0x00000064
            Índice de Configuração de Energia CC Atual: 0x00000005
        """;

    [Fact]
    public void ParsesPowercfgQueryInAnyLanguage()
    {
        var s = PowerPlanSettings.ParseQuery(Query);
        Assert.Equal((900u, 300u), s["3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"]);
        Assert.Equal((60u, 60u), s["8ec4b3a5-6868-48c2-be75-4f3044be88a7"]);
        Assert.Equal((1u, 1u), s["48e6b7a6-50f5-4782-a5d4-53bb8f07e226"]);
        Assert.Equal((100u, 5u), s["893dee8e-2bef-41e0-89c6-b55d0929964c"]);
        // Plano e subgrupos não viram configurações; o que não aparece não existe nesta máquina.
        Assert.False(s.ContainsKey("11111111-2222-3333-4444-555555555555"));
        Assert.False(s.ContainsKey(PowerPlanSettings.SubVideo));
        Assert.False(s.ContainsKey("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"));
        Assert.Empty(PowerPlanSettings.ParseQuery(""));
    }

    [Fact]
    public void DryRunCommandsUseArgumentListAndSkipMissingSettings()
    {
        var state = PowerPlanSettings.ParseQuery(Query);
        var (commands, missing) = PowerPlanSettings.BuildCommands("PLANO", PowerPlanSettings.ForTier(PowerTier.Base), state, hasBattery: false);
        Assert.Contains(commands, c => c.Arguments.SequenceEqual(["/setacvalueindex", "PLANO", PowerPlanSettings.SubVideo, "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e", "0"]));
        Assert.Contains(commands, c => c.Arguments.SequenceEqual(["/setdcvalueindex", "PLANO", PowerPlanSettings.SubVideo, "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e", "0"]));
        // Monitor na tela de bloqueio também vai para "Nunca".
        Assert.Contains(commands, c => c.Arguments.Contains("8ec4b3a5-6868-48c2-be75-4f3044be88a7") && c.Arguments[^1] == "0");
        Assert.Equal(4, commands.Count); // tela e tela de bloqueio, AC e DC
        Assert.Contains(missing, m => m.Setting == "29f6c1db-86da-48c5-9fdb-f2b67b1f44da"); // suspender: não existe aqui → não aplicável
        Assert.All(commands, c => Assert.Equal(5, c.Arguments.Count));
    }

    [Fact]
    public void CommandsAreIdempotent()
    {
        var state = new Dictionary<string, (uint, uint)>(PowerPlanSettings.ParseQuery(Query), StringComparer.OrdinalIgnoreCase)
        {
            ["3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"] = (0, 0),
            ["8ec4b3a5-6868-48c2-be75-4f3044be88a7"] = (0, 0)
        };
        var (commands, _) = PowerPlanSettings.BuildCommands("PLANO", PowerPlanSettings.ForTier(PowerTier.Base), state, hasBattery: false);
        Assert.Empty(commands); // já no alvo: nada a fazer
    }

    [Fact]
    public void LaptopKeepsPerformanceSettingsOnlyOnAc()
    {
        var state = PowerPlanSettings.ParseQuery(Query);
        var (adv, _) = PowerPlanSettings.BuildCommands("PLANO", PowerPlanSettings.ForTier(PowerTier.Advanced), state, hasBattery: true);
        Assert.Contains(adv, c => c.Arguments[0] == "/setacvalueindex" && c.Arguments[3] == "48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
        Assert.DoesNotContain(adv, c => c.Arguments[0] == "/setdcvalueindex");
        var (baseCmds, _) = PowerPlanSettings.BuildCommands("PLANO", PowerPlanSettings.ForTier(PowerTier.Base), state, hasBattery: true);
        Assert.Contains(baseCmds, c => c.Arguments[0] == "/setdcvalueindex"); // tempos: tomada e bateria
    }

    [Fact]
    public void AdvancedDoesNotLowerMaximumProcessorState()
    {
        var state = PowerPlanSettings.ParseQuery(Query); // mínimo 100% na tomada, 5% na bateria
        var (adv, _) = PowerPlanSettings.BuildCommands("PLANO", PowerPlanSettings.ForTier(PowerTier.Advanced), state, hasBattery: false);
        Assert.DoesNotContain(adv, c => c.Arguments[3] == "893dee8e-2bef-41e0-89c6-b55d0929964c"); // 100 e 5 já atendem "pelo menos 5%"
        var (max, _) = PowerPlanSettings.BuildCommands("PLANO", PowerPlanSettings.ForTier(PowerTier.Maximum), state, hasBattery: false);
        Assert.Contains(max, c => c.Arguments[0] == "/setdcvalueindex" && c.Arguments[3] == "893dee8e-2bef-41e0-89c6-b55d0929964c" && c.Arguments[4] == "100");
    }

    [Fact]
    public void TimeoutsAreNeverAndCStatesAreNeverTouched()
    {
        Assert.All(PowerPlanSettings.ForTier(PowerTier.Base), s => Assert.Equal(0u, s.Value));
        Assert.Contains(PowerPlanSettings.ForTier(PowerTier.Base), s => s.Setting == "bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d"); // timers de despertar
        Assert.DoesNotContain(PowerPlanSettings.All, s => s.Setting == PowerPlanSettings.ProcessorIdleDisable);
    }

    [Fact]
    public void LevelsMapToTiers()
    {
        using var key = new TestRegistryKey();
        var reg = TweakRegistry.CreateDefault(key.CreateContext(dryRun: true));
        var plan = reg.Find("power.plan")!;
        var adv = reg.Find(PowerPlanAdvancedTweak.TweakId)!;
        var max = reg.Find(PowerPlanMaximumTweak.TweakId)!;
        Assert.True(PerformanceLevels.Includes(PerformanceLevel.Baixo, plan));
        Assert.True(PerformanceLevels.Includes(PerformanceLevel.Medio, plan));
        Assert.False(PerformanceLevels.Includes(PerformanceLevel.Medio, adv));
        Assert.True(PerformanceLevels.Includes(PerformanceLevel.Alto, adv));
        Assert.False(PerformanceLevels.Includes(PerformanceLevel.Alto, max));
        Assert.True(PerformanceLevels.Includes(PerformanceLevel.Maximo, max));
        Assert.Contains("pequeno", plan.Info.Benefit); // honestidade sobre o ganho de FPS
        Assert.Contains("calor", plan.Info.RiskDetails);
    }

    [Fact]
    public void PlanIsReusedAndOnlyDeletedIfCreatedByApp()
    {
        var schemes = PowerCfgParser.ParseList("""
            GUID do Esquema de Energia: 381b4222-f694-41f0-9685-ff5bb260df2e  (Equilibrado)
            GUID do Esquema de Energia: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee  (Gueibs Optimizer Performance) *
            """);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", PowerPlanTweak.FindOurPlan(schemes));
        Assert.Null(PowerPlanTweak.FindOurPlan(PowerCfgParser.ParseList("GUID do Esquema de Energia: 381b4222-f694-41f0-9685-ff5bb260df2e  (Equilibrado) *")));
        Assert.False(PowerPlanTweak.ShouldDeleteOnRevert(null, PowerPlanTweak.BalancedGuid)); // não foi o app que criou
        Assert.True(PowerPlanTweak.ShouldDeleteOnRevert("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", PowerPlanTweak.BalancedGuid));
        Assert.False(PowerPlanTweak.ShouldDeleteOnRevert(PowerPlanTweak.BalancedGuid, PowerPlanTweak.BalancedGuid));
    }

    [Fact]
    public async Task DryRunApplyChangesNothingAndSavesNoBackup()
    {
        using var key = new TestRegistryKey();
        var ctx = key.CreateContext(dryRun: true);
        var simulated = new List<string>();
        key.Logger.EntryLogged += e => { if (e.Level == GueibsOptimizer.Core.LogLevel.Simulation) simulated.Add(e.Message); };
        await new PowerPlanTweak(ctx).ApplyAsync();
        Assert.Empty(key.Store.ForTweak("power.plan"));
        // Em simulação só aparecem comandos do powercfg e o aviso "Backup de ..." (que não grava nada, ver acima).
        Assert.All(simulated, m => Assert.True(m.Contains("powercfg") || m.StartsWith("Backup de '"), m));
    }
}

public class RestorePointOptionTests
{
    private sealed class CountingRestorePoints : IRestorePointService
    {
        public int Calls;
        public Task<RestorePointResult> CreateAsync(string description, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new RestorePointResult(true, "criado"));
        }
    }

    [Fact]
    public async Task RestorePointIsOptional()
    {
        var inner = new CountingRestorePoints();
        bool enabled = true;
        var svc = new OptionalRestorePointService(inner, () => enabled, new GueibsOptimizer.Core.Logger(null));
        Assert.Equal("criado", (await svc.CreateAsync("x")).Message);
        enabled = false;
        var skipped = await svc.CreateAsync("x");
        Assert.True(skipped.Success);
        Assert.Equal(OptionalRestorePointService.SkippedMessage, skipped.Message);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public void RestorePointOptionDefaultsToOnAndIsSaved()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"go-settings-{Guid.NewGuid():N}.json");
        try
        {
            Assert.True(new GueibsOptimizer.Core.AppSettings().CreateRestorePoints);
            new GueibsOptimizer.Core.AppSettings { CreateRestorePoints = false }.Save(path);
            Assert.False(GueibsOptimizer.Core.AppSettings.Load(path).CreateRestorePoints);
        }
        finally { System.IO.File.Delete(path); }
    }
}
