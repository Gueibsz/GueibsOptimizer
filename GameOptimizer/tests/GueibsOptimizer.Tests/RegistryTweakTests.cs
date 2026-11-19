using GueibsOptimizer.Core;
using Microsoft.Win32;

namespace GueibsOptimizer.Tests;

public class RegistryTweakTests : IDisposable
{
    private readonly TestRegistryKey _key = new();

    public void Dispose() => _key.Dispose();

    [Fact]
    public void SetOnMissingKeyThenRestoreRemovesKey()
    {
        _key.Write("", "Existente", 1, RegistryValueKind.DWord);
        var reg = _key.CreateRegistry();
        reg.Set("t1", RegHive.CurrentUser, _key.Combine(@"Novo\Sub"), "Valor", 1u);

        Assert.Equal(1, _key.ReadRaw(@"Novo\Sub", "Valor"));
        var entry = Assert.Single(_key.Store.ForTweak("t1"));
        Assert.False(entry.ValueExisted);
        Assert.Equal(_key.Combine("Novo"), entry.CreatedKeyPath);

        Assert.Equal(1, reg.Restore("t1"));
        Assert.False(_key.KeyExists("Novo"));
        Assert.True(_key.KeyExists(""));
        Assert.Empty(_key.Store.All);
    }

    [Fact]
    public void RestorePreservesTypeAfterReloadingJson()
    {
        _key.Write("", "Dword", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord);
        _key.Write("", "Texto", "original", RegistryValueKind.String);
        _key.Write("", "Expand", @"%SystemRoot%\x", RegistryValueKind.ExpandString);
        _key.Write("", "Multi", new[] { "a", "b" }, RegistryValueKind.MultiString);
        _key.Write("", "Bin", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
        _key.Write("", "Qword", 1234567890123L, RegistryValueKind.QWord);

        var reg = _key.CreateRegistry();
        reg.Set("t2", RegHive.CurrentUser, _key.Path, "Dword", 0u);
        reg.Set("t2", RegHive.CurrentUser, _key.Path, "Texto", "novo", RegistryValueKind.String);
        reg.Set("t2", RegHive.CurrentUser, _key.Path, "Expand", "x", RegistryValueKind.String);
        reg.Set("t2", RegHive.CurrentUser, _key.Path, "Multi", new[] { "z" }, RegistryValueKind.MultiString);
        reg.Set("t2", RegHive.CurrentUser, _key.Path, "Bin", new byte[] { 9 }, RegistryValueKind.Binary);
        reg.Set("t2", RegHive.CurrentUser, _key.Path, "Qword", 1u);
        Assert.Equal(RegistryValueKind.DWord, _key.Kind("", "Qword"));

        // Simula fechar e abrir o app: o backup é relido do JSON.
        _key.Store.Reload();
        var reloadedReg = new RegistryTweak(new BackupStore(_key.BackupFile), _key.Logger, false);
        Assert.Equal(6, reloadedReg.Restore("t2"));

        Assert.Equal(-1, _key.ReadRaw("", "Dword"));
        Assert.Equal(RegistryValueKind.DWord, _key.Kind("", "Dword"));
        Assert.Equal("original", _key.ReadRaw("", "Texto"));
        Assert.Equal(@"%SystemRoot%\x", _key.ReadRaw("", "Expand"));
        Assert.Equal(RegistryValueKind.ExpandString, _key.Kind("", "Expand"));
        Assert.Equal(new[] { "a", "b" }, (string[])_key.ReadRaw("", "Multi")!);
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])_key.ReadRaw("", "Bin")!);
        Assert.Equal(1234567890123L, _key.ReadRaw("", "Qword"));
        Assert.Equal(RegistryValueKind.QWord, _key.Kind("", "Qword"));
    }

    [Fact]
    public void ReapplyingDoesNotOverwriteOriginalBackup()
    {
        _key.Write("", "V", 5, RegistryValueKind.DWord);
        var reg = _key.CreateRegistry();
        reg.Set("t3", RegHive.CurrentUser, _key.Path, "V", 6u);
        reg.Set("t3", RegHive.CurrentUser, _key.Path, "V", 7u);

        Assert.Single(_key.Store.ForTweak("t3"));
        reg.Restore("t3");
        Assert.Equal(5, _key.ReadRaw("", "V"));
    }

    [Fact]
    public void RestoreAllUndoesInReverseOrder()
    {
        _key.Write("", "V", 1, RegistryValueKind.DWord);
        var reg = _key.CreateRegistry();
        reg.Set("first", RegHive.CurrentUser, _key.Path, "V", 2u);
        Thread.Sleep(15);
        reg.Set("second", RegHive.CurrentUser, _key.Path, "V", 3u);

        Assert.Equal(2, reg.RestoreAll());
        // "second" guardou 2; "first" guardou 1. Ordem inversa termina no valor original.
        Assert.Equal(1, _key.ReadRaw("", "V"));
    }

    [Fact]
    public void DryRunDoesNotWriteNorBackup()
    {
        var reg = _key.CreateRegistry(dryRun: true);
        var simulated = new List<LogEntry>();
        _key.Logger.EntryLogged += simulated.Add;

        reg.Set("t4", RegHive.CurrentUser, _key.Combine("X"), "V", 1u);

        Assert.False(_key.KeyExists("X"));
        Assert.Empty(_key.Store.All);
        Assert.Contains(simulated, e => e.Level == LogLevel.Simulation && e.Message.Contains(@"X\V"));
    }

    [Fact]
    public void DeleteValueBacksUpAndRestores()
    {
        _key.Write("", "Apagar", "valor", RegistryValueKind.String);
        var reg = _key.CreateRegistry();
        reg.DeleteValue("t5", RegHive.CurrentUser, _key.Path, "Apagar");
        Assert.Null(_key.ReadRaw("", "Apagar"));

        reg.Restore("t5");
        Assert.Equal("valor", _key.ReadRaw("", "Apagar"));
    }

    [Fact]
    public async Task RegistrySettingsTweakAppliesReadsRealStateAndReverts()
    {
        var ctx = _key.CreateContext();
        var tweak = new TestKeyTweak(ctx, _key.Combine("T"));

        Assert.False(tweak.IsApplied());
        await tweak.ApplyAsync();
        Assert.True(tweak.IsApplied());

        // Usuário muda fora do app: IsApplied reflete a realidade.
        _key.Write("T", "Flag", 0, RegistryValueKind.DWord);
        Assert.False(tweak.IsApplied());

        await tweak.RevertAsync();
        Assert.False(_key.KeyExists("T"));
        Assert.Empty(_key.Store.All);
    }

    [Fact]
    public async Task RevertWithoutBackupFallsBackToWindowsDefault()
    {
        _key.Write("T", "Flag", 1, RegistryValueKind.DWord);
        _key.Write("T", "Mode", "High", RegistryValueKind.String);
        var tweak = new TestKeyTweak(_key.CreateContext(), _key.Combine("T"));
        Assert.True(tweak.IsApplied());

        await tweak.RevertAsync();

        Assert.Equal(0, _key.ReadRaw("T", "Flag"));
        Assert.Null(_key.ReadRaw("T", "Mode"));
        Assert.Empty(_key.Store.All);
    }

    [Fact]
    public void ValueDataSerializesWithExplicitType()
    {
        var data = RegistryValueData.FromObject(unchecked((int)0x80000000), RegistryValueKind.DWord);
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        Assert.Contains("\"DWord\"", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<RegistryValueData>(json)!;
        Assert.True(data.SameValueAs(back));
        Assert.Equal(unchecked((int)0x80000000), back.ToRegistryObject());
    }
}
