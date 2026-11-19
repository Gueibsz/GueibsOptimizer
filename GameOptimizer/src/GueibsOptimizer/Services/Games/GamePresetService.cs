using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services.Games;

public sealed class GamePresetFile
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("games")] public List<GamePreset> Games { get; set; } = [];
}

public sealed class GamePreset
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("exeNames")] public List<string> ExeNames { get; set; } = [];
    [JsonPropertyName("antiCheat")] public string? AntiCheat { get; set; }
    /// <summary>Por que o app não altera as configurações deste jogo (quando não há configFile).</summary>
    [JsonPropertyName("configNote")] public string? ConfigNote { get; set; }
    [JsonPropertyName("general")] public List<string> General { get; set; } = [];
    [JsonPropertyName("tiers")] public Dictionary<string, List<string>> Tiers { get; set; } = [];
    [JsonPropertyName("configFile")] public GameConfigFile? ConfigFile { get; set; }

    /// <summary>
    /// Tem arquivo de configuração conhecido: o app mostra "atual × recomendado" e aplica só o que o usuário marcar.
    /// É o mesmo arquivo de opções que o menu do jogo grava (nunca executáveis, nunca memória).
    /// </summary>
    [JsonIgnore] public bool CanAutoApply => ConfigFile is { Settings.Count: > 0 };

    /// <summary>
    /// Recomendação genérica (detecção automática): só sugere SUBIR qualidade, nunca reduzir, exceto em PC nível Entrada.
    /// Sem dados de FPS por jogo, baixar a qualidade de um jogo leve que já roda bem só pioraria a imagem.
    /// </summary>
    [JsonIgnore] public bool NeverLowerUnlessEntrada { get; init; }

    /// <summary>Recomendado efetivo para o valor atual, respeitando <see cref="NeverLowerUnlessEntrada"/>.</summary>
    public string? EffectiveRecommendation(GameSetting setting, string? current, PcTier tier)
    {
        var rec = setting.RecommendedFor(tier);
        if (!NeverLowerUnlessEntrada || tier == PcTier.Entrada || rec is null || current is null) return rec;
        return int.TryParse(current, out var cur) && int.TryParse(rec, out var r) && cur > r ? current : rec;
    }

    public IReadOnlyList<string> RecommendationsFor(PcTier tier) =>
        Tiers.TryGetValue(tier.ToString(), out var list) ? list : [];
}

public sealed class GameConfigFile
{
    /// <summary>Caminho com variáveis de ambiente e, opcionalmente, {GameFolder} (pasta do jogo detectada).</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    /// <summary>Pasta usada no lugar de {GameFolder} quando o jogo não foi detectado.</summary>
    [JsonPropertyName("defaultFolder")] public string? DefaultFolder { get; set; }
    /// <summary>"ini", "keyvalue" (chave&lt;separador&gt;valor por linha) ou "xml" (&lt;Chave&gt;valor&lt;/Chave&gt;).</summary>
    [JsonPropertyName("format")] public string Format { get; set; } = "keyvalue";
    [JsonPropertyName("separator")] public string Separator { get; set; } = ":";
    [JsonPropertyName("settings")] public List<GameSetting> Settings { get; set; } = [];

    public string ResolvePath(string? gameFolder = null) =>
        Environment.ExpandEnvironmentVariables(Path.Replace("{GameFolder}", gameFolder ?? DefaultFolder ?? string.Empty, StringComparison.Ordinal));
}

public sealed class GameSetting
{
    [JsonPropertyName("section")] public string? Section { get; set; }
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    /// <summary>Valor bruto → texto (ex.: "0" → "Baixa"). Vazio: mostra o número.</summary>
    [JsonPropertyName("values")] public Dictionary<string, string> Values { get; set; } = [];
    [JsonPropertyName("tiers")] public Dictionary<string, string> Tiers { get; set; } = [];
    /// <summary>Não aparece na tabela; é gravado junto quando qualquer item é aplicado (ex.: "predefinição = personalizada").</summary>
    [JsonPropertyName("hidden")] public bool Hidden { get; set; }

    public string? RecommendedFor(PcTier tier) => Tiers.GetValueOrDefault(tier.ToString());

    public string Display(string? raw) =>
        raw is null ? "(não definido)"
        : Values.FirstOrDefault(kv => string.Equals(kv.Key, raw, StringComparison.OrdinalIgnoreCase)).Value ?? raw;

