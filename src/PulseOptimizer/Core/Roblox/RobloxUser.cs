using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace Pulse.Core.Roblox;

/// <summary>
/// The Windows account that is actually sitting at the PC and plays Roblox.
///
/// Pulse restarts itself as administrator. On a PC where the player uses a standard account and someone types an
/// administrator's password into the prompt, Pulse then runs as that *other* account: its AppData, its registry and
/// its state file are not the player's. Roblox is installed per user, and the Play button is registered per user, so
/// everything Roblox-related has to use the account logged on to this Windows session instead.
/// When Pulse runs as the player (the usual case), this is simply the current user.
/// </summary>
public static class RobloxUser
{
    static readonly Lazy<string> _sid = new(FindOtherSessionUserSid);

    /// <summary>The player's SID when it differs from the account Pulse runs as; null when they're the same.</summary>
    public static string OtherSid => _sid.Value;

    public static bool IsDifferentAccount => OtherSid != null;

    /// <summary>The player's AppData\Local folder.</summary>
    public static string LocalAppData
    {
        get
        {
            var own = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var sid = OtherSid;
            if (sid == null) return own;
            if (Reg.Get(RegistryHive.Users, $@"{sid}\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders", "Local AppData") is string shell
                && Directory.Exists(shell)) return shell;
            if (Reg.Get(RegistryHive.LocalMachine, $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}", "ProfileImagePath") is string profile)
            {
                var p = Path.Combine(Environment.ExpandEnvironmentVariables(profile), "AppData", "Local");
                if (Directory.Exists(p)) return p;
            }
            return own;
        }
    }

    /// <summary>Where the player's own registry settings live (HKCU for them).</summary>
    public static (RegistryHive Hive, string Prefix) UserRoot =>
        OtherSid is string sid ? (RegistryHive.Users, sid + @"\") : (RegistryHive.CurrentUser, "");

    // ───────── finding the session's user ─────────

    const int WTSUserName = 5, WTSDomainName = 7;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr memory);

    static string SessionString(int session, int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, infoClass, out var buf, out _) || buf == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { WTSFreeMemory(buf); }
    }

    static string FindOtherSessionUserSid()
    {
        try
        {
            int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            var user = SessionString(session, WTSUserName);
            if (string.IsNullOrEmpty(user)) return null;
            var domain = SessionString(session, WTSDomainName);
            var account = string.IsNullOrEmpty(domain) ? new NTAccount(user) : new NTAccount(domain, user);
            var sid = ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
            var me = WindowsIdentity.GetCurrent().User?.Value;
            if (string.Equals(sid, me, StringComparison.OrdinalIgnoreCase)) return null;
            // Only use it if that user's registry is loaded (they're logged on), otherwise fall back to our own.
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
            return users.GetSubKeyNames().Contains(sid, StringComparer.OrdinalIgnoreCase) ? sid : null;
        }
        catch { return null; }
    }
}
