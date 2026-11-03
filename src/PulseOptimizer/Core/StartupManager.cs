using System.Diagnostics;
using System.Windows.Media;
using Microsoft.Win32;
using Pulse.Ui;

namespace Pulse.Core;

public class StartupItem : Observable
{
    public string Name { get; init; }
    public string Command { get; init; }
    public string ExePath { get; init; }
    public string Publisher { get; init; }
    public string SourceText { get; init; }
    public ImageSource Icon { get; init; }
    public string Advice { get; init; }
    public string AdviceKind { get; init; } // Keep, Safe, Unknown
    public bool HasIcon => Icon != null;
    public string PublisherText => string.IsNullOrEmpty(Publisher) ? "Unknown publisher" : Publisher;

    internal RegistryHive ApprovedHive { get; init; }
    internal string ApprovedKey { get; init; }
    internal string ApprovedValue { get; init; }

    bool _enabled, _busy;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool IsBusy { get => _busy; set => Set(ref _busy, value); }

    public Brush AdviceBrush => AdviceKind switch { "Keep" => Palette.Info, "Safe" => Palette.Good, _ => Palette.Warn };
    public Brush AdviceBackground => AdviceKind switch { "Keep" => Palette.InfoSoft, "Safe" => Palette.GoodSoft, _ => Palette.WarnSoft };
    public string AdviceLabel => AdviceKind switch { "Keep" => "Keep on", "Safe" => "Safe to turn off", _ => "Check first" };
}

/// <summary>
/// Reads and toggles startup programs exactly the way Task Manager does: via the StartupApproved keys,
/// so nothing is deleted and Task Manager shows the same state.
/// </summary>
public static class StartupManager
{
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    static readonly (RegistryHive hive, string run, string approved, string label)[] RegSources =
    {
        (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", Approved + @"\Run", "Starts for you"),
        (RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", Approved + @"\Run", "Starts for all users"),
        (RegistryHive.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", Approved + @"\Run32", "Starts for all users (32-bit)"),
    };

    static readonly (Environment.SpecialFolder folder, RegistryHive hive, string label)[] FolderSources =
    {
        (Environment.SpecialFolder.Startup, RegistryHive.CurrentUser, "Startup folder (you)"),
        (Environment.SpecialFolder.CommonStartup, RegistryHive.LocalMachine, "Startup folder (all users)"),
    };

    public static List<StartupItem> Load()
    {
        var list = new List<StartupItem>();
        foreach (var (hive, run, approved, label) in RegSources)
        {
            foreach (var name in Reg.ValueNames(hive, run))
            {
                if (string.IsNullOrEmpty(name)) continue;
                var cmd = Reg.Get(hive, run, name)?.ToString() ?? "";
                list.Add(Make(name, cmd, label, hive, approved, name));
            }
        }
        foreach (var (folder, hive, label) in FolderSources)
        {
            string dir;
            try { dir = Environment.GetFolderPath(folder); } catch { continue; }
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir))
            {
                var fn = Path.GetFileName(file);
                if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(file) ?? file : file;
                list.Add(Make(Path.GetFileNameWithoutExtension(file), target, label, hive, Approved + @"\StartupFolder", fn));
            }
        }
        return list.OrderByDescending(i => i.Enabled).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static StartupItem Make(string name, string cmd, string label, RegistryHive hive, string approvedKey, string approvedValue)
    {
        var exe = ExeFromCommand(cmd);
        string publisher = "";
        try { if (File.Exists(exe)) publisher = FileVersionInfo.GetVersionInfo(exe).CompanyName ?? ""; } catch { }
        var (kind, advice) = Advise(name + " " + cmd);
        return new StartupItem
        {
            Name = name, Command = cmd, ExePath = exe, Publisher = publisher.Trim(), SourceText = label,
            Icon = Native.FileIcon(exe), Advice = advice, AdviceKind = kind,
            ApprovedHive = hive, ApprovedKey = approvedKey, ApprovedValue = approvedValue,
            Enabled = IsEnabled(hive, approvedKey, approvedValue),
        };
    }

    static bool IsEnabled(RegistryHive hive, string key, string value)
    {
        // Odd first byte (03, 07) = disabled; even (02, 06) or missing = enabled.
        return Reg.Get(hive, key, value) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
    }

    public static void SetEnabled(StartupItem item, bool enabled)
    {
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        Reg.Set(item.ApprovedHive, item.ApprovedKey, item.ApprovedValue, data, RegistryValueKind.Binary);
        item.Enabled = enabled;
        TweakEngine.AddLog("startup", $"{(enabled ? "Turned on" : "Turned off")} startup app “{item.Name}”");
    }

    public static string ExeFromCommand(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return "";
        var c = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            if (end > 1) return c[1..end];
        }
        var i = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (i > 0) return c[..(i + 4)];
        return c.Split(' ')[0];
    }

    static string ShortcutTarget(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return null;
            dynamic shell = Activator.CreateInstance(t);
            dynamic sc = shell.CreateShortcut(lnk);
            string target = sc.TargetPath;
            string args = sc.Arguments;
            return string.IsNullOrEmpty(target) ? null : $"\"{target}\" {args}".Trim();
        }
        catch { return null; }
    }

