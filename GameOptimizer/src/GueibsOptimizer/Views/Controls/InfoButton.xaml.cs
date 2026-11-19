using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GueibsOptimizer.Views.Controls;

/// <summary>
/// Botão "i" reutilizável. Mostra uma dica curta no hover e, ao clicar (ou Enter/Espaço pelo teclado),
/// abre um painel com <see cref="Title"/>, um <see cref="Text"/> simples e/ou <see cref="Sections"/>
/// (cabeçalho + texto). Esc fecha e devolve o foco ao botão.
/// </summary>
public partial class InfoButton : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(InfoButton), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(InfoButton), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SectionsProperty =
        DependencyProperty.Register(nameof(Sections), typeof(IEnumerable), typeof(InfoButton), new PropertyMetadata(null));

    public static readonly DependencyProperty ShortTipProperty =
        DependencyProperty.Register(nameof(ShortTip), typeof(string), typeof(InfoButton), new PropertyMetadata("Mais informações"));

    public InfoButton() => InitializeComponent();

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public IEnumerable? Sections { get => (IEnumerable?)GetValue(SectionsProperty); set => SetValue(SectionsProperty, value); }
    public string ShortTip { get => (string)GetValue(ShortTipProperty); set => SetValue(ShortTipProperty, value); }

    private void OnOpened(object? sender, EventArgs e) => Panel.Focus();

    private void OnClosed(object? sender, EventArgs e)
    {
        if (Toggle.IsKeyboardFocusWithin || Panel.IsKeyboardFocusWithin) Toggle.Focus();
    }

    private void OnPanelKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Toggle.IsChecked = false;
        Toggle.Focus();
        e.Handled = true;
    }
}
