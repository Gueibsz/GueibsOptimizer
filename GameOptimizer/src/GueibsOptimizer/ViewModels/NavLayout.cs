namespace GueibsOptimizer.ViewModels;

/// <summary>
/// Ordem e grupos do menu lateral (Fase 8.2). "Sistema" não está na especificação: mantém Restauração,
/// Instalar Apps e Configurações acessíveis (a aba Restauração precisa estar sempre à mão).
/// </summary>
public static class NavLayout
{
    public const string Principal = "PRINCIPAL", Otimizar = "OTIMIZAR", Ferramentas = "FERRAMENTAS", Sistema = "SISTEMA";

    public static readonly (string Group, string[] Titles)[] Groups =
    [
        (Principal, ["Painel", "Manutenção", "Desempenho", "Meu PC", "Hardware Monitor", "Otimização Completa"]),
        (Otimizar, ["Formatação", "Debloat", "Windows", "Preferências", "Mouse", "Teclado", "GPU", "Perfis", "Jogos"]),
        (Ferramentas, ["Overlay", "Teste do mouse", "Teste do monitor", "Teste de velocidade", "Perda de pacotes", "DNS", "Recursos do Windows", "Correções", "Windows Update", "Atalhos", "Defender e IA", "Check-up"]),
        (Sistema, ["Restauração", "Instalar Apps", "Configurações"])
    ];

    /// <summary>Monta a lista na ordem acima a partir de (título → item sem grupo). Falha se faltar algum título.</summary>
    public static List<NavItem> Build(IReadOnlyDictionary<string, NavItem> byTitle) =>
        Groups.SelectMany(g => g.Titles.Select(t => byTitle.TryGetValue(t, out var item)
                ? item with { Group = g.Group }
                : throw new KeyNotFoundException($"Item de menu sem página: {t}")))
              .ToList();
}
