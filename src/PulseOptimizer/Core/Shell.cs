using System.Diagnostics;
using System.Text;

namespace Pulse.Core;

public record ShellResult(int Code, string Output, string Error)
{
    public bool Ok => Code == 0;
}

/// <summary>Runs Windows command-line tools quietly and captures their output.</summary>
public static class Shell
{
    static readonly string Sys = Environment.SystemDirectory;
    public static string CmdExe => Path.Combine(Sys, "cmd.exe");
    public static string PowerShellExe => Path.Combine(Sys, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static ShellResult Run(string file, string args, int timeoutMs = 120_000, Action<Process> onStart = null)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        onStart?.Invoke(p);
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            return new ShellResult(-1, "", "Timed out");
        }
        p.WaitForExit();
        return new ShellResult(p.ExitCode, outTask.Result, errTask.Result);
    }

    public static ShellResult Cmd(string command, int timeoutMs = 120_000) =>
        Run(CmdExe, "/c " + command, timeoutMs);

    /// <summary>Runs a PowerShell script from a temp .ps1 file (no encoded commands).</summary>
    public static ShellResult PowerShell(string script, int timeoutMs = 180_000, Action<Process> onStart = null)
    {
        var dir = Path.Combine(TweakEngine.DataDir, "scripts");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".ps1");
        var header = "$ProgressPreference='SilentlyContinue'\r\n[Console]::OutputEncoding=[Text.Encoding]::UTF8\r\n";
        File.WriteAllText(file, header + script, new UTF8Encoding(true));
        try
        {
            return Run(PowerShellExe, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"", timeoutMs, onStart);
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }
}
