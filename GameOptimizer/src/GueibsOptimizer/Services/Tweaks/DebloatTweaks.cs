using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using Microsoft.Win32;

namespace GueibsOptimizer.Services.Tweaks;

public sealed class StartAndLockScreenAdsTweak(TweakContext context) : RegistrySettingsTweak(context)
{
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    public override string Id => "privacy.start-lockscreen-ads-off";
    public override string Name => "Desativar sugestões no Iniciar e na tela de bloqueio";
    public override string Description =>
        "Desliga dicas e propagandas da tela de bloqueio (Windows Spotlight com anúncios) e as recomendações de apps no Menu Iniciar.";
    public override TweakCategory Category => TweakCategory.Privacy;
    public override TweakRisk Risk => TweakRisk.Low;
    public override int Points => 3;
    public override string? Notice => "A tela de bloqueio volta para uma imagem fixa ou a sua apresentação de slides.";

    protected override IReadOnlyList<RegSetting> Settings { get; } =
    [
        RegSetting.DWord(RegHive.CurrentUser, Cdm, "RotatingLockScreenOverlayEnabled", 0, 1),
        RegSetting.DWord(RegHive.CurrentUser, Cdm, "SubscribedContent-338387Enabled", 0, 1),
        RegSetting.DWord(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_IrisRecommendations", 0, null)
    ];
}

public sealed class WidgetsOffTweak(TweakContext context) : RegistrySettingsTweak(context)
{
    public override string Id => "windows.widgets-off";
    public override string Name => "Desativar Widgets (notícias e clima)";
    public override string Description =>
        "Aplica a política que desliga o painel de Widgets do Windows 11, que carrega notícias e anúncios em segundo plano.";
    public override TweakCategory Category => TweakCategory.Windows;
    public override TweakRisk Risk => TweakRisk.Low;
    public override int Points => 3;
    public override string? Notice => "Tem efeito completo depois de reiniciar o Explorador ou o PC.";

    public override bool IsApplicable() => OsInfo.Supports(Id);

    protected override IReadOnlyList<RegSetting> Settings { get; } =
    [
        RegSetting.DWord(RegHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0, null)
    ];
}

public sealed class BingSearchOffTweak(TweakContext context) : RegistrySettingsTweak(context)
{
    public override string Id => "windows.bing-search-off";
    public override string Name => "Desativar Bing na pesquisa do Iniciar";
    public override string Description =>
        "A pesquisa do Menu Iniciar passa a mostrar só arquivos, apps e configurações do PC, sem resultados da web.";
    public override TweakCategory Category => TweakCategory.Windows;
    public override TweakRisk Risk => TweakRisk.Low;
    public override int Points => 3;
    public override string? Notice => "Tem efeito completo depois de reiniciar o Explorador ou fazer logoff.";

    protected override IReadOnlyList<RegSetting> Settings { get; } =
    [
        RegSetting.DWord(RegHive.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null),
        RegSetting.DWord(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, null)
    ];
}

/// <summary>Menu de contexto completo do Windows 10 no Windows 11. Preferência visual: fora do Boost e da pontuação.</summary>
public sealed class ClassicContextMenuTweak(TweakContext context) : RegistrySettingsTweak(context)
{
    private const string Key = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32";

    public override string Id => "windows.classic-context-menu";
    public override string Name => "Restaurar o menu de contexto clássico";
    public override string Description =>
        "Mostra direto o menu completo do botão direito (como no Windows 10), sem precisar clicar em \"Mostrar mais opções\".";
    public override TweakCategory Category => TweakCategory.Windows;
    public override TweakRisk Risk => TweakRisk.Low;
    public override int Points => 0;
    public override bool InBoost => false;
    public override string? Notice => "Reinicie o Explorador (ou faça logoff) para ver a mudança.";

    public override bool IsApplicable() => OsInfo.Supports(Id);

    // Valor padrão vazio na chave: é a presença dele que ativa o menu clássico.
    protected override IReadOnlyList<RegSetting> Settings { get; } =
    [
        RegSetting.String(RegHive.CurrentUser, Key, "", "", null)
    ];
}

/// <summary>
/// Impede o OneDrive de abrir com o Windows, sem desinstalar (mesmo mecanismo do Gerenciador de Tarefas).
/// Fora do Boost: quem usa a sincronização precisa dele aberto.
/// </summary>
public sealed class OneDriveStartupTweak(TweakContext ctx) : ITweak
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Value = "OneDrive";

    public string Id => "windows.onedrive-startup-off";
    public string Name => "Não abrir o OneDrive com o Windows";
    public string Description => "Desativa a inicialização automática do OneDrive (sem desinstalar). Você ainda pode abri-lo pelo Menu Iniciar quando quiser sincronizar.";
    public TweakCategory Category => TweakCategory.Windows;
    public TweakRisk Risk => TweakRisk.Low;
    public int Points => 0;
    public bool InBoost => false;
    public string? Notice => "Os arquivos só sincronizam enquanto o OneDrive estiver aberto.";

    public bool IsApplicable() => ctx.Registry.Read(RegHive.CurrentUser, RunKey, Value) is not null;

    public bool IsApplied()
    {
        var current = ctx.Registry.Read(RegHive.CurrentUser, StartupApproved.Base + @"\Run", Value);
        return StartupApproved.IsDisabled(current?.Base64 is { } b ? Convert.FromBase64String(b) : null);
    }

    public Task ApplyAsync()
    {
        ctx.Registry.Set(Id, RegHive.CurrentUser, StartupApproved.Base + @"\Run", Value,
            RegistryValueData.FromObject(StartupApproved.Disabled(DateTime.UtcNow), RegistryValueKind.Binary));
        return Task.CompletedTask;
    }

    public Task RevertAsync()
    {
        if (ctx.Registry.Restore(Id) == 0)
        {
            ctx.Registry.Set(Id, RegHive.CurrentUser, StartupApproved.Base + @"\Run", Value,
                RegistryValueData.FromObject(StartupApproved.Enabled(), RegistryValueKind.Binary));
            if (!ctx.DryRun) ctx.Backup.RemoveTweak(Id);
        }
        return Task.CompletedTask;
    }
}
