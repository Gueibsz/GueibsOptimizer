using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services;
using GueibsOptimizer.Services.Games;
using GueibsOptimizer.ViewModels;
using GueibsOptimizer.Views;

namespace GueibsOptimizer;

public partial class App : Application
{
    private const string MutexName = @"Local\GueibsOptimizer.SingleInstance";
    private Mutex? _singleInstance;
    private Logger? _logger;
    private GameBoosterService? _booster;
    private HardwareMonitorService? _hardware;
    private FpsService? _fps;
    private OverlayController? _overlay;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = CommandLineOptions.Parse(e.Args);
        // Versão antiga (GameOptimizer): leva backup, configurações e perfis para a pasta nova antes de abrir o log.
        // Nunca em simulação, capturas, autoteste ou relatório (não mexe nos dados de uma versão antiga ainda em uso).
        var migration = options.DryRun || options.ScreenshotsDir is not null || options.SelfTest || options.Report
            ? DataMigrationOutcome.NothingToDo
            : LegacyMigration.MigrateDataFolder(LegacyMigration.LegacyDataDirectory, AppPaths.DataDirectory);
        _logger = new Logger(AppPaths.LogDirectory);
        if (migration != DataMigrationOutcome.NothingToDo)
            _logger.Info($"Pasta de dados da versão antiga ({LegacyMigration.LegacyDataDirectory}): {migration}.");
        RegisterGlobalExceptionHandlers();

        if (options.SelfTest)
        {
            RunSelfTest(options);
            return;
        }

        if (options.Report)
        {
            RunHeadlessReport();
            return;
        }

        if (options.Weekly)
        {
            RunWeeklyReview(options.DryRun);
            return;
        }

        if (options.Setup != SetupCommand.None)
        {
            RunSetupCommand(options);
            return;
        }

        // Capturas em simulação: sem elevação nem mutex (pode rodar com o app normal aberto).
        if (options.ScreenshotsDir is not null)
        {
            StartUi(options);
            return;
        }

        // Elevação antes do mutex: a nova instância (elevada) não pode encontrar o mutex preso por esta.
        switch (Elevation.Decide(AdminCheck.IsAdministrator(), options.DryRun, options.Elevated))
        {
            case StartupDecision.Relaunch:
                var error = Elevation.RelaunchElevated(e.Args);
                if (error is null)
                {
                    _logger.Info("Sem privilégios de administrador: reiniciando elevado.");
                    Shutdown();
                    return;
                }
                ShowAdminRequired(error);
                return;
            case StartupDecision.ShowError:
                ShowAdminRequired("O Windows não concedeu privilégios de administrador.");
                return;
        }

        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("O Gueibs Optimizer já está aberto.", "Gueibs Optimizer", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        StartUi(options);
    }

    private void ShowAdminRequired(string reason)
    {
        _logger?.Warn($"Sem privilégios de administrador: {reason}");
        MessageBox.Show(
            $"{reason}\n\nO Gueibs Optimizer precisa ser executado como administrador para alterar configurações do Windows.\n\n" +
            "Clique com o botão direito no executável e escolha \"Executar como administrador\".\n" +
            "Para apenas explorar o app sem alterar nada, use o argumento --dry-run.",
            "Permissão de administrador necessária", MessageBoxButton.OK, MessageBoxImage.Warning);
        Shutdown();
    }

