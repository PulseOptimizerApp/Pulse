using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
        Use(Load(Current));
        if (Current != "en") Hook();
    }

    static bool _hooked;

    /// <summary>
    /// Translates each element the first time it's laid out. WPF only sends Loaded to elements that have their own
    /// Loaded handler, so a class handler on Loaded misses most text written in XAML (the sidebar, buttons, card
    /// titles). SizeChanged is raised for every element when it first gets a size, which is exactly when it appears.
    /// </summary>
    static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => TranslateOnce(s as FrameworkElement)), true);
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((s, _) => TranslateOnce(s as FrameworkElement)), true);
    }

    static readonly ConditionalWeakTable<FrameworkElement, object> Done = new();

    static void TranslateOnce(FrameworkElement fe)
    {
        if (fe == null || _map.Count == 0 || Done.TryGetValue(fe, out _)) return;
        Done.Add(fe, null);
        try { Translate(fe); } catch { /* a translation must never break a screen */ }
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
    internal static void UseForTests(string code, Dictionary<string, string> map) { Current = code; Use(map); }

    // Entries with {0}, {1}… also match text built in code, like "Freed 34.8 MB" for "Freed {0}".
    static List<(Regex Rx, string Translation, int[] Order)> _patterns = new();
    static readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    static void Use(Dictionary<string, string> map)
    {
        _map = map;
        lock (_cache) _cache.Clear();
        var list = new List<(Regex, string, int[], int)>();
        foreach (var (en, tr) in map)
        {
            if (string.IsNullOrEmpty(tr) || !Regex.IsMatch(en, @"\{\d\}")) continue;
            var literal = Regex.Replace(en, @"\{\d\}", "");
            if (Regex.Matches(literal, @"\p{L}").Count < 3) continue; // too little text to match safely
            var seen = new List<int>();
            var rx = "^" + Regex.Replace(Regex.Escape(en).Replace(@"\{", "{"), @"\{(\d)\}", m =>
            {
                int n = m.Groups[1].Value[0] - '0';
                if (seen.Contains(n)) return $@"\k<a{n}>";
                seen.Add(n);
                return $"(?<a{n}>[^\n]*?)";
            }) + "$";
            try { list.Add((new Regex(rx, RegexOptions.CultureInvariant), tr, seen.ToArray(), literal.Length)); }
            catch (ArgumentException) { }
        }
        // Most specific first, so "{0} freed · {1} in use" wins over "{0} freed".
        _patterns = list.OrderByDescending(x => x.Item4).Select(x => (x.Item1, x.Item2, x.Item3)).ToList();
    }

    /// <summary>
    /// The translation of an English text, or the text itself. Besides exact matches it understands text built in
    /// code: entries with {0} match the same sentence with numbers or names filled in, and text made of several
    /// sentences or lines is translated piece by piece.
    /// </summary>
    public static string T(string english)
    {
        if (string.IsNullOrEmpty(english) || _map.Count == 0) return english;
        lock (_cache) if (_cache.TryGetValue(english, out var hit)) return hit;
        // Whole-text match first; for joined sentences prefer translating every piece, so a pattern like
        // "Freed {0}" can't swallow the sentences after it as its value.
        var result = (_map.TryGetValue(english, out var exact) && !string.IsNullOrEmpty(exact) ? exact : null)
                     ?? Pieces(english, all: true) ?? Lookup(english, true) ?? Pieces(english, all: false) ?? english;
        lock (_cache) { if (_cache.Count > 20000) _cache.Clear(); _cache[english] = result; }
        return result;
    }

    static string Lookup(string s, bool patterns)
    {
        if (_map.TryGetValue(s, out var t) && !string.IsNullOrEmpty(t)) return t;
        var trimmed = s.Trim();
        if (trimmed.Length > 0 && trimmed.Length != s.Length)
        {
            var inner = Lookup(trimmed, patterns);
            if (inner != null) { int i = s.IndexOf(trimmed, StringComparison.Ordinal); return s[..i] + inner + s[(i + trimmed.Length)..]; }
        }
        if (!patterns || !Regex.IsMatch(s, @"\p{L}")) return null;
        foreach (var (rx, tr, order) in _patterns)
        {
            var m = rx.Match(s);
            if (!m.Success) continue;
            // Values filled in can be words too ("On", "Clean"): translate those that have an exact entry.
            return Regex.Replace(tr, @"\{(\d)\}", g =>
            {
                var v = m.Groups["a" + g.Groups[1].Value];
                if (!v.Success) return g.Value;
                return v.Value.Length < s.Length ? Lookup(v.Value, true) ?? v.Value : v.Value;
            });
        }
        return null;
    }

    static readonly Regex Separators = new(@"(\n+|(?<=[.!?:…)])[ ]+|[ ]·[ ]|[ ]—[ ])", RegexOptions.CultureInvariant);

    /// <summary>Text joined from several sentences or lines: translate each piece that has an entry.</summary>
    static string Pieces(string s, bool all)
    {
        var parts = Separators.Split(s);
        if (parts.Length < 3) return null;
        bool any = false, missed = false;
        for (int i = 0; i < parts.Length; i += 2)
        {
            if (parts[i].Length == 0 || !Regex.IsMatch(parts[i], @"\p{L}")) continue;
            var t = Lookup(parts[i], true);
            if (t != null) { parts[i] = t; any = true; } else missed = true;
        }
        return any && (!all || !missed) ? string.Concat(parts) : null;
    }

    /// <summary>A translated pattern with values filled in, e.g. F("{0} min ago", 5).</summary>
    public static string F(string englishPattern, params object[] args)
    {
        try { return string.Format(T(englishPattern), args); }
        catch (FormatException) { return string.Format(englishPattern, args); }
    }

    // Text set after a screen appears (scan results, statuses, bound values that change) is translated too.
    static readonly DependencyPropertyDescriptor TextProp = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
    static readonly ConditionalWeakTable<TextBlock, object> Watched = new();
    static bool _setting;

    static void Watch(TextBlock tb)
    {
        if (Watched.TryGetValue(tb, out _)) return;
        Watched.Add(tb, null);
        TextProp.AddValueChanged(tb, TextChanged);
        tb.Unloaded += Unwatch;
    }

    static void Unwatch(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock tb) return;
        TextProp.RemoveValueChanged(tb, TextChanged);
        tb.Unloaded -= Unwatch;
        Watched.Remove(tb);
        Done.Remove(tb);
    }

    static void TextChanged(object sender, EventArgs e)
    {
        if (_setting || sender is not TextBlock tb || tb.Inlines.Count > 1) return;
        var t = T(tb.Text);
        if (t == tb.Text) return;
        _setting = true;
        try { tb.SetCurrentValue(TextBlock.TextProperty, t); }
        finally { _setting = false; }
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
                    Watch(tb);
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
