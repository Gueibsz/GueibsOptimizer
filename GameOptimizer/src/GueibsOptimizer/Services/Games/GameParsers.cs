using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GueibsOptimizer.Services.Games;

/// <summary>Nó de um arquivo VDF/ACF da Steam: valor texto ou filhos (chave → nó).</summary>
public sealed class VdfNode
{
    public string? Value { get; init; }
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public VdfNode? this[string key] => Children.GetValueOrDefault(key);
    public string? Get(string key) => Children.GetValueOrDefault(key)?.Value;
}

/// <summary>
/// Parser VDF próprio e simples (formato "chave" "valor" / "chave" { ... } usado pela Steam).
/// Aceita escapes \\ e \", comentários // e tokens sem aspas. Entrada malformada não lança: devolve o que leu.
/// </summary>
public static class VdfParser
{
    public static VdfNode Parse(string text)
    {
        var tokens = Tokenize(text).GetEnumerator();
        var root = new VdfNode();
        ParseInto(root, tokens);
        return root;
    }

    private static void ParseInto(VdfNode node, IEnumerator<string> tokens)
    {
        while (tokens.MoveNext())
        {
            var key = tokens.Current;
            if (key == "}") return;
            if (key == "{") continue;
            if (!tokens.MoveNext()) return;
            var next = tokens.Current;
            if (next == "{")
            {
                var child = new VdfNode();
                ParseInto(child, tokens);
                node.Children[key] = child;
            }
            else if (next != "}")
            {
                node.Children[key] = new VdfNode { Value = next };
            }
            else return;
        }
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c is '{' or '}') { i++; yield return c.ToString(); continue; }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) { i++; sb.Append(text[i] switch { 'n' => '\n', 't' => '\t', _ => text[i] }); }
                    else sb.Append(text[i]);
                    i++;
                }
                i++;
                yield return sb.ToString();
                continue;
            }
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"')) i++;
            yield return text[start..i];
        }
    }

    /// <summary>Caminhos das bibliotecas em libraryfolders.vdf (formato novo com "path" e antigo com o caminho direto).</summary>
    public static IReadOnlyList<string> LibraryPaths(string libraryFoldersVdf)
    {
        var root = Parse(libraryFoldersVdf);
        var lib = root["libraryfolders"] ?? root["LibraryFolders"] ?? root;
        var paths = new List<string>();
        foreach (var (key, node) in lib.Children)
        {
            if (!key.All(char.IsDigit)) continue;
            var path = node.Value ?? node.Get("path");
            if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
        }
        return paths;
    }

    /// <summary>(appid, name, installdir) de um appmanifest_*.acf.</summary>
    public static (string AppId, string Name, string InstallDir)? AppManifest(string acf)
    {
        var state = Parse(acf)["AppState"];
        if (state is null) return null;
        var id = state.Get("appid");
        var name = state.Get("name");
        var dir = state.Get("installdir");
        return id is null || name is null || dir is null ? null : (id, name, dir);
    }
}

public sealed record EpicManifest(string DisplayName, string InstallLocation, string? LaunchExecutable, string AppName);

public static class EpicManifestParser
{
    public static EpicManifest? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var name = S("DisplayName");
            var location = S("InstallLocation");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(location)) return null;
            // Itens que não são jogos (ex.: Unreal Engine, plugins) não têm executável de lançamento.
            var exe = S("LaunchExecutable");
            if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) return null;
            return new EpicManifest(name, location, string.IsNullOrWhiteSpace(exe) ? null : exe, S("AppName") ?? name);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Escolhe o executável principal de uma pasta de jogo: ignora instaladores, desinstaladores, launchers,
/// crash handlers, anti-cheat e redistribuíveis; prefere "*-Shipping.exe" (Unreal) e, depois, o maior arquivo.
/// </summary>
public static partial class MainExecutablePicker
{
    // Trechos curtos e ambíguos (ex.: "eac", que aparece em "Peace") ficaram de fora de propósito.
    [GeneratedRegex(@"(unins\d*|setup|install|launcher|bootstrapper|restarter|vconsole|crash|report|redist|dxsetup|dotnet|directx|prereq|easyanticheat|_eac|_be$|beservice|battleye|vanguard|helper|updater|cefprocess|webhelper|steamworks|service|server|benchmark|config|settings|touchup|cleanup|activation|editor|unitycrashhandler|oalinst|physx)", RegexOptions.IgnoreCase)]
    private static partial Regex Excluded();

    private static readonly string[] ExcludedFolders = ["_CommonRedist", "Redist", "Support", "Modding", "Tools", "Editor", "Extras", "Engine", "DirectX"];

    public static bool IsExcluded(string fileName) => Excluded().IsMatch(Path.GetFileNameWithoutExtension(fileName));

    /// <summary>Só letras e números, minúsculo (ex.: "Biped 2" → "biped2").</summary>
    public static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <param name="files">Caminho relativo à pasta do jogo e tamanho em bytes.</param>
    /// <param name="nameHints">Nome do jogo e da pasta: um .exe com o mesmo nome é o principal.</param>
    public static string? Pick(IEnumerable<(string RelativePath, long Size)> files, params string[] nameHints)
    {
        var candidates = files
            .Where(f => f.RelativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !IsExcluded(f.RelativePath))
            .Where(f => !f.RelativePath.Split('\\', '/')[..^1].Any(part => ExcludedFolders.Contains(part, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        if (candidates.Count == 0) return null;

        var hints = nameHints.Select(Normalize).Where(h => h.Length >= 3).ToList();
        int Score((string RelativePath, long Size) f)
        {
            var exe = Normalize(Path.GetFileNameWithoutExtension(f.RelativePath));
            int score = 0;
            if (Path.GetFileName(f.RelativePath).Contains("-Shipping", StringComparison.OrdinalIgnoreCase)) score += 2; // Unreal Engine
            if (hints.Any(h => exe.Length >= 3 && (h.Contains(exe) || exe.Contains(h)))) score += 1;
            return score;
        }
        return candidates.OrderByDescending(Score).ThenByDescending(f => f.Size).First().RelativePath;
    }

    /// <summary>Varre a pasta (até 4 níveis) e escolhe o .exe principal. Null se não achar.</summary>
    public static string? PickInFolder(string folder, string? gameName = null)
    {
        if (!Directory.Exists(folder)) return null;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var files = Directory.EnumerateFiles(folder, "*.exe", options)
                .Select(f => (Path.GetRelativePath(folder, f), new FileInfo(f).Length));
            var rel = Pick(files, gameName ?? string.Empty, new DirectoryInfo(folder).Name);
            return rel is null ? null : Path.GetFullPath(Path.Combine(folder, rel));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
