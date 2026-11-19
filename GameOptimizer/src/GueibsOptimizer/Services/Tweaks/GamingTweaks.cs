using GueibsOptimizer.Core;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services.Tweaks;

public sealed class GameModeTweak(TweakContext context) : RegistrySettingsTweak(context)
{
    private const string GamesTask = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";

    public override string Id => "gaming.game-mode";
    public override string Name => "Modo de Jogo e prioridade multimídia";
    public override string Description =>
        "Liga o Modo de Jogo do Windows e dá prioridade alta de CPU/GPU à tarefa \"Games\" do agendador multimídia (MMCSS).";
    public override TweakCategory Category => TweakCategory.Gaming;
    public override TweakRisk Risk => TweakRisk.Low;
    public override int Points => 9;

    protected override IReadOnlyList<RegSetting> Settings { get; } =
    [
        RegSetting.DWord(RegHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, 1),
        RegSetting.DWord(RegHive.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1, null),
        RegSetting.DWord(RegHive.LocalMachine, GamesTask, "GPU Priority", 8, 8),
        RegSetting.DWord(RegHive.LocalMachine, GamesTask, "Priority", 6, 2),
        RegSetting.String(RegHive.LocalMachine, GamesTask, "Scheduling Category", "High", "Medium"),
        RegSetting.String(RegHive.LocalMachine, GamesTask, "SFIO Priority", "High", "Normal")
    ];
}

public sealed class GameDvrTweak(TweakContext context) : RegistrySettingsTweak(context)
{
    public override string Id => "gpu.game-dvr-off";
    public override string Name => "Desativar gravação em segundo plano (Game DVR)";
    public override string Description =>
        "Desliga a captura contínua da Xbox Game Bar, que grava os últimos minutos de jogo e consome GPU e disco.";
    public override TweakCategory Category => TweakCategory.Gpu;
    public override TweakRisk Risk => TweakRisk.Low;
    public override int Points => 8;

    protected override IReadOnlyList<RegSetting> Settings { get; } =
    [
        RegSetting.DWord(RegHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0, 1),
        RegSetting.DWord(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, null),
        RegSetting.DWord(RegHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, null)
    ];
}
