using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Microsoft.Win32;
using Pulse.Ui;

namespace Pulse.Core.Declutter;

public record StorageSlice(string Name, long Bytes, string Color);

public class StorageMap
{
    public string Drive { get; init; } = "C:";
    public long Total { get; init; }
    public long Free { get; init; }
    public long Used => Total - Free;
    public List<StorageSlice> Slices { get; init; } = new();
}

/// <summary>A file the user reviews before it goes to the Recycle Bin.</summary>
public class ReviewFile : Observable
{
    public string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path);
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public string SizeText => Format.Bytes(Size);
    public string AgeText => $"Last changed {Format.Ago(Modified)}";
    bool _selected = true;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

public enum AppAdvice { Safe, Check, Keep }

public class AppEntry : Observable
{
    public string Name { get; init; }
    public string Publisher { get; init; } = "";
    public long Size { get; set; }
    public string Why { get; init; } = "";
    public AppAdvice Advice { get; init; }
    public bool IsStoreApp { get; init; }
    /// <summary>Store apps: package full name. Desktop apps: uninstall command.</summary>
    public string RemoveKey { get; init; }
    public DateTime? Installed { get; init; }
    /// <summary>Suggested (known bloat), Game, or Program.</summary>
    public string Kind { get; init; } = "Program";
    public bool Suggested { get; init; }
    public string IconPath { get; init; }
    public string InstallLocation { get; init; }
    public bool Wait { get; init; } = true;
    /// <summary>The program is already gone (its folder or uninstaller is missing); only its entry in Windows' list is left.</summary>
    public bool Leftover { get; init; }
    /// <summary>Where that entry lives, so a leftover can be removed from the list.</summary>
    public RegistryHive EntryHive { get; init; }
    public string EntryKey { get; init; }

    System.Windows.Media.ImageSource _icon;
    bool _iconLoaded;
    /// <summary>The program's own icon, loaded the first time it's shown.</summary>
    public System.Windows.Media.ImageSource Icon
    {
        get
        {
            if (!_iconLoaded)
            {
                _iconLoaded = true;
                var path = IconPath;
                Task.Run(() => IsStoreApp ? DeclutterEngine.StoreLogo(path) : Native.LargeIcon(path, 64))
                    .ContinueWith(t => { if (t.Result != null) { _icon = t.Result; Raise(nameof(Icon)); Raise(nameof(HasIcon)); } });
            }
            return _icon;
        }
    }
    public bool HasIcon => _icon != null;

    bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value && CanRemove); }

    bool _busy, _removed;
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public bool Removed { get => _removed; set { if (Set(ref _removed, value)) { Raise(nameof(CanRemove)); Selected = false; } } }
    public bool CanRemove => Advice != AppAdvice.Keep && !Removed && !string.IsNullOrEmpty(RemoveKey);

    public string SizeText => Leftover ? "0 B" : Size > 0 ? Format.Bytes(Size) : "";
    public string AdviceText => Leftover ? "Already removed" : !Suggested ? (Kind == "Game" ? "Game" : "") : Advice switch { AppAdvice.Safe => "Safe to remove", AppAdvice.Keep => "Keep", _ => "Check first" };
    public bool HasAdvice => AdviceText.Length > 0;
    public string Detail => string.Join("  ·  ", new[] { Why, Publisher, Installed is DateTime d ? $"Installed {d:MMM yyyy}" : "" }.Where(x => !string.IsNullOrEmpty(x)).Distinct());
    public Brush AdviceBrush => !Suggested ? Palette.Info : Advice switch { AppAdvice.Safe => Palette.Good, AppAdvice.Keep => Palette.Info, _ => Palette.Warn };
    public Brush AdviceBackground => !Suggested ? Palette.InfoSoft : Advice switch { AppAdvice.Safe => Palette.GoodSoft, AppAdvice.Keep => Palette.InfoSoft, _ => Palette.WarnSoft };
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
    public string Source => IsStoreApp ? "Microsoft Store app" : "Desktop program";
}

