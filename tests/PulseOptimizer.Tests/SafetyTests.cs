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
            "your own last match from Riot with the Riot Client's local login (ValorantMatches.cs)",
            "Ffmpeg.cs", "Updater.cs", "LeagueLive.cs", "ValorantMatches.cs");

    [Fact]
    public void Updates_and_downloads_only_come_from_Pulses_own_repos()
    {
        var info = File.ReadAllText(Repo.SourceFiles().Single(f => Path.GetFileName(f) == "AppInfo.cs"));
        Assert.Matches(@"Repo = ""[A-Za-z0-9-]+/PulseOptimizer""", info);
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
        foreach (var name in new[] { "LeagueLive.cs", "ValorantMatches.cs", "GsiServer.cs" })
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
    public void Never_injects_into_or_reads_other_processes_memory() =>
        AssertNotUsed(@"\b(OpenProcess|WriteProcessMemory|ReadProcessMemory|CreateRemoteThread|VirtualAllocEx|SetWindowsHookEx)\b",
            "Pulse must never touch game or other processes' memory (anti-cheat safety)");

    [Fact]
    public void Only_the_cleaner_deletes_files() =>
        AssertNotUsed(@"\bFile\.Delete\(|\bDirectory\.Delete\(|\.Delete\(\)",
            "deleting files is only allowed in the cleanup engine (and Pulse's own temporary script/state files)",
            "Cleaner.cs", "Shell.cs", "AbTest.cs",
            "ClipLibrary.cs" /* the user's own clips, when they delete one or the storage limit is reached */,
            "Ffmpeg.cs" /* its own download */, "ReplayRecorder.cs" /* its own temporary file */);

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
            "Reg.cs", "Controls.cs" /* WPF DependencyObject.SetValue, not the registry */);
}
