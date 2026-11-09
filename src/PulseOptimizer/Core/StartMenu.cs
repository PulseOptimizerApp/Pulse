using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Pulse.Core;

/// <summary>
/// Puts Pulse in the Start menu, so Windows Search lists it as "Pulse · App" (with its icon) like installed apps,
/// instead of as a loose Pulse.exe. Pulse has no installer, so it does this itself: one shortcut in the user's own
/// Start menu folder, kept pointing at wherever Pulse.exe is now. Nothing else is touched, and turning it off removes
/// only that shortcut.
/// </summary>
public static class StartMenu
{
    public const string ShortcutName = "Pulse.lnk";

    public static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName);

    /// <summary>Creates the shortcut, or fixes it if Pulse.exe moved. Skips copies running from a temporary folder.</summary>
    public static bool Ensure()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
            if (exe.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(Target(ShortcutPath), exe, StringComparison.OrdinalIgnoreCase)) return true;
            Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
            link.SetIconLocation(exe, 0);
            link.SetDescription("Pulse");
            ((IPersistFile)link).Save(ShortcutPath, true);
            Marshal.FinalReleaseComObject(link);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Removes Pulse's own shortcut (only if it really is Pulse's: named Pulse.lnk and pointing at a Pulse.exe).</summary>
    public static void Remove()
    {
        try
        {
            var t = Target(ShortcutPath);
            if (t != null && Path.GetFileName(t).Equals("Pulse.exe", StringComparison.OrdinalIgnoreCase)) File.Delete(ShortcutPath);
        }
        catch { }
    }

    public static bool Exists => File.Exists(ShortcutPath);

    /// <summary>The program a shortcut opens, or null.</summary>
    static string Target(string lnk)
    {
        if (!File.Exists(lnk)) return null;
        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(lnk, 0);
            var sb = new char[1024];
            link.GetPath(sb, sb.Length, IntPtr.Zero, 0);
            Marshal.FinalReleaseComObject(link);
            var s = new string(sb).TrimEnd('\0');
            return s.Length > 0 ? s : null;
        }
        catch { return null; }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
