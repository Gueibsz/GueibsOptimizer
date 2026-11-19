namespace GueibsOptimizer.Models;

/// <summary>
/// Textos do botão "i" das telas (Windows, Otimizações, Overlay &amp; Testes, Hardware).
/// Ficam aqui, e não espalhados no XAML, para serem revisados juntos e testados (nenhum vazio).
/// </summary>
public static class UiHelp
{
    // --- Otimizações > ferramentas ---
    public const string RamClean =
        "Pede ao Windows para esvaziar a memória de trabalho de cada processo (EmptyWorkingSet). A memória disponível é medida antes e depois e o valor mostrado é o real, que pode ser zero. " +
        "Os programas recarregam o que precisarem, então o efeito é temporário. Útil antes de abrir um jogo pesado em PCs com pouca RAM; em PCs com 16 GB ou mais o ganho costuma ser pequeno. Não apaga nada e não precisa desfazer.";
    public const string GameBooster =
        "A cada 5 segundos procura jogos da sua lista entre os processos abertos e muda a prioridade deles para Alta (nunca Tempo Real, que pode travar o sistema). " +
        "Ao desligar, os jogos ainda abertos voltam à prioridade original. Ajuda quando há muitos programas em segundo plano; com o PC ocioso a diferença é pequena. Alguns anti-cheats bloqueiam a mudança; nesse caso o jogo é ignorado.";
    public const string GamesList =
        "Abre o arquivo games.json no Bloco de Notas. Coloque um nome de executável por linha, entre aspas e separado por vírgulas (ex.: \"cs2.exe\"). A lista é relida a cada verificação, sem reiniciar o app.";

    // --- Windows ---
    public const string CleanupAnalyze =
        "Só calcula: soma o tamanho dos arquivos de cada categoria marcada, sem apagar nada.";
    public const string CleanupRun =
        "Apaga o conteúdo das categorias marcadas depois de mostrar o total e pedir confirmação. Arquivos em uso são pulados. Atalhos e junções nunca são seguidos, e a pasta Prefetch nunca é tocada. " +
        "Não pode ser desfeito, mas só remove arquivos temporários e caches que o Windows e os jogos recriam. O cache de shaders pode deixar a primeira partida de cada jogo com algumas travadinhas.";
    public const string StartupList =
        "Lista o que inicia com o Windows: chaves Run do registro, pastas Inicializar e tarefas agendadas de logon (exceto as do próprio Windows). O \"assinado por\" vem da assinatura digital do executável.";
    public const string StartupToggle =
        "Desativar não apaga nada: o item é marcado como desativado no mesmo lugar que o Gerenciador de Tarefas usa (StartupApproved) ou a tarefa fica desabilitada. " +
        "Reativar volta exatamente ao estado anterior, e o histórico também aparece na aba Restauração. Menos programas na inicialização deixam o boot mais rápido e liberam memória.";
    public const string Repair =
        "Abre um console que roda DISM /Online /Cleanup-Image /RestoreHealth (repara a imagem do Windows usando o Windows Update) e depois sfc /scannow (verifica os arquivos de sistema). " +
        "Use quando o Windows apresenta erros estranhos. Leva de 5 a 30 minutos e não altera configurações. Não feche a janela no meio.";
    public const string Weekly =
        "Cria a tarefa agendada \"GueibsOptimizer\\RevisaoSemanal\" (domingo, 20h, com privilégios de administrador) que roda o app com --weekly: limpa temporários e cache de shaders, libera RAM e mostra uma notificação. " +
        "Desligar apaga a tarefa. Não aplica ajustes de registro.";
    public const string DefenderExclusion =
        "Adiciona SOMENTE a pasta do Gueibs Optimizer às exclusões do Microsoft Defender (Add-MpPreference -ExclusionPath). Evita que o antivírus atrase a leitura de sensores e as ações do app. " +
        "O Defender continua ligado para todo o resto do PC; raiz de disco e pastas do sistema são recusadas. Remover usa Remove-MpPreference. Só faça se confiar no app.";

