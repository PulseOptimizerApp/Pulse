using System.Runtime.InteropServices;
using System.Text;

namespace Pulse.Core.Roblox;

/// <summary>A driver setting Pulse can set in NVIDIA's per-game profile.</summary>
public record NvSetting(uint Id, string Name, uint Value, string ValueText);

/// <summary>What was there before Pulse changed a setting, so Undo can put it back.</summary>
public class NvBackup
{
    public uint Id { get; set; }
    /// <summary>True when the profile itself had a user value (restored on undo); false means it was inherited (deleted on undo).</summary>
    public bool HadUserValue { get; set; }
    public uint OldValue { get; set; }
}

/// <summary>
/// Sets NVIDIA driver settings for Roblox only, through NVIDIA's own driver settings API (NVAPI DRS), the same store
/// NVIDIA Control Panel → Manage 3D settings → Program Settings uses. Nothing global changes and the Roblox process is
/// never touched. Setting IDs and structure layouts come from NVIDIA's public NVAPI headers (github.com/NVIDIA/nvapi).
/// </summary>
public static class NvidiaProfile
{
    public const string RobloxExe = "robloxplayerbeta.exe";
    const string PulseProfileName = "Roblox (Pulse)";

    // NvApiDriverSettings.h
    public const uint PRERENDERLIMIT_ID = 0x007BA09E;    // Low Latency Mode: 1 = On (one frame queued)
    public const uint PREFERRED_PSTATE_ID = 0x1057EB71;  // Power management: 1 = Prefer maximum performance
    public const uint VSYNCMODE_ID = 0x00A879CF;         // Vertical sync
    public const uint VSYNCMODE_FORCEOFF = 0x08416747;
    public const uint FRL_FPS_ID = 0x10835002;           // Max Frame Rate (driver limiter), 0 = off

    // nvapi_interface.h
    const uint ID_Initialize = 0x0150E828, ID_CreateSession = 0x0694D52E, ID_DestroySession = 0xDAD9CFF8,
               ID_LoadSettings = 0x375DBD6B, ID_SaveSettings = 0xFCBC7E14, ID_CreateProfile = 0xCC176068,
               ID_FindProfileByName = 0x7E4A9A0B, ID_CreateApplication = 0x4347A9DE, ID_FindApplicationByName = 0xEEE566B2,
               ID_SetSetting = 0x577DD202, ID_GetSetting = 0x73BF8338, ID_DeleteProfileSetting = 0xE4A26362;

