using System.IO;
using System.Text.Json;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services;

public sealed record ProfileValidation(Profile? Profile, IReadOnlyList<string> Errors, IReadOnlyList<string> IgnoredIds)
{
    public bool IsValid => Profile is not null && Errors.Count == 0;
}

public sealed record ProfilePlan(IReadOnlyList<ITweak> ToApply, IReadOnlyList<ITweak> ToRevert, IReadOnlyList<ITweak> Unchanged)
{
    public string Describe()
    {
        var lines = new List<string>();
        if (ToApply.Count > 0) lines.Add("Aplicar:\n" + string.Join("\n", ToApply.Select(t => $"  • {t.Name} (risco {TweakText.RiskText(t.Risk)})")));
        if (ToRevert.Count > 0) lines.Add("Desfazer:\n" + string.Join("\n", ToRevert.Select(t => $"  • {t.Name}")));
        if (lines.Count == 0) lines.Add("O sistema já está exatamente como neste perfil.");
        return string.Join("\n\n", lines);
    }
}

/// <summary>
/// Perfis em %AppData%\GueibsOptimizer\profiles\*.gopro. A importação valida o schema e aceita
/// apenas ids de ajustes conhecidos; nada do arquivo é executado.
/// </summary>
public sealed class ProfileService
{
    public const string Extension = ".gopro";
    public const long MaxFileBytes = 256 * 1024;
    public const int MaxNameLength = 80;
    public const int MaxDescriptionLength = 500;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly TweakRegistry _registry;
    private readonly string _directory;
    private readonly Logger _logger;

    public ProfileService(TweakRegistry registry, Logger logger, string? directory = null)
    {
        _registry = registry;
        _logger = logger;
        _directory = directory ?? Path.Combine(AppPaths.DataDirectory, "profiles");
    }

    public IReadOnlyList<Profile> BuiltIn()
    {
        var all = _registry.All;
        string[] balanced =
        [
            "gaming.game-mode", "gpu.game-dvr-off", "input.mouse-accel-off", "input.accessibility-shortcuts-off",
            "privacy.telemetry-policy", "privacy.compat-tasks", "privacy.copilot-off", "privacy.recall-policy",
            "privacy.ads-suggestions-off"
        ];
        string[] economy =
        [
            "gpu.game-dvr-off", "privacy.telemetry-policy", "privacy.compat-tasks", "privacy.copilot-off",
            "privacy.recall-policy", "privacy.ads-suggestions-off"
        ];
        return
        [
            new Profile
            {
                Name = "Competitivo", IsBuiltIn = true,
                Description = "Máximo desempenho e menor latência: todos os ajustes recomendados (risco Baixo e Médio).",
                Tweaks = all.Where(TweakText.IsRecommended).Select(t => t.Id).ToList()
            },
            new Profile
            {
                Name = "Equilibrado", IsBuiltIn = true,
                Description = "Melhora jogos e privacidade sem mudar o plano de energia nem o agendamento da GPU.",
                Tweaks = balanced.Where(_registry.IsKnown).ToList()
            },
            new Profile
            {
                Name = "Economia", IsBuiltIn = true,
                Description = "Para notebooks na bateria: reduz tarefas em segundo plano e mantém o plano de energia do Windows.",
                Tweaks = economy.Where(_registry.IsKnown).ToList()
            }
        ];
    }

    public IReadOnlyList<Profile> List()
    {
        var result = BuiltIn().ToList();
        try
        {
            if (!Directory.Exists(_directory)) return result;
            foreach (var file in Directory.EnumerateFiles(_directory, "*" + Extension).OrderBy(f => f))
            {
                var v = Validate(file);
                if (v.Profile is not null)
                {
                    v.Profile.FilePath = file;
                    result.Add(v.Profile);
                }
                else
                {
                    _logger.Warn($"Perfil ignorado ({Path.GetFileName(file)}): {string.Join("; ", v.Errors)}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Não foi possível listar perfis: {ex.Message}");
        }
        return result;
    }

    /// <summary>Cria um perfil com os ajustes atualmente aplicados.</summary>
    public Profile SaveCurrent(string name, string description, IEnumerable<TweakState> states) =>
        Save(new Profile
        {
            Name = name.Trim(),
            Description = description.Trim(),
            Tweaks = states.Where(s => s.Applicable && s.Applied).Select(s => s.Tweak.Id).ToList()
        });

    public Profile Duplicate(Profile source) =>
        Save(new Profile
        {
            Name = UniqueName($"{source.Name} (cópia)"),
            Description = source.Description,
            Tweaks = [.. source.Tweaks]
        });

    public void Delete(Profile profile)
    {
        if (profile.IsBuiltIn || profile.FilePath is null)
            throw new InvalidOperationException("Perfis padrão não podem ser excluídos.");
        File.Delete(profile.FilePath);
        _logger.Info($"Perfil excluído: {profile.Name}");
    }

    public void Export(Profile profile, string path)
    {
        var copy = new Profile
        {
            Name = profile.Name, Description = profile.Description,
            Tweaks = [.. profile.Tweaks], CreatedUtc = profile.CreatedUtc
        };
        File.WriteAllText(path, JsonSerializer.Serialize(copy, WriteOptions));
        _logger.Info($"Perfil exportado: {profile.Name} → {path}");
    }

    /// <summary>Salva um perfil já validado (resultado de <see cref="Validate"/>).</summary>
    public Profile Import(ProfileValidation validation)
    {
        if (!validation.IsValid) throw new InvalidOperationException("Perfil inválido.");
        var p = validation.Profile!;
        return Save(new Profile { Name = UniqueName(p.Name), Description = p.Description, Tweaks = [.. p.Tweaks] });
    }

    public ProfileValidation Validate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Invalid("Arquivo não encontrado.");
            if (info.Length > MaxFileBytes) return Invalid("Arquivo grande demais para um perfil.");
            return ValidateJson(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Invalid($"Não foi possível ler o arquivo: {ex.Message}");
        }
    }

    /// <summary>Validação estrita do schema: tipos exatos, limites de tamanho e só ids conhecidos.</summary>
    public ProfileValidation ValidateJson(string json)
    {
        var errors = new List<string>();
        var ignored = new List<string>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            return Invalid("O arquivo não é um JSON válido.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Invalid("Formato inesperado.");

            if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String ||
                format.GetString() is not (Profile.FormatId or Profile.LegacyFormatId))
                errors.Add("Não é um perfil do Gueibs Optimizer (campo \"format\").");
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var v) || v < 1 || v > Profile.CurrentVersion)
                errors.Add("Versão de perfil não suportada.");

