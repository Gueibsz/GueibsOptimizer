namespace GueibsOptimizer.Models;

/// <summary>
/// Textos detalhados de cada ajuste. Honestos: quando o ganho é pequeno ou discutível, isso é dito.
/// Para ajustes de registro, a lista exata de valores é gerada a partir do próprio ajuste e somada a
/// <see cref="TweakInfo.WhatItChanges"/> (ver <c>RegistrySettingsTweak.Info</c>).
/// </summary>
public static class TweakInfoCatalog
{
    private static readonly Dictionary<string, TweakInfo> Infos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["power.plan"] = new(
            "Duplica o plano \"Desempenho Máximo\" do Windows (ou \"Alto Desempenho\", se o primeiro não existir), dá a ele o nome \"Gueibs Optimizer Performance\" e o ativa. Nesse plano a CPU não reduz a frequência nem estaciona núcleos, e PCIe/USB não entram em economia de energia.",
            "Comandos powercfg: -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61 (ou 8c5e7fda-…), -changename e -setactive. O GUID do plano que estava ativo é guardado para o desfazer.",
            "Menos variação de clock e menos travadinhas em CPUs que demoram a subir a frequência. Em desktops modernos o ganho médio de FPS costuma ser pequeno (0 a 3%); o benefício maior é a estabilidade.",
            "Mais consumo de energia e calor. Em notebooks a bateria dura bem menos. Não danifica o hardware.",
            "Botão Desfazer: reativa o plano anterior e apaga o plano criado. Também dá para trocar de plano em Painel de Controle > Opções de Energia.",
            "Se o PC já usa Alto Desempenho ou Desempenho Máximo, o ajuste já conta como aplicado e nada é criado."),

        ["gaming.game-mode"] = new(
            "Garante que o Modo de Jogo do Windows esteja ligado e aumenta a prioridade da tarefa \"Games\" do agendador multimídia (MMCSS), usada por jogos e programas que se registram nela.",
            "Valores de registro listados abaixo (GameBar e Multimedia\\SystemProfile\\Tasks\\Games).",
            "O Modo de Jogo evita instalações de drivers e avisos de reinício durante o jogo e prioriza o processo em primeiro plano. O efeito do MMCSS existe só nos jogos que usam a tarefa \"Games\"; em muitos títulos a diferença é pequena.",
            "Baixo. Em casos raros, com software de captura antigo, o Modo de Jogo já foi associado a travadas; se notar algo, desfaça.",
            "Botão Desfazer (volta os valores originais) ou Configurações > Jogos > Modo de Jogo.",
            "O Modo de Jogo já vem ligado por padrão no Windows 10 e 11; aqui ele só é confirmado."),

        ["gpu.hags"] = new(
            "Liga o Agendamento de GPU Acelerado por Hardware: a própria placa de vídeo passa a organizar a fila de trabalho, em vez do Windows.",
            "Valor de registro listado abaixo (GraphicsDrivers\\HwSchMode = 2).",
            "Pode reduzir um pouco a latência e o uso de CPU. É exigido pelo Frame Generation da NVIDIA (DLSS 3). Em muitos sistemas a diferença de FPS é nula.",
            "Médio: com alguns drivers e jogos causa travadas, tela preta momentânea ou problemas em captura e VR. Precisa reiniciar o PC.",
            "Botão Desfazer e reiniciar, ou Configurações > Sistema > Tela > Elementos gráficos > Alterar configurações padrão de elementos gráficos.",
            "Só funciona com GPU e driver compatíveis (WDDM 2.7+: NVIDIA GTX 1000+, AMD RX 5000+, Intel Arc/Xe). Se não houver suporte, o Windows ignora a chave."),

        ["gpu.game-dvr-off"] = new(
            "Desliga a gravação contínua em segundo plano da Xbox Game Bar (\"Gravar o que aconteceu\"), que fica gravando os últimos minutos de jogo.",
            "Valores de registro listados abaixo, incluindo a política AllowGameDVR = 0 para todos os usuários.",
            "Libera um pouco de GPU, codificador de vídeo e disco em segundo plano. A diferença é mais perceptível em PCs fracos.",
            "Baixo. Você perde a gravação retroativa da Game Bar. Gravar com OBS, ShadowPlay ou Radeon ReLive continua funcionando.",
            "Botão Desfazer ou Configurações > Jogos > Capturas.",
            ""),

        ["input.mouse-accel-off"] = new(
            "Desliga \"Aumentar precisão do ponteiro\", a aceleração do mouse do Windows. Com ela desligada, o mesmo movimento físico sempre move o cursor a mesma distância.",
            "Valores de registro listados abaixo (HKCU\\Control Panel\\Mouse).",
            "Movimento previsível, que ajuda a memória muscular. Muitos jogos já usam entrada bruta (raw input) e ignoram essa opção, então o efeito é maior no Windows e em jogos sem raw input.",
            "Baixo. O cursor pode parecer lento no desktop se você estava acostumado com a aceleração; ajuste o DPI ou a velocidade do ponteiro.",
            "Botão Desfazer ou Painel de Controle > Mouse > Opções do ponteiro > Aumentar precisão do ponteiro.",
            "Tem efeito completo depois de sair e entrar de novo na conta (logoff)."),

