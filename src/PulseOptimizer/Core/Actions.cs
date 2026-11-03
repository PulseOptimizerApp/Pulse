using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Pulse.Core;

/// <summary>Sets a single registry value; backs up the old one first.</summary>
public class RegAction : TweakAction
{
    public RegistryHive Hive { get; }
    public string Path { get; }
    public string Name { get; }
    public object Value { get; }
    public RegistryValueKind Kind { get; }
    /// <summary>Windows' default used when undoing without a backup. Null = remove the value.</summary>
    public object Default { get; }

    public RegAction(RegistryHive hive, string path, string name, object value,
                     RegistryValueKind kind = RegistryValueKind.DWord, object @default = null)
    {
        Hive = hive; Path = path; Name = name; Value = value; Kind = kind; Default = @default;
    }

    public override bool? Check() => Reg.Equal(Reg.Get(Hive, Path, Name), Value);

    public override ActionBackup Apply()
    {
        var b = Reg.Snapshot(Hive, Path, Name);
        Reg.Set(Hive, Path, Name, Value, Kind);
        return b;
    }

    public override void Revert(ActionBackup backup)
    {
        if (backup != null) Reg.Restore(backup);
        else if (Default == null) Reg.Delete(Hive, Path, Name);
        else Reg.Set(Hive, Path, Name, Default, Kind);
    }
}

/// <summary>Runs a command to apply and another to undo.</summary>
public class CmdAction : TweakAction
{
    readonly string _apply, _revert;
    readonly Func<bool?> _check;

    public CmdAction(string apply, string revert, Func<bool?> check = null)
    {
        _apply = apply; _revert = revert; _check = check;
    }

    public override bool? Check() => _check?.Invoke();

    public override ActionBackup Apply()
    {
        var r = Shell.Cmd(_apply);
        if (!r.Ok) throw new InvalidOperationException(FirstLine(r.Error + r.Output) ?? "The command failed.");
        return new ActionBackup { Kind = "cmd" };
    }

    public override void Revert(ActionBackup backup) => Shell.Cmd(_revert);

    internal static string FirstLine(string s) =>
        s?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
}

/// <summary>Runs a PowerShell snippet to apply and another to undo.</summary>
public class PsAction : TweakAction
{
    readonly string _apply, _revert, _check;

    public PsAction(string apply, string revert, string checkReturnsTrueFalse = null)
    {
        _apply = apply; _revert = revert; _check = checkReturnsTrueFalse;
    }