    private void StartUi(CommandLineOptions options)
    {
        var logger = _logger!;
        logger.Info(options.DryRun ? "Gueibs Optimizer iniciado em MODO SIMULAÇÃO." : "Gueibs Optimizer iniciado.");

        var backup = new BackupStore(AppPaths.BackupFile);
        var ctx = new TweakContext(logger, backup, options.DryRun);
        var registry = TweakRegistry.CreateDefault(ctx);
        var dialogs = new DialogService();
        var score = new ScoreService(logger);
        var settingsService = new SettingsService();
        var settings = settingsService.Current;
        var restorePointsRaw = new RestorePointService(ctx.Shell, logger);
        // Opcional (Configurações); o ponto manual do Painel e a revisão da Manutenção usam o serviço direto.
        var restorePoints = new OptionalRestorePointService(restorePointsRaw, () => settings.CreateRestorePoints, logger);
        var boost = new BoostService(registry, score, restorePoints, logger, options.DryRun);
        var startup = new StartupService(ctx);
        var defender = new DefenderService(ctx);
        var debloat = new DebloatService(ctx);
        // Tema antes de criar qualquer janela, para não piscar com as cores padrão.
        var theme = new ThemeService();
        theme.Apply(settings.Theme, settings.AccentColor);
        // Idioma antes de criar as telas (os textos {l:T} já nascem traduzidos).
        LocalizationService.Instance.SetLanguage(settings.Language);
        var icons = new IconService();
        icons.Apply(settings.AccentColor);
        var memory = new MemoryCleanerService(logger, options.DryRun);
        _booster = new GameBoosterService(logger, options.DryRun);
        if (settings.GameBoosterEnabled) _booster.Start();
        var gameProfiles = new GameProfileService(ctx, _booster);
        var dnsService = new DnsService(ctx);
        var extraActions = new ExtraActionsService(ctx);
        var featureService = new WindowsFeatureService(ctx);
        var fixesVm = new FixesViewModel(new FixesService(ctx), restorePoints, dialogs, logger);
        var restoration = new RestorationService(registry, backup, ctx.Registry, logger, [startup, defender, debloat, gameProfiles, dnsService, featureService, extraActions]);
        var cleanup = new CleanupService(ctx.Shell, logger, options.DryRun);

        _hardware = new HardwareMonitorService(logger);
        var systemInfo = new SystemInfoService(logger);
        var detection = new HardwareDetectionService(systemInfo, _hardware, logger);
        var home = new HomeViewModel(registry, score, boost, dialogs, logger, options.DryRun, detection);
        var tweaks = new TweaksViewModel(registry, score, dialogs, logger, memory, _booster, settings);
        var windows = new WindowsViewModel(cleanup, startup, new RepairService(logger, options.DryRun),
            new WeeklyReviewService(ctx.Shell, logger), defender, dialogs, logger);
        windows.ConfigureMaintenance(new IntegrityToolRunner(logger, options.DryRun), restorePointsRaw, settings, ctx.Shell);
        var restore = new RestoreViewModel(restoration, backup, dialogs, logger);
        var hardware = new HardwareViewModel(_hardware, systemInfo,
            new CheckupService(registry, systemInfo, ctx.Shell, logger), new WingetService(ctx.Shell, logger), dialogs, logger);
        _fps = new FpsService(logger, settings);
        var overlayTests = new OverlayTestsViewModel(_fps, new PresentMonDownloader(logger, settings, options.DryRun),
            new NetworkTestService(), settings, dialogs, logger,
            () => new PollingTestWindow { Owner = MainWindow }.Show(),
            () => new MonitorTestWindow { Owner = MainWindow }.Show());
        var profileService = new ProfileService(registry, logger);
        var profiles = new ProfilesViewModel(profileService, registry, score, restorePoints, dialogs, logger);
        var wizard = new WizardViewModel(profileService, registry, score, restorePoints, ctx.Shell, dialogs, logger) { Debloat = new DebloatService(ctx) };
        var games = new GamesViewModel(new GameLibraryService(logger), gameProfiles, new GamePresetService(logger, options.DryRun),
            detection, dialogs, logger, options.DryRun);
        var installApps = new InstallAppsViewModel(new WingetService(ctx.Shell, logger), dialogs, logger, wizard);

        logger.EntryLogged += entry => Dispatcher.BeginInvoke(() => home.AddActivity(entry));

        var settingsVm = new SettingsViewModel(settingsService, theme, icons);
        var debloatVm = new DebloatViewModel(debloat, restorePoints, registry, score, dialogs, logger) { ExtraActions = extraActions };
        CategoryTweaksViewModel Category(string title, string subtitle, string info, Func<Models.ITweak, bool> filter) =>
            new(title, subtitle, info, registry, score, dialogs, logger, filter);
        void OpenPolling() => new PollingTestWindow { Owner = MainWindow }.Show();
        void OpenMonitor() => new MonitorTestWindow { Owner = MainWindow }.Show();
        var pages = new Dictionary<string, NavItem>
        {
            ["Painel"] = new("Painel", "", home),
            ["Manutenção"] = new("Manutenção", "", windows),
            ["Desempenho"] = new("Desempenho", "", Category("Desempenho", "Plano de energia e Modo de Jogo: os ajustes que mais pesam na pontuação.",
                "Lista só os ajustes das categorias Energia e Jogos, os mesmos usados pelo Boost e pela pontuação. O resultado depende do hardware.", CategoryTweaksViewModel.IsPerformance)),
            ["Meu PC"] = new("Meu PC", "", hardware, Tab: 1),
            ["Hardware Monitor"] = new("Hardware Monitor", "", hardware, Tab: 0),
            ["Otimização Completa"] = new("Otimização Completa", "", tweaks),
            ["Formatação"] = new("Formatação", "", installApps, Tab: 2),
            ["Debloat"] = new("Debloat", "", debloatVm),
            ["Windows"] = new("Windows", "", Category("Windows", "Ajustes do próprio Windows (Widgets, pesquisa Bing, menu de contexto, OneDrive).",
                "Lista só os ajustes da categoria Windows. Cada um tem descrição, risco e Desfazer.", CategoryTweaksViewModel.IsWindows)),
            ["Preferências"] = new("Preferências", "", new PreferencesViewModel(Category("Preferências", "Explorador, barra de tarefas, encaixe de janelas e tema.", "Preferências pessoais do Windows; não mudam o desempenho.", PreferencesViewModel.IsPreference), ctx.Shell, dialogs, logger)),
            ["Mouse"] = new("Mouse", "", Category("Mouse", "Precisão do ponteiro: aceleração do mouse desligada para mira consistente.",
                "Lista só os ajustes de mouse. Para medir a taxa de polling, use Ferramentas > Teste do mouse.", CategoryTweaksViewModel.IsMouse)),
            ["Teclado"] = new("Teclado", "", Category("Teclado", "Repetição de teclas e atalhos de acessibilidade que atrapalham no jogo.",
                "Lista só os ajustes de teclado. Algumas mudanças só valem depois de sair e entrar de novo no Windows.", CategoryTweaksViewModel.IsKeyboard)),
            ["GPU"] = new("GPU", "", Category("GPU", "Agendamento de GPU acelerado por hardware (HAGS) e gravação em segundo plano (Game DVR).",
                "Lista só os ajustes de placa de vídeo. O HAGS exige reiniciar o PC.", CategoryTweaksViewModel.IsGpu)),
            ["Perfis"] = new("Perfis", "", profiles),
            ["Jogos"] = new("Jogos", "", games),
            ["Overlay"] = new("Overlay", "", overlayTests),
            ["Teste do mouse"] = new("Teste do mouse", "", overlayTests, OnOpen: OpenPolling),
            ["Teste do monitor"] = new("Teste do monitor", "", overlayTests, OnOpen: OpenMonitor),
            ["Teste de velocidade"] = new("Teste de velocidade", "", overlayTests),
            ["Perda de pacotes"] = new("Perda de pacotes", "", overlayTests),
            ["DNS"] = new("DNS", "\uE968", new DnsViewModel(dnsService, Category("DNS e rede", "IPv6.", "Ajustes de IPv6.", DnsViewModel.IsNetwork), dialogs, logger)),
            ["Recursos do Windows"] = new("Recursos do Windows", "\uE74C", new FeaturesViewModel(featureService, dialogs, logger)),
            ["Correções"] = new("Correções", "\uE90F", fixesVm),
            ["Windows Update"] = new("Windows Update", "\uE895", new WindowsUpdateViewModel(new WindowsUpdateModeService(ctx), dialogs, logger)),
            ["Atalhos"] = new("Atalhos", "\uE71D", new ShortcutsViewModel(logger)),
            ["Defender e IA"] = new("Defender e IA", "", new DefenderAiViewModel(defender, Category("Defender e IA", "Privacidade e IA: telemetria, Copilot, Recall e sugestões.",
                "Lista os ajustes de privacidade e IA (os mesmos do Boost). O Microsoft Defender nunca é desligado pelo app.", CategoryTweaksViewModel.IsPrivacy), dialogs, logger)),
            ["Check-up"] = new("Check-up", "", hardware, Tab: 2),
            ["Restauração"] = new("Restauração", "", restore),
            ["Instalar Apps"] = new("Instalar Apps", "", installApps, Tab: 0),
            ["Configurações"] = new("Configurações", "", settingsVm)
        };
        var nav = NavLayout.Build(pages);
        var mainVm = new MainViewModel(nav, options.DryRun, settings.NavCollapsed, v => { settings.NavCollapsed = v; settings.Save(); });
        void GoTitle(string title) { if (nav.FirstOrDefault(n => n.Title == title) is { } item) mainVm.SelectNavCommand.Execute(item); }
        home.ConfigureDashboard(new DashboardDeps(settings, restorePointsRaw, RestorePointReader.ReadAsync, cleanup, memory,
            OpenCleanup: () => GoTitle("Manutenção"), OpenCheckup: () => GoTitle("Check-up"), OpenWizard: () => GoTitle("Formatação")));
        home.ConfigurePerformance(new SystemSnapshotService(logger, () => startup.List().Count(i => i.Enabled)));
        fixesVm.OpenMaintenanceAction = () => GoTitle("Manutenção");
        settingsVm.DisplayNameChanged += home.UpdateGreeting;
        settingsVm.RestorePointOptionChanged += home.RefreshRestoreOption;
        home.RestorePointOptionChanged += settingsVm.RefreshRestoreOption;

        var window = new MainWindow { DataContext = mainVm };
        if (options.DryRun) window.Title = "Gueibs Optimizer — MODO SIMULAÇÃO";
        window.Icon = icons.WindowIcon(icons.CurrentHex);
        MainWindow = window;
        // Aberto pela tarefa de logon: começa minimizado, sem roubar o foco da área de trabalho.
        if (options.AutoStart) window.WindowState = WindowState.Minimized;
        window.Show();
        _overlay = new OverlayController(new OverlayViewModel(_fps, _hardware, settings), window);
        overlayTests.Overlay = _overlay;
        _tray = new TrayIcon(window, () =>
        {
            ((MainViewModel)window.DataContext).SelectNavCommand.Execute(nav[0]);
            if (home.BoostCommand.CanExecute(null)) home.BoostCommand.Execute(null);
        }, options.DryRun, icons);

        _ = home.RefreshAsync();
        _ = tweaks.RefreshAsync();
        _ = home.LoadHardwareAsync();

        if (options.ScreenshotsDir is { } shotsDir)
        {
            _ = ScreenshotRunner.RunAsync(window, mainVm, theme, settings, shotsDir, logger, () => Shutdown(0));
            return;
        }

        if (!options.DryRun && Environment.ProcessPath is { } exePath)
            _ = Task.Run(() => LegacyMigration.MigrateSystemAsync(ctx.Shell, logger, exePath));
        if (!options.DryRun && !settings.DefenderOffered)
            Dispatcher.BeginInvoke(() => _ = OfferDefenderExclusionAsync(defender, dialogs, settings), DispatcherPriority.ApplicationIdle);
        if (WizardState.Load() is { Finished: false } pending)
            logger.Info($"Há uma formatação guiada em andamento ({pending.StepTitle}). Retome em Instalar Apps > Formatação guiada.");
    }

