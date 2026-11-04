using System.Text.RegularExpressions;
using Xunit;

namespace PulseOptimizer.Tests;

public class XamlResourceTests
{
    /// <summary>
    /// A page that uses a style it can't see crashes when it opens ("Provide value on StaticResourceExtension threw
    /// an exception"). Every {StaticResource X} must be defined in the theme, App.xaml or the page itself.
    /// </summary>
    [Fact]
    public void Every_page_only_uses_styles_it_can_see()
    {
        var src = Path.Combine(Repo.Root, "src", "PulseOptimizer");
        string Keys(string file) => File.ReadAllText(file);
        var shared = new HashSet<string>();
        foreach (var f in new[] { Path.Combine(src, "Themes", "Theme.xaml"), Path.Combine(src, "App.xaml") })
            if (File.Exists(f)) foreach (Match m in Regex.Matches(Keys(f), "x:Key=\"([^\"]+)\"")) shared.Add(m.Groups[1].Value);
        var missing = new List<string>();
        foreach (var f in Directory.GetFiles(src, "*.xaml", SearchOption.AllDirectories))
        {
            var text = Keys(f);
            var local = Regex.Matches(text, "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
            foreach (Match m in Regex.Matches(text, @"\{StaticResource ([A-Za-z0-9_]+)\}"))
                if (!shared.Contains(m.Groups[1].Value) && !local.Contains(m.Groups[1].Value))
                    missing.Add($"{Path.GetFileName(f)}: {m.Groups[1].Value}");
        }
        Assert.True(missing.Count == 0, "Undefined styles: " + string.Join(", ", missing.Distinct()));
    }
}
