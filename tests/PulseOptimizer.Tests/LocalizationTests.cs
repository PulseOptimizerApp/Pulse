using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pulse.Ui;
using Xunit;

namespace PulseOptimizer.Tests;

public class LocalizationTests
{
    static string LangDir => Path.Combine(Repo.Root, "src", "PulseOptimizer", "Lang");

    public static IEnumerable<object[]> Files() =>
        new[] { "es.json", "pt-BR.json" }.Select(f => new object[] { f });

    static Dictionary<string, string> Read(string file) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(LangDir, file)))!;

    [Theory, MemberData(nameof(Files))]
    public void Every_translation_is_filled_in_and_keeps_its_placeholders(string file)
    {
        foreach (var (en, tr) in Read(file))
        {
            Assert.False(string.IsNullOrWhiteSpace(tr), $"{file}: '{en}' has no translation");
            // The same placeholders must be there; a translation may repeat one (Spanish plurals: "ajuste{1} recomendado{1}").
            var want = Regex.Matches(en, @"\{\d\}").Select(m => m.Value).Distinct().OrderBy(x => x);
            var got = Regex.Matches(tr, @"\{\d\}").Select(m => m.Value).Distinct().OrderBy(x => x);
            Assert.True(want.SequenceEqual(got), $"{file}: placeholders differ for '{en}'");
        }
    }

    [Theory, MemberData(nameof(Files))]
    public void Every_navigation_label_is_translated(string file)
    {
        var map = Read(file);
        foreach (var label in new[] { "Home", "Games", "Clips", "Diagnose", "Tweaks", "Debloat", "Keep it fresh", "Measure", "Startup apps", "Security", "Undo & restore" })
            Assert.True(map.ContainsKey(label), $"{file} is missing '{label}'");
    }

    [Theory]
    [InlineData("es-MX", "es")]
    [InlineData("es-ES", "es")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("en-US", "en")]
    [InlineData("de-DE", "en")]
    [InlineData("ja-JP", "en")]
    public void Picks_the_language_from_Windows(string culture, string expected) =>
        Assert.Equal(expected, Loc.FromWindows(new CultureInfo(culture)));

    [Fact]
    public void Untranslated_text_stays_in_English() =>
        Assert.Equal("Some text with no translation 123", Loc.T("Some text with no translation 123"));

    // Loc keeps one table for the whole app, so these tests share it one at a time.
    static readonly object Gate = new();

    static string WithTable(Func<string> body)
    {
        lock (Gate)
        {
            Loc.UseForTests("es", new Dictionary<string, string>
            {
                ["Clean"] = "Limpio",
                ["Freed {0}"] = "Liberado {0}",
                ["{0} freed"] = "{0} liberados",
                ["{0} freed · {1} in use"] = "{0} liberados · {1} en uso",
                ["Status: {0}"] = "Estado: {0}",
                ["Files that are in use are skipped."] = "Los archivos en uso se omiten.",
                ["Free up space?"] = "¿Liberar espacio?",
                ["Move {0} file{1} to the Recycle Bin"] = "Mover {0} archivo{1} a la Papelera",
            });
            try { return body(); }
            finally { Loc.UseForTests("en", new Dictionary<string, string>()); }
        }
    }

    [Theory]
    [InlineData("Freed 34.8 MB", "Liberado 34.8 MB")]
    [InlineData("32.6 MB freed", "32.6 MB liberados")]
    [InlineData("32.6 MB freed · 1.1 MB in use", "32.6 MB liberados · 1.1 MB en uso")]   // the longer entry wins
    [InlineData("Status: Clean", "Estado: Limpio")]                                       // filled-in words are translated too
    [InlineData("Move 3 files to the Recycle Bin", "Mover 3 archivos a la Papelera")]
    [InlineData("Move 1 file to the Recycle Bin", "Mover 1 archivo a la Papelera")]
    public void Text_built_in_code_is_translated(string english, string expected) =>
        Assert.Equal(expected, WithTable(() => Loc.T(english)));

    [Fact]
    public void Joined_sentences_and_lines_are_translated_piece_by_piece() =>
        Assert.Equal("Liberado 2 GB\n\nLos archivos en uso se omiten.",
            WithTable(() => Loc.T("Freed 2 GB\n\nFiles that are in use are skipped.")));

    [Fact]
    public void Leading_and_trailing_spaces_are_kept() =>
        Assert.Equal("  Limpio ", WithTable(() => Loc.T("  Clean ")));

    [Fact]
    public void Text_with_no_entry_is_left_alone() =>
        Assert.Equal("Something else entirely. Freedom", WithTable(() => Loc.T("Something else entirely. Freedom")));
}

public class LocalizationOnScreenTests
{
    /// <summary>Text written in XAML (no Loaded handler of its own) is translated once it's on screen.</summary>
    [Fact]
    public void Xaml_text_without_handlers_is_translated()
    {
        Exception error = null;
        string text = null, button = null;
        var t = new Thread(() =>
        {
            try
            {
                Loc.Init("es");
                var tb = new System.Windows.Controls.TextBlock { Text = "Run a checkup" };
                var btn = new System.Windows.Controls.Button { Content = "Measure FPS" };
                var w = new System.Windows.Window
                {
                    Content = new System.Windows.Controls.StackPanel { Children = { tb, btn } },
                    Width = 300, Height = 200, Left = -2000, Top = -2000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = System.Windows.WindowStyle.None,
                };
                w.Show();
                w.UpdateLayout();
                text = tb.Text;
                var inner = FindText(btn);
                button = inner?.Text;
                w.Close();
            }
            catch (Exception ex) { error = ex; }
            finally { Loc.UseForTests("en", new Dictionary<string, string>()); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.Null(error);
        Assert.Equal("Haz una revisión", text);
        Assert.NotEqual("Measure FPS", button);
    }

    static System.Windows.Controls.TextBlock FindText(System.Windows.DependencyObject d)
    {
        if (d is System.Windows.Controls.TextBlock tb) return tb;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindText(System.Windows.Media.VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }
}