    /// <summary>Primeiro uso: oferece (com explicação) excluir só a pasta do app no Defender. Pergunta uma única vez.</summary>
    private static async Task OfferDefenderExclusionAsync(DefenderService defender, IDialogService dialogs, AppSettings settings)
    {
        settings.DefenderOffered = true;
        settings.Save();
        var status = await defender.GetStatusAsync();
        if (!status.Available || status.ExclusionPresent) return;
        if (status.ManagedByPolicyOrThirdParty)
        {
            dialogs.Info("Microsoft Defender", status.Message);
            return;
        }
        if (!dialogs.Confirm("Exclusão do Defender (opcional)",
                $"Deseja adicionar SOMENTE a pasta do Gueibs Optimizer às exclusões do Microsoft Defender?\n\n{DefenderService.AppFolder}\n\n" +
                "Isso evita que a leitura de sensores e as ações do app sejam atrasadas ou bloqueadas. O Defender continua protegendo todo o resto do PC. " +
                "Você pode remover depois em Windows > Exclusão no Microsoft Defender ou na aba Restauração."))
            return;
        var error = await defender.AddExclusionAsync();
        if (error is not null) dialogs.Error("Microsoft Defender", error);
    }

    /// <summary>--selftest: bateria de testes sem janela. Código de saída 0 = nenhuma falha; 1 = falhas; 2 = não executou.</summary>
    private async void RunSelfTest(CommandLineOptions options)
    {
        int exit;
        string summary;
        try
        {
            var runner = new Services.SelfTest.SelfTestRunner(_logger!, options);
            (exit, summary) = await Task.Run(runner.RunAsync);
        }
        catch (Exception ex)
        {
            _logger?.Error("Falha no autoteste", ex);
            (exit, summary) = (2, $"Erro no autoteste: {ex.Message}");
        }
        if (AttachConsole(AttachParentProcess))
        {
            try
            {
                using var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                stdout.Write(Environment.NewLine + summary + Environment.NewLine);
            }
            catch (IOException) { }
        }
        Shutdown(exit);
    }

