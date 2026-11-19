using GueibsOptimizer.Core;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services.Tweaks;

public sealed class TelemetryPolicyTweak(TweakContext context) : RegistrySettingsTweak(context)
{
    private const string Policy = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";

    public override string Id => "privacy.telemetry-policy";
    public override string Name => "Reduzir telemetria do Windows";
    public override string Description =>
        "Define a política de dados de diagnóstico no nível mínimo e oculta pedidos de feedback. No Windows Home/Pro o mínimo efetivo é \"Obrigatório\".";
    public override TweakCategory Category => TweakCategory.Privacy;
    public override TweakRisk Risk => TweakRisk.Low;
    public override int Points => 7;

    protected override IReadOnlyList<RegSetting> Settings { get; } =
    [
        RegSetting.DWord(RegHive.LocalMachine, Policy, "AllowTelemetry", 0, null),
        RegSetting.DWord(RegHive.LocalMachine, Policy, "DoNotShowFeedbackNotifications", 1, null)
    ];
}

/// <summary>Desativa serviços de telemetria pelo valor Start do registro (com backup tipado) e os para.</summary>
public sealed class TelemetryServicesTweak(TweakContext context) : RegistrySettingsTweak(context)
{
    private static readonly (string Name, uint DefaultStart)[] Services =
    [
        ("DiagTrack", 2),          // Automático
        ("dmwappushservice", 3)    // Manual
    ];

    public override string Id => "privacy.telemetry-services";
    public override string Name => "Desativar serviços de telemetria";
    public override string Description =>
        "Desativa \"Experiências do Usuário Conectado e Telemetria\" (DiagTrack) e o serviço de roteamento de mensagens WAP (dmwappushservice).";
    public override TweakCategory Category => TweakCategory.Privacy;
    public override TweakRisk Risk => TweakRisk.Medium;
    public override int Points => 6;

    private static string KeyOf(string service) => $@"SYSTEM\CurrentControlSet\Services\{service}";

    // Só considera serviços que existem: Set nunca deve criar a chave de um serviço inexistente.
    protected override IReadOnlyList<RegSetting> Settings =>
        Services
            .Where(s => Context.Registry.KeyExists(RegHive.LocalMachine, KeyOf(s.Name)))
            .Select(s => RegSetting.DWord(RegHive.LocalMachine, KeyOf(s.Name), "Start", 4, s.DefaultStart))
            .ToList();

    public override bool IsApplicable() => Settings.Count > 0;

    public override async Task ApplyAsync()
    {
        await base.ApplyAsync().ConfigureAwait(false);
        foreach (var (name, _) in Services)
            if (Context.Registry.KeyExists(RegHive.LocalMachine, KeyOf(name)))
                await Context.Shell.RunMutatingAsync($"Parar serviço {name}", "sc.exe", ["stop", name]).ConfigureAwait(false);
    }

    public override async Task RevertAsync()
    {
        await base.RevertAsync().ConfigureAwait(false);
        var diag = Context.Registry.Read(RegHive.LocalMachine, KeyOf("DiagTrack"), "Start");
        if (diag?.Number == 2)
            await Context.Shell.RunMutatingAsync("Iniciar serviço DiagTrack", "sc.exe", ["start", "DiagTrack"]).ConfigureAwait(false);
    }
}

/// <summary>Desativa tarefas agendadas de compatibilidade/CEIP via Agendador (COM).</summary>
public sealed class CompatibilityTasksTweak(TweakContext ctx) : ITweak
{
    private static readonly (string Folder, string Name)[] Tasks =
    [
        (@"\Microsoft\Windows\Application Experience", "Microsoft Compatibility Appraiser"),
        (@"\Microsoft\Windows\Application Experience", "ProgramDataUpdater"),
        (@"\Microsoft\Windows\Application Experience", "PcaPatchDbTask"),
        (@"\Microsoft\Windows\Customer Experience Improvement Program", "Consolidator"),
        (@"\Microsoft\Windows\Customer Experience Improvement Program", "UsbCeip")
    ];

    /// <summary>Tarefas que este ajuste altera (usado pelo --selftest para ler o estado bruto).</summary>
    public static IReadOnlyList<(string Folder, string Name)> TaskList => Tasks;

    /// <summary>Serviços que o ajuste de telemetria altera (usado pelo --selftest).</summary>
    public static IReadOnlyList<string> TelemetryServiceNames => ["DiagTrack", "dmwappushservice"];

