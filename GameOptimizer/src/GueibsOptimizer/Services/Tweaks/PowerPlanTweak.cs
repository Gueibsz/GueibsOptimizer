using System.Management;
using System.Text.RegularExpressions;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services.Tweaks;

public sealed record PowerScheme(string Guid, string Name, bool Active);

/// <summary>Parser da saída do powercfg (independente de idioma: só usa GUID, nome entre parênteses e o '*').</summary>
public static partial class PowerCfgParser
{
    [GeneratedRegex(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*(?:\((.*?)\))?\s*(\*)?\s*$", RegexOptions.Multiline)]
    private static partial Regex SchemeLine();

    public static IReadOnlyList<PowerScheme> ParseList(string output) =>
        SchemeLine().Matches(output)
            .Select(m => new PowerScheme(m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.Trim(), m.Groups[3].Success))
            .ToList();

    public static string? FirstGuid(string output) => ParseList(output).FirstOrDefault()?.Guid;
}

/// <summary>Ajuste que sabe dizer se foi aplicado pelo app e depois mudou fora dele ("Desviado" → "Reaplicar").</summary>
public interface IDriftAware
{
    bool WasAppliedByApp { get; }
}

/// <summary>
/// Base das três camadas do plano de energia. Cada configuração é lida com "powercfg /qh" antes de mudar; o que não existe
/// nesta máquina vira "não aplicável" e é pulado sem erro. Os valores anteriores (tomada e bateria) ficam no backup.
/// </summary>
public abstract class PowerTierTweak(TweakContext ctx, PowerTier tier) : ITweak, ILeveledTweak, IDriftAware
{
    protected TweakContext Ctx { get; } = ctx;
    public PowerTier Tier { get; } = tier;
    private const string SettingPrefix = "powersetting:";

    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public TweakCategory Category => TweakCategory.Power;
    public abstract TweakRisk Risk { get; }
    public abstract int Points { get; }
    public abstract PerformanceLevel Level { get; }

    public virtual string? Notice => HasBattery.Value
        ? "Notebook detectado: este plano gasta mais bateria. Os tempos ficam em \"Nunca\" também na bateria; as configurações de desempenho mudam só na tomada."
        : null;

    protected IReadOnlyList<PowerSettingDef> Settings => PowerPlanSettings.ForTier(Tier);

    public bool WasAppliedByApp => Ctx.Backup.ForTweak(Id).Count > 0;

    /// <summary>PC com bateria (Win32_Battery presente). Lido uma vez.</summary>
    public static readonly Lazy<bool> HasBattery = new(() =>
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Battery");
            using var r = s.Get();
            return r.Count > 0;
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException) { return false; }
    });

    /// <summary>O nosso plano, se existir; senão o plano ativo.</summary>
    protected string TargetScheme(IReadOnlyList<PowerScheme> schemes) =>
        PowerPlanTweak.FindOurPlan(schemes) ?? "SCHEME_CURRENT";

    protected async Task<IReadOnlyList<PowerScheme>> ListSchemesAsync()
    {
        var result = await Ctx.Shell.RunAsync("powercfg.exe", ["/list"]).ConfigureAwait(false);
        return result.Success ? PowerCfgParser.ParseList(result.StdOut) : [];
    }

