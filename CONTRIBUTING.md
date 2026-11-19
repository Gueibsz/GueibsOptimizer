# Como contribuir

1. Abra uma issue (bug ou sugestão) antes de mudanças grandes.
2. Compile e teste antes do pull request:
   ```
   cd GameOptimizer
   dotnet build GueibsOptimizer.sln -c Release
   dotnet test GueibsOptimizer.sln -c Release
   ```
   O build trata warnings como erros.
3. **Nunca teste ajustes reais no seu PC principal.** Use os testes xUnit (registro numa subchave de teste em HKCU) e o modo `--dry-run`.
4. Testes que precisam de hardware real, administrador ou HKLM devem ter `[Trait("Category", "RequiresHardware")]`, `"RequiresAdmin"` ou `"RequiresHKLM"`; o CI os exclui.

## Regras do projeto

- Todo ajuste implementa `ITweak`, guarda o valor anterior antes de mudar e lê o estado real em `IsApplied()`.
- Nada de desligar Defender, Firewall ou UAC; nada de injeção em jogos; nada de telemetria própria; downloads só do winget ou de URL oficial.
- Comandos externos com `ProcessStartInfo.ArgumentList`; PowerShell só com `-EncodedCommand`.
- Textos da interface em português do Brasil, com tradução em `Data/i18n/*.en.json` e `*.es.json`.
- Código original. Não copie código de outros otimizadores; chaves de registro documentadas pela Microsoft podem ser usadas.
- Não inclua binários de terceiros, caminhos pessoais, segredos ou dados da sua máquina.

Ao contribuir, você concorda que sua contribuição fica sob a licença MIT do projeto.
