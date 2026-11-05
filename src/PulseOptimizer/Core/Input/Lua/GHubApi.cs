using System.Diagnostics;

namespace Pulse.Core.Input.Lua;

/// <summary>
/// Logitech G Hub's scripting functions, so scripts written for Logitech mice run on any mouse and keyboard.
/// Same names, same arguments, same quirks (like mouse button numbers differing between events and functions).
/// Lighting and LCD functions are accepted and do nothing.
/// </summary>
public static class GHubApi
{
    /// <summary>G Hub's numbering in PressMouseButton / IsMouseButtonPressed: 1 left, 2 middle, 3 right, 4 back, 5 forward.</summary>
    public static int ButtonFromFunctionNumber(int n) => n switch { 1 => 0, 2 => 2, 3 => 1, 4 => 3, 5 => 4, _ => -1 };

    /// <summary>G Hub's numbering in MOUSE_BUTTON_PRESSED events: 1 left, 2 right, 3 middle, 4 back, 5 forward.</summary>
    public static int EventNumberFromVk(int vk) => vk switch
    {
        Keys.VK_LBUTTON => 1, Keys.VK_RBUTTON => 2, Keys.VK_MBUTTON => 3, Keys.VK_XBUTTON1 => 4, Keys.VK_XBUTTON2 => 5, _ => 0,
    };

    /// <summary>Per-script state the functions share.</summary>
    public sealed class Context
    {
        public readonly Stopwatch Started = Stopwatch.StartNew();
        public bool PrimaryEvents;
        public readonly Dictionary<string, int> MKeys = new();
    }