            string name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : string.Empty;
            if (name.Length == 0 || name.Length > MaxNameLength) errors.Add($"O nome deve ter de 1 a {MaxNameLength} caracteres.");
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

            string description = string.Empty;
            if (root.TryGetProperty("description", out var d))
            {
                if (d.ValueKind != JsonValueKind.String) errors.Add("Descrição inválida.");
                else description = d.GetString()!.Trim();
                if (description.Length > MaxDescriptionLength) description = description[..MaxDescriptionLength];
            }

            var tweaks = new List<string>();
            if (!root.TryGetProperty("tweaks", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                errors.Add("Lista de ajustes ausente.");
            }
            else
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) { errors.Add("A lista de ajustes só pode conter textos (ids)."); break; }
                    var id = item.GetString()!;
                    var known = _registry.Find(id);
                    if (known is null) ignored.Add(id.Length > 60 ? id[..60] + "…" : id);
                    else if (!tweaks.Contains(known.Id)) tweaks.Add(known.Id);
                }
                if (list.GetArrayLength() > 500) errors.Add("Lista de ajustes grande demais.");
            }

            if (errors.Count > 0) return new ProfileValidation(null, errors, ignored);
            return new ProfileValidation(new Profile { Name = name, Description = description, Tweaks = tweaks }, errors, ignored);
        }
    }

    /// <summary>O perfil é o estado desejado: aplica o que falta e desfaz o que não faz parte dele.</summary>
    public static ProfilePlan Plan(Profile profile, IEnumerable<TweakState> states)
    {
        var wanted = profile.Tweaks.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var apply = new List<ITweak>();
        var revert = new List<ITweak>();
        var unchanged = new List<ITweak>();
        foreach (var s in states.Where(s => s.Applicable))
        {
            bool want = wanted.Contains(s.Tweak.Id);
            if (want && !s.Applied) apply.Add(s.Tweak);
            else if (!want && s.Applied) revert.Add(s.Tweak);
            else unchanged.Add(s.Tweak);
        }
        return new ProfilePlan(apply, revert, unchanged);
    }

    public static async Task<IReadOnlyList<string>> ExecuteAsync(ProfilePlan plan, Logger logger, IProgress<string>? progress = null)
    {
        var errors = new List<string>();
        foreach (var t in plan.ToRevert)
        {
            progress?.Report($"Desfazendo: {t.Name}");
            try { await t.RevertAsync().ConfigureAwait(false); }
            catch (Exception ex) { errors.Add($"{t.Name}: {ex.Message}"); logger.Error($"Perfil: falha ao desfazer {t.Name}", ex); }
        }
        foreach (var t in plan.ToApply)
        {
            progress?.Report($"Aplicando: {t.Name}");
            try { await t.ApplyAsync().ConfigureAwait(false); }
            catch (Exception ex) { errors.Add($"{t.Name}: {ex.Message}"); logger.Error($"Perfil: falha ao aplicar {t.Name}", ex); }
        }
        return errors;
    }

    private Profile Save(Profile profile)
    {
        if (profile.Name.Length == 0 || profile.Name.Length > MaxNameLength)
            throw new ArgumentException($"O nome deve ter de 1 a {MaxNameLength} caracteres.");
        Directory.CreateDirectory(_directory);
        var safe = string.Concat(profile.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(_directory, safe + Extension);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(_directory, $"{safe} {i}{Extension}");
        File.WriteAllText(path, JsonSerializer.Serialize(profile, WriteOptions));
        profile.FilePath = path;
        _logger.Info($"Perfil salvo: {profile.Name}");
        return profile;
    }

    private string UniqueName(string name)
    {
        var existing = List().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        if (!existing.Contains(name)) return name;
        for (int i = 2; ; i++)
        {
            var candidate = $"{name} {i}";
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    private static ProfileValidation Invalid(string error) => new(null, [error], []);
}