    public override bool? Check()
    {
        if (_check == null) return null;
        var r = Shell.PowerShell(_check, 30_000);
        var t = r.Output.Trim();
        if (t.EndsWith("True", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.EndsWith("False", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    public override ActionBackup Apply()
    {
        var r = Shell.PowerShell("$ErrorActionPreference='Stop'\r\n" + _apply, 60_000);
        if (!r.Ok || r.Error.Trim().Length > 0)
            throw new InvalidOperationException(CmdAction.FirstLine(r.Error) ?? "The command failed.");
        return new ActionBackup { Kind = "ps" };
    }

    public override void Revert(ActionBackup backup) => Shell.PowerShell(_revert, 60_000);
}

/// <summary>Sets a Windows service to Disabled (and stops it). Undo restores the previous start type.</summary>
public class ServiceAction : TweakAction
{
    readonly string _svc;
    string Key => $@"SYSTEM\CurrentControlSet\Services\{_svc}";
    readonly int _defaultStart;

    public ServiceAction(string serviceName, int defaultStart = 2)
    {
        _svc = serviceName; _defaultStart = defaultStart;
    }

    public override bool? Check()
    {
        var v = Reg.Get(RegistryHive.LocalMachine, Key, "Start");
        return v == null ? null : Convert.ToInt32(v) == 4;
    }

    public override ActionBackup Apply()
    {
        if (Reg.Get(RegistryHive.LocalMachine, Key, "Start") == null)
            throw new InvalidOperationException($"The {_svc} service isn't installed on this PC.");
        var b = Reg.Snapshot(RegistryHive.LocalMachine, Key, "Start");
        Reg.Set(RegistryHive.LocalMachine, Key, "Start", 4, RegistryValueKind.DWord);
        Shell.Cmd($"sc stop {_svc}", 30_000);
        return b;
    }

    public override void Revert(ActionBackup backup)
    {
        if (backup != null) Reg.Restore(backup);
        else Reg.Set(RegistryHive.LocalMachine, Key, "Start", _defaultStart, RegistryValueKind.DWord);
        var start = Convert.ToInt32(Reg.Get(RegistryHive.LocalMachine, Key, "Start") ?? 3);
        if (start == 2) Shell.Cmd($"sc start {_svc}", 30_000);
    }
}

/// <summary>Switches to the Ultimate Performance power plan (falls back to High performance).</summary>
public class PowerPlanAction : TweakAction
{
    const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    const string High = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    static readonly Regex Guid = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

    static string Active() => Guid.Match(Shell.Cmd("powercfg /getactivescheme").Output).Value;

    public override bool? Check()
    {
        var o = Shell.Cmd("powercfg /getactivescheme").Output;
        return o.Contains("Ultimate", StringComparison.OrdinalIgnoreCase) || o.Contains(Ultimate) || o.Contains(High);
    }

    public override ActionBackup Apply()
    {
        var b = new ActionBackup { Kind = "powerplan" };
        b.Data["previous"] = Active();

        var dup = Shell.Cmd($"powercfg -duplicatescheme {Ultimate}");
        var created = Guid.Match(dup.Output).Value;
        if (dup.Ok && created.Length > 0 && Shell.Cmd($"powercfg /setactive {created}").Ok)
        {
            b.Data["created"] = created;
            return b;
        }
        if (Shell.Cmd($"powercfg /setactive {High}").Ok) return b;
        throw new InvalidOperationException("This PC doesn't allow switching power plans (common on laptops with Modern Standby).");
    }

    public override void Revert(ActionBackup backup)
    {
        var prev = backup?.Data.GetValueOrDefault("previous");
        Shell.Cmd($"powercfg /setactive {(string.IsNullOrEmpty(prev) ? Balanced : prev)}");
        var created = backup?.Data.GetValueOrDefault("created");
        if (!string.IsNullOrEmpty(created)) Shell.Cmd($"powercfg /delete {created}");
    }
}

/// <summary>Sets a power-plan setting (AC and battery) on the active plan.</summary>
public class PowerSettingAction : TweakAction
{
    readonly string _sub, _setting;
    readonly int _value, _default;

    public PowerSettingAction(string subgroup, string setting, int value, int @default)
    {
        _sub = subgroup; _setting = setting; _value = value; _default = @default;
    }

    public override bool? Check()
    {
        var o = Shell.Cmd($"powercfg /q SCHEME_CURRENT {_sub} {_setting}").Output;
        var m = Regex.Matches(o, @"0x([0-9a-fA-F]{8})");
        if (m.Count < 2) return null;
        int ac = int.Parse(m[^2].Groups[1].Value, NumberStyles.HexNumber);
        return ac == _value;
    }

    void SetAll(int v)
    {
        Shell.Cmd($"powercfg /setacvalueindex SCHEME_CURRENT {_sub} {_setting} {v}");
        Shell.Cmd($"powercfg /setdcvalueindex SCHEME_CURRENT {_sub} {_setting} {v}");
        Shell.Cmd("powercfg /setactive SCHEME_CURRENT");
    }

    public override ActionBackup Apply()
    {
        SetAll(_value);
        return new ActionBackup { Kind = "powersetting" };
    }

    public override void Revert(ActionBackup backup) => SetAll(_default);
}

/// <summary>Sets TcpAckFrequency = 1 (acknowledge every packet) on every network adapter with an IP address.
/// TCPNoDelay is deliberately not touched: it isn't in Microsoft's TCP/IP documentation.</summary>
public class TcpAckAction : TweakAction
{
    const string Root = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    static IEnumerable<string> ActiveInterfaces()
    {
        foreach (var id in Reg.SubKeys(RegistryHive.LocalMachine, Root))
        {
            var path = $@"{Root}\{id}";
            var dhcp = Reg.Get(RegistryHive.LocalMachine, path, "DhcpIPAddress") as string;
            var stat = (Reg.Get(RegistryHive.LocalMachine, path, "IPAddress") as string[])?.FirstOrDefault();
            bool real(string ip) => !string.IsNullOrWhiteSpace(ip) && ip != "0.0.0.0";
            if (real(dhcp) || real(stat)) yield return path;
        }
    }

    public override bool? Check()
    {
        var list = ActiveInterfaces().ToList();
        if (list.Count == 0) return null;
        return list.All(p => Reg.Equal(Reg.Get(RegistryHive.LocalMachine, p, "TcpAckFrequency"), 1));
    }

    public override ActionBackup Apply()
    {
        var b = new ActionBackup { Kind = "multi" };
        foreach (var p in ActiveInterfaces())
        {
            b.Children.Add(Reg.Snapshot(RegistryHive.LocalMachine, p, "TcpAckFrequency"));
            Reg.Set(RegistryHive.LocalMachine, p, "TcpAckFrequency", 1, RegistryValueKind.DWord);
        }
        return b;
    }

    public override void Revert(ActionBackup backup)
    {
        if (backup != null) { foreach (var c in backup.Children) Reg.Restore(c); return; }
        foreach (var p in ActiveInterfaces())
        {
            Reg.Delete(RegistryHive.LocalMachine, p, "TcpAckFrequency"); // absent = Windows default of 2
        }
    }
}

/// <summary>Turns mouse acceleration off through the documented SystemParametersInfo API (applies live).</summary>
public class MouseAccelAction : TweakAction
{
    static readonly int[] Off = { 0, 0, 0 };
    static readonly int[] WindowsDefault = { 6, 10, 1 };

    public override bool? Check() => Native.GetMouse()[2] == 0;

    public override ActionBackup Apply()
    {
        var cur = Native.GetMouse();
        var b = new ActionBackup { Kind = "mouse" };
        b.Data["values"] = string.Join(",", cur);
        Native.SetMouse(Off);
        return b;
    }

    public override void Revert(ActionBackup backup)
    {
        var v = backup?.Data.GetValueOrDefault("values")?.Split(',').Select(int.Parse).ToArray();
        Native.SetMouse(v is { Length: 3 } ? v : WindowsDefault);
    }
}

/// <summary>Turns off the Sticky Keys shortcut (Shift ×5) through SystemParametersInfo. Sticky Keys itself stays available.</summary>
public class StickyKeysHotkeyAction : TweakAction
{
    public override bool? Check() => (Native.GetStickyKeysFlags() & Native.SKF_HOTKEYACTIVE) == 0;

    public override ActionBackup Apply()
    {
        var flags = Native.GetStickyKeysFlags();
        var b = new ActionBackup { Kind = "stickykeys" };
        b.Data["flags"] = flags.ToString(CultureInfo.InvariantCulture);
        Native.SetStickyKeysFlags(flags & ~Native.SKF_HOTKEYACTIVE);
        return b;
    }

    public override void Revert(ActionBackup backup)
    {
        if (backup != null && uint.TryParse(backup.Data.GetValueOrDefault("flags"), out var f))
            Native.SetStickyKeysFlags(f);
        else
            Native.SetStickyKeysFlags(Native.GetStickyKeysFlags() | Native.SKF_HOTKEYACTIVE);
    }
}

/// <summary>Sets Defender's potentially-unwanted-app protection to Block. Undo restores the previous mode (usually Audit).</summary>
public class PuaAction : TweakAction
{
    static int? Current()
    {
        var r = Shell.PowerShell("(Get-MpPreference).PUAProtection", 30_000);
        return int.TryParse(r.Output.Trim(), out var v) ? v : null;
    }

    public override bool? Check()
    {
        var v = Current();
        return v == null ? null : v == 1;
    }

    public override ActionBackup Apply()
    {
        var b = new ActionBackup { Kind = "pua" };
        var prev = Current() ?? throw new InvalidOperationException("Microsoft Defender isn't available (another antivirus may be in charge).");
        b.Data["previous"] = prev.ToString(CultureInfo.InvariantCulture);
        var r = Shell.PowerShell("$ErrorActionPreference='Stop'\r\nSet-MpPreference -PUAProtection 1", 60_000);
        if (Current() != 1) throw new InvalidOperationException(CmdAction.FirstLine(r.Error) ?? "Defender didn't accept the change (Tamper Protection or a policy may block it).");
        return b;
    }

    public override void Revert(ActionBackup backup)
    {
        // Microsoft's default on current security intelligence is Audit (2).
        var prev = backup?.Data.GetValueOrDefault("previous") ?? "2";
        Shell.PowerShell($"Set-MpPreference -PUAProtection {int.Parse(prev, CultureInfo.InvariantCulture)}", 60_000);
    }
}