    protected async Task<IReadOnlyDictionary<string, (uint Ac, uint Dc)>> QueryAsync(string scheme)
    {
        var r = await Ctx.Shell.RunAsync("powercfg.exe", ["/qh", scheme], TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        return r.Success ? PowerPlanSettings.ParseQuery(r.StdOut) : new Dictionary<string, (uint, uint)>();
    }

    public virtual TweakInfo Info => new(
        Description,
        string.Join("\n", Settings.Select(s => $"• {s.Title}")),
        "Clock estável e sem dispositivos \"adormecendo\" durante o jogo. O ganho de FPS médio costuma ser pequeno (0 a 3%); o benefício principal é a estabilidade e menos travadinhas.",
        "Mais calor, consumo de energia e ruído das ventoinhas, principalmente com o PC parado. Os estados de ociosidade do processador (C-states) nunca são desativados.",
        "Botão Desfazer ou aba Restauração: cada valor volta ao que estava antes (tomada e bateria). Se algo mudar fora do app, o cartão mostra \"Desviado\" e o botão Reaplicar.",
        "Cada configuração é lida com powercfg /qh antes de mudar; as que não existem nesta máquina são puladas (não aplicáveis).");

    public virtual bool IsApplicable()
    {
        var state = QueryAsync(TargetScheme(ListSchemesAsync().GetAwaiter().GetResult())).GetAwaiter().GetResult();
        return Settings.Any(s => state.ContainsKey(s.Setting));
    }

    public virtual bool IsApplied()
    {
        var state = QueryAsync(TargetScheme(ListSchemesAsync().GetAwaiter().GetResult())).GetAwaiter().GetResult();
        return SettingsSatisfied(state);
    }

    protected bool SettingsSatisfied(IReadOnlyDictionary<string, (uint Ac, uint Dc)> state) =>
        Settings.All(s => !state.TryGetValue(s.Setting, out var cur) || PowerPlanSettings.IsSatisfied(s, cur, PowerPlanSettings.ChangesDc(s, HasBattery.Value)));

    public virtual async Task ApplyAsync()
    {
        var scheme = TargetScheme(await ListSchemesAsync().ConfigureAwait(false));
        await ApplySettingsAsync(scheme).ConfigureAwait(false);
    }

    /// <summary>Aplica as configurações da camada no plano indicado e reativa o plano para valer na hora.</summary>
    protected async Task ApplySettingsAsync(string scheme)
    {
        var state = await QueryAsync(scheme).ConfigureAwait(false);
        var (commands, missing) = PowerPlanSettings.BuildCommands(scheme, Settings, state, HasBattery.Value);
        foreach (var m in missing) Ctx.Logger.Info($"{Name}: \"{m.Title}\" não existe nesta máquina (não aplicável).");

        // Backup do valor anterior de cada configuração alterada (uma vez).
        foreach (var s in Settings.Where(s => state.ContainsKey(s.Setting) && !PowerPlanSettings.IsSatisfied(s, state[s.Setting], PowerPlanSettings.ChangesDc(s, HasBattery.Value))))
        {
            string key = SettingPrefix + s.Setting;
            if (Ctx.DryRun || Ctx.Backup.ForTweak(Id).Any(b => b.CustomKey == key)) continue;
            var cur = state[s.Setting];
            Ctx.SaveCustomBackup(Id, key, $"{scheme}|{s.Subgroup}|{cur.Ac}|{cur.Dc}", $"{s.Title} (antes: tomada {cur.Ac}, bateria {cur.Dc})");
        }

        foreach (var c in commands)
        {
            var r = await Ctx.Shell.RunMutatingAsync($"Energia: {c.Description}", "powercfg.exe", c.Arguments).ConfigureAwait(false);
            if (!r.Success) Ctx.Logger.Warn($"{c.Display} falhou (código {r.ExitCode}); seguindo com as demais.");
        }
        if (commands.Count > 0)
            await Ctx.Shell.RunMutatingAsync("Reaplicar o plano", "powercfg.exe", ["/setactive", scheme]).ConfigureAwait(false);
    }

    /// <summary>Volta os valores guardados (tomada e bateria) da camada.</summary>
    protected async Task RestoreSettingsAsync()
    {
        foreach (var b in Ctx.Backup.ForTweak(Id).Where(b => b.CustomKey?.StartsWith(SettingPrefix, StringComparison.Ordinal) == true))
        {
            string setting = b.CustomKey![SettingPrefix.Length..];
            var parts = (b.CustomValue ?? string.Empty).Split('|');
            if (parts.Length != 4 || !uint.TryParse(parts[2], out var ac) || !uint.TryParse(parts[3], out var dc)) continue;
            if (!PowerPlanSettings.All.Any(s => s.Setting == setting && s.Subgroup == parts[1])) continue; // só GUIDs conhecidos
            await Ctx.Shell.RunMutatingAsync($"Restaurar {setting}", "powercfg.exe", ["/setacvalueindex", parts[0], parts[1], setting, ac.ToString()]).ConfigureAwait(false);
            await Ctx.Shell.RunMutatingAsync($"Restaurar {setting}", "powercfg.exe", ["/setdcvalueindex", parts[0], parts[1], setting, dc.ToString()]).ConfigureAwait(false);
        }
    }

    public virtual async Task RevertAsync()
    {
        await RestoreSettingsAsync().ConfigureAwait(false);
        await Ctx.Shell.RunMutatingAsync("Reaplicar o plano", "powercfg.exe", ["/setactive", "SCHEME_CURRENT"]).ConfigureAwait(false);
        if (!Ctx.DryRun) Ctx.Backup.RemoveTweak(Id);
    }
}

/// <summary>
/// Plano "Gueibs Optimizer Performance" (camada Base: Baixo e Médio). Duplica o Desempenho Máximo (ou Alto Desempenho) só se o
/// plano ainda não existir; se existir, reutiliza e reaplica os valores. Guarda o plano anterior e, ao desfazer, reativa-o e
/// só apaga o nosso se foi o app que o criou.
/// </summary>
public sealed class PowerPlanTweak(TweakContext ctx) : PowerTierTweak(ctx, PowerTier.Base)
{
    public const string UltimateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string PlanName = "Gueibs Optimizer Performance";
    /// <summary>Nome do plano até a versão 2.0, quando o app se chamava GameOptimizer.</summary>
    public const string LegacyPlanName = "GameOptimizer Performance";
    private const string PreviousKey = "previous-scheme";
    private const string CreatedKey = "created-scheme";

