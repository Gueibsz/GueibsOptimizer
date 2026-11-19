using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using Microsoft.Win32;

namespace GueibsOptimizer.Services.Tweaks;

/// <summary>Um valor de registro desejado e, opcionalmente, o padrão do Windows (null = valor ausente).</summary>
public sealed record RegSetting(RegHive Hive, string KeyPath, string ValueName, RegistryValueData Desired, RegistryValueData? WindowsDefault)
{
    public static RegSetting DWord(RegHive hive, string keyPath, string name, uint desired, uint? windowsDefault) =>
        new(hive, keyPath, name, RegistryValueData.DWord(desired),
            windowsDefault is { } d ? RegistryValueData.DWord(d) : null);

    public static RegSetting String(RegHive hive, string keyPath, string name, string desired, string? windowsDefault) =>
        new(hive, keyPath, name, RegistryValueData.String(desired),
            windowsDefault is null ? null : RegistryValueData.String(windowsDefault));

    public static RegSetting Typed(RegHive hive, string keyPath, string name, object desired, RegistryValueKind kind, object? windowsDefault) =>
        new(hive, keyPath, name, RegistryValueData.FromObject(desired, kind),
            windowsDefault is null ? null : RegistryValueData.FromObject(windowsDefault, kind));
}

/// <summary>
/// Base para ajustes que são só um conjunto de valores de registro.
/// Reverter usa o backup; sem backup (aplicado fora do app), volta ao padrão do Windows.
/// </summary>
public abstract class RegistrySettingsTweak : ITweak
{
    protected RegistrySettingsTweak(TweakContext context) => Context = context;

    protected TweakContext Context { get; }

    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract TweakCategory Category { get; }
    public abstract TweakRisk Risk { get; }
    public abstract int Points { get; }
    public virtual string? Notice => null;
    public virtual bool InBoost => true;

    protected abstract IReadOnlyList<RegSetting> Settings { get; }

    /// <summary>Valores que este ajuste lê e grava (usado pelo --selftest para ler o valor bruto).</summary>
    public IReadOnlyList<RegSetting> RegistryLocations => Settings;

    /// <summary>Texto do catálogo + a lista exata de valores de registro que este ajuste grava.</summary>
    public virtual TweakInfo Info
    {
        get
        {
            var info = TweakInfoCatalog.For(Id);
            var values = string.Join("\n", Settings.Select(s =>
                $"• {RegistryTweak.HiveName(s.Hive)}\\{s.KeyPath}\\{s.ValueName} = {s.Desired.Display()}"));
            if (values.Length == 0) return info;
            var what = string.IsNullOrWhiteSpace(info.WhatItChanges) ? values : $"{info.WhatItChanges}\n{values}";
            return info with { WhatItChanges = what };
        }
    }

    public virtual bool IsApplicable() => true;

    public virtual bool IsApplied() => Settings.All(IsSatisfied);

    public virtual Task ApplyAsync()
    {
        // Só escreve o que difere: evita backups inúteis de valores que já estão corretos.
        foreach (var s in Settings.Where(s => !IsSatisfied(s)))
            Context.Registry.Set(Id, s.Hive, s.KeyPath, s.ValueName, s.Desired);
        return Task.CompletedTask;
    }

    /// <summary>Valor ausente conta como o padrão do Windows (ex.: Modo de Jogo vem ligado por padrão).</summary>
    private bool IsSatisfied(RegSetting s)
    {
        var current = Context.Registry.Read(s.Hive, s.KeyPath, s.ValueName);
        return current is null
            ? s.WindowsDefault is not null && s.Desired.SameValueAs(s.WindowsDefault)
            : s.Desired.SameValueAs(current);
    }

    public virtual Task RevertAsync()
    {
        if (Context.Registry.Restore(Id) > 0) return Task.CompletedTask;

        Context.Logger.Info($"{Name}: sem backup registrado; voltando ao padrão do Windows.");
        foreach (var s in Settings)
        {
            if (s.WindowsDefault is null)
                Context.Registry.DeleteValue(Id, s.Hive, s.KeyPath, s.ValueName);
            else
                Context.Registry.Set(Id, s.Hive, s.KeyPath, s.ValueName, s.WindowsDefault);
        }
        // O "padrão" vira o novo estado; não deve ficar como algo a desfazer.
        if (!Context.DryRun) Context.Backup.RemoveTweak(Id);
        return Task.CompletedTask;
    }
}
