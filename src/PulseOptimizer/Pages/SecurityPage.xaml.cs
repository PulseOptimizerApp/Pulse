using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Pulse.Core;
using Pulse.Ui;

namespace Pulse.Pages;

public partial class SecurityPage : UserControl, IPage
{
    bool _loaded, _scanning;
    CancellationTokenSource _scanCts;
    List<PersistItem> _audit = new();
    bool _showAll;

    public SecurityPage() => InitializeComponent();

    public async void OnShown(object parameter)
    {
        if (!_loaded) { _loaded = true; await RefreshStatus(); }
    }

    async Task RefreshStatus()
    {
        StatusSpinner.Visibility = Visibility.Visible;
        ShieldIcon.Visibility = Visibility.Hidden;
        StatusTitle.Text = "Checking protection…";
        var s = await Task.Run(() => SecurityScanner.GetStatus());
        StatusSpinner.Visibility = Visibility.Collapsed;
        ShieldIcon.Visibility = Visibility.Visible;

        Brush fg, bg;
        if (!s.Available || !s.Enabled)
        {
            (fg, bg) = (Palette.Warn, Palette.WarnSoft);
            StatusTitle.Text = "Microsoft Defender isn't your active antivirus";
            StatusDetail.Text = s.Products.Count > 0
                ? $"Windows reports: {string.Join(", ", s.Products)}. That's fine if you trust it, but Pulse's scans use Defender, so they won't run while another antivirus is in charge."
                : "Windows didn't report any active antivirus. Turn Defender back on in Windows Security.";
        }
        else
        {
            bool active = s.Threats.Any(t => t.Active);
            (fg, bg) = s.Healthy ? (Palette.Good, Palette.GoodSoft) : active ? (Palette.Bad, Palette.BadSoft) : (Palette.Warn, Palette.WarnSoft);
            StatusTitle.Text = s.Healthy ? "Protected" : active ? "Threats need your attention" : "Protection needs attention";
            var lines = new List<string>
            {
                $"Real-time protection: {(s.RealTime ? "on" : "OFF — turn it back on in Windows Security")}",
                $"Virus definitions: {(s.SignatureAgeDays == 0 ? "updated today" : $"{s.SignatureAgeDays} day{(s.SignatureAgeDays == 1 ? "" : "s")} old")} (version {s.SignatureVersion})",
                $"Last quick scan: {Age(s.QuickScanAgeDays)} · Last full scan: {Age(s.FullScanAgeDays)}",
                $"Tamper protection: {(s.TamperProtected ? "on" : "off")}",
            };
            if (active) lines.Add($"{s.Threats.Count(t => t.Active)} threat(s) weren't fully removed. See the history below.");
            StatusDetail.Text = string.Join("\n", lines);
        }
        ShieldIcon.Foreground = fg;
        ShieldBg.Background = bg;

        ThreatList.ItemsSource = s.Threats;
        NoThreats.Visibility = s.Threats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static string Age(long days) => days > 10000 ? "never" : days == 0 ? "today" : $"{days} day{(days == 1 ? "" : "s")} ago";

    async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshStatus();

    // ───────── Scans ─────────

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning) return;
        var kind = (string)((FrameworkElement)sender).Tag;
        string file = null;

        if (kind == "file")
        {
            var dlg = new OpenFileDialog { Title = "Choose a file to scan" };
            if (dlg.ShowDialog() != true) return;
            file = dlg.FileName;
        }
        if (kind == "offline")
        {
            if (!await MainWindow.Instance.Confirm("Restart into an offline scan?",
                    "Your PC restarts and Microsoft Defender scans before Windows loads, which catches malware that hides while Windows is running. It takes about 15 minutes. Save your work first.", "Restart and scan"))
                return;
            var msg = await Task.Run(() => SecurityScanner.StartOfflineScan());
            MainWindow.Instance.Toast(msg, ToastKind.Info);
            return;
        }

