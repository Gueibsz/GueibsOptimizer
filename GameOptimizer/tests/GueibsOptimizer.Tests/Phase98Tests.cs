using GueibsOptimizer.ViewModels;
using Xunit;

namespace GueibsOptimizer.Tests;

public class Phase98Tests
{
    [Fact]
    public void ShortcutsAreOnlyNativeWindowsTools()
    {
        string[] expected = ["control.exe", "devmgmt.msc", "services.msc", "diskmgmt.msc", "powercfg.cpl", "ncpa.cpl", "mmsys.cpl",
            "appwiz.cpl", "sysdm.cpl", "eventvwr.msc", "main.cpl", "ms-settings:"];
        Assert.Equal(expected, ShortcutsViewModel.All.Select(s => s.Target));
        // Nenhum argumento ou caminho: só o nome da ferramenta nativa.
        Assert.All(ShortcutsViewModel.All, s => Assert.DoesNotContain(' ', s.Target));
        Assert.All(ShortcutsViewModel.All, s => Assert.DoesNotContain('\\', s.Target));
    }
}