    public override string Id => "power.plan";
    public override string Name => "Plano de energia de alto desempenho";
    public override string Description =>
        "Cria (ou reutiliza) e ativa o plano \"Gueibs Optimizer Performance\", baseado no Desempenho Máximo ou no Alto Desempenho, com tela, suspensão, hibernação e disco em \"Nunca\" e timers de despertar desativados.";
    public override TweakRisk Risk => TweakRisk.Low;
    public override int Points => 15;
    public override PerformanceLevel Level => PerformanceLevel.Baixo;

    public override bool IsApplicable() => true;

    public override bool IsApplied()
    {
        var schemes = ListSchemesAsync().GetAwaiter().GetResult();
        var active = schemes.FirstOrDefault(s => s.Active);
        if (active is null || !IsOurPlanName(active.Name)) return false;
        return SettingsSatisfied(QueryAsync(active.Guid).GetAwaiter().GetResult());
    }

    public override async Task ApplyAsync()
    {
        var schemes = await ListSchemesAsync().ConfigureAwait(false);
        var active = schemes.FirstOrDefault(s => s.Active)
                     ?? throw new InvalidOperationException("Não foi possível ler o plano de energia ativo.");

        // Plano anterior para o rollback (só a primeira vez). Se o nosso plano já era o ativo, ele mesmo é o "anterior":
        // desfazer deve voltar ao estado de antes (nosso plano ativo, com os valores restaurados), e não trocar para o Equilibrado.
        if (Ctx.Backup.GetCustom(Id, PreviousKey) is null)
            Ctx.SaveCustomBackup(Id, PreviousKey, active.Guid, $"Plano de energia anterior: {active.Name} ({active.Guid})");

        // Nunca duplica de novo: se o plano já existe, reutiliza.
        string? target = FindOurPlan(schemes);
        if (target is null)
        {
            target = await DuplicateAsync(UltimateGuid).ConfigureAwait(false)
                     ?? await DuplicateAsync(HighPerformanceGuid).ConfigureAwait(false);
            if (target is null)
                throw new InvalidOperationException("O Windows não permitiu duplicar o plano de desempenho.");

            await Run("Renomear plano", "/changename", target, PlanName, "Criado pelo Gueibs Optimizer").ConfigureAwait(false);
            if (!Ctx.DryRun) Ctx.SaveCustomBackup(Id, CreatedKey, target, $"Plano criado: {PlanName} ({target})");
        }
        else if (FindLegacyPlan(schemes) is { } legacy)
        {
            await Run("Renomear plano", "/changename", legacy, PlanName, "Criado pelo Gueibs Optimizer").ConfigureAwait(false);
        }

        // Só ativa se ainda não for o plano ativo (evita um /setactive inútil no relatório).
        if (!target.Equals(active.Guid, StringComparison.OrdinalIgnoreCase))
        {
            var result = await Run("Ativar plano", "/setactive", target).ConfigureAwait(false);
            if (!result.Success) throw new InvalidOperationException($"powercfg /setactive falhou: {result.StdErr}{result.StdOut}".Trim());
        }

        // No --dry-run o plano novo não existe: os valores são lidos do plano ativo só para mostrar o que mudaria.
        await ApplySettingsAsync(Ctx.DryRun && target.StartsWith('<') ? "SCHEME_CURRENT" : target).ConfigureAwait(false);
    }

    public override async Task RevertAsync()
    {
        var schemes = await ListSchemesAsync().ConfigureAwait(false);
        string previous = Ctx.Backup.GetCustom(Id, PreviousKey) ?? BalancedGuid;
        if (!schemes.Any(s => s.Guid == previous)) previous = BalancedGuid;
        string? created = Ctx.Backup.GetCustom(Id, CreatedKey);

        // Se o plano não foi criado pelo app, volta os valores que mudamos nele.
        if (created is null) await RestoreSettingsAsync().ConfigureAwait(false);

        var activate = await Run("Reativar plano anterior", "/setactive", previous).ConfigureAwait(false);
        if (!activate.Success) throw new InvalidOperationException($"Não foi possível reativar o plano anterior: {activate.StdErr}".Trim());

        // Só apaga o nosso plano se foi o app que o criou.
        if (created is not null && ShouldDeleteOnRevert(created, previous))
        {
            await Run("Apagar plano criado", "/delete", created).ConfigureAwait(false);
            // As camadas Alto/Máximo moravam nesse plano: os backups delas deixam de fazer sentido.
            if (!Ctx.DryRun)
                foreach (var id in new[] { PowerPlanAdvancedTweak.TweakId, PowerPlanMaximumTweak.TweakId }) Ctx.Backup.RemoveTweak(id);
        }

        if (!Ctx.DryRun) Ctx.Backup.RemoveTweak(Id);
    }