    // Structure sizes from nvapi.h: NvAPI_UnicodeString is 2048 UTF-16 chars (4096 bytes).
    const int Str = 4096;
    const int SettingSize = 4 + Str + 4 * 5 + 4100 + 4100;        // NVDRS_SETTING_V1, pack(4) = 12320
    const int AppSize = 4 + 4 + Str * 4 + 4 + Str;                // NVDRS_APPLICATION_V4 = 20492
    const int ProfileSize = 4 + Str + 4 + 4 + 4 + 4;              // NVDRS_PROFILE_V1 = 4116
    static uint Ver(int size, int v) => (uint)size | ((uint)v << 16);

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Void();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Session(out IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_SessionOnly(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_CreateProfile(IntPtr session, IntPtr profileInfo, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_FindProfile(IntPtr session, IntPtr name, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_CreateApp(IntPtr session, IntPtr profile, IntPtr app);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_FindApp(IntPtr session, IntPtr appName, out IntPtr profile, IntPtr app);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_SetSetting(IntPtr session, IntPtr profile, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_GetSetting(IntPtr session, IntPtr profile, uint id, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_DeleteSetting(IntPtr session, IntPtr profile, uint id);

    static T Fn<T>(uint id) where T : Delegate
    {
        var p = QueryInterface(id);
        if (p == IntPtr.Zero) throw new InvalidOperationException("This NVIDIA driver doesn't offer that function.");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    /// <summary>True on PCs with an NVIDIA driver installed.</summary>
    public static bool Available
    {
        get
        {
            try { return Fn<D_Void>(ID_Initialize)() == 0; }
            catch { return false; }
        }
    }

    static IntPtr Alloc(int size)
    {
        var p = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i += 8) Marshal.WriteInt64(p, i, 0);
        return p;
    }

    static void WriteString(IntPtr baseAddr, int offset, string s)
    {
        var bytes = Encoding.Unicode.GetBytes(s);
        Marshal.Copy(bytes, 0, baseAddr + offset, Math.Min(bytes.Length, Str - 2));
    }

    static void Check(int status, string what)
    {
        if (status != 0) throw new InvalidOperationException($"NVIDIA's driver refused to {what} (code {status}).");
    }

    /// <summary>Opens the driver settings, finds (or creates) the profile that runs for Roblox, and hands it to the action.</summary>
    static void WithRobloxProfile(Action<IntPtr, IntPtr> action, bool save)
    {
        Check(Fn<D_Void>(ID_Initialize)(), "start");
        Check(Fn<D_Session>(ID_CreateSession)(out var session), "open its settings");
        try
        {
            Check(Fn<D_SessionOnly>(ID_LoadSettings)(session), "load its settings");
            var profile = FindOrCreate(session);
            action(session, profile);
            if (save) Check(Fn<D_SessionOnly>(ID_SaveSettings)(session), "save the settings");
        }
        finally { Fn<D_SessionOnly>(ID_DestroySession)(session); }
    }

    static IntPtr FindOrCreate(IntPtr session)
    {
        // 1. NVIDIA usually ships a profile that already contains Roblox: use that one.
        var name = Alloc(Str);
        var app = Alloc(AppSize);
        try
        {
            WriteString(name, 0, RobloxExe);
            Marshal.WriteInt32(app, 0, (int)Ver(AppSize, 4));
            if (Fn<D_FindApp>(ID_FindApplicationByName)(session, name, out var existing, app) == 0 && existing != IntPtr.Zero) return existing;
        }
        finally { Marshal.FreeHGlobal(name); Marshal.FreeHGlobal(app); }

        // 2. Otherwise Pulse's own profile, created once, holding just Roblox.
        var pname = Alloc(Str);
        try
        {
            WriteString(pname, 0, PulseProfileName);
            if (Fn<D_FindProfile>(ID_FindProfileByName)(session, pname, out var mine) == 0 && mine != IntPtr.Zero) return mine;
        }
        finally { Marshal.FreeHGlobal(pname); }

        var info = Alloc(ProfileSize);
        var newApp = Alloc(AppSize);
        try
        {
            Marshal.WriteInt32(info, 0, (int)Ver(ProfileSize, 1));
            WriteString(info, 4, PulseProfileName);
            Check(Fn<D_CreateProfile>(ID_CreateProfile)(session, info, out var profile), "create a Roblox profile");
            Marshal.WriteInt32(newApp, 0, (int)Ver(AppSize, 4));
            WriteString(newApp, 8, RobloxExe);
            WriteString(newApp, 8 + Str, "Roblox");
            Check(Fn<D_CreateApp>(ID_CreateApplication)(session, profile, newApp), "add Roblox to the profile");
            return profile;
        }
        finally { Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(newApp); }
    }

    // NVDRS_SETTING field offsets
    const int OffId = 4 + Str, OffType = OffId + 4, OffLocation = OffType + 4, OffCurrent = OffLocation + 4 + 4 + 4 + 4100;

    /// <summary>Applies the settings to Roblox's profile and returns what was there before.</summary>
    public static List<NvBackup> Apply(IEnumerable<NvSetting> settings)
    {
        var backups = new List<NvBackup>();
        WithRobloxProfile((session, profile) =>
        {
            var buf = Alloc(SettingSize);
            try
            {
                foreach (var s in settings)
                {
                    // Read what the profile has now (location 0 = set on this profile itself).
                    for (int i = 0; i < SettingSize; i += 8) Marshal.WriteInt64(buf, i, 0);
                    Marshal.WriteInt32(buf, 0, (int)Ver(SettingSize, 1));
                    var b = new NvBackup { Id = s.Id };
                    if (Fn<D_GetSetting>(ID_GetSetting)(session, profile, s.Id, buf) == 0 && Marshal.ReadInt32(buf, OffLocation) == 0)
                    {
                        b.HadUserValue = true;
                        b.OldValue = (uint)Marshal.ReadInt32(buf, OffCurrent);
                    }
                    backups.Add(b);

                    for (int i = 0; i < SettingSize; i += 8) Marshal.WriteInt64(buf, i, 0);
                    Marshal.WriteInt32(buf, 0, (int)Ver(SettingSize, 1));
                    Marshal.WriteInt32(buf, OffId, (int)s.Id);
                    Marshal.WriteInt32(buf, OffType, 0); // NVDRS_DWORD_TYPE
                    Marshal.WriteInt32(buf, OffCurrent, (int)s.Value);
                    Check(Fn<D_SetSetting>(ID_SetSetting)(session, profile, buf), $"set {s.Name}");
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }, save: true);
        return backups;
    }

    /// <summary>Puts every setting back the way it was before Pulse changed it.</summary>
    public static void Undo(IEnumerable<NvBackup> backups)
    {
        WithRobloxProfile((session, profile) =>
        {
            var buf = Alloc(SettingSize);
            try
            {
                foreach (var b in backups)
                {
                    if (!b.HadUserValue) { Fn<D_DeleteSetting>(ID_DeleteProfileSetting)(session, profile, b.Id); continue; }
                    for (int i = 0; i < SettingSize; i += 8) Marshal.WriteInt64(buf, i, 0);
                    Marshal.WriteInt32(buf, 0, (int)Ver(SettingSize, 1));
                    Marshal.WriteInt32(buf, OffId, (int)b.Id);
                    Marshal.WriteInt32(buf, OffCurrent, (int)b.OldValue);
                    Fn<D_SetSetting>(ID_SetSetting)(session, profile, buf);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }, save: true);
    }
}
