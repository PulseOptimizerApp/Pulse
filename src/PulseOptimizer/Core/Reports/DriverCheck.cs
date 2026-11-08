using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pulse.Core.Games.Live;

namespace Pulse.Core.Reports;

/// <summary>
/// Is a newer graphics driver out? For NVIDIA cards Pulse asks NVIDIA's own driver lookup (the one nvidia.com's
/// download page uses) for the newest Game Ready driver; nothing about this PC is sent, the question is the same for
/// every GeForce RTX and GTX 16 card. AMD and Intel don't offer a lookup like it, so for them Pulse only says how
/// old the installed driver is and where to check.
/// </summary>
public static class DriverCheck
{
    public sealed record Latest(string Version, DateTime Date, string Name);

    public sealed record Status(string Vendor, string Card, string Installed, DateTime InstalledDate, Latest Newest)
    {
        /// <summary>True when NVIDIA has a newer Game Ready driver than the one installed.</summary>
        public bool UpdateAvailable => Newest != null && Version.TryParse(Newest.Version, out var n) && Version.TryParse(Installed, out var i) && n > i;
        public int AgeDays => InstalledDate == default ? -1 : (int)(DateTime.Now - InstalledDate).TotalDays;
    }

    // NVIDIA's lookup is per product, but every GeForce RTX and GTX 16 card gets the same Game Ready driver; this asks
    // for the RTX 3070's (product family 933), Windows 10/11 64-bit (57), DCH, Game Ready (not Studio).
    const string NvidiaLookup = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
        "?func=DriverManualLookup&pfid=933&osID=57&languageCode=1033&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0&sort1=0&numberOfResults=1";
    public const string NvidiaPage = "https://www.nvidia.com/en-us/geforce/drivers/";
    public const string AmdPage = "https://www.amd.com/en/support/download/drivers.html";
    public const string IntelPage = "https://www.intel.com/content/www/us/en/support/detect.html";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// GeForce cards still getting new Game Ready drivers: RTX cards and the GTX 16 series. (GTX 10 series and older
    /// moved to security-only updates in late 2025, so "a newer driver" wouldn't apply to them.)
    /// </summary>
    public static bool NvidiaCurrent(string card) => Regex.IsMatch(card ?? "", @"RTX|GTX\s*16\d\d|TITAN\s*RTX", RegexOptions.IgnoreCase);

    public static async Task<Latest> NewestNvidia(CancellationToken ct = default)
    {
        try
        {
            Http.DefaultRequestHeaders.UserAgent.Clear();
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("PulseOptimizer");
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(NvidiaLookup, ct));
            return ParseNvidia(doc.RootElement);
        }
        catch { return null; }
    }

    internal static Latest ParseNvidia(JsonElement root)
    {
        if (root.Get("IDS") is not { ValueKind: JsonValueKind.Array } ids || ids.GetArrayLength() == 0) return null;
        var info = ids[0].Get("downloadInfo") ?? default;
        var v = info.Str("Version");
        if (!Regex.IsMatch(v, @"^\d{3}\.\d{2}$")) return null;
        DateTime.TryParse(Regex.Replace(info.Str("ReleaseDateTime"), @"^\w{3}\s", ""), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date);
        return new Latest(v, date, Uri.UnescapeDataString(info.Str("Name")).Replace('+', ' '));
    }

    /// <summary>What's installed and, for NVIDIA, the newest Game Ready driver. Null when there's no graphics card to check.</summary>
    public static async Task<Status> Check(CancellationToken ct = default)
    {
        var g = PlayLog.Gpu();
        if (g is not { } gpu) return null;
        var installed = PlayLog.FriendlyVersion(gpu.Version, gpu.Vendor);
        Latest newest = null;
        if (gpu.Vendor == "NVIDIA" && NvidiaCurrent(gpu.Name))
        {
            newest = await NewestNvidia(ct);
            var st = TweakEngine.State.Report;
            st.LastDriverCheck = DateTime.UtcNow;
            if (newest != null) { st.LatestDriver = newest.Version; st.LatestDriverDate = newest.Date; }
        }
        return new Status(gpu.Vendor, gpu.Name, installed, gpu.Date, newest);
    }

    public const string NvidiaAppPage = "https://www.nvidia.com/en-us/software/nvidia-app/";

    /// <summary>The NVIDIA app on this PC (it installs and updates GeForce drivers), or null when it isn't installed.</summary>
    public static string NvidiaApp()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var p in new[]
        {
            Path.Combine(pf, "NVIDIA Corporation", "NVIDIA app", "CEF", "NVIDIA app.exe"),
            Path.Combine(pf, "NVIDIA Corporation", "NVIDIA App", "CEF", "NVIDIA App.exe"),
        })
            if (File.Exists(p)) return p;
        return null;
    }

    /// <summary>Opens the NVIDIA app when it's installed, otherwise its download page.</summary>
    public static void OpenNvidiaApp()
    {
        try
        {
            var exe = NvidiaApp();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe ?? NvidiaAppPage) { UseShellExecute = true });
        }
        catch { }
    }

    public static string PageFor(string vendor) => vendor switch { "NVIDIA" => NvidiaPage, "AMD" => AmdPage, "Intel" => IntelPage, _ => NvidiaPage };
}
