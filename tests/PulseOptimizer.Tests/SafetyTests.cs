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
            "Pulse never sends or downloads anything itself (links open in the user's browser)");

    [Fact]
    public void Never_injects_into_or_reads_other_processes_memory() =>
        AssertNotUsed(@"\b(OpenProcess|WriteProcessMemory|ReadProcessMemory|CreateRemoteThread|VirtualAllocEx|SetWindowsHookEx)\b",
            "Pulse must never touch game or other processes' memory (anti-cheat safety)");

    [Fact]
    public void Only_the_cleaner_deletes_files() =>
        AssertNotUsed(@"\bFile\.Delete\(|\bDirectory\.Delete\(|\.Delete\(\)",
            "deleting files is only allowed in the cleanup engine (and Pulse's own temporary script/state files)",
            "Cleaner.cs", "Shell.cs", "AbTest.cs");

    [Fact]
    public void Never_deletes_whole_registry_keys() =>
        AssertNotUsed(@"DeleteSubKeyTree|DeleteSubKey\(",
            "tweaks only change single values, which are backed up first");

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
