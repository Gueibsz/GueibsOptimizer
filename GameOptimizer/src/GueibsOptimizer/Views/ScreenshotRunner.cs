using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GueibsOptimizer.Core;
using GueibsOptimizer.ViewModels;

namespace GueibsOptimizer.Views;

/// <summary>
/// --screenshots: percorre todas as páginas do menu em tema escuro e claro e nos três idiomas e grava um PNG
/// de cada uma com RenderTargetBitmap (funciona com a sessão remota bloqueada). Roda sempre em simulação e
/// nunca salva tema/idioma em settings.json. Não chama OnOpen (não abre as janelas de teste do mouse/monitor).
/// </summary>
public static class ScreenshotRunner
{
    public static readonly string[] Languages = [LocalizationService.Portuguese, LocalizationService.English, LocalizationService.Spanish];
    public static readonly AppTheme[] Themes = [AppTheme.Dark, AppTheme.Light];

    public static string FileName(string language, AppTheme theme, int index, string title) =>
        $"{language}-{(theme == AppTheme.Dark ? "escuro" : "claro")}-{index:00}-{Slug(title)}.png";

    public static string Slug(string title)
    {
        var normalized = title.Normalize(System.Text.NormalizationForm.FormD);
        var chars = normalized
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        var slug = new string(chars.ToArray());
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('-');
    }

    public static async Task RunAsync(Window window, MainViewModel vm, ThemeService theme, AppSettings settings,
        string folder, Logger logger, Action done)
    {
        int saved = 0, failed = 0;
        try
        {
            Directory.CreateDirectory(folder);
            window.WindowState = WindowState.Normal;
            window.Width = 1100;
            window.Height = 700;
            await Settle(window, 3000);

            foreach (var language in Languages)
            {
                LocalizationService.Instance.SetLanguage(language);
                foreach (var mode in Themes)
                {
                    theme.Apply(mode, settings.AccentColor);
                    for (int i = 0; i < vm.NavItems.Count; i++)
                    {
                        var item = vm.NavItems[i];
                        try
                        {
                            if (item.Tab is { } tab && item.Page is ITabbedPage tabbed) tabbed.SelectedTab = tab;
                            vm.SelectedNav = item;
                            await Settle(window, 1200);
                            Save(window, Path.Combine(folder, FileName(language, mode, i, item.Title)));
                            saved++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            logger.Error($"Captura de tela falhou: {item.Title} ({language}, {mode})", ex);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error("Capturas de tela interrompidas", ex);
        }
        finally
        {
            // Volta ao que estava em settings.json (nada foi salvo).
            LocalizationService.Instance.SetLanguage(settings.Language);
            theme.Apply(settings.Theme, settings.AccentColor);
            logger.Info($"Capturas de tela: {saved} salvas, {failed} com erro, em {folder}.");
            done();
        }
    }

    private static async Task Settle(Window window, int ms)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(ms);
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
    }

    private static void Save(Window window, string path)
    {
        var root = (Visual)window.Content;
        var element = (FrameworkElement)root;
        int w = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        int h = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        // Fundo opaco: a janela pode ter cantos transparentes.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = Application.Current.TryFindResource("BgBrush") as Brush ?? Brushes.Black;
            dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
        }
        bitmap.Render(visual);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