    public string Id => "privacy.compat-tasks";
    public string Name => "Desativar tarefas de compatibilidade e CEIP";
    public string Description =>
        "Desativa tarefas agendadas que coletam dados de compatibilidade de programas e do Programa de Aperfeiçoamento da Experiência do Usuário.";
    public TweakCategory Category => TweakCategory.Privacy;
    public TweakRisk Risk => TweakRisk.Low;
    public int Points => 4;
    public string? Notice => null;

    private IEnumerable<(string Folder, string Name, bool Enabled)> Existing() =>
        Tasks.Select(t => (t.Folder, t.Name, State: TaskSchedulerCom.IsEnabled(t.Folder, t.Name)))
             .Where(t => t.State is not null)
             .Select(t => (t.Folder, t.Name, t.State!.Value));

    public bool IsApplicable() => Existing().Any();

    public bool IsApplied() => Existing().All(t => !t.Enabled);

    public Task ApplyAsync()
    {
        foreach (var t in Existing().Where(t => t.Enabled))
        {
            string full = $@"{t.Folder}\{t.Name}";
            if (ctx.DryRun)
            {
                ctx.Logger.Simulated($"Desativar tarefa agendada {full}");
                continue;
            }
            ctx.SaveCustomBackup(Id, "task:" + full, "habilitada", $"Tarefa {full} desativada");
            if (!TaskSchedulerCom.SetEnabled(t.Folder, t.Name, false))
                throw new InvalidOperationException($"Não foi possível desativar a tarefa {full}.");
        }
        return Task.CompletedTask;
    }

    public Task RevertAsync()
    {
        var backups = ctx.Backup.ForTweak(Id);
        // Sem backup, volta ao padrão do Windows (todas habilitadas).
        var targets = backups.Count > 0
            ? backups.Select(b => b.CustomKey!["task:".Length..]).ToList()
            : Existing().Select(t => $@"{t.Folder}\{t.Name}").ToList();

        foreach (var full in targets)
        {
            int idx = full.LastIndexOf('\\');
            string folder = full[..idx], name = full[(idx + 1)..];
            if (ctx.DryRun) { ctx.Logger.Simulated($"Reativar tarefa agendada {full}"); continue; }
            if (!TaskSchedulerCom.SetEnabled(folder, name, true))
                ctx.Logger.Warn($"Não foi possível reativar a tarefa {full}.");
        }
        if (!ctx.DryRun) ctx.Backup.RemoveTweak(Id);
        return Task.CompletedTask;
    }
}

public sealed class CopilotTweak(TweakContext context) : RegistrySettingsTweak(context)
{
    private const string Policy = @"Software\Policies\Microsoft\Windows\WindowsCopilot";

    public override string Id => "privacy.copilot-off";
    public override string Name => "Desativar Copilot do Windows";
    public override string Description =>
        "Aplica a política que desliga o Copilot integrado e esconde o botão do Copilot da barra de tarefas.";
    public override TweakCategory Category => TweakCategory.Privacy;
    public override TweakRisk Risk => TweakRisk.Low;
    public override int Points => 5;
    public override string? Notice => "Pode exigir reiniciar o Explorer ou fazer logoff.";

    // Copilot integrado existe a partir do Windows 11 22H2.
    public override bool IsApplicable() => OsInfo.Supports(Id);

    protected override IReadOnlyList<RegSetting> Settings { get; } =
    [
        RegSetting.DWord(RegHive.CurrentUser, Policy, "TurnOffWindowsCopilot", 1, null),
        RegSetting.DWord(RegHive.LocalMachine, Policy, "TurnOffWindowsCopilot", 1, null),
        RegSetting.DWord(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 0, null)
    ];
}

public sealed class RecallPolicyTweak(TweakContext context) : RegistrySettingsTweak(context)
{
    private const string Policy = @"Software\Policies\Microsoft\Windows\WindowsAI";

    public override string Id => "privacy.recall-policy";
    public override string Name => "Bloquear capturas do Recall";
    public override string Description =>
        "Aplica a política que impede o Recall de salvar capturas de tela da sua atividade.";
    public override TweakCategory Category => TweakCategory.Privacy;
    public override TweakRisk Risk => TweakRisk.Low;
    public override int Points => 6;

    // Recall existe a partir do Windows 11 24H2.
    public override bool IsApplicable() => OsInfo.Supports(Id);

    protected override IReadOnlyList<RegSetting> Settings { get; } =
    [
        RegSetting.DWord(RegHive.CurrentUser, Policy, "DisableAIDataAnalysis", 1, null),
        RegSetting.DWord(RegHive.LocalMachine, Policy, "DisableAIDataAnalysis", 1, null)
    ];
}

