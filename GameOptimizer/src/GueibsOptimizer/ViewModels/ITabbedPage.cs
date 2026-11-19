namespace GueibsOptimizer.ViewModels;

/// <summary>Página com abas: o menu lateral pode abrir direto numa aba (ex.: Hardware > Check-up).</summary>
public interface ITabbedPage
{
    int SelectedTab { get; set; }
}