        ["input.keyboard-repeat"] = new(
            "Deixa o menor atraso antes de uma tecla segurada começar a repetir e a maior velocidade de repetição.",
            "Valores de registro listados abaixo (HKCU\\Control Panel\\Keyboard).",
            "Teclas seguradas repetem mais rápido no Windows, em editores e em chats. Para o tempo de resposta dentro dos jogos o ganho é praticamente nulo: os jogos leem a tecla pressionada, não a repetição.",
            "Baixo. Ao digitar, um caractere pode repetir sem querer se você segurar a tecla um pouco mais.",
            "Botão Desfazer ou Painel de Controle > Teclado.",
            "Tem efeito completo depois de logoff."),

        ["input.accessibility-shortcuts-off"] = new(
            "Desliga os ATALHOS que abrem Teclas de Aderência (Shift 5 vezes), Teclas de Filtragem (segurar Shift direito) e Teclas de Alternância (segurar Num Lock).",
            "Valores de registro listados abaixo (HKCU\\Control Panel\\Accessibility).",
            "Evita que uma janela de acessibilidade apareça no meio da partida e tire o jogo do foco.",
            "Baixo. Os recursos de acessibilidade continuam existindo; quem precisa deles pode ligá-los em Configurações > Acessibilidade > Teclado.",
            "Botão Desfazer ou Configurações > Acessibilidade > Teclado.",
            "Tem efeito completo depois de logoff."),

        ["privacy.telemetry-policy"] = new(
            "Define a política de dados de diagnóstico no nível mínimo e esconde os pedidos de feedback do Windows.",
            "Valores de registro listados abaixo (Policies\\Microsoft\\Windows\\DataCollection).",
            "Menos dados de diagnóstico enviados e nenhum pedido de avaliação. Para desempenho, a diferença é praticamente nula.",
            "Baixo. As Configurações passam a mostrar \"Algumas configurações são gerenciadas pela sua organização\". No Windows Home/Pro o mínimo real é \"Obrigatório\"; o nível \"Segurança\" (0) só vale no Enterprise/Education.",
            "Botão Desfazer (remove as políticas).",
            "O Programa Windows Insider exige dados de diagnóstico opcionais."),

        ["privacy.telemetry-services"] = new(
            "Desativa e para os serviços \"Experiências do Usuário Conectado e Telemetria\" (DiagTrack) e \"Serviço de Roteamento de Mensagens WAP\" (dmwappushservice).",
            "Valor Start = 4 (Desativado) de cada serviço, listado abaixo, e o comando \"sc stop\" para cada um.",
            "Menos atividade em segundo plano e menos envio de diagnósticos. O ganho de desempenho é pequeno.",
            "Médio: algumas ferramentas de diagnóstico e o Windows Insider deixam de funcionar. O dmwappushservice é usado no gerenciamento de dispositivos (MDM) de empresas: não use em PC corporativo.",
            "Botão Desfazer: restaura o tipo de inicialização original e inicia o DiagTrack de novo.",
            "Só serviços que existem no seu Windows são alterados."),

        ["privacy.compat-tasks"] = new(
            "Desativa tarefas agendadas que avaliam a compatibilidade de programas e enviam dados do Programa de Aperfeiçoamento da Experiência do Usuário.",
            "Tarefas do Agendador: Application Experience\\Microsoft Compatibility Appraiser, ProgramDataUpdater, PcaPatchDbTask; Customer Experience Improvement Program\\Consolidator e UsbCeip (as que existirem).",
            "Evita picos esporádicos de CPU e disco; o Compatibility Appraiser é conhecido por usar CPU por alguns minutos.",
            "Baixo. O Windows pode avaliar com menos dados a compatibilidade para grandes atualizações.",
            "Botão Desfazer reativa exatamente as tarefas que estavam ligadas.",
            ""),

        ["privacy.copilot-off"] = new(
            "Aplica a política que desliga o Copilot integrado ao Windows e esconde o botão dele na barra de tarefas.",
            "Valores de registro listados abaixo (políticas WindowsCopilot e ShowCopilotButton).",
            "Menos integração de IA e um botão a menos na barra. Não muda o desempenho em jogos.",
            "Baixo. Nas versões mais novas o Copilot virou um aplicativo comum e a política pode não ter efeito total; nesse caso, remova o app em Debloat.",
            "Botão Desfazer (remove as políticas).",
            "Só aparece como aplicável no Windows 11 22H2 ou mais novo."),

        ["privacy.recall-policy"] = new(
            "Aplica a política que impede o Recall de salvar capturas periódicas da sua tela.",
            "Valores de registro listados abaixo (políticas WindowsAI\\DisableAIDataAnalysis).",
            "Privacidade, e menos uso de disco e NPU nos PCs Copilot+ que têm o Recall.",
            "Baixo.",
            "Botão Desfazer (remove as políticas).",
            "Só aparece como aplicável no Windows 11 24H2 ou mais novo; na maioria dos PCs o Recall nem existe."),

