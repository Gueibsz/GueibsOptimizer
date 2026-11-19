using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GueibsOptimizer.Core;

namespace GueibsOptimizer.ViewModels;

/// <summary>Atalho para uma ferramenta nativa do Windows (lista fechada; nada é alterado).</summary>
public sealed record ClassicShortcut(string Title, string Target, string Description, string Glyph);

/// <summary>9.8: abre painéis clássicos do Windows. Só abre; não muda configuração nenhuma.</summary>
public sealed partial class ShortcutsViewModel(Logger logger) : ObservableObject
{
    public static readonly IReadOnlyList<ClassicShortcut> All =
    [
        new("Painel de Controle", "control.exe", "O Painel de Controle clássico.", ""),
        new("Gerenciador de Dispositivos", "devmgmt.msc", "Drivers e dispositivos do PC.", ""),
        new("Serviços", "services.msc", "Serviços do Windows e seus tipos de inicialização.", ""),
        new("Gerenciamento de Disco", "diskmgmt.msc", "Partições e volumes dos discos.", ""),
        new("Opções de Energia", "powercfg.cpl", "Planos de energia.", ""),
        new("Conexões de Rede", "ncpa.cpl", "Adaptadores de rede.", ""),
        new("Som", "mmsys.cpl", "Dispositivos de reprodução e gravação.", ""),
        new("Programas e Recursos", "appwiz.cpl", "Desinstalar programas clássicos.", ""),
        new("Propriedades do Sistema", "sysdm.cpl", "Nome do PC, memória virtual, efeitos visuais e Proteção do Sistema.", ""),
        new("Visualizador de Eventos", "eventvwr.msc", "Logs de erros e avisos do Windows.", ""),
        new("Mouse", "main.cpl", "Propriedades clássicas do mouse.", ""),
        new("Configurações do Windows", "ms-settings:", "O app Configurações.", "")
    ];

    public IReadOnlyList<ClassicShortcut> Items => All;

    [ObservableProperty] private string _status = string.Empty;

    [RelayCommand]
    private void Open(ClassicShortcut? s)
    {
        if (s is null || !All.Contains(s)) return;
        try
        {
            Process.Start(new ProcessStartInfo(s.Target) { UseShellExecute = true })?.Dispose();
            logger.Info($"Atalho aberto: {s.Title} ({s.Target}).");
            Status = string.Empty;
        }
        catch (Exception ex)
        {
            logger.Error($"Não foi possível abrir {s.Target}", ex);
            Status = $"Não foi possível abrir {s.Title}.";
        }
    }
}
