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
            var want = Regex.Matches(en, @"\{\d\}").Select(m => m.Value).OrderBy(x => x);
            var got = Regex.Matches(tr, @"\{\d\}").Select(m => m.Value).OrderBy(x => x);
            Assert.True(want.SequenceEqual(got), $"{file}: placeholders differ for '{en}'");
        }
    }

    [Theory, MemberData(nameof(Files))]
    public void Every_navigation_label_is_translated(string file)
    {
        var map = Read(file);
        foreach (var label in new[] { "Home", "Games", "Diagnose", "Tweaks", "Debloat", "Keep it fresh", "Measure", "Startup apps", "Security", "Undo & restore" })
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
}