/// <summary>
/// Finds where disk space went and what can safely go. Rules:
/// your own files only ever go to the Recycle Bin; Windows' update files are cleaned with Microsoft's own DISM command;
/// nothing in WinSxS or the Windows folder is ever deleted by hand (Microsoft warns that can break Windows).
/// </summary>
public static class DeclutterEngine
{
    static readonly EnumerationOptions Deep = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static long FolderSize(string path, CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return 0;
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", Deep))
            {
                if (cancel.IsCancellationRequested) break;
                try { total += f.Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    static string Env(Environment.SpecialFolder f) { try { return Environment.GetFolderPath(f); } catch { return ""; } }

    static bool OnDrive(string path, string drive) =>
        path.Length > 2 && path.StartsWith(drive, StringComparison.OrdinalIgnoreCase);

    /// <summary>Breaks down the system drive. Takes up to a minute on big drives; call off the UI thread.</summary>
    public static StorageMap Map(long clutterBytes, IProgress<string> progress = null, CancellationToken cancel = default)
    {
        var drive = SystemInfo.SystemDrive;
        var letter = drive.Name.TrimEnd('\\');

        progress?.Report("Measuring your games…");
        var gameFolders = PcDiagnostics.GameInstallFolders()
            .Concat(new[] { $@"{letter}\Riot Games", $@"{letter}\XboxGames" })
            .Where(p => OnDrive(p, letter) && Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        long games = gameFolders.AsParallel().Sum(p => FolderSize(p, cancel));

        progress?.Report("Measuring your apps…");
        var appRoots = new[]
        {
            Env(Environment.SpecialFolder.ProgramFiles), Env(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Env(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        }.Where(p => OnDrive(p, letter)).Distinct(StringComparer.OrdinalIgnoreCase);
        long apps = appRoots.AsParallel().Sum(p => FolderSize(p, cancel));
        // Steam's default library lives inside Program Files (x86); don't count those games twice.
        apps -= gameFolders.Where(g => appRoots.Any(r => g.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                           .AsParallel().Sum(p => FolderSize(p, cancel));
        apps = Math.Max(apps, 0);

        progress?.Report("Measuring your files…");
        var userFolders = new[]
        {
            Env(Environment.SpecialFolder.Desktop), Env(Environment.SpecialFolder.MyDocuments), DownloadsFolder,
            Env(Environment.SpecialFolder.MyPictures), Env(Environment.SpecialFolder.MyMusic), Env(Environment.SpecialFolder.MyVideos),
            Environment.GetEnvironmentVariable("OneDrive") ?? "",
        }.Where(p => OnDrive(p, letter)).Distinct(StringComparer.OrdinalIgnoreCase);
        long files = userFolders.AsParallel().Sum(p => FolderSize(p, cancel));
        files -= Math.Min(files, gameFolders.Where(g => userFolders.Any(u => g.StartsWith(u, StringComparison.OrdinalIgnoreCase))).Sum(p => FolderSize(p, cancel)));

        long used = drive.TotalSize - drive.AvailableFreeSpace;
        long rest = Math.Max(used - games - apps - files - clutterBytes, 0);
        return new StorageMap
        {
            Drive = letter, Total = drive.TotalSize, Free = drive.AvailableFreeSpace,
            Slices = new()
            {
                new("Games", games, "#5B8CFF"),
                new("Apps", apps, "#22D3EE"),
                new("Your files", files, "#8B5CF6"),
                new("Windows and other", rest, "#5D6272"),
                new("Clutter", clutterBytes, "#F87171"),
            },
        };
    }

    public static string DownloadsFolder
    {
        get
        {
            // The Downloads folder has no SpecialFolder value; this is its documented Known Folder ID.
            try
            {
                var p = Reg.Get(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", "{374DE290-123F-4565-9164-39C4925E467B}") as string;
                if (!string.IsNullOrEmpty(p)) return Environment.ExpandEnvironmentVariables(p);
            }
            catch { }
            return Path.Combine(Env(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    // ───────── Files to review ─────────

    static readonly HashSet<string> InstallerExt = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".msi", ".msix", ".appx", ".zip", ".rar", ".7z", ".iso" };

    /// <summary>Installers and archives in Downloads older than 30 days.</summary>
    public static List<ReviewFile> OldInstallers()
    {
        var dir = DownloadsFolder;
        if (!Directory.Exists(dir)) return new();
        var cutoff = DateTime.Now.AddDays(-30);
        return SafeFiles(dir, new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System })
            .Where(f => InstallerExt.Contains(f.Extension) && f.LastWriteTime < cutoff)
            .Select(f => new ReviewFile { Path = f.FullName, Size = f.Length, Modified = f.LastWriteTime })
            .OrderByDescending(f => f.Size).ToList();
    }

    /// <summary>Files over 500 MB in your folders that haven't changed in 6 months. Unticked by default: you decide.</summary>
    public static List<ReviewFile> LargeOldFiles(CancellationToken cancel = default)
    {
        var cutoff = DateTime.Now.AddMonths(-6);
        var roots = new[]
        {
            Env(Environment.SpecialFolder.Desktop), Env(Environment.SpecialFolder.MyDocuments), DownloadsFolder,
            Env(Environment.SpecialFolder.MyVideos), Env(Environment.SpecialFolder.MyMusic), Env(Environment.SpecialFolder.MyPictures),
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        var games = PcDiagnostics.GameInstallFolders();
        var list = new List<ReviewFile>();
        foreach (var root in roots)
        {
            foreach (var f in SafeFiles(root, Deep))
            {
                if (cancel.IsCancellationRequested) return list;
                long len;
                try { len = f.Length; } catch { continue; }
                if (len < 500L * 1024 * 1024 || f.LastWriteTime > cutoff) continue;
                if (games.Any(g => f.FullName.StartsWith(g, StringComparison.OrdinalIgnoreCase))) continue;
                if (InstallerExt.Contains(f.Extension) && f.DirectoryName.Equals(DownloadsFolder, StringComparison.OrdinalIgnoreCase)) continue; // listed above
                list.Add(new ReviewFile { Path = f.FullName, Size = len, Modified = f.LastWriteTime, Selected = false });
            }
        }
        return list.OrderByDescending(f => f.Size).Take(60).ToList();
    }

    static IEnumerable<FileInfo> SafeFiles(string dir, EnumerationOptions o)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", o); }
        catch { return Enumerable.Empty<FileInfo>(); }
    }

    // ───────── Recycle Bin ─────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    /// <summary>
    /// Sends files to the Recycle Bin (never a permanent delete). Returns bytes moved.
    /// The shell's file operations need a single-threaded (STA) thread, so this runs on its own one.
    /// </summary>
    public static Task<long> RecycleAsync(IEnumerable<ReviewFile> files)
    {
        var list = files.ToList();
        var done = new TaskCompletionSource<long>();
        var th = new Thread(() =>
        {
            try { done.SetResult(Recycle(list)); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        return done.Task;
    }

    static long Recycle(IEnumerable<ReviewFile> files)
    {
        long moved = 0;
        foreach (var f in files)
        {
            if (!File.Exists(f.Path)) continue;
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = f.Path + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            if (SHFileOperation(ref op) == 0 && op.fAnyOperationsAborted == 0 && !File.Exists(f.Path)) moved += f.Size;
        }
        if (moved > 0) TweakEngine.AddLog("clean", $"Moved {Format.Bytes(moved)} of files to the Recycle Bin");
        return moved;
    }

    // ───────── Windows' own cleanup ─────────

    public static long HibernationFileSize()
    {
        try
        {
            var f = new FileInfo(Path.Combine(SystemInfo.SystemDrive.Name, "hiberfil.sys"));
            return f.Exists ? f.Length : 0;
        }
        catch { return 0; }
    }

    public static long WindowsOldSize(CancellationToken cancel = default) =>
        FolderSize(Path.Combine(SystemInfo.SystemDrive.Name, "Windows.old"), cancel);

    /// <summary>Asks DISM whether a component cleanup is worth it (about a minute). DISM doesn't report a reliable size, so only the verdict is used.</summary>
    public static (bool recommended, long bytes) AnalyzeComponentStore()
    {
        var dism = Path.Combine(Environment.SystemDirectory, "Dism.exe");
        var r = Shell.Run(dism, "/Online /Cleanup-Image /AnalyzeComponentStore", (int)TimeSpan.FromMinutes(5).TotalMilliseconds);
        var m = Regex.Match(r.Output, @"Cleanup Recommended\s*:\s*(\w+)", RegexOptions.IgnoreCase);
        if (m.Success) return (m.Groups[1].Value.Equals("Yes", StringComparison.OrdinalIgnoreCase), 0);
        return (r.Ok, 0); // other languages: offer it when the analysis ran
    }

    /// <summary>Runs Microsoft's supported component cleanup (no /ResetBase, so installed updates can still be removed).</summary>
    public static (bool ok, long freed, string message) CleanWindowsUpdateFiles()
    {
        var before = SystemInfo.SystemDrive.AvailableFreeSpace;
        var dism = Path.Combine(Environment.SystemDirectory, "Dism.exe");
        var r = Shell.Run(dism, "/Online /Cleanup-Image /StartComponentCleanup /Quiet", (int)TimeSpan.FromMinutes(40).TotalMilliseconds);
        var freed = Math.Max(SystemInfo.SystemDrive.AvailableFreeSpace - before, 0);
        TweakEngine.AddLog("clean", $"Ran Windows component cleanup: freed {Format.Bytes(freed)}");
        return r.Ok || r.Code == 3010
            ? (true, freed, freed > 0 ? $"Freed {Format.Bytes(freed)}." : "Windows' update files were already tidy.")
            : (false, freed, CmdAction.FirstLine(r.Output + "\n" + r.Error) ?? $"DISM ended with code {r.Code}.");
    }

    // ───────── Apps ─────────

    internal static readonly (string match, AppAdvice advice, string why)[] StoreBloat =
    {
        ("king.com", AppAdvice.Safe, "Game installed by Windows"),
        ("CandyCrush", AppAdvice.Safe, "Game installed by Windows"),
        ("Microsoft.MicrosoftSolitaireCollection", AppAdvice.Safe, "Card games with ads"),
        ("Microsoft.BingNews", AppAdvice.Safe, "News feed"),
        ("Microsoft.Getstarted", AppAdvice.Safe, "Windows tips"),
        ("Microsoft.MicrosoftOfficeHub", AppAdvice.Safe, "Shortcut to the Microsoft 365 website"),
        ("Microsoft.WindowsFeedbackHub", AppAdvice.Safe, "Sends feedback to Microsoft"),
        ("Microsoft.WindowsMaps", AppAdvice.Safe, "Offline maps"),
        ("Microsoft.ZuneVideo", AppAdvice.Safe, "Movies & TV"),
        ("Microsoft.People", AppAdvice.Safe, "Contacts app"),
        ("Microsoft.PowerAutomateDesktop", AppAdvice.Safe, "Automation tool for businesses"),
        ("Microsoft.549981C3F5F10", AppAdvice.Safe, "Cortana"),
        ("Microsoft.Windows.DevHome", AppAdvice.Safe, "Tools for software developers"),
        ("Microsoft.MixedReality.Portal", AppAdvice.Safe, "For VR headsets only"),
        ("Microsoft.Microsoft3DViewer", AppAdvice.Safe, "3D model viewer"),
        ("Microsoft.SkypeApp", AppAdvice.Safe, "Skype (discontinued)"),
        ("Disney", AppAdvice.Safe, "Streaming app shortcut installed by Windows"),
        ("TikTok", AppAdvice.Check, "Social app installed by Windows"),
        ("BytedancePte", AppAdvice.Check, "Social app installed by Windows"),
        ("Facebook", AppAdvice.Check, "Social app installed by Windows"),
        ("Instagram", AppAdvice.Check, "Social app installed by Windows"),
        ("AmazonVideo", AppAdvice.Check, "Prime Video"),
        ("Netflix", AppAdvice.Check, "Netflix"),
        ("SpotifyAB", AppAdvice.Check, "Spotify (keep it if you use it)"),
        ("Clipchamp", AppAdvice.Check, "Video editor"),
        ("Microsoft.BingWeather", AppAdvice.Check, "Weather"),
        ("Microsoft.Todos", AppAdvice.Check, "To-do list"),
        ("Microsoft.GetHelp", AppAdvice.Check, "Microsoft support chat"),
        ("Microsoft.YourPhone", AppAdvice.Check, "Phone Link"),
        ("MicrosoftTeams", AppAdvice.Check, "Teams (personal)"),
        ("MSTeams", AppAdvice.Check, "Teams"),
        ("Microsoft.MicrosoftStickyNotes", AppAdvice.Check, "Sticky Notes"),
        ("Microsoft.WindowsSoundRecorder", AppAdvice.Check, "Sound Recorder"),
        ("MicrosoftCorporationII.QuickAssist", AppAdvice.Check, "Lets someone control your PC to help you"),
        // Xbox Identity Provider, Xbox Live in-game (TCUI) and Gaming Services are never offered: Game Pass, Minecraft
        // and Xbox PC games stop signing in or starting without them.
        ("Microsoft.GamingApp", AppAdvice.Keep, "Xbox app: needed for Game Pass games"),
        ("Microsoft.XboxGamingOverlay", AppAdvice.Keep, "Game Bar: some games use it"),
        ("Microsoft.XboxApp", AppAdvice.Safe, "Xbox Console Companion (old app Microsoft replaced)"),
        ("Microsoft.XboxSpeechToTextOverlay", AppAdvice.Check, "Xbox chat speech-to-text"),
    };

    internal static readonly (string match, AppAdvice advice, string why)[] DesktopBloat =
    {
        ("McAfee", AppAdvice.Safe, "Often a preinstalled trial. Windows Security already protects you"),
        ("Norton", AppAdvice.Check, "Often a preinstalled trial. Windows Security already protects you"),
        ("WildTangent", AppAdvice.Safe, "Preinstalled game launcher"),
        ("Booking.com", AppAdvice.Safe, "Preinstalled shortcut"),
        ("ExpressVPN", AppAdvice.Check, "Often preinstalled. Keep it if you pay for it"),
        ("Dropbox Promotion", AppAdvice.Safe, "Preinstalled offer"),
        ("CCleaner", AppAdvice.Check, "Pulse covers cleanup; known to push offers"),
        ("Driver Booster", AppAdvice.Safe, "Driver updaters often bundle adware; get drivers from the maker instead"),
        ("Driver Easy", AppAdvice.Safe, "Driver updaters often bundle adware; get drivers from the maker instead"),
        ("Advanced SystemCare", AppAdvice.Safe, "System 'optimizers' like this mostly apply placebo tweaks"),
        ("WinZip", AppAdvice.Check, "Windows opens zip files on its own"),
    };

    static readonly string[] GamePublishers =
    {
        "Valve", "Riot Games", "Epic Games", "Blizzard", "Ubisoft", "Electronic Arts", "EA ", "Activision", "Bethesda", "Rockstar",
        "Roblox", "Mojang", "NetEase", "HoYoverse", "miHoYo", "Bandai Namco", "CAPCOM", "SEGA", "Square Enix", "2K", "Krafton",
        "Embark", "Respawn", "Bungie", "Behaviour", "Paradox", "CD PROJEKT", "FromSoftware", "Larian", "Supercell", "Wargaming",
    };

    /// <summary>
    /// Every removable app on this PC: known preinstalled extras (marked Suggested), games, and all other desktop programs,
    /// each with its size. Takes a little while: call off the UI thread.
    /// </summary>
    public static List<AppEntry> Apps()
    {
        var list = new List<AppEntry>();
        var launcherGames = PcDiagnostics.LauncherGames();

        // Store apps from the known list (removed per user with Remove-AppxPackage; reinstallable from the Microsoft Store).
        try
        {
            var r = Shell.PowerShell("ConvertTo-Json -Compress -InputObject @(Get-AppxPackage | Where-Object { -not $_.IsFramework -and $_.SignatureKind -ne 'System' } | ForEach-Object { [pscustomobject]@{ N = $_.Name; F = $_.PackageFullName; L = [string]$_.InstallLocation } })", 90_000);
            using var doc = JsonDocument.Parse(r.Output.Trim().Length == 0 ? "[]" : r.Output);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var name = e.GetProperty("N").GetString() ?? "";
                var rule = StoreBloat.FirstOrDefault(b => name.Contains(b.match, StringComparison.OrdinalIgnoreCase));
                if (rule.match == null) continue;
                var loc = e.GetProperty("L").GetString();
                list.Add(new AppEntry
                {
                    Name = FriendlyStoreName(name), Advice = rule.advice, Why = rule.why, IsStoreApp = true, Suggested = true, Kind = "Suggested",
                    RemoveKey = e.GetProperty("F").GetString(), Size = FolderSize(loc), IconPath = loc, InstallLocation = loc,
                });
            }
        }
        catch { }

        // Desktop programs and games, from Windows' installed-programs list.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, path) in new[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
        {
            foreach (var sub in Reg.SubKeys(hive, path))
            {
                var k = $@"{path}\{sub}";
                if (Reg.Get(hive, k, "DisplayName") is not string name || name.Length == 0) continue;
                if (Reg.Get(hive, k, "SystemComponent") is int sc && sc == 1) continue;
                if (Reg.Get(hive, k, "ParentKeyName") is string) continue; // updates and sub-components
                if (Reg.Get(hive, k, "ReleaseType") is string rt && rt.Contains("Update", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsRuntime(name)) continue;
                var uninstall = (Reg.Get(hive, k, "QuietUninstallString") ?? Reg.Get(hive, k, "UninstallString")) as string;
                if (string.IsNullOrEmpty(uninstall) || !seen.Add(name)) continue;

                var publisher = Reg.Get(hive, k, "Publisher") as string ?? "";
                var loc = (Reg.Get(hive, k, "InstallLocation") as string ?? "").Trim('"');
                long size = Reg.Get(hive, k, "EstimatedSize") is int kb ? kb * 1024L : 0;
                // Deleted by hand (common with repacks): the folder or the uninstaller is gone, only the entry is left.
                bool leftover = (loc.Length > 0 && !Directory.Exists(loc)) || UninstallerMissing(uninstall);
                DateTime? date = DateTime.TryParseExact(Reg.Get(hive, k, "InstallDate") as string, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
                var rule = DesktopBloat.FirstOrDefault(b => name.Contains(b.match, StringComparison.OrdinalIgnoreCase));
                bool isGame = sub.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase)
                              || launcherGames.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                              || GamePublishers.Any(gp => publisher.StartsWith(gp, StringComparison.OrdinalIgnoreCase)) && !IsLauncher(name);

                if (leftover)
                {
                    list.Add(new AppEntry
                    {
                        Name = name, Publisher = publisher, Installed = date, InstallLocation = loc, Size = 0,
                        Kind = "Leftover", Advice = AppAdvice.Safe, Leftover = true, EntryHive = hive, EntryKey = k,
                        Why = "Already removed: only its entry in Windows' list of programs is left. Removing it uses no uninstaller and frees nothing.",
                        RemoveKey = uninstall, Wait = false,
                    });
                    continue;
                }
                list.Add(new AppEntry
                {
                    Name = name, Publisher = publisher, Installed = date, InstallLocation = loc,
                    Suggested = rule.match != null, Advice = rule.match != null ? rule.advice : AppAdvice.Check, Why = rule.why ?? "",
                    Kind = rule.match != null ? "Suggested" : isGame ? "Game" : "Program",
                    RemoveKey = PassiveMsi(uninstall), Size = size,
                    IconPath = Environment.ExpandEnvironmentVariables(Reg.Get(hive, k, "DisplayIcon") as string ?? ""),
                    // Steam hands the uninstall to its own window and returns at once.
                    Wait = !uninstall.Contains("steam://", StringComparison.OrdinalIgnoreCase),
                });
            }
        }

        // Epic games have no uninstall entry: list them so their size shows, removed from the Epic Games Launcher.
        foreach (var (name, folder) in launcherGames.Where(g => !seen.Contains(g.Name) && !list.Any(a => a.Name == g.Name)))
        {
            if (!folder.Contains("Epic", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(Path.Combine(folder, ".egstore"))) continue;
            list.Add(new AppEntry { Name = name, Kind = "Game", Why = "Uninstall it from the Epic Games Launcher", InstallLocation = folder, Advice = AppAdvice.Check });
        }

        // Fill in sizes Windows doesn't know (common for games). A folder several entries point at (like D:\Games)
        // isn't any one program's size, so it isn't measured for them.
        var shared = list.Where(a => !string.IsNullOrEmpty(a.InstallLocation))
            .GroupBy(a => a.InstallLocation.TrimEnd('\\').ToUpperInvariant()).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        Parallel.ForEach(list.Where(a => a.Size == 0 && !a.Leftover && !string.IsNullOrEmpty(a.InstallLocation) && Directory.Exists(a.InstallLocation)
                                         && !shared.Contains(a.InstallLocation.TrimEnd('\\').ToUpperInvariant())).ToList(),
            new ParallelOptions { MaxDegreeOfParallelism = 4 }, a => a.Size = FolderSize(a.InstallLocation));

        return list.OrderBy(a => a.Suggested ? (a.Advice switch { AppAdvice.Safe => 0, AppAdvice.Check => 1, _ => 2 }) : 3).ThenByDescending(a => a.Size).ToList();
    }

    static bool IsLauncher(string name) =>
        new[] { "Steam", "Epic Games Launcher", "Battle.net", "Ubisoft Connect", "EA app", "Riot Client", "Rockstar Games Launcher" }
            .Any(l => name.Equals(l, StringComparison.OrdinalIgnoreCase));

    /// <summary>Shared runtimes other programs depend on. Removing them breaks those programs, so they're never listed.</summary>
    internal static bool IsRuntime(string name) =>
        new[] { "Microsoft Visual C++", "Microsoft .NET", "Microsoft Windows Desktop Runtime", "Microsoft ASP.NET", "DirectX", "Microsoft Edge WebView2",
                "NVIDIA ", "AMD Software", "AMD Chipset", "Intel(R) ", "Realtek", "Microsoft Update Health", "Windows SDK", "Windows Software Development Kit",
                "Microsoft GameInput", "Microsoft XNA", "Vulkan Run", "PhysX", "Easy Anti-Cheat", "EasyAntiCheat", "BattlEye", "Riot Vanguard", "Microsoft Edge" }
            .Any(r => name.StartsWith(r, StringComparison.OrdinalIgnoreCase));

    /// <summary>"MsiExec.exe /I{GUID}" opens a repair dialog; /X with /passive uninstalls with a progress bar and no questions.</summary>
    internal static string PassiveMsi(string cmd)
    {
        var m = Regex.Match(cmd ?? "", @"msiexec(\.exe)?\s+/[IX]\s*(\{[0-9A-Fa-f-]+\})", RegexOptions.IgnoreCase);
        return m.Success ? $"MsiExec.exe /X{m.Groups[2].Value} /passive /norestart" : cmd;
    }

    /// <summary>A Store app's logo from its manifest, picking the closest scaled image.</summary>
    internal static System.Windows.Media.ImageSource StoreLogo(string installLocation)
    {
        try
        {
            var manifest = Path.Combine(installLocation, "AppxManifest.xml");
            if (!File.Exists(manifest)) return null;
            var xml = File.ReadAllText(manifest);
            var m = Regex.Match(xml, @"Square44x44Logo=""([^""]+)""") is { Success: true } sq ? sq : Regex.Match(xml, @"<Logo>([^<]+)</Logo>");
            if (!m.Success) return null;
            var rel = m.Groups[1].Value;
            var dir = Path.Combine(installLocation, Path.GetDirectoryName(rel) ?? "");
            var stem = Path.GetFileNameWithoutExtension(rel);
            if (!Directory.Exists(dir)) return null;
            var candidates = Directory.GetFiles(dir, stem + "*.png")
                .OrderBy(f => f.Contains("contrast", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(f => f.Contains("targetsize-48") || f.Contains("scale-200") ? 0 : f.Contains("targetsize-64") || f.Contains("scale-150") ? 1 : 2)
                .ToList();
            var file = candidates.FirstOrDefault(f => !f.Contains("contrast", StringComparison.OrdinalIgnoreCase)) ?? candidates.FirstOrDefault();
            if (file == null) return null;
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(file);
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 64;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    internal static string FriendlyStoreName(string package)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.MicrosoftSolitaireCollection"] = "Microsoft Solitaire Collection",
            ["Microsoft.BingNews"] = "Microsoft News", ["Microsoft.BingWeather"] = "Weather",
            ["Microsoft.Getstarted"] = "Tips", ["Microsoft.MicrosoftOfficeHub"] = "Microsoft 365 (Office) hub",
            ["Microsoft.WindowsFeedbackHub"] = "Feedback Hub", ["Microsoft.WindowsMaps"] = "Maps",
            ["Microsoft.ZuneVideo"] = "Movies & TV", ["Microsoft.People"] = "People",
            ["Microsoft.PowerAutomateDesktop"] = "Power Automate", ["Microsoft.549981C3F5F10"] = "Cortana",
            ["Microsoft.Windows.DevHome"] = "Dev Home", ["Microsoft.Todos"] = "Microsoft To Do",
            ["Microsoft.GetHelp"] = "Get Help", ["Microsoft.YourPhone"] = "Phone Link",
            ["Microsoft.MicrosoftStickyNotes"] = "Sticky Notes", ["Microsoft.WindowsSoundRecorder"] = "Sound Recorder",
            ["MicrosoftCorporationII.QuickAssist"] = "Quick Assist", ["Microsoft.GamingApp"] = "Xbox",
            ["Microsoft.XboxGamingOverlay"] = "Xbox Game Bar", ["Clipchamp.Clipchamp"] = "Clipchamp",
            ["Microsoft.XboxApp"] = "Xbox Console Companion", ["Microsoft.XboxSpeechToTextOverlay"] = "Xbox speech to text",
            ["Microsoft.Xbox.TCUI"] = "Xbox Live in-game", ["Microsoft.XboxIdentityProvider"] = "Xbox Identity Provider",
            ["Microsoft.GamingServices"] = "Gaming Services",
            ["Microsoft.MixedReality.Portal"] = "Mixed Reality Portal", ["Microsoft.Microsoft3DViewer"] = "3D Viewer",
            ["Microsoft.SkypeApp"] = "Skype", ["MSTeams"] = "Microsoft Teams", ["MicrosoftTeams"] = "Microsoft Teams (personal)",
        };
        if (known.TryGetValue(package, out var n)) return n;
        if (package.Contains("CandyCrush", StringComparison.OrdinalIgnoreCase)) return "Candy Crush";
        // e.g. "SpotifyAB.SpotifyMusic" → "Spotify Music"
        var last = package.Split('.').Last();
        return Regex.Replace(last, "(?<=[a-z])(?=[A-Z])", " ");
    }

    /// <summary>Removes a Store app for this user (reinstall any time from the Microsoft Store).</summary>
    public static (bool ok, string message) RemoveStoreApp(AppEntry app)
    {
        var name = app.RemoveKey.Replace("'", "''");
        var r = Shell.PowerShell($"Remove-AppxPackage -Package '{name}' -ErrorAction Stop; 'OK'", 180_000);
        bool ok = r.Output.Trim().EndsWith("OK");
        if (ok) TweakEngine.AddLog("apps", $"Removed {app.Name} (reinstall from the Microsoft Store any time)");
        return (ok, ok ? "" : CmdAction.FirstLine(r.Error) ?? "Windows didn't allow removing this app.");
    }

    /// <summary>True when the uninstall command points at an exe that no longer exists (Windows Installer entries are checked by folder instead).</summary>
    internal static bool UninstallerMissing(string uninstall)
    {
        if (string.IsNullOrWhiteSpace(uninstall)) return false;
        var u = Environment.ExpandEnvironmentVariables(uninstall.Trim());
        if (u.Contains("msiexec", StringComparison.OrdinalIgnoreCase) || u.Contains("://")) return false;
        string exe = u.StartsWith('"') ? u[1..Math.Max(1, u.IndexOf('"', 1))] : (u.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) is int i and >= 0 ? u[..(i + 4)] : u);
        if (!Path.IsPathRooted(exe)) return false; // a command like "rundll32 ..." on the PATH: can't tell
        return !File.Exists(exe);
    }

    /// <summary>
    /// Removes a leftover entry from Windows' list of programs. Its registry key is exported to Pulse's backup folder
    /// first (double-click the .reg file to put it back).
    /// </summary>
    public static (bool ok, string message) RemoveLeftover(AppEntry app)
    {
        try
        {
            var file = Reg.ExportAndDeleteKey(app.EntryHive, app.EntryKey, Path.Combine(TweakEngine.DataDir, "backups"), "program-" + app.Name);
            TweakEngine.AddLog("apps", $"Removed the leftover entry for {app.Name} (backup: {Path.GetFileName(file)})");
            return (true, "");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Runs the program's own uninstaller and waits for it to close (the user may need to click through it).</summary>
    public static (bool ok, string message) StartUninstaller(AppEntry app)
    {
        if (app.Leftover) return RemoveLeftover(app);
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Shell.CmdExe, "/c \"" + app.RemoveKey + "\"")
            { UseShellExecute = false, CreateNoWindow = true });
            if (app.Wait) p?.WaitForExit((int)TimeSpan.FromMinutes(30).TotalMilliseconds);
            TweakEngine.AddLog("apps", $"Ran the uninstaller for {app.Name}");
            return (true, "");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>True once the program no longer appears in Windows' installed-programs list.</summary>
    public static bool StillInstalled(AppEntry app) =>
        new[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        }.Any(h => Reg.SubKeys(h.Item1, h.Item2).Any(sub => Reg.Get(h.Item1, $@"{h.Item2}\{sub}", "DisplayName") as string == app.Name));
}
