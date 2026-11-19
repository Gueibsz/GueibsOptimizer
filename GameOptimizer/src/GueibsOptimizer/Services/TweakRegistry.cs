using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services.Tweaks;

namespace GueibsOptimizer.Services;

/// <summary>Lista única de todos os ajustes. Todo o app consulta esta lista.</summary>
public sealed class TweakRegistry
{
    private readonly Dictionary<string, ITweak> _byId;

    public TweakRegistry(IEnumerable<ITweak> tweaks)
    {
        All = tweaks.ToList();
        _byId = new Dictionary<string, ITweak>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in All)
        {
            if (!_byId.TryAdd(t.Id, t))
                throw new InvalidOperationException($"Id de ajuste duplicado: {t.Id}");
        }
    }

    public IReadOnlyList<ITweak> All { get; }

    public ITweak? Find(string id) => _byId.GetValueOrDefault(id);

    private Dictionary<string, int>? _normalized;

    /// <summary>Pesos normalizados para somar 100 (maior resto). A pontuação já é reescalada, então isto só afeta a exibição.</summary>
    public int PointsOf(ITweak tweak) => (_normalized ??= Normalize(All.Select(t => (t.Id, t.Points)))).GetValueOrDefault(tweak.Id);

    public static Dictionary<string, int> Normalize(IEnumerable<(string Id, int Points)> raw)
    {
        var list = raw.Where(r => r.Points > 0).ToList();
        int total = list.Sum(r => r.Points);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (total == 0) return result;
        var exact = list.Select(r => (r.Id, Value: r.Points * 100.0 / total)).ToList();
        foreach (var (id, v) in exact) result[id] = (int)Math.Floor(v);
        int missing = 100 - result.Values.Sum();
        foreach (var (id, _) in exact.OrderByDescending(e => e.Value - Math.Floor(e.Value)).ThenBy(e => e.Id).Take(missing)) result[id]++;
        return result;
    }

    public bool IsKnown(string id) => _byId.ContainsKey(id);

    /// <summary>Ajustes nativos (C#) + os declarativos de Data/tweaks.json (9.1).</summary>
    public static TweakRegistry CreateDefault(TweakContext ctx)
    {
        var native = CreateNative(ctx);
        var declarative = DeclarativeTweakLoader.LoadDefault(ctx, native.Select(t => t.Id)).Cast<ITweak>().ToList();
        if (ServicesManual.CreateDefault(ctx) is { } services) declarative.Add(services);
        return new TweakRegistry(native.Concat(declarative));
    }

    private static List<ITweak> CreateNative(TweakContext ctx) =>
    [
        new PowerPlanTweak(ctx),
        new PowerPlanAdvancedTweak(ctx),
        new PowerPlanMaximumTweak(ctx),
        new GameModeTweak(ctx),
        new HagsTweak(ctx),
        new GameDvrTweak(ctx),
        new MouseAccelerationTweak(ctx),
        new KeyboardRepeatTweak(ctx),
        new AccessibilityKeysTweak(ctx),
        new TelemetryPolicyTweak(ctx),
        new TelemetryServicesTweak(ctx),
        new CompatibilityTasksTweak(ctx),
        new CopilotTweak(ctx),
        new RecallPolicyTweak(ctx),
        new RecallFeatureTweak(ctx),
        new AdsSuggestionsTweak(ctx),
        new StartAndLockScreenAdsTweak(ctx),
        new WidgetsOffTweak(ctx),
        new BingSearchOffTweak(ctx),
        new OneDriveStartupTweak(ctx),
        new ClassicContextMenuTweak(ctx)
    ];
}
