using System.IO;
using System.Reflection;
using System.Text.Json;
using GueibsOptimizer.Core;
using Microsoft.Win32;

namespace GueibsOptimizer.Services.Tweaks;

public sealed record ManualServiceEntry(string Name, string Default, string Reason);

/// <summary>
/// 9.2: "Serviços para Manual" a partir da allowlist fechada Data/services-manual.json. Serviços protegidos nunca entram,
/// e um serviço do qual outro serviço automático depende fica de fora (dependências lidas de DependOnService).
/// </summary>
public static class ServicesManual
{
    public const string TweakId = "services.safe-manual";

    public static IReadOnlyList<ManualServiceEntry> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<ManualServiceEntry>();
        foreach (var e in doc.RootElement.GetProperty("services").EnumerateArray())
        {
            var name = e.GetProperty("name").GetString() ?? string.Empty;
            var def = e.GetProperty("default").GetString() ?? "Automatic";
            var reason = e.GetProperty("reason").GetString() ?? string.Empty;
            if (DeclarativeTweakLoader.ProtectedServices.Contains(name))
                throw new DeclarativeValidationException([$"services-manual.json: o serviço {name} é protegido"]);
            if (!DeclarativeTweakLoader.StartupTypes.Contains(def))
                throw new DeclarativeValidationException([$"services-manual.json: tipo inválido em {name}"]);
            if (list.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new DeclarativeValidationException([$"services-manual.json: {name} repetido"]);
            list.Add(new ManualServiceEntry(name, def, reason));
        }
        return list;
    }

    /// <summary>Serviços que dependem de <paramref name="service"/> e iniciam automaticamente (Start = 2).</summary>
    public static IReadOnlyList<string> AutomaticDependents(string service, IEnumerable<(string Name, string[] DependsOn, int Start)> all) =>
        all.Where(s => s.Start == 2 && s.DependsOn.Contains(service, StringComparer.OrdinalIgnoreCase)).Select(s => s.Name).ToList();

    /// <summary>Lê (só leitura) a lista de serviços com DependOnService e Start.</summary>
    public static IReadOnlyList<(string Name, string[] DependsOn, int Start)> ReadServiceGraph()
    {
        var list = new List<(string, string[], int)>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (root is null) return list;
            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var k = root.OpenSubKey(name);
                    if (k is null) continue;
                    var deps = k.GetValue("DependOnService") as string[] ?? [];
                    int start = k.GetValue("Start") is int s ? s : -1;
                    list.Add((name, deps, start));
                }
                catch (System.Security.SecurityException) { }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return list;
    }

    public static DeclarativeDefinition BuildDefinition(IReadOnlyList<ManualServiceEntry> entries, Func<string, IReadOnlyList<string>> dependents)
    {
        var eligible = entries.Where(e => dependents(e.Name).Count == 0).ToList();
        var skipped = entries.Except(eligible).Select(e => $"{e.Name} (outros serviços automáticos dependem dele)").ToList();
        return new DeclarativeDefinition
        {
            Id = TweakId,
            Category = "Windows",
            Risk = "Medium",
            Points = 0,
            Level = "Alto",
            InBoost = false,
            Name = "Serviços pouco usados em Manual",
            Description = "Coloca em inicialização Manual uma lista fechada de serviços que o Windows inicia sozinho quando precisa. Desfazer restaura o tipo de inicialização original de todos.",
            Notice = "Desfazer = restaurar todos os serviços ao tipo de inicialização que tinham antes.",
            Info = new DeclarativeInfo
            {
                Long = "Lista fechada (Data/services-manual.json). Nunca inclui segurança, Windows Update, Defender, Firewall, BITS, rede essencial, áudio ou impressão. Antes de alterar, o app confere se outro serviço automático depende de cada um.",
                Benefit = "Menos serviços iniciando com o Windows.",
                Risk = "Médio: algum recurso pouco usado pode demorar um instante a mais na primeira vez. Fora do Boost.",
                Notes = string.Join("\n", eligible.Select(e => $"• {e.Name}: {e.Reason}")) +
                        (skipped.Count > 0 ? "\nFora desta vez: " + string.Join(", ", skipped) : string.Empty)
            },
            Services = eligible.Select(e => new DeclarativeService { Name = e.Name, Startup = "Manual", Default = e.Default }).ToList()
        };
    }

    public static DeclarativeTweak? CreateDefault(TweakContext ctx)
    {
        var asm = Assembly.GetExecutingAssembly();
        var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Data.services-manual.json", StringComparison.OrdinalIgnoreCase));
        if (res is null) return null;
        using var stream = asm.GetManifestResourceStream(res)!;
        using var reader = new StreamReader(stream);
        var entries = Parse(reader.ReadToEnd());
        var graph = ReadServiceGraph();
        var def = BuildDefinition(entries, name => AutomaticDependents(name, graph));
        return def.Services.Count == 0 ? null : new DeclarativeTweak(def, ctx);
    }
}