    public bool SameValue(string? a, string? b)
    {
        if (a is null || b is null) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        // "120" e "120.000000" são o mesmo número.
        return double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) &&
               double.TryParse(b, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) &&
               Math.Abs(x - y) < 1e-9;
    }
}

/// <summary>Uma linha da tabela "Configuração / Atual / Recomendado".</summary>
public sealed record GameSettingState(GameSetting Setting, string? Current, string? Recommended)
{
    public bool IsRecommended => Setting.SameValue(Current, Recommended);
}

/// <summary>Leitura e escrita pontual de chaves, preservando o resto do arquivo (linhas, comentários, ordem).</summary>
public static class GameConfigEditor
{
    public static string? Read(GameConfigFile config, string content, GameSetting s) => config.Format switch
    {
        "ini" => IniRead(content, s.Section ?? string.Empty, s.Key),
        "xml" => XmlRead(content, s.Key),
        _ => KeyValueRead(content, s.Key, config.Separator)
    };

    public static string Write(GameConfigFile config, string content, GameSetting s, string value) => config.Format switch
    {
        "ini" => IniWrite(content, s.Section ?? string.Empty, s.Key, value),
        "xml" => XmlWrite(content, s.Key, value),
        _ => GamePresetService.MergeKeyValues(content, new Dictionary<string, string> { [s.Key] = value }, config.Separator)
    };

    private static string NewLine(string content) => content.Contains("\r\n") ? "\r\n" : "\n";

    private static string? KeyValueRead(string content, string key, string sep)
    {
        foreach (var line in content.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            int idx = l.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0 && l[..idx] == key) return l[(idx + sep.Length)..];
        }
        return null;
    }

    // --- INI (seções [Nome], linhas Chave=Valor; a primeira ocorrência da chave na seção vale) ---

    private static (int Start, int End) IniSection(List<string> lines, string section)
    {
        int start = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                if (start >= 0) return (start, i);
                if (string.Equals(t[1..^1], section, StringComparison.OrdinalIgnoreCase)) start = i + 1;
            }
        }
        return start >= 0 ? (start, lines.Count) : (-1, -1);
    }

    private static int IniKeyLine(List<string> lines, (int Start, int End) sec, string key)
    {
        for (int i = sec.Start; i < sec.End; i++)
        {
            int idx = lines[i].IndexOf('=');
            if (idx > 0 && lines[i][..idx].Trim() == key) return i;
        }
        return -1;
    }

    public static string? IniRead(string content, string section, string key)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        var sec = IniSection(lines, section);
        if (sec.Start < 0) return null;
        int i = IniKeyLine(lines, sec, key);
        return i < 0 ? null : lines[i][(lines[i].IndexOf('=') + 1)..].Trim();
    }

    public static string IniWrite(string content, string section, string key, string value)
    {
        var nl = NewLine(content);
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        bool endsWithNewLine = lines.Count > 0 && lines[^1].Length == 0;
        if (endsWithNewLine) lines.RemoveAt(lines.Count - 1);
        var sec = IniSection(lines, section);
        if (sec.Start < 0)
        {
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
        }
        else
        {
            int i = IniKeyLine(lines, sec, key);
            if (i >= 0) lines[i] = $"{key}={value}";
            else
            {
                // Insere depois da última linha não vazia da seção.
                int at = sec.End;
                while (at > sec.Start && lines[at - 1].Trim().Length == 0) at--;
                lines.Insert(at, $"{key}={value}");
            }
        }
        return string.Join(nl, lines) + (endsWithNewLine ? nl : string.Empty);
    }

    // --- XML simples de elementos (<Chave>valor</Chave>), sem reformatar o arquivo ---

    private static Regex XmlElement(string key) => new($"<{Regex.Escape(key)}>([^<]*)</{Regex.Escape(key)}>");

    public static string? XmlRead(string content, string key) =>
        XmlElement(key).Match(content) is { Success: true } m ? m.Groups[1].Value.Trim() : null;

    /// <summary>Só troca elementos que já existem (não inventa chaves num formato que o jogo controla).</summary>
    public static string XmlWrite(string content, string key, string value)
    {
        var re = XmlElement(key);
        if (!re.IsMatch(content)) throw new InvalidDataException($"A opção {key} não existe neste arquivo (versão diferente do jogo?).");
        var safe = System.Security.SecurityElement.Escape(value) ?? value;
        return re.Replace(content, $"<{key}>{safe}</{key}>", 1);
    }
}

