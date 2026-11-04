using System.Globalization;
using Microsoft.Win32;

namespace Pulse.Core;

/// <summary>Small registry helper that always uses the 64-bit view and can snapshot/restore single values.</summary>
public static class Reg
{
    static RegistryKey Base(RegistryHive h) => RegistryKey.OpenBaseKey(h, RegistryView.Registry64);

    public static object Get(RegistryHive h, string path, string name)
    {
        try
        {
            using var b = Base(h);
            using var k = b.OpenSubKey(path);
            return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch { return null; }
    }

    public static void Set(RegistryHive h, string path, string name, object value, RegistryValueKind kind)
    {
        using var b = Base(h);
        using var k = b.CreateSubKey(path, true);
        k.SetValue(name, value, kind);
    }

    public static void Delete(RegistryHive h, string path, string name)
    {
        using var b = Base(h);
        using var k = b.OpenSubKey(path, true);
        k?.DeleteValue(name, false);
    }

    public static string[] SubKeys(RegistryHive h, string path)
    {
        try
        {
            using var b = Base(h);
            using var k = b.OpenSubKey(path);
            return k?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    public static string[] ValueNames(RegistryHive h, string path)
    {
        try
        {
            using var b = Base(h);
            using var k = b.OpenSubKey(path);
            return k?.GetValueNames() ?? Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    public static bool Equal(object a, object b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a is byte[] x && b is byte[] y) return x.SequenceEqual(y);
        return string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture),
                             Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Saves a whole key to a .reg file (Windows' own export) and then deletes it. The file puts it back when opened.
    /// Returns the backup file. Throws if the backup couldn't be made (then nothing is deleted).
    /// </summary>
    /// <summary>True only for one entry directly inside Windows' Uninstall list (no deeper keys, no "..").</summary>
    internal static bool IsUninstallEntry(string path)
    {
        foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" })
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                var name = path[root.Length..];
                return name.Length > 0 && !name.Contains('\\') && !name.Contains("..");
            }
        return false;
    }

    public static string ExportAndDeleteKey(RegistryHive h, string path, string backupDir, string name)
    {
        if (!IsUninstallEntry(path)) throw new InvalidOperationException("Only a program's entry in Windows' list of programs can be removed.");
        var root = h switch { RegistryHive.LocalMachine => "HKLM", RegistryHive.CurrentUser => "HKCU", RegistryHive.Users => "HKU", _ => throw new ArgumentException("Unsupported hive") };
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        Directory.CreateDirectory(backupDir);
        var file = Path.Combine(backupDir, $"{name} {DateTime.Now:yyyyMMdd-HHmmss}.reg");
        var r = Shell.Run(Path.Combine(Environment.SystemDirectory, "reg.exe"), $"export \"{root}\\{path}\" \"{file}\" /y /reg:64", 30_000);
        if (!r.Ok || !File.Exists(file)) throw new InvalidOperationException("Windows couldn't back up the entry, so it wasn't removed.");
        using var b = Base(h);
        b.DeleteSubKeyTree(path, false);
        return file;
    }

    public static ActionBackup Snapshot(RegistryHive h, string path, string name)
    {
        var bk = new ActionBackup { Kind = "reg" };
        bk.Data["hive"] = h.ToString();
        bk.Data["path"] = path;
        bk.Data["name"] = name;
        try
        {
            using var b = Base(h);
            using var k = b.OpenSubKey(path);
            var v = k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (v == null) { bk.Data["existed"] = "0"; return bk; }
            var kind = k.GetValueKind(name);
            bk.Data["existed"] = "1";
            bk.Data["kind"] = kind.ToString();
            bk.Data["value"] = Serialize(v, kind);
        }
        catch { bk.Data["existed"] = "0"; }
        return bk;
    }

    public static void Restore(ActionBackup bk)
    {
        var h = Enum.Parse<RegistryHive>(bk.Data["hive"]);
        var path = bk.Data["path"];
        var name = bk.Data["name"];
        if (bk.Data.GetValueOrDefault("existed") == "1")
        {
            var kind = Enum.Parse<RegistryValueKind>(bk.Data["kind"]);
            Set(h, path, name, Deserialize(bk.Data["value"], kind), kind);
        }
        else
        {
            Delete(h, path, name);
        }
    }

    static string Serialize(object v, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => Convert.ToInt32(v).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.ToBase64String((byte[])v),
        RegistryValueKind.MultiString => string.Join("\u0001", (string[])v),
        _ => v.ToString()
    };

    static object Deserialize(string s, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => int.Parse(s, CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => long.Parse(s, CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.FromBase64String(s),
        RegistryValueKind.MultiString => s.Split('\u0001'),
        _ => s
    };
}