        await RunScan(kind, file);
    }

    async Task RunScan(string kind, string file)
    {
        _scanning = true;
        _scanCts = new CancellationTokenSource();
        ScanButtons.IsEnabled = false;
        ScanRunning.Visibility = Visibility.Visible;
        ScanResult.Visibility = Visibility.Collapsed;
        var started = DateTime.Now;
        var label = kind switch { "quick" => "Quick scan", "full" => "Full scan", "update" => "Updating definitions", _ => "Scanning " + Path.GetFileName(file) };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => ScanStatus.Text = $"{label}… {(DateTime.Now - started):mm\\:ss}";
        timer.Start();
        ScanStatus.Text = label + "…";
        try
        {
            var token = _scanCts.Token;
            string message;
            bool threats = false;
            if (kind == "update")
                message = await Task.Run(() => SecurityScanner.UpdateSignatures());
            else
            {
                var k = kind switch { "quick" => SecurityScanner.ScanKind.Quick, "full" => SecurityScanner.ScanKind.Full, _ => SecurityScanner.ScanKind.File };
                (threats, message) = await Task.Run(() => SecurityScanner.Scan(k, file, token));
            }
            ScanResult.Text = message;
            ScanResult.Foreground = threats ? Palette.Bad : (Brush)FindResource("TextBrush");
            ScanResult.Visibility = Visibility.Visible;
            if (threats) MainWindow.Instance.Toast("Defender found threats. See the history below.", ToastKind.Warning);
            else MainWindow.Instance.Toast(message);
        }
        finally
        {
            timer.Stop();
            _scanning = false;
            ScanButtons.IsEnabled = true;
            ScanRunning.Visibility = Visibility.Collapsed;
        }
        await RefreshStatus();
    }

    void CancelScan_Click(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    // ───────── Startup check ─────────

    async void Audit_Click(object sender, RoutedEventArgs e)
    {
        AuditBtn.IsEnabled = false;
        AuditRunning.Visibility = Visibility.Visible;
        AuditList.ItemsSource = null;
        try { _audit = await Task.Run(() => SecurityScanner.PersistenceAudit()); }
        finally
        {
            AuditBtn.IsEnabled = true;
            AuditRunning.Visibility = Visibility.Collapsed;
        }
        int bad = _audit.Count(i => i.Level == Severity.Problem);
        int review = _audit.Count(i => i.Level == Severity.Check);
        AuditSummary.Text = bad == 0 && review == 0
            ? $"Checked {_audit.Count} startup entries. Nothing unusual found."
            : $"Checked {_audit.Count} startup entries: {bad} suspicious, {review} to review.";
        AuditSummary.Foreground = bad > 0 ? Palette.Bad : review > 0 ? Palette.Warn : Palette.Good;
        AuditSummary.Visibility = Visibility.Visible;
        AuditFilters.Visibility = Visibility.Visible;
        ShowAudit();
    }

    void AuditFilter_Checked(object sender, RoutedEventArgs e)
    {
        _showAll = (string)((FrameworkElement)sender).Tag == "all";
        if (IsLoaded) ShowAudit();
    }

    void ShowAudit() =>
        AuditList.ItemsSource = _showAll ? _audit : _audit.Where(i => i.Level != Severity.Ok).ToList();

    async void ScanItem_Click(object sender, RoutedEventArgs e)
    {
        var item = (PersistItem)((FrameworkElement)sender).DataContext;
        if (_scanning) { MainWindow.Instance.Toast("A scan is already running.", ToastKind.Info); return; }
        await RunScan("file", item.Path);
    }

    void VirusTotal_Click(object sender, RoutedEventArgs e)
    {
        var item = (PersistItem)((FrameworkElement)sender).DataContext;
        // Opens VirusTotal's public report page for this file's hash. Only the hash is sent, never the file.
        Process.Start(new ProcessStartInfo("https://www.virustotal.com/gui/file/" + item.Sha256) { UseShellExecute = true });
    }

    void ShowItem_Click(object sender, RoutedEventArgs e)
    {
        var item = (PersistItem)((FrameworkElement)sender).DataContext;
        if (File.Exists(item.Path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
    }
}
