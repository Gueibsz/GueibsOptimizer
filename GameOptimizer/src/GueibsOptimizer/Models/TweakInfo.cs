namespace GueibsOptimizer.Models;

public sealed record InfoSection(string Header, string Text);

/// <summary>
/// Explicação detalhada de um ajuste, exibida no botão "i". O campo de risco detalhado chama-se
/// <see cref="RiskDetails"/> porque <c>ITweak.Risk</c> já é o nível (Baixo/Médio/Alto).
/// </summary>
public sealed record TweakInfo(
    string LongDescription,
    string WhatItChanges,
    string Benefit,
    string RiskDetails,
    string HowToRevert,
    string Notes)
{
    public static readonly TweakInfo Empty = new("", "", "", "", "", "");

    public IReadOnlyList<InfoSection> Sections =>
        new[]
        {
            new InfoSection("O que faz", LongDescription),
            new InfoSection("O que é alterado", WhatItChanges),
            new InfoSection("Benefício", Benefit),
            new InfoSection("Risco", RiskDetails),
            new InfoSection("Como desfazer", HowToRevert),
            new InfoSection("Observações", Notes)
        }.Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
}