    /// <summary>--dry-run --report: escreve o relatório no console (se houver) e em arquivo, sem abrir janela.</summary>
    private async void RunHeadlessReport()
    {
        var logger = new Logger(null);
        string text;
        try
        {
            var ctx = new TweakContext(logger, new BackupStore(AppPaths.BackupFile), dryRun: true);
            var reporter = new DryRunReporter(TweakRegistry.CreateDefault(ctx), ctx, new DebloatService(ctx));
            var gameProfiles = new GameProfileService(ctx, new GameBoosterService(logger, dryRun: true));
            reporter.ExtraSections.Add(w => GamesReport.WriteAsync(w, new GameLibraryService(logger), gameProfiles));
            reporter.ExtraSections.Add(FixesService.WriteReportAsync);
            reporter.ExtraSections.Add(w => MaintenanceReport.WriteAsync(w, AppSettings.Load().LastIntegrityReview));
            reporter.ExtraSections.Add(w => PerformanceLevelsReport.WriteAsync(w, TweakRegistry.CreateDefault(ctx), new SystemSnapshotService(logger, () => new StartupService(ctx).List().Count(i => i.Enabled))));
            text = await reporter.BuildReportAsync();
        }
        catch (Exception ex)
        {
            text = $"Erro ao gerar o relatório: {ex.Message}";
        }

        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            var path = Path.Combine(AppPaths.LogDirectory, $"simulacao-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            await File.WriteAllTextAsync(path, text);
            text += $"{Environment.NewLine}Relatório salvo em: {path}{Environment.NewLine}";
        }
        catch (IOException)
        {
        }

        if (AttachConsole(AttachParentProcess))
        {
            using var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            stdout.Write(Environment.NewLine + text);
        }
        Shutdown(0);
    }

