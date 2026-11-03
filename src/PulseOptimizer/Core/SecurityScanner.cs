using System.Diagnostics;
using System.Text.Json;
using System.Windows.Media;
using Pulse.Ui;

namespace Pulse.Core;

public class ThreatEntry
{
    public string Name { get; init; }
    public string When { get; init; }
    public string Files { get; init; }
    public string Status { get; init; }
    public bool Active { get; init; }
}

public class DefenderStatus
{
    public bool Available { get; init; }
    public bool Enabled { get; init; }
    public bool RealTime { get; init; }
    public bool TamperProtected { get; init; }
    public long SignatureAgeDays { get; init; }
    public string SignatureVersion { get; init; } = "";
    public long QuickScanAgeDays { get; init; }
    public long FullScanAgeDays { get; init; }
    public List<string> Products { get; init; } = new();
    public List<ThreatEntry> Threats { get; init; } = new();

    public bool Healthy => Enabled && RealTime && SignatureAgeDays <= 3 && !Threats.Any(t => t.Active);
}

public class PersistItem : Observable
{
    public string Type { get; init; }
    public string Name { get; init; }
    public string Command { get; init; }
    public string Path { get; init; }
    public bool Exists { get; init; }
    public string Signature { get; init; }
    public string Publisher { get; init; }
    public List<string> Flags { get; init; } = new();
    public string Sha256 { get; init; }
    /// <summary>ATT&CK technique this location belongs to.</summary>
    public string Technique { get; init; }

    public Severity Level =>
        Flags.Any(f => f.StartsWith("!")) ? Severity.Problem :
        Flags.Count > 0 ? Severity.Check : Severity.Ok;

    public IEnumerable<string> FlagTexts => Flags.Select(f => f.TrimStart('!'));
    public string FlagsText => string.Join(" · ", FlagTexts);
    public bool HasHash => !string.IsNullOrEmpty(Sha256);
    public string LevelText => Level switch { Severity.Problem => "Suspicious", Severity.Check => "Review", _ => "OK" };
    public Brush LevelBrush => Level switch { Severity.Problem => Palette.Bad, Severity.Check => Palette.Warn, _ => Palette.Good };
    public Brush LevelBackground => Level switch { Severity.Problem => Palette.BadSoft, Severity.Check => Palette.WarnSoft, _ => Palette.GoodSoft };
    public string SignerText => string.IsNullOrEmpty(Publisher) ? (Exists ? "Not signed" : "File missing") : "Signed by " + Publisher;
}

/// <summary>
/// Security uses Microsoft Defender's engine for scanning (Pulse never writes its own virus scanner),
/// plus a startup/persistence check covering MITRE ATT&CK T1547.001 locations, scheduled tasks (T1053),
/// services (T1543), Winlogon (T1547.004) and WMI subscriptions (T1546.003).
/// </summary>
public static class SecurityScanner
{
    public static string MpCmdRun => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");

