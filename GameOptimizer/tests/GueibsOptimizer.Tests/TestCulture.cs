using System.Globalization;
using System.Runtime.CompilerServices;

namespace GueibsOptimizer.Tests;

/// <summary>
/// Os testes esperam a formatação do app em pt-BR (ex.: "1,5 GB"). Fixar a cultura aqui faz o
/// resultado ser o mesmo em qualquer máquina, inclusive no runner do CI (en-US).
/// </summary>
internal static class TestCulture
{
#pragma warning disable CA2255 // Inicializador de módulo de propósito: vale só para o assembly de testes.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void UsePortuguese()
    {
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        CultureInfo.DefaultThreadCurrentCulture = pt;
        CultureInfo.DefaultThreadCurrentUICulture = pt;
        CultureInfo.CurrentCulture = pt;
        CultureInfo.CurrentUICulture = pt;
    }
}
