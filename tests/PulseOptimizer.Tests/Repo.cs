namespace PulseOptimizer.Tests;

static class Repo
{
    /// <summary>The repository root (the folder that holds LICENSE and src).</summary>
    public static string Root
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !(File.Exists(Path.Combine(d.FullName, "LICENSE")) && Directory.Exists(Path.Combine(d.FullName, "src"))))
                d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("Couldn't find the repository root.");
        }
    }

    public static string[] SourceFiles() =>
        Directory.GetFiles(Path.Combine(Root, "src", "PulseOptimizer"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) && !p.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
            .ToArray();
}
