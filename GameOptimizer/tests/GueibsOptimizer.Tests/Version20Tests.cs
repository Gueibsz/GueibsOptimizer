using GueibsOptimizer.Services;
using GueibsOptimizer.ViewModels;
using Xunit;

namespace GueibsOptimizer.Tests;

/// <summary>Correções de bugs feitas na versão 2.0.</summary>
public class Version20Tests
{
    [Fact]
    public void TweakCardStartsCheckingAndDisabled()
    {
        var vm = new TweakItemViewModel(new FakeTweak("x", 5), new StubDialogs(), new GueibsOptimizer.Core.Logger(null));
        Assert.True(vm.IsChecking);
        Assert.False(vm.CanInteract);
        Assert.Equal("Verificando...", vm.StatusText);
        vm.Update(new TweakState(vm.Tweak, true, false));
        Assert.False(vm.IsChecking);
        Assert.True(vm.CanInteract);
        Assert.Equal("Não aplicado", vm.StatusText);
    }

    [Theory]
    [InlineData("Parsec Virtual Display Adapter", true)]
    [InlineData("Microsoft Basic Display Adapter", true)]
    [InlineData("NVIDIA GeForce RTX 2060 SUPER", false)]
    public void VirtualAdaptersAreMarked(string name, bool isVirtual)
    {
        var g = new GpuInfo(name, "1", "01/01/2026", null);
        Assert.Equal(isVirtual, g.IsVirtual);
        Assert.Equal(isVirtual, g.DisplayName.EndsWith("(adaptador virtual)"));
    }

    [Theory]
    [InlineData(IntegrityTool.Sfc, 0, "Windows Resource Protection found integrity violations.", IntegrityOutcome.ErrorsFound)]
    [InlineData(IntegrityTool.Sfc, 0, "Windows Resource Protection did not find any integrity violations.", IntegrityOutcome.Ok)]
    [InlineData(IntegrityTool.Dism, 0, "The component store is repairable.", IntegrityOutcome.ErrorsFound)]
    [InlineData(IntegrityTool.Dism, 0, "The component store is not repairable.", IntegrityOutcome.Failed)]
    [InlineData(IntegrityTool.Dism, 0, "No component store corruption detected.", IntegrityOutcome.Ok)]
    public void CheckOnlyModesAreInterpreted(IntegrityTool tool, int code, string output, IntegrityOutcome expected) =>
        Assert.Equal(expected, IntegrityCommands.Interpret(tool, code, output).Outcome);

    [Fact]
    public void FixCommandsUseNeutralDatePlaceholder() =>
        Assert.DoesNotContain(FixesService.Actions.SelectMany(a => a.Commands), c => c.Contains("<data>"));

    private sealed class StubDialogs : IDialogService
    {
        public bool Confirm(string title, string message) => true;
        public void Info(string title, string message) { }
        public void Error(string title, string message) { }
        public string? PickOpenFile(string filter) => null;
        public string? PickSaveFile(string filter, string defaultName) => null;
        public void CopyText(string text) { }
    }
}