    /// <summary>
    /// Ações do instalador/desinstalador, sem janela. O código de saída diz se deu certo (0) ou não (1);
    /// detalhes ficam no log. O instalador roda elevado, então a tarefa é criada para o usuário que instalou.
    /// </summary>
    private async void RunSetupCommand(CommandLineOptions options)
    {
        var logger = _logger!;
        int exit = 0;
        try
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppPaths.AppDirectory, "GueibsOptimizer.exe");
            var autoStart = new AutoStartService(logger, options.DryRun);
            var ctx = new TweakContext(logger, new BackupStore(AppPaths.BackupFile), options.DryRun);
            var defender = new DefenderService(ctx);
            switch (options.Setup)
            {
                case SetupCommand.AutoStartOff:
                    exit = autoStart.Disable() is null ? 0 : 1;
                    break;
                case SetupCommand.DefenderAdd:
                    exit = await defender.AddExclusionAsync() is null ? 0 : 1;
                    break;
                case SetupCommand.UninstallCleanup:
                    if (autoStart.Disable() is not null) exit = 1;
                    await new WeeklyReviewService(ctx.Shell, logger).DisableAsync();
                    if ((await defender.GetStatusAsync()).ExclusionPresent && await defender.RemoveExclusionAsync() is not null) exit = 1;
                    if (!options.DryRun) LegacyMigration.RemoveLegacyTasks();
                    // Componentes instalados junto com o app saem junto: PresentMon baixado pelo app e PawnIO (se foi o app que instalou).
                    var uninstallSettings = AppSettings.Load();
                    if (await new AppComponentsInstaller(new WingetService(ctx.Shell, logger),
                            new PresentMonDownloader(logger, uninstallSettings, options.DryRun),
                            new FpsService(logger, uninstallSettings).Locate, logger, options.DryRun).UninstallAsync() != 0) exit = 1;
                    break;
                case SetupCommand.MigrateLegacy:
                    if (!options.DryRun) await LegacyMigration.MigrateSystemAsync(ctx.Shell, logger, exe);
                    break;
                case SetupCommand.InstallComponents:
                    var settings = AppSettings.Load();
                    var fps = new FpsService(logger, settings);
                    exit = await new AppComponentsInstaller(new WingetService(ctx.Shell, logger),
                        new PresentMonDownloader(logger, settings, options.DryRun), fps.Locate, logger, options.DryRun).InstallAsync(options.Components);
                    break;
            }
            logger.Info($"Comando de instalação {options.Setup}: {(exit == 0 ? "ok" : "com erros")}.");
        }
        catch (Exception ex)
        {
            logger.Error($"Falha no comando de instalação {options.Setup}", ex);
            exit = 1;
        }
        Shutdown(exit);
    }

    /// <summary>--weekly: limpeza segura + RAM, relatório no log e notificação. Sem janela.</summary>
    private async void RunWeeklyReview(bool dryRun)
    {
        var logger = _logger!;
        string report;
        try
        {
            var shell = new Shell(logger, dryRun);
            report = await WeeklyReviewService.RunReviewAsync(
                new CleanupService(shell, logger, dryRun), new MemoryCleanerService(logger, dryRun), logger);
        }
        catch (Exception ex)
        {
            logger.Error("Falha na revisão semanal", ex);
            report = "A revisão semanal não pôde ser concluída. Veja o log.";
        }

        // No Windows 10/11 o balão do NotifyIcon aparece como notificação (toast).
        using var icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Information,
            Visible = true,
            Text = "Gueibs Optimizer"
        };
        icon.ShowBalloonTip(8000, "Gueibs Optimizer: revisão semanal", report, System.Windows.Forms.ToolTipIcon.Info);
        await Task.Delay(TimeSpan.FromSeconds(9));
        icon.Visible = false;
        Shutdown(0);
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.Error("Exceção não observada em tarefa", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger?.Error("Exceção fatal", args.ExceptionObject as Exception);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error("Erro inesperado na interface", e.Exception);
        MessageBox.Show($"Ocorreu um erro inesperado, mas o app continua aberto.\n\n{e.Exception.Message}",
            "Gueibs Optimizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _overlay?.Dispose();
        _fps?.Dispose();
        _booster?.Dispose();
        _hardware?.Dispose();
        try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
