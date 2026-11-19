using GueibsOptimizer.Core;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services.Tweaks;

public sealed class HagsTweak(TweakContext context) : RegistrySettingsTweak(context)
{
    public override string Id => "gpu.hags";
    public override string Name => "Agendamento de GPU acelerado por hardware (HAGS)";
    public override string Description =>
        "Deixa a própria GPU gerenciar sua fila de trabalho, reduzindo latência e uso de CPU. Exige GPU e driver compatíveis (WDDM 2.7+).";
    public override TweakCategory Category => TweakCategory.Gpu;
    public override TweakRisk Risk => TweakRisk.Medium;
    public override int Points => 8;

    public override string? Notice
    {
        get
        {
            var gpus = GpuVendorService.Adapters;
            // Montado em partes traduzíveis (os nomes das GPUs variam de PC para PC).
            string detected = gpus.Count == 0
                ? LocalizationService.Tr("GPU não identificada")
                : LocalizationService.Tr("GPU detectada:") + " " + string.Join(", ", gpus.Select(g => $"{g.Name} ({LocalizationService.Tr(GpuVendorService.VendorText(g.Vendor))})"));
            return $"{LocalizationService.Tr("Requer reinício para ter efeito.")} {detected}.";
        }
    }

    // HAGS existe a partir do Windows 10 2004 (build 19041).
    public override bool IsApplicable() => OsInfo.Supports(Id);

    protected override IReadOnlyList<RegSetting> Settings { get; } =
    [
        RegSetting.DWord(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, null)
    ];
}

public sealed class MouseAccelerationTweak(TweakContext context) : RegistrySettingsTweak(context)
{
    private const string Mouse = @"Control Panel\Mouse";

    public override string Id => "input.mouse-accel-off";
    public override string Name => "Desativar aceleração do mouse";
    public override string Description =>
        "Desliga \"Aumentar precisão do ponteiro\": o cursor passa a mover a mesma distância para o mesmo movimento físico, o que ajuda a mira.";
    public override TweakCategory Category => TweakCategory.Input;
    public override TweakRisk Risk => TweakRisk.Low;
    public override int Points => 7;
    public override string? Notice => "Tem efeito completo após sair e entrar de novo na conta (logoff).";

    protected override IReadOnlyList<RegSetting> Settings { get; } =
    [
        RegSetting.String(RegHive.CurrentUser, Mouse, "MouseSpeed", "0", "1"),
        RegSetting.String(RegHive.CurrentUser, Mouse, "MouseThreshold1", "0", "6"),
        RegSetting.String(RegHive.CurrentUser, Mouse, "MouseThreshold2", "0", "10")
    ];
}

public sealed class KeyboardRepeatTweak(TweakContext context) : RegistrySettingsTweak(context)
{
    private const string Keyboard = @"Control Panel\Keyboard";

    public override string Id => "input.keyboard-repeat";
    public override string Name => "Repetição de teclado mais rápida";
    public override string Description =>
        "Menor atraso antes de repetir uma tecla segurada e velocidade máxima de repetição.";
    public override TweakCategory Category => TweakCategory.Input;
    public override TweakRisk Risk => TweakRisk.Low;
    public override int Points => 4;
    public override string? Notice => "Tem efeito completo após logoff.";

    protected override IReadOnlyList<RegSetting> Settings { get; } =
    [
        RegSetting.String(RegHive.CurrentUser, Keyboard, "KeyboardDelay", "0", "1"),
        RegSetting.String(RegHive.CurrentUser, Keyboard, "KeyboardSpeed", "31", "31")
    ];
}

public sealed class AccessibilityKeysTweak(TweakContext context) : RegistrySettingsTweak(context)
{
    private const string Access = @"Control Panel\Accessibility";

    public override string Id => "input.accessibility-shortcuts-off";
    public override string Name => "Desativar atalhos de Teclas de Aderência, Filtro e Alternância";
    public override string Description =>
        "Impede que apertar Shift 5 vezes (ou segurar Shift direito / Num Lock) abra janelas de acessibilidade no meio do jogo. Os recursos continuam disponíveis nas Configurações.";
    public override TweakCategory Category => TweakCategory.Input;
    public override TweakRisk Risk => TweakRisk.Low;
    public override int Points => 4;
    public override string? Notice => "Tem efeito completo após logoff.";

    protected override IReadOnlyList<RegSetting> Settings { get; } =
    [
        RegSetting.String(RegHive.CurrentUser, $@"{Access}\StickyKeys", "Flags", "506", "510"),
        RegSetting.String(RegHive.CurrentUser, $@"{Access}\Keyboard Response", "Flags", "122", "126"),
        RegSetting.String(RegHive.CurrentUser, $@"{Access}\ToggleKeys", "Flags", "58", "62")
    ];
}
