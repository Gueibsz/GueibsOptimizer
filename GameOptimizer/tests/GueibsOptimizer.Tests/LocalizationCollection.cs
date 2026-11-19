namespace GueibsOptimizer.Tests;

/// <summary>Testes que trocam o idioma global (LocalizationService.Instance) rodam sozinhos, sem paralelismo.</summary>
[CollectionDefinition("Localization", DisableParallelization = true)]
public sealed class LocalizationCollection;
