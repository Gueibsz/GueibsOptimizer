using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;
using GueibsOptimizer.Services.Games;

namespace GueibsOptimizer.Tests;

public class Phase78Tests
{
    // --- VDF (Steam) ---

    [Fact]
    public void LibraryFoldersNewFormat()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"C:\\Program Files (x86)\\Steam"
                    "apps" { "228980" "1" }
                }
                "1"
                {
                    "path"		"H:\\SteamLibrary"
                }
            }
            """;
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"H:\SteamLibrary"], VdfParser.LibraryPaths(vdf));
    }

    [Fact]
    public void LibraryFoldersOldFormat()
    {
        const string vdf = """
            "LibraryFolders"
            {
                // comentário
                "TimeNextStatsReport"	"1234"
                "ContentStatsID"	"-99"
                "1"		"D:\\Games\\Steam"
            }
            """;
        Assert.Equal([@"D:\Games\Steam"], VdfParser.LibraryPaths(vdf));
    }

    [Fact]
    public void AppManifestReadsNameAndFolder()
    {
        const string acf = """
            "AppState"
            {
                "appid"		"730"
                "name"		"Counter-Strike 2"
                "installdir"		"Counter-Strike Global Offensive"
                "UserConfig" { "language" "brazilian" }
            }
            """;
        var m = VdfParser.AppManifest(acf);
        Assert.Equal(("730", "Counter-Strike 2", "Counter-Strike Global Offensive"), m);
        Assert.Null(VdfParser.AppManifest("\"Outra\" { }"));
    }

    [Fact]
    public void VdfToleratesMalformedInput()
    {
        var root = VdfParser.Parse("\"a\" { \"b\" \"c\" ");
        Assert.Equal("c", root["a"]?.Get("b"));
    }

    // --- Epic ---

    [Fact]
    public void EpicManifestParsed()
    {
        const string json = """
            { "DisplayName": "Fortnite", "InstallLocation": "H:\\EpicGames\\Fortnite",
              "LaunchExecutable": "FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe", "AppName": "Fortnite" }
            """;
        var m = EpicManifestParser.Parse(json)!;
        Assert.Equal("Fortnite", m.DisplayName);
        Assert.Equal(@"H:\EpicGames\Fortnite", m.InstallLocation);
        Assert.EndsWith("Shipping.exe", m.LaunchExecutable);
        Assert.Null(EpicManifestParser.Parse("""{ "DisplayName": "X", "InstallLocation": "C:\\X", "bIsIncompleteInstall": true }"""));
        Assert.Null(EpicManifestParser.Parse("não é json"));
    }

    // --- Escolha do executável (casos reais vistos na detecção) ---

    public static TheoryData<string, string[], long[], string> PickCases => new()
    {
        { "Counter-Strike 2", [@"game\bin\win64\vconsole2.exe", @"game\bin\win64\cs2.exe"], [4_985_000, 2_899_000], @"game\bin\win64\cs2.exe" },
        { "Deadlock", [@"game\bin\win64\vconsole2.exe", @"game\bin\win64\deadlock.exe"], [5_012_000, 1_039_000], @"game\bin\win64\deadlock.exe" },
        { "Oxygen Not Included", ["Restarter.exe", "UnityCrashHandler64.exe", "OxygenNotIncluded.exe"], [4_213_000, 1_000_000, 652_000], "OxygenNotIncluded.exe" },
        { "Biped 2", ["EOSBootstrapper.exe", "UnityCrashHandler64.exe", "Biped2.exe"], [2_561_000, 1_000_000, 652_000], "Biped2.exe" },
        { "Tabletop Simulator", [@"Modding\Deck Builder\TTS-Deck-Editor.exe", "UnityCrashHandler64.exe", "Tabletop Simulator.exe"], [9_000_000, 1_000_000, 653_000], "Tabletop Simulator.exe" },
        { "Fortnite", [@"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", @"FortniteGame\Binaries\Win64\FortniteLauncher.exe",
                       @"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping_EAC_EOS.exe", @"FortniteGame\Binaries\Win64\FortniteBootstrapper.exe"],
                      [496_607_000, 400_000, 500_000_000, 300_000], @"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe" },
        { "Jogo X", ["unins000.exe", "setup.exe", @"_CommonRedist\vcredist.exe", "game.exe"], [1, 2, 3, 4], "game.exe" }
    };

    [Theory]
    [MemberData(nameof(PickCases))]
    public void PicksMainExecutable(string name, string[] files, long[] sizes, string expected) =>
        Assert.Equal(expected, MainExecutablePicker.Pick(files.Zip(sizes), name));

    [Fact]
    public void PickReturnsNullWithoutCandidates() =>
        Assert.Null(MainExecutablePicker.Pick([("uninstall.exe", 10L), ("readme.txt", 5L)]));

    // --- Ajustes por jogo ---

    private sealed class Fixture : IDisposable
    {
        public TestRegistryKey Key { get; } = new();
        public string BoosterList { get; } = Path.Combine(Path.GetTempPath(), $"go-games-{Guid.NewGuid():N}.json");
        public GameRegistryPaths Paths => new(
            RegHive.CurrentUser, Key.Combine("Gpu"), RegHive.CurrentUser, Key.Combine("Layers"), RegHive.CurrentUser, Key.Combine("Ifeo"));
        public GameEntry Game { get; } = new("Jogo Teste", GameSource.Manual, @"C:\Jogos\Teste", @"C:\Jogos\Teste\teste.exe");

        public GameProfileService Create(bool dryRun)
        {
            var ctx = Key.CreateContext(dryRun);
            return new GameProfileService(ctx, new GameBoosterService(Key.Logger, dryRun, BoosterList), Paths);
        }

        public void Dispose()
        {
            Key.Dispose();
            try { File.Delete(BoosterList); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task DryRunWritesNothing()
    {
        using var f = new Fixture();
        var service = f.Create(dryRun: true);
        var errors = await service.OptimizeAsync(f.Game);
        await service.ApplyAsync(f.Game, GameOption.DisableFullscreenOptimizations);
        Assert.Empty(errors);
        Assert.False(f.Key.KeyExists("Gpu"));
        Assert.False(f.Key.KeyExists("Ifeo"));
        Assert.False(f.Key.KeyExists("Layers"));
        Assert.Empty(f.Key.Store.All);
        Assert.DoesNotContain("teste.exe", File.Exists(f.BoosterList) ? File.ReadAllText(f.BoosterList) : string.Empty);
    }

    [Fact]
    public async Task ApplyAndRevertEveryOption()
    {
        using var f = new Fixture();
        var service = f.Create(dryRun: false);
        f.Key.Write("Layers", f.Game.Executable!, "~ RUNASADMIN", Microsoft.Win32.RegistryValueKind.String);

        foreach (var info in GameProfileService.Options) await service.ApplyAsync(f.Game, info.Option);
        foreach (var info in GameProfileService.Options) Assert.True(service.IsApplied(f.Game, info.Option), info.Name);

        Assert.Equal("GpuPreference=2;", f.Key.ReadRaw("Gpu", f.Game.Executable!));
        Assert.Equal(3, f.Key.ReadRaw(@"Ifeo\teste.exe\PerfOptions", "CpuPriorityClass"));
        Assert.Equal("~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE", f.Key.ReadRaw("Layers", f.Game.Executable!));
        Assert.Contains("teste.exe", File.ReadAllText(f.BoosterList));

        // Desfaz pelo histórico (IUndoProvider), como a aba Restauração faz.
        var ids = f.Key.Store.All.Select(e => e.TweakId).Distinct().ToList();
        Assert.All(ids, id => Assert.True(service.CanUndo(id)));
        foreach (var id in ids) await service.UndoAsync(id);

        foreach (var info in GameProfileService.Options) Assert.False(service.IsApplied(f.Game, info.Option), info.Name);
        Assert.Null(f.Key.ReadRaw("Gpu", f.Game.Executable!));
        Assert.False(f.Key.KeyExists(@"Ifeo\teste.exe"));
        Assert.Equal("~ RUNASADMIN", f.Key.ReadRaw("Layers", f.Game.Executable!));
        Assert.DoesNotContain("teste.exe", File.ReadAllText(f.BoosterList));
        Assert.Empty(f.Key.Store.All);
    }

    [Fact]
    public async Task BoosterRevertKeepsGameThatWasAlreadyListed()
    {
        using var f = new Fixture();
        File.WriteAllText(f.BoosterList, """["teste.exe"]""");
        var service = f.Create(dryRun: false);
        await service.ApplyAsync(f.Game, GameOption.GameBooster);
        await service.RevertAsync(f.Game, GameOption.GameBooster);
        Assert.Contains("teste.exe", File.ReadAllText(f.BoosterList));
    }

    [Fact]
    public void IfeoGuardBlocksRealtimeDebuggerAndOtherKeys()
    {
        var ok = GameProfileService.EnsureSafeIfeo(@"X\a.exe\PerfOptions", "CpuPriorityClass", 3);
        Assert.Equal(3u, ok.Data);
        Assert.Throws<InvalidOperationException>(() => GameProfileService.EnsureSafeIfeo(@"X\a.exe\PerfOptions", "CpuPriorityClass", 4));
        Assert.Throws<InvalidOperationException>(() => GameProfileService.EnsureSafeIfeo(@"X\a.exe", "Debugger", 3));
        Assert.Throws<InvalidOperationException>(() => GameProfileService.EnsureSafeIfeo(@"X\a.exe\PerfOptions", "Debugger", 3));
        Assert.Throws<InvalidOperationException>(() => GameProfileService.EnsureSafeIfeo(@"X\a.exe\PerfOptions", "IoPriority", 3));
        Assert.Throws<ArgumentException>(() => GameProfileService.ValidExeName(@"..\x.exe"));
        Assert.Throws<ArgumentException>(() => GameProfileService.ValidExeName("x.bat"));
        Assert.Equal(3u, GameProfileService.HighPriorityClass);
    }

    [Fact]
    public void SourceNeverWritesDebuggerOrRealtime()
    {
        var dir = Path.Combine(FindRepoSrc(), "Services", "Games");
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            Assert.DoesNotMatch(new Regex(@"Set\([^;]*""Debugger""", RegexOptions.IgnoreCase), code);
            Assert.DoesNotContain("ProcessPriorityClass.RealTime", code);
        }
    }

    private static string FindRepoSrc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "GueibsOptimizer"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "GueibsOptimizer");
    }

    [Theory]
    [InlineData(null, "~ DISABLEDXMAXIMIZEDWINDOWEDMODE")]
    [InlineData("~ RUNASADMIN", "~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE")]
    [InlineData("~ DISABLEDXMAXIMIZEDWINDOWEDMODE", "~ DISABLEDXMAXIMIZEDWINDOWEDMODE")]
    [InlineData("~ HIGHDPIAWARE  RUNASADMIN", "~ HIGHDPIAWARE RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE")]
    public void LayersMergeKeepsExistingFlags(string? existing, string expected)
    {
        Assert.Equal(expected, GameProfileService.MergeLayerFlag(existing, GameProfileService.FullscreenFlag));
        Assert.True(GameProfileService.HasLayerFlag(expected, GameProfileService.FullscreenFlag));
    }

    // --- Presets ---

    [Fact]
    public void EmbeddedPresetsCoverPopularGamesAndAllTiers()
    {
        var ids = GamePresetService.All.Select(p => p.Id).ToList();
        foreach (var id in new[] { "cs2", "valorant", "fortnite", "apex", "gtav", "roblox", "minecraft", "lol", "warzone" })
            Assert.Contains(id, ids);
        foreach (var preset in GamePresetService.All)
            foreach (var tier in Enum.GetValues<PcTier>())
                Assert.NotEmpty(preset.RecommendationsFor(tier));
    }

    [Fact]
    public void GamesWithKnownConfigFileHaveCompleteSettings()
    {
        var auto = GamePresetService.All.Where(p => p.CanAutoApply).Select(p => p.Id).Order().ToList();
        Assert.Equal(["eldenring", "fortnite", "lol", "minecraft"], auto);
        foreach (var p in GamePresetService.All.Where(p => p.CanAutoApply))
        {
            Assert.Contains(p.ConfigFile!.Format, new[] { "ini", "keyvalue", "xml" });
            foreach (var s in p.ConfigFile.Settings)
            {
                Assert.False(string.IsNullOrWhiteSpace(s.Label), $"{p.Id}:{s.Key}");
                if (p.ConfigFile.Format == "ini") Assert.False(string.IsNullOrEmpty(s.Section), $"{p.Id}:{s.Key} sem seção");
                foreach (var tier in Enum.GetValues<PcTier>())
                {
                    var v = s.RecommendedFor(tier);
                    Assert.NotNull(v);
                    if (s.Values.Count > 0 && p.Id != "minecraft") Assert.True(s.Values.ContainsKey(v!), $"{p.Id}:{s.Key}:{tier}={v} sem rótulo");
                }
            }
        }
        // Sem arquivo verificado: só texto, com o motivo.
        Assert.All(GamePresetService.All.Where(p => p.Id is "cs2" or "valorant"), p => Assert.False(string.IsNullOrEmpty(p.ConfigNote)));
    }

    // --- Edição de arquivos de opções (atual × recomendado) ---

    private const string FortniteIni =
        "[/Script/FortniteGame.FortGameUserSettings]\r\nFullscreenMode=0\r\nbUseVSync=True\r\nFrameRateLimit=120.000000\r\n\r\n" +
        "[ScalabilityGroups]\r\nsg.ResolutionQuality=100\r\nsg.ShadowQuality=3\r\nsg.TextureQuality=2\r\n\r\n[ChatSettings]\r\n";

    [Fact]
    public void IniReadAndWriteTouchOnlyTheKeyInItsSection()
    {
        Assert.Equal("3", GameConfigEditor.IniRead(FortniteIni, "ScalabilityGroups", "sg.ShadowQuality"));
        Assert.Equal("True", GameConfigEditor.IniRead(FortniteIni, "/Script/FortniteGame.FortGameUserSettings", "bUseVSync"));
        Assert.Null(GameConfigEditor.IniRead(FortniteIni, "ScalabilityGroups", "sg.FoliageQuality"));

        var w = GameConfigEditor.IniWrite(FortniteIni, "ScalabilityGroups", "sg.ShadowQuality", "0");
        Assert.Equal(FortniteIni.Replace("sg.ShadowQuality=3", "sg.ShadowQuality=0"), w);

        // Chave nova entra no fim da própria seção (antes da linha vazia), não no fim do arquivo.
        w = GameConfigEditor.IniWrite(FortniteIni, "ScalabilityGroups", "sg.FoliageQuality", "1");
        Assert.Contains("sg.TextureQuality=2\r\nsg.FoliageQuality=1\r\n\r\n[ChatSettings]", w);

        w = GameConfigEditor.IniWrite("a=1\n", "Nova", "x", "2");
        Assert.Equal("a=1\n[Nova]\nx=2\n", w);
    }

    [Fact]
    public void XmlReplacesExistingElementsOnly()
    {
        const string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\" ?>\r\n\r\n<config><QualitySetting>MEDIUM</QualitySetting>\r\n\r\n<MotionBlur>MEDIUM</MotionBlur>\r\n\r\n</config>";
        Assert.Equal("MEDIUM", GameConfigEditor.XmlRead(xml, "MotionBlur"));
        var w = GameConfigEditor.XmlWrite(xml, "MotionBlur", "OFF");
        Assert.Equal(xml.Replace("<MotionBlur>MEDIUM</MotionBlur>", "<MotionBlur>OFF</MotionBlur>"), w);
        Assert.Throws<InvalidDataException>(() => GameConfigEditor.XmlWrite(xml, "NaoExiste", "1"));
    }

    [Fact]
    public void NumbersAreComparedByValue()
    {
        var s = new GameSetting { Key = "FrameRateLimit" };
        Assert.True(s.SameValue("120", "120.000000"));
        Assert.False(s.SameValue("120", "144"));
        Assert.True(new GameSetting().SameValue("False", "false"));
    }

    private static (string Dir, string Path) TempConfig(string name, byte[] content)
    {
        var dir = Directory.CreateTempSubdirectory("go-cfg-").FullName;
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, content);
        return (dir, path);
    }

    [Fact]
    public void AppliesOnlyChosenSettingsAndKeepsUtf16Encoding()
    {
        var elden = GamePresetService.All.Single(p => p.Id == "eldenring");
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-16\" ?>\r\n<config><Auto-detectBestRenderingSettings>ON</Auto-detectBestRenderingSettings>\r\n" +
                  "<QualitySetting>MEDIUM</QualitySetting>\r\n" +
                  string.Concat(elden.ConfigFile!.Settings.Where(s => !s.Hidden).Select(s => $"<{s.Key}>MEDIUM</{s.Key}>\r\n")) + "</config>";
        var (dir, path) = TempConfig("GraphicsConfig.xml", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(xml)]);
        try
        {
            var states = GamePresetService.ReadStates(elden, PcTier.Medio, path);
            Assert.DoesNotContain(states, s => s.Setting.Hidden);
            var motion = states.Single(s => s.Setting.Key == "MotionBlur");
            Assert.Equal("MEDIUM", motion.Current);
            Assert.Equal("OFF", motion.Recommended);
            Assert.False(motion.IsRecommended);

            var service = new GamePresetService(new Logger(null), dryRun: false, _ => false);
            service.ApplySettings(elden, path, new Dictionary<GameSetting, string> { [motion.Setting] = "OFF" }, PcTier.Medio);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(0xFF, bytes[0]);
            Assert.Equal(0xFE, bytes[1]);
            var after = GamePresetService.ReadText(path).Text;
            Assert.Contains("<MotionBlur>OFF</MotionBlur>", after);
            Assert.Contains("<ShadowQuality>MEDIUM</ShadowQuality>", after);         // não marcado: intacto
            Assert.Contains("<QualitySetting>CUSTOM</QualitySetting>", after);      // oculto que acompanha
            Assert.Contains("<Auto-detectBestRenderingSettings>OFF</Auto-detectBestRenderingSettings>", after);
            Assert.True(GamePresetService.HasBackup(path));

            Assert.True(service.RestoreConfig(elden, path));
            Assert.Equal(xml, GamePresetService.ReadText(path).Text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Utf16WithoutBomIsReadAndWrittenWithoutBom()
    {
        var elden = GamePresetService.All.Single(p => p.Id == "eldenring");
        const string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\" ?>\r\n<config><Auto-detectBestRenderingSettings>ON</Auto-detectBestRenderingSettings>" +
                           "<QualitySetting>MEDIUM</QualitySetting><MotionBlur>MEDIUM</MotionBlur></config>";
        var (dir, path) = TempConfig("GraphicsConfig.xml", Encoding.Unicode.GetBytes(xml)); // sem BOM, como o jogo grava
        try
        {
            var motion = GamePresetService.ReadStates(elden, PcTier.Medio, path).Single(s => s.Setting.Key == "MotionBlur");
            Assert.Equal("MEDIUM", motion.Current);
            new GamePresetService(new Logger(null), false, _ => false)
                .ApplySettings(elden, path, new Dictionary<GameSetting, string> { [motion.Setting] = "OFF" }, PcTier.Medio);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal((byte)'<', bytes[0]);
            Assert.Equal(0, bytes[1]);
            Assert.Contains("<MotionBlur>OFF</MotionBlur>", Encoding.Unicode.GetString(bytes));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RefusesWhileGameIsRunningOrFileIsReadOnly()
    {
        var fortnite = GamePresetService.All.Single(p => p.Id == "fortnite");
        var (dir, path) = TempConfig("GameUserSettings.ini", Encoding.UTF8.GetBytes(FortniteIni));
        try
        {
            var shadow = fortnite.ConfigFile!.Settings.Single(s => s.Key == "sg.ShadowQuality");
            var values = new Dictionary<GameSetting, string> { [shadow] = "0" };

            var running = new GamePresetService(new Logger(null), false, exe => exe == "fortniteclient-win64-shipping.exe");
            var ex = Assert.Throws<InvalidOperationException>(() => running.ApplySettings(fortnite, path, values, PcTier.Medio));
            Assert.Contains("Feche o jogo", ex.Message);

            File.SetAttributes(path, FileAttributes.ReadOnly);
            var closed = new GamePresetService(new Logger(null), false, _ => false);
            Assert.Throws<UnauthorizedAccessException>(() => closed.ApplySettings(fortnite, path, values, PcTier.Medio));
            File.SetAttributes(path, FileAttributes.Normal);

            Assert.Equal(FortniteIni, File.ReadAllText(path));
            Assert.False(GamePresetService.HasBackup(path));

            // Simulação: lê, mas não grava nem cria backup.
            new GamePresetService(new Logger(null), true, _ => false).ApplySettings(fortnite, path, values, PcTier.Medio);
            Assert.Equal(FortniteIni, File.ReadAllText(path));
            Assert.False(GamePresetService.HasBackup(path));

            closed.ApplySettings(fortnite, path, values, PcTier.Medio);
            Assert.Equal(FortniteIni.Replace("sg.ShadowQuality=3", "sg.ShadowQuality=0"), File.ReadAllText(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
    }

    // --- Detecção automática (Unreal Engine) ---

    [Theory]
    [InlineData(@"H:\S\Big Helmet Heroes\BigHelmetHeroes\Binaries\Win64\BigHelmetHeroes-Win64-Shipping.exe", "BigHelmetHeroes")]
    [InlineData(@"H:\S\The Invincible\TheInvincible\Binaries\Win64\TheInvincible-Win64-Shipping.exe", "TheInvincible")]
    [InlineData(@"C:\Jogos\X\Jogo-Win64-Shipping.exe", "Jogo")]
    [InlineData(@"H:\S\Hades\x64\Hades.exe", null)]
    public void UnrealProjectNameFromExe(string exe, string? expected) =>
        Assert.Equal(expected, UnrealConfigDetector.ProjectName(exe));

    [Fact]
    public void DetectsOnlyOptionsPresentInTheFile()
    {
        var root = Directory.CreateTempSubdirectory("go-ue-").FullName;
        try
        {
            var local = Path.Combine(root, "Local");
            var cfgDir = Path.Combine(local, "TheInvincible", "Saved", "Config", "WindowsNoEditor");
            Directory.CreateDirectory(cfgDir);
            File.WriteAllText(Path.Combine(cfgDir, "GameUserSettings.ini"),
                "[ScalabilityGroups]\r\nsg.ResolutionQuality=90.000000\r\nsg.ShadowQuality=2\r\nsg.TextureQuality=3\r\nsg.EffectsQuality=7\r\n\r\n" +
                "[/Script/Engine.GameUserSettings]\r\nbUseVSync=True\r\n");
            var game = new GameEntry("The Invincible", GameSource.Steam, @"H:\S\The Invincible",
                @"H:\S\The Invincible\TheInvincible\Binaries\Win64\TheInvincible-Win64-Shipping.exe");

            var preset = UnrealConfigDetector.Detect(game, local, Path.Combine(root, "MyGames"))!;
            Assert.NotNull(preset);
            Assert.True(preset.CanAutoApply);
            Assert.Equal(["theinvincible-win64-shipping.exe"], preset.ExeNames);
            // Resolução (porcentagem) e valor desconhecido (7) ficam de fora; VSync achado na seção certa.
            Assert.Equal(["sg.ShadowQuality", "sg.TextureQuality", "bUseVSync"], preset.ConfigFile!.Settings.Select(s => s.Key));
            Assert.Equal("/Script/Engine.GameUserSettings", preset.ConfigFile.Settings.Last().Section);

            // PC Médio: não sugere baixar Sombras (Alta, recomendado genérico Média) nem Texturas (Épica).
            var states = GamePresetService.ReadStates(preset, PcTier.Medio, preset.ConfigFile.ResolvePath());
            var shadow = states.Single(s => s.Setting.Key == "sg.ShadowQuality");
            Assert.Equal("Alta", shadow.Setting.Display(shadow.Current));
            Assert.Equal("Alta", shadow.Setting.Display(shadow.Recommended));
            Assert.True(shadow.IsRecommended);
            Assert.True(states.Single(s => s.Setting.Key == "sg.TextureQuality").IsRecommended);

            // PC Topo: sugere subir.
            var topo = GamePresetService.ReadStates(preset, PcTier.Topo, preset.ConfigFile.ResolvePath());
            Assert.Equal("3", topo.Single(s => s.Setting.Key == "sg.ShadowQuality").Recommended);

            // PC Entrada: aí sim pode sugerir reduzir.
            var entrada = GamePresetService.ReadStates(preset, PcTier.Entrada, preset.ConfigFile.ResolvePath());
            Assert.Equal("0", entrada.Single(s => s.Setting.Key == "sg.ShadowQuality").Recommended);

            // VSync não é "qualidade": continua recomendando desligar.
            Assert.Equal("False", states.Single(s => s.Setting.Key == "bUseVSync").Recommended);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExcludedAndNonUnrealGamesAreNotDetected()
    {
        var valorant = new GameEntry("VALORANT", GameSource.Launcher, @"C:\Riot Games\VALORANT",
            @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe");
        Assert.Null(UnrealConfigDetector.Detect(valorant));
        Assert.Null(UnrealConfigDetector.Detect(new GameEntry("Sem exe", GameSource.Manual, @"C:\x", null)));
        var empty = Directory.CreateTempSubdirectory("go-ue-").FullName;
        try
        {
            Assert.Null(UnrealConfigDetector.Detect(new GameEntry("Hades", GameSource.Steam, @"H:\Hades", @"H:\Hades\x64\Hades.exe"), empty, empty));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public void LeagueConfigPathUsesDetectedFolder()
    {
        var lol = GamePresetService.All.Single(p => p.Id == "lol");
        Assert.Equal(@"D:\Jogos\LoL\Config\game.cfg", lol.ConfigFile!.ResolvePath(@"D:\Jogos\LoL"));
        Assert.Equal(@"C:\Riot Games\League of Legends\Config\game.cfg", lol.ConfigFile.ResolvePath());
    }

    [Fact]
    public void PresetFoundByExecutable()
    {
        var game = new GameEntry("CS2", GameSource.Steam, @"C:\x", @"C:\x\game\bin\win64\cs2.exe");
        Assert.Equal("cs2", GamePresetService.ForGame(game)?.Id);
        Assert.Null(GamePresetService.ForGame(game with { Executable = @"C:\x\outro.exe" }));
    }

    [Fact]
    public void MergeKeyValuesReplacesAndAppends()
    {
        var merged = GamePresetService.MergeKeyValues("version:3465\nrenderDistance:12\nfov:0.5\n",
            new Dictionary<string, string> { ["renderDistance"] = "6", ["maxFps"] = "120" }, ":");
        Assert.Equal("version:3465\nrenderDistance:6\nfov:0.5\nmaxFps:120\n", merged);
        Assert.Equal("a:1\r\nb:2\r\n", GamePresetService.MergeKeyValues("a:0\r\n", new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }, ":"));
    }

    [Fact]
    public void ConfigApplyBacksUpAndRestoresOriginal()
    {
        var dir = Directory.CreateTempSubdirectory("go-mc-").FullName;
        try
        {
            var path = Path.Combine(dir, "options.txt");
            const string original = "version:3465\nrenderDistance:32\nmaxFps:260\n";
            File.WriteAllText(path, original);
            var preset = GamePresetService.All.Single(p => p.Id == "minecraft");

            // Simulação: nada muda.
            Assert.Null(new GamePresetService(new Logger(null), dryRun: true).ApplyConfig(preset, PcTier.Entrada, path));
            Assert.Equal(original, File.ReadAllText(path));
            Assert.False(GamePresetService.HasBackup(path));

            var service = new GamePresetService(new Logger(null), dryRun: false);
            var backup = service.ApplyConfig(preset, PcTier.Entrada, path, new DateTime(2026, 1, 2, 3, 4, 5))!;
            Assert.EndsWith("options.txt.gueibsoptimizer-20260102-030405.bak", backup);
            Assert.Equal(original, File.ReadAllText(backup));
            Assert.Contains("renderDistance:6", File.ReadAllText(path));
            Assert.Contains("version:3465", File.ReadAllText(path));

            // Segunda aplicação cria outro backup; restaurar volta o mais antigo (o original).
            service.ApplyConfig(preset, PcTier.Alto, path, new DateTime(2026, 1, 3));
            Assert.Equal(2, GamePresetService.Backups(path).Count);
            Assert.True(service.RestoreConfig(preset, path));
            Assert.Equal(original, File.ReadAllText(path));
            Assert.False(GamePresetService.HasBackup(path));
            Assert.False(service.RestoreConfig(preset, path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AntiCheatPresetNeverTouchesFiles()
    {
        var cs2 = GamePresetService.All.Single(p => p.Id == "cs2");
        Assert.Throws<InvalidOperationException>(() => new GamePresetService(new Logger(null), false).ApplyConfig(cs2, PcTier.Alto, @"C:\nao\existe.cfg"));
    }

    // --- Biblioteca manual ---

    [Fact]
    public void ManualGamesArePersisted()
    {
        var dir = Directory.CreateTempSubdirectory("go-game-").FullName;
        var list = Path.Combine(Path.GetTempPath(), $"go-lib-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "MeuJogo.exe"), new byte[100]);
            File.WriteAllBytes(Path.Combine(dir, "unins000.exe"), new byte[500]);
            var library = new GameLibraryService(new Logger(null), list);
            var entry = library.AddManual(dir);
            Assert.Equal(Path.Combine(dir, "MeuJogo.exe"), entry.Executable);
            library.AddManual(dir); // sem duplicar
            var loaded = library.LoadManual();
            Assert.Single(loaded);
            Assert.Equal(GameSource.Manual, loaded[0].Source);
            Assert.Throws<FileNotFoundException>(() => library.AddManual(Path.Combine(dir, "nao-existe")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            File.Delete(list);
        }
    }

    [Fact]
    public void UndoProviderNamesGameItems()
    {
        using var f = new Fixture();
        var service = f.Create(dryRun: true);
        var id = GameProfileService.UndoId(f.Game, GameOption.CpuPriority);
        Assert.Equal("game:teste.exe:CpuPriority", id);
        Assert.Contains("teste.exe", service.DisplayName(id));
        Assert.False(service.CanUndo("startup:x"));
    }
}
