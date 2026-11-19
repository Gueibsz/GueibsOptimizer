using System.IO;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;
using GueibsOptimizer.Services.Tweaks;
using Microsoft.Win32;

namespace GueibsOptimizer.Tests;

/// <summary>Subchave temporária em HKCU\Software\GueibsOptimizer.Tests\{guid}, apagada no Dispose.</summary>
public sealed class TestRegistryKey : IDisposable
{
    public const string Root = @"Software\GueibsOptimizer.Tests";

    public TestRegistryKey()
    {
        Path = $@"{Root}\{Guid.NewGuid():N}";
        BackupFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"go-backup-{Guid.NewGuid():N}.json");
        Logger = new Logger(null);
        Store = new BackupStore(BackupFile);
    }

    public string Path { get; }
    public string BackupFile { get; }
    public Logger Logger { get; }
    public BackupStore Store { get; }

    public RegistryTweak CreateRegistry(bool dryRun = false) => new(Store, Logger, dryRun);

    public TweakContext CreateContext(bool dryRun = false) => new(Logger, Store, dryRun);

    public void Write(string subPath, string name, object value, RegistryValueKind kind)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Combine(subPath), writable: true);
        key.SetValue(name, value, kind);
    }

    public object? ReadRaw(string subPath, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Combine(subPath));
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public RegistryValueKind? Kind(string subPath, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Combine(subPath));
        if (key?.GetValue(name) is null) return null;
        return key.GetValueKind(name);
    }

    public bool KeyExists(string subPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Combine(subPath));
        return key is not null;
    }

    public string Combine(string subPath) => string.IsNullOrEmpty(subPath) ? Path : $@"{Path}\{subPath}";

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false); } catch { }
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Root);
            if (root is { SubKeyCount: 0, ValueCount: 0 })
                Registry.CurrentUser.DeleteSubKey(Root, throwOnMissingSubKey: false);
        }
        catch { }
        try { File.Delete(BackupFile); } catch { }
    }
}

/// <summary>Ajuste falso em memória para testar Boost/Pontuação sem tocar no sistema.</summary>
public sealed class FakeTweak(string id, int points, TweakRisk risk = TweakRisk.Low,
    bool applied = false, bool applicable = true, bool failOnApply = false) : ITweak
{
    public string Id => id;
    public string Name => $"Ajuste {id}";
    public string Description => "Ajuste de teste.";
    public TweakCategory Category => TweakCategory.Gaming;
    public TweakRisk Risk => risk;
    public int Points => points;
    public string? Notice => null;
    public bool Applied { get; private set; } = applied;
    public int ApplyCalls { get; private set; }

    public bool IsApplicable() => applicable;
    public bool IsApplied() => Applied;

    public Task ApplyAsync()
    {
        ApplyCalls++;
        if (failOnApply) throw new InvalidOperationException("falha simulada");
        Applied = true;
        return Task.CompletedTask;
    }

    public Task RevertAsync()
    {
        Applied = false;
        return Task.CompletedTask;
    }
}

public sealed class FakeRestorePoints(bool success) : IRestorePointService
{
    public int Calls { get; private set; }

    public Task<RestorePointResult> CreateAsync(string description, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(new RestorePointResult(success, success ? "ok" : "falhou"));
    }
}

/// <summary>Ajuste de registro real, mas apontando para a subchave de teste.</summary>
public sealed class TestKeyTweak(TweakContext ctx, string keyPath) : RegistrySettingsTweak(ctx)
{
    public override string Id => "test.registry";
    public override string Name => "Ajuste de teste";
    public override string Description => "Teste";
    public override TweakCategory Category => TweakCategory.Windows;
    public override TweakRisk Risk => TweakRisk.Low;
    public override int Points => 10;

    protected override IReadOnlyList<RegSetting> Settings { get; } =
    [
        RegSetting.DWord(RegHive.CurrentUser, keyPath, "Flag", 1, 0),
        RegSetting.String(RegHive.CurrentUser, keyPath, "Mode", "High", null)
    ];
}