    public static DefenderStatus GetStatus()
    {
        const string script = @"
$ErrorActionPreference = 'SilentlyContinue'
$s = Get-MpComputerStatus
$names = @{}
Get-MpThreat | ForEach-Object { $names[[string]$_.ThreatID] = $_.ThreatName }
$threats = @(Get-MpThreatDetection | Sort-Object InitialDetectionTime -Descending | Select-Object -First 25 | ForEach-Object {
  [pscustomobject]@{
    Name = [string]$names[[string]$_.ThreatID]
    When = if ($_.InitialDetectionTime) { $_.InitialDetectionTime.ToString('o') } else { '' }
    Files = (@($_.Resources) -join '; ')
    StatusId = [int]$_.ThreatStatusID
    Success = [bool]$_.ActionSuccess
  }
})
$av = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct | ForEach-Object { [string]$_.displayName })
ConvertTo-Json -Depth 4 -Compress -InputObject ([pscustomobject]@{
  Available = [bool]$s
  Enabled = [bool]$s.AntivirusEnabled
  RealTime = [bool]$s.RealTimeProtectionEnabled
  Tamper = [bool]$s.IsTamperProtected
  SigAge = [long]$s.AntivirusSignatureAge
  SigVersion = [string]$s.AntivirusSignatureVersion
  QuickAge = [long]$s.QuickScanAge
  FullAge = [long]$s.FullScanAge
  Products = $av
  Threats = $threats
})";
        var r = Shell.PowerShell(script, 60_000);
        try
        {
            using var doc = JsonDocument.Parse(r.Output);
            var e = doc.RootElement;
            var threats = new List<ThreatEntry>();
            foreach (var t in e.GetProperty("Threats").EnumerateArray())
            {
                int sid = t.GetProperty("StatusId").GetInt32();
                threats.Add(new ThreatEntry
                {
                    Name = Fallback(t.GetProperty("Name").GetString(), "Unknown threat"),
                    When = DateTime.TryParse(t.GetProperty("When").GetString(), out var w) ? w.ToString("MMM d, yyyy h:mm tt") : "",
                    Files = t.GetProperty("Files").GetString(),
                    Status = StatusText(sid),
                    Active = sid is 1 or 102 or 103 or 105 or 107 || !t.GetProperty("Success").GetBoolean(),
                });
            }
            return new DefenderStatus
            {
                Available = e.GetProperty("Available").GetBoolean(),
                Enabled = e.GetProperty("Enabled").GetBoolean(),
                RealTime = e.GetProperty("RealTime").GetBoolean(),
                TamperProtected = e.GetProperty("Tamper").GetBoolean(),
                SignatureAgeDays = e.GetProperty("SigAge").GetInt64(),
                SignatureVersion = e.GetProperty("SigVersion").GetString() ?? "",
                QuickScanAgeDays = e.GetProperty("QuickAge").GetInt64(),
                FullScanAgeDays = e.GetProperty("FullAge").GetInt64(),
                Products = e.GetProperty("Products").EnumerateArray().Select(p => p.GetString()).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList(),
                Threats = threats,
            };
        }
        catch
        {
            return new DefenderStatus { Available = false };
        }
    }

    static string Fallback(string s, string f) => string.IsNullOrWhiteSpace(s) ? f : s;

    static string StatusText(int id) => id switch
    {
        1 => "Detected",
        2 => "Cleaned",
        3 => "Quarantined",
        4 => "Removed",
        5 => "Allowed by you",
        6 => "Blocked",
        102 => "Quarantine failed",
        103 => "Removal failed",
        104 => "Allow failed",
        105 => "Abandoned",
        107 => "Block failed",
        _ => "Status " + id,
    };

    public enum ScanKind { Quick = 1, Full = 2, File = 3 }

    /// <summary>Runs a Defender scan and waits for it. Returns (threatsFound, message). Call off the UI thread.</summary>
    public static (bool threats, string message) Scan(ScanKind kind, string path = null, CancellationToken cancel = default)
    {
        if (!File.Exists(MpCmdRun)) return (false, "Microsoft Defender isn't installed or isn't your active antivirus.");
        var args = $"-Scan -ScanType {(int)kind}" + (kind == ScanKind.File ? $" -File \"{path}\"" : "");
        Process proc = null;
        using var reg = cancel.Register(() =>
        {
            try { Shell.Run(MpCmdRun, "-Scan -Cancel", 30_000); } catch { }
            try { proc?.Kill(true); } catch { }
        });
        var r = Shell.Run(MpCmdRun, args, (int)TimeSpan.FromHours(6).TotalMilliseconds, p => proc = p);
        TweakEngine.AddLog("security", $"Ran a Defender {kind.ToString().ToLower()} scan" + (r.Code == 2 ? " — threats found" : ""));
        if (cancel.IsCancellationRequested) return (false, "Scan cancelled.");
        return r.Code switch
        {
            0 => (false, "No threats found."),
            2 => (true, "Defender found threats and handled them. See the history below for details."),
            _ => (false, CmdAction.FirstLine(r.Output + "\n" + r.Error) ?? $"The scan ended with code {r.Code}."),
        };
    }

    public static string UpdateSignatures()
    {
        if (!File.Exists(MpCmdRun)) return "Microsoft Defender isn't available.";
        var r = Shell.Run(MpCmdRun, "-SignatureUpdate", 600_000);
        TweakEngine.AddLog("security", "Updated Defender definitions");
        return r.Ok ? "Definitions are up to date." : CmdAction.FirstLine(r.Output + "\n" + r.Error) ?? "Update failed.";
    }

    /// <summary>Restarts the PC into Defender's offline scan (finds rootkits that hide while Windows runs).</summary>
    public static string StartOfflineScan()
    {
        var r = Shell.PowerShell("Start-MpWDOScan", 60_000);
        return r.Ok ? "Restarting into Microsoft Defender Offline scan…" : CmdAction.FirstLine(r.Error) ?? "Couldn't start the offline scan.";
    }

    /// <summary>Checks every common place malware uses to survive a reboot.</summary>
    public static List<PersistItem> PersistenceAudit()
    {
        const string script = @"
$ErrorActionPreference = 'SilentlyContinue'
$items = New-Object System.Collections.ArrayList
function Get-ExePath([string]$cmd) {
  if (-not $cmd) { return $null }
  $c = [Environment]::ExpandEnvironmentVariables($cmd.Trim())
  if ($c.StartsWith('""')) { $e = $c.IndexOf('""', 1); if ($e -gt 1) { return $c.Substring(1, $e - 1) } }
  $m = [regex]::Match($c, '^(.+?\.(exe|dll|bat|cmd|ps1|vbs|js|scr|com))', 'IgnoreCase')
  if ($m.Success) { return $m.Groups[1].Value }
  return ($c -split ' ')[0]
}
function Add-Item($type, $name, $cmd, $tech) {
  [void]$items.Add([pscustomobject]@{ Type = $type; Name = [string]$name; Command = [string]$cmd; Path = (Get-ExePath $cmd); Technique = $tech })
}
$skip = 'PSPath','PSParentPath','PSChildName','PSDrive','PSProvider','(default)'

# T1547.001 Run / RunOnce keys, including policy Run keys
$runKeys = @(
  'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run','HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce',
  'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run','HKLM:\Software\Microsoft\Windows\CurrentVersion\RunOnce',
  'HKLM:\Software\Microsoft\Windows\CurrentVersion\RunOnceEx',
  'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce',
  'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run','HKLM:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run',
  'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunServices','HKLM:\Software\Microsoft\Windows\CurrentVersion\RunServices')
foreach ($k in $runKeys) {
  $p = Get-ItemProperty -Path $k
  if ($p) { $p.PSObject.Properties | Where-Object { $skip -notcontains $_.Name } | ForEach-Object { Add-Item 'Startup registry key' $_.Name ([string]$_.Value) 'T1547.001' } }
}
$load = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Windows').load
if ($load) { Add-Item 'Legacy load value' 'load' $load 'T1547.001' }

# T1547.001 Startup folders
$sh = New-Object -ComObject WScript.Shell
foreach ($f in @([Environment]::GetFolderPath('Startup'), [Environment]::GetFolderPath('CommonStartup'))) {
  Get-ChildItem -LiteralPath $f -File | Where-Object Name -ne 'desktop.ini' | ForEach-Object {
    $cmd = $_.FullName
    if ($_.Extension -eq '.lnk') { $l = $sh.CreateShortcut($_.FullName); $cmd = ('""' + $l.TargetPath + '"" ' + $l.Arguments).Trim() }
    Add-Item 'Startup folder' $_.Name $cmd 'T1547.001'
  }
}

# T1547.004 Winlogon helper hijacks
$wl = Get-ItemProperty 'HKLM:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon'
if ($wl.Shell -and $wl.Shell.Trim() -ne 'explorer.exe') { Add-Item 'Winlogon shell (changed!)' 'Shell' $wl.Shell 'T1547.004' }
if ($wl.Userinit -and ($wl.Userinit.Trim().TrimEnd(',') -notmatch '^C:\\Windows\\system32\\userinit\.exe$')) { Add-Item 'Winlogon userinit (changed!)' 'Userinit' $wl.Userinit 'T1547.004' }

# T1053.005 Scheduled tasks outside \Microsoft\
Get-ScheduledTask | Where-Object { $_.TaskPath -notlike '\Microsoft\*' } | ForEach-Object {
  $t = $_
  foreach ($a in @($t.Actions)) { if ($a.Execute) { Add-Item 'Scheduled task' ($t.TaskPath + $t.TaskName) (('""' + $a.Execute.Trim('""') + '"" ' + $a.Arguments).Trim()) 'T1053.005' } }
}

# T1543.003 Services running from outside Windows' own folder
Get-CimInstance Win32_Service | Where-Object { $_.PathName -and ([Environment]::ExpandEnvironmentVariables($_.PathName) -notmatch '^""?[A-Za-z]:\\Windows\\') } | ForEach-Object {
  Add-Item 'Service' $_.DisplayName $_.PathName 'T1543.003'
}

# T1546.003 WMI event subscriptions
Get-CimInstance -Namespace root\subscription -ClassName CommandLineEventConsumer | ForEach-Object { Add-Item 'WMI subscription (command)' $_.Name $_.CommandLineTemplate 'T1546.003' }
Get-CimInstance -Namespace root\subscription -ClassName ActiveScriptEventConsumer | ForEach-Object { Add-Item 'WMI subscription (script)' $_.Name ('script: ' + $_.ScriptingEngine) 'T1546.003' }

$results = foreach ($i in $items) {
  $flags = @(); $sig = 'Unknown'; $pub = ''; $exists = $false; $p = $i.Path
  if ($p -and -not [IO.Path]::IsPathRooted($p)) { $w = Get-Command $p -CommandType Application | Select-Object -First 1; if ($w) { $p = $w.Source } }
  if ($p -and (Test-Path -LiteralPath $p -PathType Leaf)) {
    $exists = $true
    $s = Get-AuthenticodeSignature -LiteralPath $p
    $sig = [string]$s.Status
    if ($s.SignerCertificate) { $pub = $s.SignerCertificate.GetNameInfo('SimpleName', $false) }
    if ($s.Status -ne 'Valid') { $flags += 'Not digitally signed' }
  } elseif ($p) { $flags += 'File is missing (leftover entry)' }
  $lc = ([string]$i.Command).ToLower()
  if ($lc -match '\\temp\\|\\appdata\\local\\temp|\\downloads\\|\\users\\public\\|\\\$recycle') { $flags += '!Runs from a temporary, download or public folder' }
  if ($lc -match 'powershell|pwsh') { if ($lc -match '-enc|-encodedcommand|frombase64|iex|invoke-expression|downloadstring|downloadfile|-w(indowstyle)?\s+h') { $flags += '!Hidden or encoded PowerShell command' } }
  if ($lc -match 'mshta|wscript|cscript|regsvr32\s+.*/i:http|rundll32\s+.*(javascript|http)|certutil.*-urlcache|bitsadmin') { $flags += '!Uses a Windows tool often abused by malware' }
  if ($lc -match 'https?://') { $flags += '!Contacts a web address when it runs' }
  if ($i.Type -like 'WMI*' -or $i.Type -like 'Winlogon*') { $flags += '!Rare startup method mostly used by malware' }
  $sha = ''
  if ($exists -and $flags.Count -gt 0) { $sha = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }
  [pscustomobject]@{ Type=$i.Type; Name=$i.Name; Command=$i.Command; Path=[string]$p; Exists=$exists; Signature=$sig; Publisher=$pub; Flags=@($flags); Sha256=$sha; Technique=$i.Technique }
}
ConvertTo-Json -Depth 4 -Compress -InputObject @($results)";
        var r = Shell.PowerShell(script, 600_000);
        var list = new List<PersistItem>();
        try
        {
            using var doc = JsonDocument.Parse(r.Output.Trim().Length == 0 ? "[]" : r.Output);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var flags = new List<string>();
                if (e.TryGetProperty("Flags", out var f))
                {
                    if (f.ValueKind == JsonValueKind.Array) flags.AddRange(f.EnumerateArray().Select(x => x.GetString()));
                    else if (f.ValueKind == JsonValueKind.String) flags.Add(f.GetString());
                }
                list.Add(new PersistItem
                {
                    Type = S(e, "Type"), Name = S(e, "Name"), Command = S(e, "Command"), Path = S(e, "Path"),
                    Exists = e.TryGetProperty("Exists", out var x) && x.ValueKind == JsonValueKind.True,
                    Signature = S(e, "Signature"), Publisher = S(e, "Publisher"), Sha256 = S(e, "Sha256"),
                    Technique = S(e, "Technique"), Flags = flags,
                });
            }
        }
        catch { }
        TweakEngine.AddLog("security", $"Ran the startup check: {list.Count(i => i.Level == Severity.Problem)} suspicious, {list.Count(i => i.Level == Severity.Check)} to review");
        return list.OrderBy(i => i.Level == Severity.Problem ? 0 : i.Level == Severity.Check ? 1 : 2).ThenBy(i => i.Name).ToList();
    }

    static string S(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
}
