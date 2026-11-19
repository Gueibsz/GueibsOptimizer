# Avisos de terceiros

O código do projeto está sob a licença MIT (veja `LICENSE`). O executável publicado inclui os componentes abaixo, sem modificações, e cada um continua sob a própria licença. A mesma lista vai no pacote como `GameOptimizer/package/THIRD-PARTY-NOTICES.txt`.

## Pacotes NuGet incluídos no executável

| Componente | Versão | Licença | Fonte |
|---|---|---|---|
| .NET Runtime, WPF e Windows Forms | 8.x | MIT | https://github.com/dotnet/runtime |
| System.Management (e System.CodeDom) | 10.0.2 | MIT | https://github.com/dotnet/runtime |
| CommunityToolkit.Mvvm | 8.3.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| DiskInfoToolkit (dependência do LHM) | 1.1.2 | MPL-2.0 | https://github.com/Blacktempel/DiskInfoToolkit |
| RAMSPDToolkit-NDD (dependência do LHM) | 1.4.2 | MPL-2.0 | https://github.com/Blacktempel/RAMSPDToolkit |
| BlackSharp.Core (dependência do LHM) | 1.0.7 | MPL-2.0 | https://github.com/Blacktempel/BlackSharp |
| HidSharp (dependência do LHM) | 2.6.4 | Apache-2.0 | http://software.seekye.com/hidsharp |
| Mono.Posix.NETStandard (dependência do LHM) | 1.0.0 | MIT | https://github.com/mono/mono |

Textos das licenças: MIT (https://opensource.org/license/mit), MPL-2.0 (https://www.mozilla.org/MPL/2.0/), Apache-2.0 (https://www.apache.org/licenses/LICENSE-2.0).

## Pacotes usados só nos testes (não vão no executável)

| Componente | Licença |
|---|---|
| xunit, xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |
| coverlet.collector | MIT |

## Programas externos (não incluídos no repositório nem no executável)

O usuário baixa ou instala estes programas, sempre a pedido dele:

- **Intel PresentMon** (MIT), https://github.com/GameTechDev/PresentMon, para medir FPS. O app só aceita o arquivo da release oficial com assinatura válida da Intel.
- **PawnIO** (driver, freeware), https://pawnio.eu/, opcional, para temperatura e clock da CPU. Instalado pelo winget (`namazso.PawnIO`).
- **Inno Setup** (licença própria), https://jrsoftware.org/, só para compilar o instalador.
- **Programas do catálogo**: instalados pelo winget, cada um com a própria licença.

## WinUtil (Chris Titus Tech): inspiração

- Projeto: https://github.com/ChrisTitusTech/winutil, licença MIT.
- Uso: **somente como referência de quais ajustes existem** (Fase 9). Nenhum script nem trecho de código do WinUtil foi copiado; todo o código é original, em C#.
- Chaves de registro, nomes de serviços e valores são fatos do próprio Windows, conferidos na documentação da Microsoft.
- Se algum trecho substancial vier a ser copiado no futuro, o aviso completo de copyright MIT do WinUtil será incluído aqui.