    // --- Overlay & Testes ---
    public const string Overlay =
        "Mostra FPS, 1%/0.1% low, uso e temperatura de CPU e GPU numa janela transparente sempre por cima, que deixa o clique passar. Atalho: Ctrl+Shift+F10. " +
        "Não injeta nada no jogo, então é seguro com anti-cheat. Funciona com \"tela cheia sem bordas\" ou janela; em tela cheia exclusiva o Windows não mostra janelas por cima.";
    public const string OverlayEdit =
        "Enquanto ligado, o overlay recebe o mouse para você arrastá-lo. Ao desligar, a posição é salva e ele volta a deixar o clique passar.";
    public const string Fps =
        "Mede o programa em primeiro plano com o PresentMon da Intel, que lê eventos do Windows (ETW); nada é injetado. FPS médio e 1%/0.1% low são calculados nos últimos 10 segundos. " +
        "1% low é o FPS médio dos 1% quadros mais lentos: mostra as travadas que a média esconde.";
    public const string PresentMonDownload =
        "Baixa a versão de console do PresentMon da página oficial de releases da Intel no GitHub (GameTechDev). O arquivo só é usado se a assinatura digital for válida e da Intel Corporation; caso contrário, é apagado. Fica em %AppData%\\GueibsOptimizer\\tools.";
    public const string PresentMonPick =
        "Use se você já tem o PresentMon de console em outra pasta. O app de interface (PresentMonApplication) não serve, porque não aceita os argumentos de captura.";
    public const string PresentMonPage =
        "Abre no navegador a página oficial de releases do PresentMon, para baixar ou conferir manualmente.";
    public const string Ping =
        "Envia 20 pings (ICMP) para cada endereço e mostra média, mínimo, máximo, jitter (variação entre pings seguidos) e perda. Jitter e perda altos causam teleportes e atrasos em jogos online, mesmo com ping médio baixo.";
    public const string SpeedTest =
        "Mede download e upload por 8 segundos cada, com 4 conexões paralelas, usando os servidores públicos do teste da Cloudflare. O upload envia só bytes zerados. Consome dados: cerca do que sua conexão transfere em 16 segundos.";
    public const string Polling =
        "Abre uma janela que mede quantas leituras por segundo o mouse envia (125 a 8000 Hz), usando Raw Input e relógio de alta precisão. Mova o mouse rápido em círculos. Taxas maiores reduzem um pouco a latência do mouse.";
    public const string MonitorTest =
        "Abre testes de movimento (UFO), de rastro (ghosting) e mede a taxa de atualização real. Use para conferir se o monitor está na taxa máxima e comparar o borrão de movimento.";

    // --- Hardware ---
    public const string PawnIO =
        "O LibreHardwareMonitor precisa do driver PawnIO para ler temperatura, clock e potência da CPU. Ele é instalado pelo winget (pacote namazso.PawnIO), assinado digitalmente, o mesmo usado pelo Fan Control e pelo Libre Hardware Monitor. " +
        "Por ser um driver, só instale se confiar na fonte. Pode ser removido em Configurações > Aplicativos.";
    public const string CopySummary =
        "Copia o resumo do PC (CPU, GPU com VRAM, memória, placa-mãe, discos, Windows, monitores) para a área de transferência, para colar em fóruns ou pedir ajuda. Não inclui nome de usuário nem números de série.";
    public const string RefreshInfo =
        "Lê de novo as informações do PC pelo WMI e pelo registro. Útil depois de trocar uma peça ou atualizar o driver de vídeo.";
    public const string Checkup =
        "Verifica dual channel e XMP/EXPO (estimativas, porque o Windows não informa o modo real), o link PCIe da placa de vídeo, o plano de energia, o Modo de Jogo, o HAGS e a taxa de atualização dos monitores. Só lê, não altera nada.";
}
