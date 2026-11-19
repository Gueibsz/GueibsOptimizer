namespace GueibsOptimizer.Models;

public enum TweakRisk
{
    Low,
    Medium,
    High
}

public enum TweakCategory
{
    Power,
    Gaming,
    Gpu,
    Input,
    Windows,
    Privacy
}

/// <summary>
/// Um ajuste reversível. Boost, Perfis, Pontuação, Restauração e a tela de Otimizações
/// usam todos a mesma lista (<see cref="Services.TweakRegistry"/>).
/// </summary>
public interface ITweak
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    TweakCategory Category { get; }
    TweakRisk Risk { get; }

    /// <summary>Peso na pontuação (a soma de todos os ajustes é 100).</summary>
    int Points { get; }

    /// <summary>Aviso extra exibido na interface (ex.: "Requer reinício"). Null se não houver.</summary>
    string? Notice { get; }

    /// <summary>
    /// False para ajustes de preferência (ex.: menu de contexto clássico) que não devem ser aplicados pelo Boost.
    /// Esses ajustes têm 0 ponto e não entram na pontuação.
    /// </summary>
    bool InBoost => true;

    /// <summary>Explicação detalhada para o botão "i" (o que faz, o que altera, benefício, risco, como desfazer).</summary>
    TweakInfo Info => TweakInfoCatalog.For(Id);

    /// <summary>False quando o recurso não existe nesta versão do Windows ("não aplicável").</summary>
    bool IsApplicable();

    /// <summary>Lê o estado REAL do sistema (registro/powercfg/serviço), não uma flag salva.</summary>
    bool IsApplied();

    Task ApplyAsync();
    Task RevertAsync();
}

public static class TweakText
{
    public static string RiskText(TweakRisk risk) => risk switch
    {
        TweakRisk.Low => "Baixo",
        TweakRisk.Medium => "Médio",
        TweakRisk.High => "Alto",
        _ => risk.ToString()
    };

    public static string CategoryText(TweakCategory category) => category switch
    {
        TweakCategory.Power => "Energia",
        TweakCategory.Gaming => "Jogos",
        TweakCategory.Gpu => "GPU",
        TweakCategory.Input => "Periféricos e input lag",
        TweakCategory.Windows => "Windows",
        TweakCategory.Privacy => "Privacidade e IA",
        _ => category.ToString()
    };

    /// <summary>O Boost aplica apenas ajustes de risco Baixo ou Médio.</summary>
    public static bool IsRecommended(ITweak tweak) => tweak.InBoost && tweak.Risk is TweakRisk.Low or TweakRisk.Medium;
}
