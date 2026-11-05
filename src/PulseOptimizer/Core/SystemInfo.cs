using Microsoft.Win32;

namespace Pulse.Core;

public static class SystemInfo
{
    const string NtKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    const string GpuClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    static readonly Lazy<string> _cpu = new(() =>
        (Reg.Get(RegistryHive.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") as string)?.Trim()
        ?? "Unknown processor");
    public static string Cpu => _cpu.Value;
    public static int Threads => Environment.ProcessorCount;

    public record Gpu(string Name, long MemoryBytes);

    static readonly Lazy<List<Gpu>> _gpus = new(() =>
    {
        var list = new List<Gpu>();
        foreach (var sub in Reg.SubKeys(RegistryHive.LocalMachine, GpuClass).Where(s => s.Length == 4 && s.All(char.IsDigit)))
        {
            var path = $@"{GpuClass}\{sub}";
            if (Reg.Get(RegistryHive.LocalMachine, path, "DriverDesc") is not string name) continue;
            if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Remote", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
            long mem = 0;
            var raw = Reg.Get(RegistryHive.LocalMachine, path, "HardwareInformation.qwMemorySize")
                   ?? Reg.Get(RegistryHive.LocalMachine, path, "HardwareInformation.MemorySize");
            try
            {
                mem = raw switch
                {
                    long l => l,
                    int i => (uint)i,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                    _ => 0
                };
            }
            catch { }
            if (!list.Any(g => g.Name == name)) list.Add(new Gpu(name, mem));
        }
        return list;
    });
    public static List<Gpu> Gpus => _gpus.Value;

    public static string GpuText =>
        Gpus.Count == 0 ? "Unknown graphics" :
        string.Join("  +  ", Gpus.Select(g => g.MemoryBytes > 0 ? $"{g.Name} ({Format.Bytes(g.MemoryBytes, 0)})" : g.Name));

    /// <summary>
    /// True when games and recording share one small graphics chip: no separate graphics card (integrated graphics,
    /// like AMD "Radeon(TM) Graphics" or Intel UHD/Iris), or a card with under 3 GB of its own memory.
    /// </summary>
    public static bool SharedGraphicsOnly => Gpus.Count > 0 && !Gpus.Any(g =>
        PcDiagnostics.IsDiscrete(g.Name) && (g.MemoryBytes == 0 || g.MemoryBytes >= 3L << 30));

    public static bool HasNvidia => Gpus.Any(g => g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
    public static bool HasAmd => Gpus.Any(g => g.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || g.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase));

    public static int Build =>
        int.TryParse(Reg.Get(RegistryHive.LocalMachine, NtKey, "CurrentBuild") as string, out var b) ? b : 0;

    public static bool IsWindows11 => Build >= 22000;

    public static string OsName
    {
        get
        {
            var name = Reg.Get(RegistryHive.LocalMachine, NtKey, "ProductName") as string ?? "Windows";
            if (IsWindows11) name = name.Replace("Windows 10", "Windows 11");
            var ver = Reg.Get(RegistryHive.LocalMachine, NtKey, "DisplayVersion") as string;
            return string.IsNullOrEmpty(ver) ? name : $"{name} {ver}";
        }
    }

    public static ulong TotalRam => Native.Memory().ullTotalPhys;

    public static bool HasBattery
    {
        get
        {
            Native.GetSystemPowerStatus(out var s);
            return s.BatteryFlag != 128 && s.BatteryFlag != 255;
        }
    }

    public static bool IsPluggedIn
    {
        get
        {
            Native.GetSystemPowerStatus(out var s);
            return s.ACLineStatus == 1;
        }
    }

    /// <summary>"SSD", "HDD" or "Unknown" for the disk Windows is installed on.</summary>
    static readonly Lazy<string> _sysMedia = new(() =>
    {
        var letter = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\', ':') ?? "C";
        var r = Shell.PowerShell(
            $"$n=(Get-Partition -DriveLetter {letter}).DiskNumber; (Get-PhysicalDisk | Where-Object DeviceId -eq \"$n\").MediaType", 30_000);
        var o = r.Output.Trim();
        return o.Contains("SSD", StringComparison.OrdinalIgnoreCase) ? "SSD"
             : o.Contains("HDD", StringComparison.OrdinalIgnoreCase) ? "HDD" : "Unknown";
    });
    public static string SystemDriveMedia => _sysMedia.Value;

    public static DriveInfo SystemDrive => new(Path.GetPathRoot(Environment.SystemDirectory));

    public static TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);
}

/// <summary>Measures total CPU usage between calls.</summary>
public class CpuMeter
{
    long _idle, _total;

    public double Sample()
    {
        Native.GetSystemTimes(out var idle, out var kernel, out var user);
        var total = kernel + user; // kernel time includes idle time
        var dIdle = idle - _idle;
        var dTotal = total - _total;
        _idle = idle; _total = total;
        if (dTotal <= 0) return 0;
        return Math.Clamp(1.0 - (double)dIdle / dTotal, 0, 1);
    }
}

public static class Format
{
    public static string Bytes(double b, int decimals = 1)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{b:0} B" : b.ToString("F" + decimals) + " " + u[i];
    }

    public static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return Pulse.Ui.Loc.T("just now");
        if (d.TotalHours < 1) return Pulse.Ui.Loc.F("{0} min ago", (int)d.TotalMinutes);
        if (d.TotalDays < 1) return Pulse.Ui.Loc.F("{0} h ago", (int)d.TotalHours);
        if (d.TotalDays < 2) return Pulse.Ui.Loc.T("yesterday");
        return Pulse.Ui.Loc.F("{0} days ago", (int)d.TotalDays);
    }
}
