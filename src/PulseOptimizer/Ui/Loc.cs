using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Pulse.Ui;

/// <summary>
/// Translations. Pulse is written in English; each language is a file in Lang/ mapping the English text to the translation.
/// The language follows Windows (Settings → Time &amp; language) unless the user picks one. Anything without a
/// translation stays in English, so a missing line never breaks the UI.
/// Every TextBlock, button and placeholder is translated as it appears (a class handler on Loaded), so the XAML stays
/// plain English; text set from code goes through <see cref="T"/>.
/// </summary>
public static class Loc
{
    /// <summary>Languages Pulse ships, by code, with their own name.</summary>
    public static readonly (string Code, string Name)[] Languages = { ("en", "English"), ("es", "Español"), ("pt-BR", "Português (Brasil)") };

    static Dictionary<string, string> _map = new(StringComparer.Ordinal);
    public static string Current { get; private set; } = "en";

    /// <summary>Picks the language: the user's choice, else Windows' display language, else English.</summary>
    public static void Init(string choice)
    {
        Current = string.IsNullOrEmpty(choice) || choice == "auto" ? FromWindows() : choice;
        _map = Load(Current);
        if (Current != "en")
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Translate(s as FrameworkElement)), true);
    }

    /// <summary>Maps a Windows language to one Pulse has: es-MX → es, pt-PT → pt-BR, anything else → en.</summary>
    public static string FromWindows(CultureInfo culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        return culture.TwoLetterISOLanguageName switch
        {
            "es" => "es",
            "pt" => "pt-BR",
            _ => "en",
        };
    }

    static Dictionary<string, string> Load(string code)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (code == "en") return map;
        try
        {
            using var s = typeof(Loc).Assembly.GetManifestResourceStream($"Lang.{code}.json");
            if (s != null) map = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? map;
        }
        catch { }
        return map;
    }

    /// <summary>For tests: use a given translation table.</summary>
    internal static void UseForTests(string code, Dictionary<string, string> map) { Current = code; _map = map; }

    /// <summary>The translation of an English text, or the text itself.</summary>
    public static string T(string english) =>
        english != null && _map.TryGetValue(english, out var t) && !string.IsNullOrEmpty(t) ? t : english;

    /// <summary>A translated pattern with values filled in, e.g. F("{0} min ago", 5).</summary>
    public static string F(string englishPattern, params object[] args)
    {
        try { return string.Format(T(englishPattern), args); }
        catch (FormatException) { return string.Format(englishPattern, args); }
    }

    static void Translate(FrameworkElement fe)
    {
        if (fe == null) return;
        switch (fe)
        {
            case TextBlock tb:
                if (tb.Inlines.Count > 1 || (tb.Inlines.FirstInline != null && tb.Inlines.FirstInline is not Run))
                {
                    foreach (var run in tb.Inlines.OfType<Run>())
                    {
                        var r = T(run.Text);
                        if (!ReferenceEquals(r, run.Text) && r != run.Text) run.SetCurrentValue(Run.TextProperty, r);
                    }
                }
                else
                {
                    var t = T(tb.Text);
                    if (t != tb.Text) tb.SetCurrentValue(TextBlock.TextProperty, t);
                }
                break;
            case TextBox box when box.Tag is string hint:
                var h = T(hint);
                if (h != hint) box.SetCurrentValue(FrameworkElement.TagProperty, h);
                break;
        }
        if (fe.ToolTip is string tip)
        {
            var t = T(tip);
            if (t != tip) fe.SetCurrentValue(FrameworkElement.ToolTipProperty, t);
        }
    }
}
