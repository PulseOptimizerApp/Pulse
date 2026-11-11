using System.Text.RegularExpressions;
using Xunit;

namespace PulseOptimizer.Tests;

/// <summary>
/// These tests read Pulse's own source code and fail the build if it ever breaks its promises:
/// no uploading data, no hidden downloads, no deleting outside the cleanup page, no touching game processes.
/// </summary>
public class SafetyTests
{
    static void AssertNotUsed(string pattern, string why, params string[] allowedFiles)
    {
        var rx = new Regex(pattern);
        foreach (var file in Repo.SourceFiles())
        {
            if (allowedFiles.Contains(Path.GetFileName(file))) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("//")) continue;
                Assert.False(rx.IsMatch(line), $"{Path.GetFileName(file)} line {i + 1}: {why}\n{line}");
            }
        }
    }

    [Fact]
    public void Has_no_network_code() =>
        AssertNotUsed(@"\b(HttpClient|WebClient|WebRequest|TcpClient|UdpClient|Socket|ClientWebSocket)\b",
            "Pulse never sends anything, and only downloads from its own GitHub releases: the opt-in Clips download of " +
            "FFmpeg (Ffmpeg.cs) and the update check and install (Updater.cs). The lag checks only send pings (NetworkCheck.cs). " +
            "Match recaps read the games' own data: League's Live Client API on 127.0.0.1 (LeagueLive.cs) and, for VALORANT, " +
            "your own last match from Riot with the Riot Client's local login (ValorantMatches.cs), plus the opt-in live lobby " +
            "(the players in your current match and their ranks, from Riot the same way: ValorantLobby.cs). The PC guide downloads " +
            "its latest prices and news from Pulse's own website (GuideLive.cs). The VALORANT store reads your own store from Riot " +
            "the same way, with skin names and pictures from valorant-api.com (ValorantStore.cs). The driver check asks " +
            "NVIDIA's public driver lookup for the newest Game Ready version (DriverCheck.cs). The speed test downloads and uploads " +
            "random filler to Cloudflare's public speed-test servers (SpeedTest.cs). The Mouse page downloads device pictures " +
            "from OpenMouse's image server, only from img.openmouse.app and openmouse.app (DeviceArt.cs). The Brawlhalla page reads " +
            "public player stats and leaderboards from Brawlhalla's official API at api.brawlhalla.com, portraits of legends newer than the " +
            "build from brawlhalla.wiki.gg, and Steam's public profile page (steamcommunity.com, ?xml=1) to turn a Steam link into a name (BrawlhallaApi.cs)",
            "Ffmpeg.cs", "Updater.cs", "LeagueLive.cs", "ValorantMatches.cs", "ValorantLobby.cs", "GuideLive.cs", "ValorantStore.cs", "DriverCheck.cs", "ValorantSearch.cs", "SpeedTest.cs", "DeviceArt.cs", "BrawlhallaApi.cs");

    [Fact]
    public void Player_search_only_reads_from_the_Riot_Client_and_Riot()
    {
        var src = File.ReadAllText(Repo.SourceFiles().Single(f => Path.GetFileName(f) == "ValorantSearch.cs"));
        var urls = Regex.Matches(src, @"https?://[^\s""]+").Select(m => m.Value).Distinct().ToList();
        Assert.All(urls, u => Assert.Matches(@"^https://(pd\.\{s\.Shard\}\.a\.pvp\.net/(match-history|match-details)/|api\.account\.riotgames\.com/aliases/)", u));
        Assert.DoesNotContain("HttpMethod.Post", src);
        Assert.DoesNotContain("HttpMethod.Put", src);
        Assert.DoesNotContain("PostAsync", src);
        Assert.DoesNotContain("PutAsync", src);
    }

    [Fact]
    public void The_store_only_reads_your_store_and_skin_pictures()
    {
        var src = File.ReadAllText(Repo.SourceFiles().Single(f => Path.GetFileName(f) == "ValorantStore.cs"));
        var urls = Regex.Matches(src, @"https?://[^\s""]+").Select(m => m.Value).Distinct().ToList();
        Assert.All(urls, u => Assert.Matches(@"^https://(pd\.\{s\.Shard\}\.a\.pvp\.net/store/|valorant-api\.com/v1/(bundles|weapons/skinlevels)/|valorant-api\.com/v1/weapons/skins$|media\.valorant-api\.com/)", u));
        // The one POST is the storefront read (Riot's store answers reads with POST); nothing is ever bought.
        Assert.Single(Regex.Matches(src, @"HttpMethod\.Post"));
        Assert.DoesNotContain("/order", src);
        Assert.DoesNotContain("PostAsync", src);
        Assert.DoesNotContain("PutAsync", src);
    }

    [Fact]
    public void The_speed_test_only_talks_to_Cloudflares_speed_test()
    {
        var src = File.ReadAllText(Repo.SourceFiles().Single(f => Path.GetFileName(f) == "SpeedTest.cs"));
        var urls = Regex.Matches(src, @"https?://[A-Za-z0-9./_-]+").Select(m => m.Value).Distinct().ToList();
        Assert.Equal(new[] { "https://speed.cloudflare.com" }, urls);
        var paths = Regex.Matches(src, @"\{Base\}(/[A-Za-z_]+)").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(paths);
        // __down / __up move test data; /meta is Cloudflare's read-only "who's testing" lookup (provider, IP, server city).
        Assert.All(paths, p => Assert.Matches("^/(__down|__up|meta)$", p));
    }

    [Fact]
    public void The_driver_check_only_asks_NVIDIAs_lookup()
    {
        var src = File.ReadAllText(Repo.SourceFiles().Single(f => Path.GetFileName(f) == "DriverCheck.cs"));
        var called = Regex.Matches(src, @"Get\w*Async\(([^,)]+)").Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();
        Assert.Equal(new[] { "NvidiaLookup" }, called);
        Assert.Contains("https://gfwsl.geforce.com/", src);
        Assert.DoesNotContain("PostAsync", src);
        Assert.DoesNotContain("SendAsync", src);
    }

    [Fact]
    public void The_guide_only_downloads_from_Pulses_own_website()
    {
        var src = File.ReadAllText(Repo.SourceFiles().Single(f => Path.GetFileName(f) == "GuideLive.cs"));
        Assert.DoesNotMatch(@"https?://", src); // the address is built from AppInfo.Website
        Assert.Contains("AppInfo.Website", src);
        Assert.DoesNotContain("PostAsync", src);
        Assert.DoesNotContain("PutAsync", src);
        Assert.DoesNotContain("SendAsync", src);
    }

    [Fact]
    public void Updates_and_downloads_only_come_from_Pulses_own_repos()
    {
        var info = File.ReadAllText(Repo.SourceFiles().Single(f => Path.GetFileName(f) == "AppInfo.cs"));
        Assert.Matches(@"Repo = ""[A-Za-z0-9-]+/[A-Za-z0-9-]+""", info);
        Assert.Contains(@"OldRepo = ""PulseOptimizer-app/PulseOptimizer""", info);
        foreach (var name in new[] { "Updater.cs", "Ffmpeg.cs" })
        {
            var src = File.ReadAllText(Repo.SourceFiles().Single(f => Path.GetFileName(f) == name));
            Assert.DoesNotMatch(@"github\.com/[A-Za-z]", src); // every GitHub URL is built from AppInfo's repos
        }
    }

    [Fact]
    public void Match_recaps_only_talk_to_this_PC_and_Riot()
    {
        foreach (var name in new[] { "LeagueLive.cs", "ValorantMatches.cs", "ValorantLobby.cs", "GsiServer.cs" })
        {
            var src = File.ReadAllText(Repo.SourceFiles().Single(f => Path.GetFileName(f) == name));
            var urls = Regex.Matches(src, @"https?://[^\s""]+").Select(m => m.Value).Distinct().ToList();
            Assert.All(urls, u => Assert.Matches(@"^https?://(127\.0\.0\.1[:/]|(pd|shared)\.\{s\.Shard\}\.a\.pvp\.net/|glz-)", u));
            Assert.DoesNotContain("PostAsync", src);
            Assert.DoesNotContain("PutAsync", src);
        }
    }

    [Fact]
    public void The_only_download_is_FFmpeg_from_this_projects_own_releases()
    {
        var src = File.ReadAllText(Repo.SourceFiles().Single(f => Path.GetFileName(f) == "Ffmpeg.cs"));
        var urls = Regex.Matches(src, @"https?://[^\s""{}]+").Select(m => m.Value).Distinct().ToList();
        Assert.All(urls, u => Assert.StartsWith("https://github.com/", u));
        Assert.Contains("AppInfo.Repo", src);
        Assert.Contains("SHA256", src); // checked before use
        Assert.DoesNotContain("PostAsync", src);
        Assert.DoesNotContain("PutAsync", src);
    }

    [Fact]
    public void Never_injects_into_or_reads_other_processes_memory()
    {
        AssertNotUsed(@"\b(OpenProcess|WriteProcessMemory|ReadProcessMemory|CreateRemoteThread|VirtualAllocEx)\b",
            "Pulse must never touch game or other processes' memory (anti-cheat safety)");
        // Hooks that load into other programs are never used. The one exception is the low-level mouse hook for
        // button remaps, which Windows runs inside Pulse's own process.
        AssertNotUsed(@"\bSetWindowsHookEx\b", "Pulse must never load hooks into other programs (anti-cheat safety)", "ButtonRemap.cs");
    }

    [Fact]
    public void The_button_remap_hook_is_only_the_low_level_mouse_hook()
    {
        var src = File.ReadAllText(Repo.SourceFiles().Single(f => Path.GetFileName(f) == "ButtonRemap.cs"));
        Assert.Matches(@"const int WH_MOUSE_LL = 14;", src);
        Assert.Matches(@"SetWindowsHookEx\(WH_MOUSE_LL,", src);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(src, @"SetWindowsHookEx\((?!int )"));   // one call (not counting the declaration)
        Assert.DoesNotMatch(@"WH_KEYBOARD|WH_GETMESSAGE|WH_CALLWNDPROC|WH_CBT|WH_MOUSE\b|WH_DEBUG|WH_SHELL", src);
        // Input sent by programs is never remapped (no loops), and left/right click are never touched.
        Assert.Matches(@"LLMHF_INJECTED\) == 0", src);
        Assert.DoesNotMatch(@"WM_LBUTTON|WM_RBUTTON", src);
    }

    [Fact]
    public void Only_the_cleaner_deletes_files() =>
        AssertNotUsed(@"\bFile\.Delete\(|\bDirectory\.Delete\(|\.Delete\(\)",
            "deleting files is only allowed in the cleanup engine (and Pulse's own temporary script/state files)",
            "Cleaner.cs", "Shell.cs", "AbTest.cs",
            "ClipLibrary.cs" /* the user's own clips, when they delete one or the storage limit is reached */,
            "Ffmpeg.cs" /* its own download */, "StartMenu.cs" /* its own Start menu shortcut, when the user turns it off */, "Lineups.cs" /* the picture Pulse copied for a lineup the user deletes */, "ReplayRecorder.cs" /* its own temporary file */, "BrawlhallaMods.cs" /* its own backup of a Brawlhalla file after putting the original back, and the copy of a mod the user removes */);

    [Fact]
    public void Never_deletes_whole_registry_keys() =>
        AssertNotUsed(@"DeleteSubKeyTree|DeleteSubKey\(",
            "tweaks only change single values, which are backed up first. The one exception is Reg.ExportAndDeleteKey, " +
            "which only accepts a single leftover entry under Windows' Uninstall list and exports it to a .reg file first",
            "Reg.cs");

    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Game\Sub")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\..\Run")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\Something")]
    public void Key_removal_is_limited_to_one_uninstall_entry(string path) =>
        Assert.False(Pulse.Core.Reg.IsUninstallEntry(path));

    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Some Game_is1")]
    [InlineData(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{1234}")]
    public void Uninstall_entries_are_allowed(string path) =>
        Assert.True(Pulse.Core.Reg.IsUninstallEntry(path));

    [Fact]
    public void Never_turns_off_Defender() =>
        AssertNotUsed(@"DisableRealtimeMonitoring|DisableAntiSpyware|Add-MpPreference\s+-Exclusion",
            "Pulse never weakens antivirus protection or adds exclusions");

    [Fact]
    public void Registry_writes_go_through_the_backup_helper() =>
        AssertNotUsed(@"\.SetValue\(|\.DeleteValue\(|CreateSubKey\(",
            "every registry change must go through Reg.cs, which is used by actions that back up first",
            "Reg.cs", "Controls.cs" /* WPF DependencyObject.SetValue, not the registry */,
            "MouseHost.cs" /* the script engine's globals, not the registry */);
}