/// <summary>
/// Presets por jogo e por nível do PC (Data/gamepresets.json), no estilo "atual × ideal": lê o arquivo de opções
/// do jogo, mostra o valor atual e o recomendado para o PC, e grava só os itens que o usuário escolher.
/// Antes de gravar: o jogo precisa estar fechado e é feito backup ".gueibsoptimizer-AAAAMMDD-HHmmss.bak" ao lado do
/// arquivo; "Restaurar config original" volta o backup mais antigo. O encoding do arquivo (UTF-8/UTF-16) é mantido.
/// </summary>
public sealed class GamePresetService(Logger logger, bool dryRun, Func<string, bool>? isProcessRunning = null)
{
    public const string BackupMarker = ".gueibsoptimizer-";

    private readonly Func<string, bool> _isRunning = isProcessRunning ?? DefaultIsRunning;

    private static readonly Lazy<GamePresetFile> Embedded = new(() =>
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("gamepresets.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    public static IReadOnlyList<GamePreset> All => Embedded.Value.Games;

    public static GamePresetFile Parse(string json) =>
        JsonSerializer.Deserialize<GamePresetFile>(json) ?? throw new JsonException("gamepresets.json vazio.");

    public static GamePreset? ForGame(GameEntry game) =>
        All.FirstOrDefault(p => p.ExeNames.Contains(game.ExeName, StringComparer.OrdinalIgnoreCase));

    /// <summary>Preset próprio do jogo; senão, opções detectadas automaticamente (Unreal Engine); senão, null.</summary>
    public static GamePreset? ForGameOrDetect(GameEntry game) => ForGame(game) ?? UnrealConfigDetector.Detect(game);

    private static bool DefaultIsRunning(string exeName)
    {
        var name = Path.GetFileNameWithoutExtension(exeName);
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    /// <summary>Nome do processo do jogo que está aberto (bloqueia a gravação), ou null.</summary>
    public string? RunningExe(GamePreset preset) => preset.ExeNames.FirstOrDefault(_isRunning);

    // --- Leitura ---

    /// <summary>Lê o arquivo mantendo o encoding (BOM) para gravar do mesmo jeito.</summary>
    public static (string Text, Encoding Encoding) ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Encoding enc = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE ? Encoding.Unicode
                     : bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF ? Encoding.BigEndianUnicode
                     : bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? new UTF8Encoding(true)
                     // UTF-16 sem BOM (ex.: GraphicsConfig.xml do ELDEN RING): texto ASCII com bytes zero intercalados.
                     : bytes.Length >= 4 && bytes[0] != 0 && bytes[1] == 0 && bytes[2] != 0 && bytes[3] == 0 ? new UnicodeEncoding(false, false)
                     : new UTF8Encoding(false);
        int skip = enc.GetPreamble().Length;
        return (enc.GetString(bytes, skip, bytes.Length - skip), enc);
    }

    /// <summary>Estado de cada opção (visível) do preset: atual × recomendado para o nível do PC.</summary>
    public static IReadOnlyList<GameSettingState> ReadStates(GamePreset preset, PcTier tier, string path)
    {
        var config = preset.ConfigFile ?? throw new InvalidOperationException($"{preset.Name}: sem arquivo de configuração conhecido.");
        var (text, _) = ReadText(path);
        return config.Settings.Where(s => !s.Hidden)
            .Select(s =>
            {
                var current = GameConfigEditor.Read(config, text, s);
                return new GameSettingState(s, current, preset.EffectiveRecommendation(s, current, tier));
            })
            .ToList();
    }

    // --- Escrita ---

    /// <summary>
    /// Grava os valores escolhidos (e as opções ocultas que acompanham). Retorna o caminho do backup (null em simulação).
    /// Falha sem alterar nada se o jogo estiver aberto, o arquivo for somente leitura ou não existir.
    /// </summary>
    public string? ApplySettings(GamePreset preset, string path, IReadOnlyDictionary<GameSetting, string> values, PcTier tier, DateTime? now = null)
    {
        var config = preset.ConfigFile ?? throw new InvalidOperationException($"{preset.Name}: apenas recomendação (o app não altera arquivos deste jogo).");
        if (values.Count == 0) throw new InvalidOperationException("Nenhuma opção marcada.");
        if (!File.Exists(path))
            throw new FileNotFoundException("Arquivo de configuração não encontrado. Abra o jogo uma vez (e feche) antes.", path);
        if (RunningExe(preset) is { } running)
            throw new InvalidOperationException($"Feche o jogo antes ({running} está aberto): ele regrava este arquivo ao sair.");
        if (new FileInfo(path).IsReadOnly)
            throw new UnauthorizedAccessException("O arquivo está marcado como somente leitura (alguém travou as configurações de propósito). Nada foi alterado.");

        var all = new Dictionary<GameSetting, string>(values);
        foreach (var hidden in config.Settings.Where(s => s.Hidden))
            if (hidden.RecommendedFor(tier) is { } v) all[hidden] = v;

        var (text, encoding) = ReadText(path);
        var updated = all.Aggregate(text, (current, kv) => GameConfigEditor.Write(config, current, kv.Key, kv.Value));
        var summary = string.Join(", ", all.Select(kv => $"{kv.Key.Label}={kv.Key.Display(kv.Value)}"));

        if (dryRun)
        {
            logger.Simulated($"{preset.Name}: backup e alteração de {path} ({summary})");
            return null;
        }

        var backup = path + BackupMarker + (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss") + ".bak";
        File.Copy(path, backup, overwrite: false);
        File.WriteAllBytes(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(updated)]);
        logger.Info($"{preset.Name}: {all.Count} opção(ões) aplicada(s) em {path} ({summary}). Backup: {Path.GetFileName(backup)}.");
        return backup;
    }

    /// <summary>Aplica todas as opções recomendadas do nível.</summary>
    public string? ApplyConfig(GamePreset preset, PcTier tier, string? configPath = null, DateTime? now = null)
    {
        var config = preset.ConfigFile ?? throw new InvalidOperationException($"{preset.Name}: apenas recomendação (o app não altera arquivos deste jogo).");
        var values = config.Settings.Where(s => !s.Hidden && s.RecommendedFor(tier) is not null)
                                    .ToDictionary(s => s, s => s.RecommendedFor(tier)!);
        return ApplySettings(preset, configPath ?? config.ResolvePath(), values, tier, now);
    }

    /// <summary>Substitui as chaves existentes e acrescenta as que faltam, sem mexer nas outras linhas.</summary>
    public static string MergeKeyValues(string content, IReadOnlyDictionary<string, string> values, string separator)
    {
        var newline = content.Contains("\r\n") ? "\r\n" : "\n";
        var lines = content.Length == 0 ? [] : content.Split(newline).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var pending = new Dictionary<string, string>(values);
        for (int i = 0; i < lines.Count; i++)
        {
            int idx = lines[i].IndexOf(separator, StringComparison.Ordinal);
            if (idx <= 0) continue;
            var key = lines[i][..idx];
            if (pending.Remove(key, out var value)) lines[i] = key + separator + value;
        }
        lines.AddRange(pending.Select(kv => kv.Key + separator + kv.Value));
        return string.Join(newline, lines) + newline;
    }

    // --- Backup / restauração ---

    public static IReadOnlyList<string> Backups(string configPath)
    {
        var dir = Path.GetDirectoryName(configPath);
        if (dir is null || !Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, Path.GetFileName(configPath) + BackupMarker + "*.bak")
                        .Order(StringComparer.Ordinal) // o nome tem a data: o primeiro é o original
                        .ToList();
    }

    public static bool HasBackup(string configPath) => Backups(configPath).Count > 0;

    /// <summary>Volta o arquivo original (backup mais antigo) e apaga os backups do app.</summary>
    public bool RestoreConfig(GamePreset preset, string? configPath = null)
    {
        if (preset.ConfigFile is null) return false;
        var path = configPath ?? preset.ConfigFile.ResolvePath();
        var backups = Backups(path);
        if (backups.Count == 0) return false;
        if (RunningExe(preset) is { } running)
            throw new InvalidOperationException($"Feche o jogo antes ({running} está aberto).");
        if (dryRun)
        {
            logger.Simulated($"{preset.Name}: restaurar {path} a partir de {Path.GetFileName(backups[0])}");
            return true;
        }
        File.Copy(backups[0], path, overwrite: true);
        foreach (var b in backups)
        {
            try { File.Delete(b); } catch (IOException) { }
        }
        logger.Info($"{preset.Name}: configuração original restaurada em {path}.");
        return true;
    }
}