    static readonly (string match, string kind, string advice)[] Known =
    {
        ("securityhealth", "Keep", "Windows Security's tray icon. Keep it on."),
        ("vgtray", "Keep", "Riot Vanguard anti-cheat. Valorant and League need it to start with Windows."),
        ("vanguard", "Keep", "Riot Vanguard anti-cheat. Valorant and League need it to start with Windows."),
        ("rtkaud", "Keep", "Realtek audio. Keep it so your audio controls and jack detection work."),
        ("realtek", "Keep", "Realtek audio. Keep it so your audio controls and jack detection work."),
        ("nahimic", "Keep", "Audio enhancement software from your motherboard maker. Keep if you use its sound features."),
        ("lghub", "Keep", "Logitech G HUB. Keep if you use custom DPI, macros or lighting."),
        ("icue", "Keep", "Corsair iCUE. Keep if you use custom lighting, fan curves or macros."),
        ("razer", "Keep", "Razer Synapse. Keep if you use custom DPI, macros or lighting."),
        ("steelseries", "Keep", "SteelSeries GG. Keep if you use its device settings."),
        ("onedrive", "Keep", "OneDrive sync. Keep if you rely on it to back up files; otherwise safe to turn off."),
        ("discord", "Safe", "Safe to turn off. Discord opens in a few seconds when you start it yourself."),
        ("steam", "Safe", "Safe to turn off. Steam updates your games when you open it."),
        ("epicgameslauncher", "Safe", "Safe to turn off. Open Epic yourself when you want to play."),
        ("eadesktop", "Safe", "Safe to turn off. The EA app only needs to run while you play EA games."),
        ("battle.net", "Safe", "Safe to turn off. Open Battle.net when you want to play."),
        ("ubisoft", "Safe", "Safe to turn off. Ubisoft Connect opens automatically when you launch its games."),
        ("upc.exe", "Safe", "Safe to turn off. Ubisoft Connect opens automatically when you launch its games."),
        ("riotclient", "Safe", "Safe to turn off. The Riot Client opens when you launch the game (keep Vanguard on)."),
        ("spotify", "Safe", "Safe to turn off. Spotify opens quickly when you start it."),
        ("teams", "Safe", "Safe to turn off unless you need Teams messages as soon as you sign in."),
        ("skype", "Safe", "Safe to turn off."),
        ("msedge", "Safe", "Edge 'startup boost'. Safe to turn off; Edge just opens a moment slower."),
        ("opera", "Safe", "Browser auto-start. Safe to turn off."),
        ("medal", "Safe", "Medal clipping. Safe to turn off if you don't need clips recorded automatically."),
        ("overwolf", "Safe", "Safe to turn off unless you use Overwolf overlays every session."),
        ("adobe", "Safe", "An Adobe updater or helper. Safe to turn off."),
        ("ccleaner", "Safe", "Safe to turn off."),
        ("update", "Safe", "An updater. Usually safe to turn off; the app still updates when you open it."),
    };

    static (string kind, string advice) Advise(string text)
    {
        var t = text.ToLowerInvariant();
        foreach (var (match, kind, advice) in Known)
            if (t.Contains(match)) return (kind, advice);
        return ("Unknown", "Not a program Pulse recognizes. Check the publisher below; if it's unsigned or you don't recognize it, run the startup check on the Security page.");
    }
}
