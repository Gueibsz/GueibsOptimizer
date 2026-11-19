# Política de segurança

## Versões com suporte

Só a versão mais recente (a do branch `main` e o último release) recebe correções.

## Como reportar uma vulnerabilidade

**Não abra uma issue pública** para problemas de segurança. Use o recurso **"Report a vulnerability"** (aba *Security* > *Advisories*) deste repositório no GitHub, que cria um relato privado.

Inclua, se puder:

- versão do app e do Windows (ex.: Windows 11 23H2, build 22631);
- o que acontece e como reproduzir;
- o impacto (ex.: um arquivo `.gopro` que executa comandos, um ajuste que desliga o Defender/Firewall/UAC, um download fora das fontes oficiais).

Você deve receber uma resposta em até 14 dias. Depois da correção, o problema é descrito nas notas do release.

## O que é considerado vulnerabilidade

- Execução de comandos vindos de arquivos importados (`.gopro`, `catalog.json`, `tweaks.json`).
- Qualquer caminho que desligue o Defender, o Firewall ou o UAC, ou que adicione uma exclusão do Defender fora da pasta do app.
- Download de executáveis de fontes não oficiais ou sem conferência de assinatura.
- Perda do backup que impeça o desfazer.

Alertas de antivírus sobre o executável não assinado são, em geral, falsos positivos (veja o README); reporte se houver algo concreto.