/// <summary>Remove o recurso opcional "Recall" quando ele existe (Copilot+ PCs).</summary>
public sealed class RecallFeatureTweak(TweakContext ctx) : ITweak
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private string? _cachedState;
    private DateTime _cachedAt;

    public string Id => "privacy.recall-feature";
    public string Name => "Desativar o recurso opcional Recall";
    public string Description =>
        "Desativa o recurso opcional do Windows \"Recall\" (só existe em alguns PCs com Windows 11 24H2+). Pode ser reativado em Recursos Opcionais.";
    public TweakCategory Category => TweakCategory.Privacy;
    public TweakRisk Risk => TweakRisk.Medium;
    public int Points => 2;
    public string? Notice => "Requer reinício para concluir.";

    public bool IsApplicable() => OsInfo.Supports(Id) && State() is not null;

    public bool IsApplied() => State() is { } s && !s.StartsWith("Enabled", StringComparison.OrdinalIgnoreCase);

    public async Task ApplyAsync()
    {
        string previous = State() ?? "Desconhecido";
        ctx.SaveCustomBackup(Id, "feature-state", previous, $"Recurso Recall estava: {previous}");
        var r = await ctx.Shell.RunPowerShellMutatingAsync("Desativar recurso Recall",
            "Disable-WindowsOptionalFeature -Online -FeatureName 'Recall' -NoRestart | Out-Null", TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        _cachedState = null;
        if (!r.Success) throw new InvalidOperationException($"Falha ao desativar o Recall: {r.StdErr}".Trim());
    }

    public async Task RevertAsync()
    {
        var r = await ctx.Shell.RunPowerShellMutatingAsync("Reativar recurso Recall",
            "Enable-WindowsOptionalFeature -Online -FeatureName 'Recall' -NoRestart | Out-Null", TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        _cachedState = null;
        if (!r.Success) throw new InvalidOperationException($"Falha ao reativar o Recall: {r.StdErr}".Trim());
        if (!ctx.DryRun) ctx.Backup.RemoveTweak(Id);
    }

    // Consulta lenta (DISM): guardada por 60 s para a pontuação não travar.
    private string? State()
    {
        if (_cachedState is not null && DateTime.UtcNow - _cachedAt < CacheTtl)
            return _cachedState.Length == 0 ? null : _cachedState;

        var r = ctx.Shell.RunPowerShellAsync(
            "$f = Get-WindowsOptionalFeature -Online -FeatureName 'Recall' -ErrorAction SilentlyContinue; if ($f) { [string]$f.State }",
            TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        _cachedState = r.Success ? r.StdOut.Trim() : string.Empty;
        _cachedAt = DateTime.UtcNow;
        return _cachedState.Length == 0 ? null : _cachedState;
    }
}

public sealed class AdsSuggestionsTweak(TweakContext context) : RegistrySettingsTweak(context)
{
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    public override string Id => "privacy.ads-suggestions-off";
    public override string Name => "Desativar sugestões e anúncios do Windows";
    public override string Description =>
        "Desliga dicas, apps sugeridos no Iniciar, instalação silenciosa de apps promovidos, anúncios no Explorador e a ID de publicidade.";
    public override TweakCategory Category => TweakCategory.Privacy;
    public override TweakRisk Risk => TweakRisk.Low;
    public override int Points => 6;

    protected override IReadOnlyList<RegSetting> Settings { get; } =
    [
        RegSetting.DWord(RegHive.CurrentUser, Cdm, "SubscribedContent-338388Enabled", 0, 1),
        RegSetting.DWord(RegHive.CurrentUser, Cdm, "SubscribedContent-338389Enabled", 0, 1),
        RegSetting.DWord(RegHive.CurrentUser, Cdm, "SubscribedContent-353698Enabled", 0, 1),
        RegSetting.DWord(RegHive.CurrentUser, Cdm, "SystemPaneSuggestionsEnabled", 0, 1),
        RegSetting.DWord(RegHive.CurrentUser, Cdm, "SilentInstalledAppsEnabled", 0, 1),
        RegSetting.DWord(RegHive.CurrentUser, Cdm, "SoftLandingEnabled", 0, 1),
        RegSetting.DWord(RegHive.CurrentUser, Advanced, "ShowSyncProviderNotifications", 0, 1),
        RegSetting.DWord(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1)
    ];
}
