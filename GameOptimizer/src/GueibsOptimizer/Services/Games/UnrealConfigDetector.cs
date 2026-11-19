using System.IO;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services.Games;

/// <summary>
/// Detecção automática das opções gráficas de jogos feitos na Unreal Engine (4 e 5). Esses jogos gravam as opções
/// do menu em GameUserSettings.ini, seção [ScalabilityGroups], com as mesmas chaves sg.* do motor.
/// O projeto é identificado pelo caminho do .exe (&lt;Projeto&gt;\Binaries\Win64\...) e o arquivo é procurado em
/// %LocalAppData%\&lt;Projeto&gt;\Saved\Config\... e Documentos\My Games\... . Só aparecem as opções que existem no
/// arquivo. Jogos que sincronizam opções com a conta (VALORANT) ficam de fora.
/// </summary>
public static class UnrealConfigDetector
{
    /// <summary>Projetos que não são lidos: opções sincronizadas com a conta ou tratadas por um preset próprio.</summary>
    public static readonly string[] ExcludedProjects = ["ShooterGame", "FortniteGame"];

    private static readonly string[] PlatformFolders = ["Windows", "WindowsNoEditor", "WindowsClient"];

    private static readonly Dictionary<string, string> QualityLabels = new()
    {
        ["0"] = "Baixa", ["1"] = "Média", ["2"] = "Alta", ["3"] = "Épica", ["4"] = "Cinematográfica"
    };

    /// <summary>Chave sg.* → rótulo e recomendado por nível (Entrada, Médio, Alto, Topo).</summary>
    private static readonly (string Key, string Label, string[] Tiers)[] Known =
    [
        ("sg.ViewDistanceQuality", "Distância de visualização", ["1", "2", "3", "3"]),
        ("sg.AntiAliasingQuality", "Anti-aliasing", ["0", "2", "2", "3"]),
        ("sg.ShadowQuality", "Sombras", ["0", "1", "2", "3"]),
        ("sg.GlobalIlluminationQuality", "Iluminação global", ["0", "1", "2", "3"]),
        ("sg.ReflectionQuality", "Reflexos", ["0", "1", "2", "3"]),
        ("sg.PostProcessQuality", "Pós-processamento", ["0", "1", "2", "3"]),
        ("sg.TextureQuality", "Texturas", ["1", "2", "3", "3"]),
        ("sg.EffectsQuality", "Efeitos", ["0", "1", "2", "3"]),
        ("sg.FoliageQuality", "Vegetação", ["0", "1", "2", "3"]),
        ("sg.ShadingQuality", "Sombreamento", ["0", "2", "2", "3"]),
        ("sg.LandscapeQuality", "Terreno", ["0", "1", "2", "3"])
    ];

    private static readonly PcTier[] TierOrder = [PcTier.Entrada, PcTier.Medio, PcTier.Alto, PcTier.Topo];

    /// <summary>Nome do projeto Unreal a partir do .exe (pasta acima de Binaries), ou null se não parecer Unreal.</summary>
    public static string? ProjectName(string exePath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(exePath) ?? string.Empty);
        if (dir.Name.Equals("Win64", StringComparison.OrdinalIgnoreCase) &&
            dir.Parent is { } bin && bin.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase) && bin.Parent is { } project)
            return project.Name;
        var file = Path.GetFileNameWithoutExtension(exePath);
        int idx = file.IndexOf("-Win64", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? file[..idx] : null;
    }

    /// <summary>Caminhos onde o GameUserSettings.ini costuma ficar, em ordem de preferência.</summary>
    public static IEnumerable<string> CandidatePaths(GameEntry game, string localAppData, string myGames)
    {
        if (game.Executable is null) yield break;
        var names = new List<string>();
        if (ProjectName(game.Executable) is { } project) names.Add(project);
        var exePrefix = Path.GetFileNameWithoutExtension(game.Executable).Split('-')[0];
        if (exePrefix.Length > 0) names.Add(exePrefix);
        names.Add(game.Name);
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
            foreach (var platform in PlatformFolders)
            {
                yield return Path.Combine(localAppData, name, "Saved", "Config", platform, "GameUserSettings.ini");
                yield return Path.Combine(myGames, name, "Saved", "Config", platform, "GameUserSettings.ini");
            }
        }
    }

    /// <summary>Preset montado a partir do arquivo encontrado; null se o jogo não for Unreal ou não houver opções.</summary>
    public static GamePreset? Detect(GameEntry game, string? localAppData = null, string? myGames = null)
    {
        if (game.Executable is null) return null;
        var project = ProjectName(game.Executable);
        if (project is not null && ExcludedProjects.Contains(project, StringComparer.OrdinalIgnoreCase)) return null;

        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        myGames ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games");
        foreach (var path in CandidatePaths(game, localAppData, myGames))
        {
            try
            {
                if (!File.Exists(path)) continue;
                var preset = FromFile(game, path);
                if (preset is not null) return preset;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // sem acesso: tenta o próximo
            }
        }
        return null;
    }

    public static GamePreset? FromFile(GameEntry game, string path)
    {
        var (text, _) = GamePresetService.ReadText(path);
        var settings = new List<GameSetting>();
        foreach (var (key, label, tiers) in Known)
        {
            var current = GameConfigEditor.IniRead(text, "ScalabilityGroups", key);
            if (current is null || !QualityLabels.ContainsKey(current)) continue; // só opções que existem e com valor conhecido
            settings.Add(new GameSetting
            {
                Section = "ScalabilityGroups", Key = key, Label = label, Values = new(QualityLabels),
                Tiers = TierOrder.Select((t, i) => (t, i)).ToDictionary(x => x.t.ToString(), x => tiers[x.i])
            });
        }
        if (settings.Count == 0) return null;

        if (FindSectionWithKey(text, "bUseVSync") is { } vsyncSection)
            settings.Add(new GameSetting
            {
                Section = vsyncSection, Key = "bUseVSync", Label = "VSync",
                Values = new() { ["False"] = "Desligado", ["True"] = "Ligado" },
                Tiers = TierOrder.ToDictionary(t => t.ToString(), _ => "False")
            });

        return new GamePreset
        {
            Id = "unreal:" + (ProjectName(game.Executable!) ?? game.Name),
            Name = game.Name,
            NeverLowerUnlessEntrada = true,
            ExeNames = [game.ExeName.ToLowerInvariant()],
            General = ["Opções detectadas automaticamente no arquivo de configuração da Unreal Engine. Alguns jogos ignoram ou limitam parte delas.", "Recomendação geral: o app só sugere subir o que estiver abaixo do ideal para o seu PC; não sugere baixar o que você já usa (exceto em PCs nível Entrada)."],
            Tiers = TierOrder.ToDictionary(t => t.ToString(), _ => new List<string> { "Use a tabela acima: atual × recomendado para o seu PC." }),
            ConfigFile = new GameConfigFile { Path = path, Format = "ini", Settings = settings }
        };
    }

    private static string? FindSectionWithKey(string text, string key)
    {
        string? section = null;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            int idx = line.IndexOf('=');
            if (section is not null && idx > 0 && line[..idx].Trim() == key) return section;
        }
        return null;
    }
}
