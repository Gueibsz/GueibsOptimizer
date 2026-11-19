using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Markup;

namespace GueibsOptimizer.Core;

/// <summary>
/// Idiomas da interface: pt-BR (padrão), en e es. A chave de cada texto é o próprio texto em português, então
/// pt-BR não precisa de arquivo e qualquer texto sem tradução cai no português (fallback). As traduções ficam em
/// Data/i18n/en.json e es.json (embutidos). A troca é ao vivo: quem usa {l:T ...} ou T(...) com o indexador
/// é avisado por PropertyChanged("Item[]").
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public const string Portuguese = "pt-BR", English = "en", Spanish = "es";
    public static readonly string[] Languages = [Portuguese, English, Spanish];

    public static LocalizationService Instance { get; } = new();

    private readonly Dictionary<string, Dictionary<string, string>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? _current;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Language { get; private set; } = Portuguese;

    /// <summary>Muda a cada troca de idioma (usado por MultiBinding para retraduzir textos vindos do ViewModel).</summary>
    public int Version { get; private set; }

    public static string Normalize(string? language) =>
        Languages.FirstOrDefault(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase)) ?? Portuguese;

    public static string DisplayName(string language) => language switch
    {
        English => "English",
        Spanish => "Español",
        _ => "Português (Brasil)"
    };

    /// <summary>Tradução do texto em português para o idioma atual; o próprio texto se não houver tradução.</summary>
    public string this[string ptText] => T(ptText);

    /// <summary>
    /// Traduz: (1) chave exata; (2) modelo com {0}, {1}... (texto montado pelo app com valores, ex.:
    /// "Liberados {0} ({1} processos)"), devolvendo a tradução com os mesmos valores; (3) texto com várias
    /// linhas: linha a linha. Sem correspondência, devolve o próprio texto (nomes de jogos, caminhos etc.).
    /// </summary>
    public string T(string ptText)
    {
        if (_current is null || string.IsNullOrEmpty(ptText)) return ptText;
        if (_current.TryGetValue(ptText, out var t) && !string.IsNullOrWhiteSpace(t)) return t;
        if (_cache.TryGetValue(ptText, out var cached)) return cached;

        string result = ptText;
        if (MatchTemplate(ptText) is { } fromTemplate) result = fromTemplate;
        else if (ptText.Contains('\n'))
            result = string.Join("\n", ptText.Split('\n').Select(line => T(line.TrimEnd('\r'))));
        if (_cache.Count > 5000) _cache.Clear();
        _cache[ptText] = result;
        return result;
    }

    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private List<(System.Text.RegularExpressions.Regex Pattern, string Translation)> _templates = [];

    private string? MatchTemplate(string text)
    {
        foreach (var (pattern, translation) in _templates)
        {
            var m = pattern.Match(text);
            if (!m.Success) continue;
            return System.Text.RegularExpressions.Regex.Replace(translation, @"\{(\d+)\}", h =>
            {
                var g = m.Groups["p" + h.Groups[1].Value];
                return g.Success ? T(g.Value) : h.Value; // o valor também pode ser traduzível (ex.: "Médio")
            });
        }
        return null;
    }

    /// <summary>Converte "Liberados {0} ({1} processos)" em regex ^Liberados (?&lt;p0&gt;.*?) \((?&lt;p1&gt;.*?) processos\)$.</summary>
    public static System.Text.RegularExpressions.Regex TemplateRegex(string ptTemplate)
    {
        var parts = System.Text.RegularExpressions.Regex.Split(ptTemplate, @"(\{\d+\})");
        var sb = new System.Text.StringBuilder("^");
        foreach (var part in parts)
        {
            var hole = System.Text.RegularExpressions.Regex.Match(part, @"^\{(\d+)\}$");
            sb.Append(hole.Success ? $"(?<p{hole.Groups[1].Value}>.*?)" : System.Text.RegularExpressions.Regex.Escape(part));
        }
        sb.Append('$');
        return new System.Text.RegularExpressions.Regex(sb.ToString(), System.Text.RegularExpressions.RegexOptions.Singleline);
    }

    /// <summary>Atalho para código: L.T("texto").</summary>
    public static string Tr(string ptText) => Instance.T(ptText);

    public void SetLanguage(string? language)
    {
        Language = Normalize(language);
        _current = Language == Portuguese ? null : Table(Language);
        _cache.Clear();
        // Modelos mais longos primeiro: o mais específico ganha.
        _templates = _current is null ? [] : _current
            .Where(kv => System.Text.RegularExpressions.Regex.IsMatch(kv.Key, @"\{\d+\}") && !string.IsNullOrWhiteSpace(kv.Value))
            .OrderByDescending(kv => kv.Key.Length)
            .Select(kv => (TemplateRegex(kv.Key), kv.Value))
            .ToList();
        Version++;
        try { CultureInfo.CurrentUICulture = new CultureInfo(Language == Portuguese ? "pt-BR" : Language); }
        catch (CultureNotFoundException) { }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    public Dictionary<string, string> Table(string language)
    {
        if (_tables.TryGetValue(language, out var t)) return t;
        t = LoadEmbedded(language);
        _tables[language] = t;
        return t;
    }

    public static Dictionary<string, string> LoadEmbedded(string language)
    {
        var asm = Assembly.GetExecutingAssembly();
        // Vários arquivos por idioma (ex.: i18n/en.json = telas, i18n/tweaks.en.json = ajustes), juntados.
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.Contains(".i18n.", StringComparison.OrdinalIgnoreCase) &&
                                                                        n.EndsWith($".{language}.json", StringComparison.OrdinalIgnoreCase)).Order())
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            foreach (var (k, v) in Parse(reader.ReadToEnd())) table[k] = v;
        }
        return table;
    }

    public static Dictionary<string, string> Parse(string json) =>
        new(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [], StringComparer.Ordinal);
}

/// <summary>
/// Texto traduzível no XAML: Text="{l:T 'Texto em português'}". Vira um Binding para o indexador do
/// LocalizationService, então atualiza sozinho quando o idioma muda.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(LocalizedText.Value)) { Source = new LocalizedText(Text), Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

/// <summary>Um texto traduzível ligado ao idioma atual (evento fraco: não segura a tela na memória).</summary>
public sealed class LocalizedText : INotifyPropertyChanged
{
    private readonly string _pt;

    public LocalizedText(string ptText)
    {
        _pt = ptText;
        PropertyChangedEventManager.AddHandler(LocalizationService.Instance, OnLanguageChanged, nameof(LocalizationService.Version));
    }

    public string Value => LocalizationService.Tr(_pt);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
}

/// <summary>
/// Traduz textos que vêm do ViewModel (ex.: títulos do menu). Uso: MultiBinding com o texto e
/// LocalizationService.Version, para reavaliar na troca de idioma.
/// </summary>
public sealed class TranslateConverter : IMultiValueConverter, IValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 0 || values[0] == System.Windows.DependencyProperty.UnsetValue ? string.Empty
        : values[0] is string s ? LocalizationService.Tr(s)
        : System.Convert.ToString(values[0], culture) ?? string.Empty; // números e outros tipos viram texto

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s ? LocalizationService.Tr(s) : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
