namespace GueibsOptimizer.Services;

/// <summary>Itens do histórico que não são ITweak (ex.: inicialização) sabem se desfazer por aqui.</summary>
public interface IUndoProvider
{
    bool CanUndo(string id);
    string DisplayName(string id);
    Task UndoAsync(string id);
}