    /// <summary>GUID do "Gueibs Optimizer Performance" já existente (para reutilizar, nunca duplicar de novo).</summary>
    /// <remarks>Aceita também o nome usado até a versão 2.0 ("GameOptimizer Performance"); o atual tem preferência.</remarks>
    public static string? FindOurPlan(IReadOnlyList<PowerScheme> schemes) =>
        (schemes.FirstOrDefault(s => s.Name.Equals(PlanName, StringComparison.OrdinalIgnoreCase))
         ?? schemes.FirstOrDefault(s => s.Name.Equals(LegacyPlanName, StringComparison.OrdinalIgnoreCase)))?.Guid;

    /// <summary>Nome do plano criado por este app (atual ou da versão antiga).</summary>
    public static bool IsOurPlanName(string name) =>
        name.Equals(PlanName, StringComparison.OrdinalIgnoreCase) || name.Equals(LegacyPlanName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Plano com o nome antigo que precisa ser renomeado (null se não houver).</summary>
    public static string? FindLegacyPlan(IReadOnlyList<PowerScheme> schemes) =>
        schemes.Any(s => s.Name.Equals(PlanName, StringComparison.OrdinalIgnoreCase))
            ? null
            : schemes.FirstOrDefault(s => s.Name.Equals(LegacyPlanName, StringComparison.OrdinalIgnoreCase))?.Guid;

    /// <summary>Só apaga o plano no desfazer se foi o app que o criou (e não for o plano a reativar).</summary>
    public static bool ShouldDeleteOnRevert(string? createdByApp, string previous) =>
        createdByApp is not null && !createdByApp.Equals(previous, StringComparison.OrdinalIgnoreCase);

    private async Task<string?> DuplicateAsync(string baseGuid)
    {
        var result = await Run("Duplicar plano", "/duplicatescheme", baseGuid).ConfigureAwait(false);
        if (result.Simulated) return "<novo-guid>";
        return result.Success ? PowerCfgParser.FirstGuid(result.StdOut) : null;
    }

    private Task<ShellResult> Run(string description, params string[] args) =>
        Ctx.Shell.RunMutatingAsync(description, "powercfg.exe", args);
}

/// <summary>Camada Alto: USB, PCIe, Wi-Fi, mídia, fundo e processador 5%–100%.</summary>
public sealed class PowerPlanAdvancedTweak(TweakContext ctx) : PowerTierTweak(ctx, PowerTier.Advanced)
{
    public const string TweakId = "power.plan-advanced";
    public override string Id => TweakId;
    public override string Name => "Plano de energia: dispositivos sem economia";
    public override string Description =>
        "No plano Gueibs Optimizer Performance: sem suspensão seletiva de USB, sem economia do link PCIe, Wi-Fi em desempenho máximo, mídia e vídeo priorizando desempenho, slides do fundo pausados e processador entre 5% e 100%.";
    public override TweakRisk Risk => TweakRisk.Low;
    public override int Points => 4;
    public override PerformanceLevel Level => PerformanceLevel.Alto;
}

/// <summary>Camada Máximo: processador 100%–100% e extras (só os que existem na máquina).</summary>
public sealed class PowerPlanMaximumTweak(TweakContext ctx) : PowerTierTweak(ctx, PowerTier.Maximum)
{
    public const string TweakId = "power.plan-maximum";
    public override string Id => TweakId;
    public override string Name => "Plano de energia: processador no máximo";
    public override string Description =>
        "No plano Gueibs Optimizer Performance: processador sempre entre 100% e 100%, boost agressivo, EPP 0, todos os núcleos ativos (core parking 100%) e resfriamento ativo, quando a máquina tiver essas opções. Os C-states não são alterados.";
    public override TweakRisk Risk => TweakRisk.Medium;
    public override int Points => 3;
    public override PerformanceLevel Level => PerformanceLevel.Maximo;

    public override string? Notice => (base.Notice is { } n ? n + " " : string.Empty) +
        "CPU sempre no clock máximo: mais calor e ruído, inclusive com o PC parado.";
}