        ["privacy.recall-feature"] = new(
            "Desativa o recurso opcional \"Recall\" do Windows, quando ele está instalado (alguns PCs Copilot+).",
            "Comando: Disable-WindowsOptionalFeature -Online -FeatureName Recall -NoRestart.",
            "Remove o componente por completo, além da política.",
            "Médio: altera um recurso do sistema e precisa reiniciar para concluir.",
            "Botão Desfazer (Enable-WindowsOptionalFeature) ou Configurações > Sistema > Recursos opcionais > Mais recursos do Windows.",
            "Só aparece como aplicável quando o recurso existe no seu Windows."),

        ["privacy.ads-suggestions-off"] = new(
            "Desliga dicas e sugestões do Windows, apps promovidos no Iniciar, instalação silenciosa de apps patrocinados, anúncios no Explorador de Arquivos e a ID de publicidade.",
            "Valores de registro listados abaixo (ContentDeliveryManager, Explorer\\Advanced e AdvertisingInfo).",
            "Menos anúncios e nenhum app instalado sem você pedir. Não muda o desempenho em jogos.",
            "Baixo.",
            "Botão Desfazer (volta os valores originais).",
            "Apps que já foram instalados automaticamente continuam lá; remova-os em Debloat se quiser."),

        ["privacy.start-lockscreen-ads-off"] = new(
            "Desliga as dicas e propagandas que aparecem sobre a tela de bloqueio (Windows Spotlight) e as recomendações de apps no Menu Iniciar.",
            "Valores de registro listados abaixo (ContentDeliveryManager e Explorer\\Advanced).",
            "Menos anúncios e menos conteúdo baixado em segundo plano. Não muda o desempenho em jogos.",
            "Baixo. A imagem dinâmica do Spotlight com textos promocionais deixa de aparecer.",
            "Botão Desfazer ou Configurações > Personalização > Tela de bloqueio / Iniciar.",
            ""),

        ["windows.widgets-off"] = new(
            "Desliga o painel de Widgets do Windows 11 (notícias, clima e anúncios ao passar o mouse no canto da barra de tarefas).",
            "Valor de registro listado abaixo (política Dsh\\AllowNewsAndInterests = 0).",
            "O painel deixa de carregar notícias em segundo plano (processos Widgets.exe/WebView). Ganho pequeno de memória e rede.",
            "Baixo. O painel e o botão somem; as Configurações mostram que a opção é gerenciada pela organização.",
            "Botão Desfazer (remove a política) e reinicie o Explorador.",
            "Para remover o app de Widgets por completo, use a lista de apps acima (categoria Opcional)."),

        ["windows.bing-search-off"] = new(
            "A pesquisa do Menu Iniciar deixa de enviar o que você digita para o Bing e de mostrar resultados da web.",
            "Valores de registro listados abaixo (política DisableSearchBoxSuggestions e Search\\BingSearchEnabled).",
            "Pesquisa mais rápida e só com itens do PC; o que você digita no Iniciar não vai para a internet.",
            "Baixo. Para pesquisar na web, use o navegador.",
            "Botão Desfazer e reinicie o Explorador.",
            ""),

        ["windows.onedrive-startup-off"] = new(
            "Impede o OneDrive de abrir sozinho quando você entra no Windows, sem desinstalá-lo.",
            "Marca a entrada \"OneDrive\" como desativada em StartupApproved\\Run (o mesmo que o Gerenciador de Tarefas faz); nada é apagado.",
            "Boot um pouco mais rápido e menos uso de disco e rede em segundo plano.",
            "Baixo, mas atenção: enquanto o OneDrive estiver fechado, seus arquivos não sincronizam. Por isso não entra no Boost nem na pontuação.",
            "Botão Desfazer, ou Gerenciador de Tarefas > Aplicativos de inicialização > OneDrive > Habilitar.",
            "Só aparece quando o OneDrive está configurado para iniciar com o Windows."),

        ["windows.classic-context-menu"] = new(
            "Faz o botão direito do mouse mostrar direto o menu completo, como no Windows 10, sem a etapa \"Mostrar mais opções\".",
            "Cria a chave listada abaixo com o valor padrão vazio, que desativa o menu novo do Windows 11.",
            "Só conveniência: menos cliques. Não muda o desempenho; por isso não entra no Boost nem na pontuação.",
            "Baixo. Alguns itens novos do menu do Windows 11 (como ícones de copiar/renomear no topo) deixam de aparecer.",
            "Botão Desfazer (apaga a chave criada) e reinicie o Explorador.",
            "Só aparece no Windows 11."),
    };

    public static TweakInfo For(string id) => Infos.GetValueOrDefault(id) ?? TweakInfo.Empty;

    public static IReadOnlyCollection<string> KnownIds => Infos.Keys;
}