    public static Context Install(LuaInterpreter vm, IInputSink io)
    {
        var ctx = new Context();
        object[] none = Array.Empty<object>();
        object[] One(object v) => new[] { v };
        int Int(object[] a, int i, string fn) =>
            (int)(LuaInterpreter.TryNum(i < a.Length ? a[i] : null) ?? throw new LuaException($"bad argument #{i + 1} to '{fn}' (number expected)"));

        // ── timing and log ──
        vm.Register("Sleep", a =>
        {
            double ms = a.Length > 0 ? LuaInterpreter.TryNum(a[0]) ?? 0 : 0;
            vm.CheckStop();
            // In pieces, so Esc / turning the macro off stops a long Sleep at once.
            double left = ms;
            while (left > 0)
            {
                double piece = Math.Min(left, 25);
                io.Sleep(piece);
                left -= piece;
                vm.CheckStop();
            }
            return none;
        });
        vm.Register("GetRunningTime", _ => One((double)ctx.Started.ElapsedMilliseconds));
        vm.Register("GetDate", a => One(LuaInterpreter.DateFormat(a.Length > 0 && a[0] is string f ? f : "%c")));
        Func<object[], object[]> log = a =>
        {
            if (a.Length == 0) return none;
            var text = a[0] is string f ? LuaInterpreter.Format(f, a[1..]) : LuaInterpreter.ToStr(a[0]);
            io.Log(text.TrimEnd('\n', '\r'));
            return none;
        };
        vm.Register("OutputLogMessage", log);
        vm.Register("OutputDebugMessage", log);
        vm.Log = io.Log;
        vm.Register("ClearLog", _ => { io.Log("\f"); return none; });

        // ── keyboard ──
        object[] KeyCall(object[] a, bool down, bool up, string fn)
        {
            var scans = new List<int>();
            foreach (var k in a)
            {
                int sc = Input.Keys.ScanOfName(k);
                if (sc <= 0) throw new LuaException($"unknown key '{LuaInterpreter.ToStr(k)}'");
                scans.Add(sc);
            }
            if (down) foreach (var sc in scans) io.Key(sc, true);
            if (down && up) io.Sleep(1);
            if (up) foreach (var sc in scans) io.Key(sc, false);
            return none;
        }
        vm.Register("PressKey", a => KeyCall(a, true, false, "PressKey"));
        vm.Register("ReleaseKey", a => KeyCall(a, false, true, "ReleaseKey"));
        vm.Register("PressAndReleaseKey", a => KeyCall(a, true, true, "PressAndReleaseKey"));
        vm.Register("IsModifierPressed", a =>
        {
            var name = a.Length > 0 ? LuaInterpreter.ToStr(a[0]).ToLowerInvariant() : "";
            int vk = name switch
            {
                "shift" => 0x10, "ctrl" => 0x11, "alt" => 0x12,
                "lshift" => 0xA0, "rshift" => 0xA1, "lctrl" => 0xA2, "rctrl" => 0xA3, "lalt" => 0xA4, "ralt" => 0xA5,
                _ => throw new LuaException($"unknown modifier '{name}'"),
            };
            return One(io.IsDown(vk));
        });
        vm.Register("IsKeyLockOn", a =>
        {
            var name = a.Length > 0 ? LuaInterpreter.ToStr(a[0]).ToLowerInvariant() : "";
            int vk = name switch { "scrolllock" => 0x91, "capslock" => 0x14, "numlock" => 0x90, _ => throw new LuaException($"unknown lock key '{name}'") };
            return One(io.IsLockOn(vk));
        });

        // ── mouse ──
        int Btn(object[] a, string fn)
        {
            int n = Int(a, 0, fn);
            int b = ButtonFromFunctionNumber(n);
            if (b < 0) throw new LuaException($"mouse button {n} doesn't exist (1–5)");
            return b;
        }
        vm.Register("PressMouseButton", a => { io.Button(Btn(a, "PressMouseButton"), true); return none; });
        vm.Register("ReleaseMouseButton", a => { io.Button(Btn(a, "ReleaseMouseButton"), false); return none; });
        vm.Register("PressAndReleaseMouseButton", a =>
        {
            int b = Btn(a, "PressAndReleaseMouseButton");
            io.Button(b, true);
            io.Sleep(1);
            io.Button(b, false);
            return none;
        });
        vm.Register("IsMouseButtonPressed", a => One(io.IsDown(Input.Keys.ButtonVk[Btn(a, "IsMouseButtonPressed")])));
        vm.Register("MoveMouseRelative", a => { io.Move(Int(a, 0, "MoveMouseRelative"), Int(a, 1, "MoveMouseRelative")); return none; });
        vm.Register("MoveMouseTo", a => { io.MoveTo(Int(a, 0, "MoveMouseTo"), Int(a, 1, "MoveMouseTo")); return none; });
        vm.Register("MoveMouseToVirtual", a => { io.MoveTo(Int(a, 0, "MoveMouseToVirtual"), Int(a, 1, "MoveMouseToVirtual")); return none; });
        vm.Register("MoveMouseWheel", a => { io.Wheel(Int(a, 0, "MoveMouseWheel")); return none; });
        vm.Register("GetMousePosition", _ => { var (x, y) = io.MousePosition(); return new object[] { (double)x, (double)y }; });
        vm.Register("EnablePrimaryMouseButtonEvents", a => { ctx.PrimaryEvents = a.Length > 0 && LuaInterpreter.Truthy(a[0]); return none; });

        // ── M keys (profiles' memory banks on Logitech keyboards) ──
        vm.Register("GetMKeyState", a => One((double)(ctx.MKeys.TryGetValue(a.Length > 1 ? LuaInterpreter.ToStr(a[1]) : "kb", out var m) ? m : 1)));
        vm.Register("SetMKeyState", a => { ctx.MKeys[a.Length > 1 ? LuaInterpreter.ToStr(a[1]) : "kb"] = Math.Clamp(Int(a, 0, "SetMKeyState"), 1, 3); return none; });

        // ── things only Logitech hardware has: accepted, nothing happens ──
        foreach (var name in new[] { "SetBacklightColor", "OutputLCDMessage", "ClearLCD", "SetSteeringWheelProperty" })
            vm.Register(name, _ => none);
        vm.Register("PlayMacro", a => { io.Log($"PlayMacro(\"{(a.Length > 0 ? LuaInterpreter.ToStr(a[0]) : "")}\") isn't available: G Hub's recorded macros live in G Hub. Use a Pulse sequence macro instead."); return none; });
        vm.Register("PressMacro", vm.Globals.Get("PlayMacro") is Builtin pm ? a => pm.Call(vm, a) : _ => none);
        vm.Register("ReleaseMacro", _ => none);
        vm.Register("AbortMacro", _ => none);
        return ctx;
    }
}
