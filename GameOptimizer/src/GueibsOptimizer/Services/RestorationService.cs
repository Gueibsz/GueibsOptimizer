using GueibsOptimizer.Core;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services;

public sealed record HistoryItem(BackupEntry Entry, string TweakName, ITweak? Tweak)
{
    public DateTime LocalTime => Entry.TimestampUtc.ToLocalTime();
    public string Description => Entry.Description;
    public string PreviousValue => Entry.PreviousValueDisplay;
}

/// <summary>Histórico de alterações e desfazer (um ajuste ou tudo, do mais recente ao mais antigo).</summary>
public sealed class RestorationService(
    TweakRegistry registry,
    BackupStore store,
    RegistryTweak registryTweak,
    Logger logger,
    IEnumerable<IUndoProvider>? providers = null)
{
    private readonly IReadOnlyList<IUndoProvider> _providers = providers?.ToList() ?? [];

    public IReadOnlyList<HistoryItem> GetHistory() =>
        store.All
            .OrderByDescending(e => e.TimestampUtc)
            .Select(e =>
            {
                var tweak = registry.Find(e.TweakId);
                return new HistoryItem(e, tweak?.Name ?? NameFor(e.TweakId), tweak);
            })
            .ToList();

    private string NameFor(string id) =>
        _providers.FirstOrDefault(p => p.CanUndo(id))?.DisplayName(id) ?? id;

    /// <summary>Desfaz o ajuste dono da entrada. Retorna mensagem de erro, ou null se deu certo.</summary>
    public async Task<string?> UndoTweakAsync(string tweakId)
    {
        var tweak = registry.Find(tweakId);
        try
        {
            if (tweak is not null)
            {
                await tweak.RevertAsync().ConfigureAwait(false);
                logger.Info($"Desfeito: {tweak.Name}");
            }
            else if (_providers.FirstOrDefault(p => p.CanUndo(tweakId)) is { } provider)
            {
                await provider.UndoAsync(tweakId).ConfigureAwait(false);
            }
            else
            {
                // Ajuste removido de versões novas do app: ainda dá para restaurar o registro.
                registryTweak.Restore(tweakId);
                if (!registryTweak.DryRun) store.RemoveTweak(tweakId);
            }
            return null;
        }
        catch (Exception ex)
        {
            logger.Error($"Falha ao desfazer {tweak?.Name ?? tweakId}", ex);
            return ex.Message;
        }
    }

    public async Task<IReadOnlyList<string>> UndoAllAsync()
    {
        var ids = store.All
            .GroupBy(e => e.TweakId)
            .OrderByDescending(g => g.Max(e => e.TimestampUtc))
            .Select(g => g.Key)
            .ToList();

        var errors = new List<string>();
        foreach (var id in ids)
        {
            var error = await UndoTweakAsync(id).ConfigureAwait(false);
            if (error is not null) errors.Add($"{registry.Find(id)?.Name ?? NameFor(id)}: {error}");
        }
        return errors;
    }
}
