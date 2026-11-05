using Pulse.Core.Input;
using Xunit;
using Pulse.Core.Input.Lua;

namespace PulseOptimizer.Tests;

/// <summary>Records what a script asked the PC to do.</summary>
sealed class FakeInput : IInputSink
{
    public readonly List<string> Did = new();
    public readonly HashSet<int> Held = new();
    public readonly List<string> Logs = new();
    public double Slept;

    public void Key(int scan, bool down) => Did.Add($"key {scan:x} {(down ? "down" : "up")}");
    public void Button(int button, bool down) => Did.Add($"button {button} {(down ? "down" : "up")}");
    public void Move(int dx, int dy) => Did.Add($"move {dx},{dy}");
    public void MoveTo(int x, int y) => Did.Add($"moveto {x},{y}");
    public void Wheel(int clicks) => Did.Add($"wheel {clicks}");
    public void Type(string text) => Did.Add($"type {text}");
    public (int X, int Y) MousePosition() => (100, 200);
    public bool IsDown(int vk) => Held.Contains(vk);
    public bool IsLockOn(int vk) => vk == 0x14;
    public void Sleep(double ms) => Slept += ms;
    public void Log(string line) => Logs.Add(line);
}

public class LuaTests
{
    static LuaInterpreter Run(string src, List<string> log = null)
    {
        var vm = new LuaInterpreter();
        if (log != null) vm.Log = log.Add;
        vm.Run(src);
        return vm;
    }

    static object Eval(string expr)
    {
        var vm = Run("result = " + expr);
        return vm.Globals.Get("result");
    }

    [Theory]
    [InlineData("1 + 2 * 3", 7.0)]
    [InlineData("(1 + 2) * 3", 9.0)]
    [InlineData("2 ^ 3 ^ 2", 512.0)]
    [InlineData("-2 ^ 2", -4.0)]
    [InlineData("7 % 3", 1.0)]
    [InlineData("-7 % 3", 2.0)]
    [InlineData("7 // 2", 3.0)]
    [InlineData("#\"hello\"", 5.0)]
    [InlineData("#{1, 2, 3}", 3.0)]
    [InlineData("math.max(3, 9, 4)", 9.0)]
    [InlineData("math.floor(3.7)", 3.0)]
    [InlineData("tonumber(\"0x10\")", 16.0)]
    [InlineData("tonumber(\"ff\", 16)", 255.0)]
    [InlineData("select(\"#\", 1, 2, 3)", 3.0)]
    [InlineData("\"10\" + 5", 15.0)]
    public void Arithmetic(string expr, double expected) => Assert.Equal(expected, (double)Eval(expr), 9);

    [Theory]
    [InlineData("\"a\" .. \"b\" .. 1", "ab1")]
    [InlineData("string.format(\"%d CPS at %.1f%%\", 16, 99.26)", "16 CPS at 99.3%")]
    [InlineData("string.format(\"%05.2f|%-4s|%x\", 3.14159, \"ab\", 255)", "03.14|ab  |ff")]
    [InlineData("string.format(\"%s %s\", nil, true)", "nil true")]
    [InlineData("(\"hello\"):upper()", "HELLO")]
    [InlineData("string.sub(\"hello\", 2, -2)", "ell")]
    [InlineData("string.rep(\"ab\", 3)", "ababab")]
    [InlineData("table.concat({1, 2, 3}, \", \")", "1, 2, 3")]
    [InlineData("tostring(1.5)", "1.5")]
    [InlineData("tostring(10)", "10")]
    [InlineData("type({})", "table")]
    [InlineData("nil and 1 or \"x\"", "x")]
    public void Strings(string expr, string expected) => Assert.Equal(expected, Eval(expr));

    [Fact]
    public void Loops_tables_and_closures()
    {
        var log = new List<string>();
        Run(@"
            local t = {}
            for i = 1, 5 do t[#t + 1] = i * i end
            local sum = 0
            for _, v in ipairs(t) do sum = sum + v end
            print(sum)

            local function counter()
              local n = 0
              return function() n = n + 1; return n end
            end
            local c = counter()
            c(); c()
            print(c())

            local keys = {}
            for k, v in pairs({ a = 1, b = 2 }) do keys[#keys + 1] = k .. v end
            table.sort = nil
            print(#keys)

            local i = 0
            repeat local j = i; i = i + 1 until j >= 3
            print(i)

            local w = 10
            while true do w = w - 3; if w < 0 then break end end
            print(w)

            for i = 10, 1, -4 do io = (io or '') .. i end
            print(io)

            local obj = { n = 2 }
            function obj:double() return self.n * 2 end
            print(obj:double())

            local function va(...) local a, b = ... ; return select('#', ...), a, b end
            print(va(7, 8, 9))
            print(string.format('%q', 'say ""hi""'))
        ", log);
        Assert.Equal(new[] { "55", "3", "2", "4", "-2", "1062", "4", "3\t7\t8", "\"say \\\"hi\\\"\"" }, log);
    }

    [Fact]
    public void Errors_say_the_line()
    {
        var ex = Assert.Throws<LuaException>(() => Run("local a = 1\nlocal b = nil\nlocal c = a + b"));
        Assert.Equal(3, ex.Line);
        Assert.Contains("arithmetic", ex.Message);
        var call = Assert.Throws<LuaException>(() => Run("\n\nDoesNotExist()"));
        Assert.Equal(3, call.Line);
        Assert.Contains("DoesNotExist", call.Message);
        Assert.Throws<LuaException>(() => Run("function OnEvent( end"));
    }

    [Fact]
    public void Pcall_catches_errors()
    {
        var log = new List<string>();
        Run("local ok, err = pcall(function() error('boom') end) print(ok, err)", log);
        Assert.Equal("false\tline 1: boom", log[0]);
    }

    [Fact]
    public void Stop_ends_a_script_stuck_in_a_loop()
    {
        var vm = new LuaInterpreter();
        var t = new Thread(() => { try { vm.Run("while true do end"); } catch (LuaStopped) { } });
        t.Start();
        Thread.Sleep(50);
        vm.Stop();
        Assert.True(t.Join(2000));
    }

    [Fact]
    public void Runaway_recursion_is_an_error_not_a_crash()
    {
        var ex = Assert.Throws<LuaException>(() => Run("local function f() return f() + 1 end f()"));
        Assert.Contains("stack overflow", ex.Message);
    }

    [Fact]
    public void Scripts_have_no_file_or_os_access()
    {
        var vm = new LuaInterpreter();
        foreach (var name in new[] { "io", "require", "dofile", "loadfile", "load", "loadstring", "debug", "package" })
            Assert.Null(vm.Globals.Get(name));
        var os = (LuaTable)vm.Globals.Get("os");
        Assert.Null(os.Get("execute"));
        Assert.Null(os.Get("remove"));
        Assert.NotNull(os.Get("time"));
    }
}

public class GHubScriptTests
{
    static (LuaInterpreter Vm, FakeInput Io, GHubApi.Context Ctx) Load(string src)
    {
        var vm = new LuaInterpreter();
        var io = new FakeInput();
        var ctx = GHubApi.Install(vm, io);
        vm.Run(src);
        return (vm, io, ctx);
    }

    [Fact]
    public void Mouse_button_numbers_match_GHub()
    {
        // In functions: 1 left, 2 middle, 3 right. In events: 1 left, 2 right, 3 middle.
        var (vm, io, _) = Load("PressMouseButton(1) ReleaseMouseButton(1) PressMouseButton(2) PressMouseButton(3) PressAndReleaseMouseButton(4) PressMouseButton(5)");
        Assert.Equal(new[] { "button 0 down", "button 0 up", "button 2 down", "button 1 down", "button 3 down", "button 3 up", "button 4 down" }, io.Did);
        Assert.Equal(1, GHubApi.EventNumberFromVk(Keys.VK_LBUTTON));
        Assert.Equal(2, GHubApi.EventNumberFromVk(Keys.VK_RBUTTON));
        Assert.Equal(3, GHubApi.EventNumberFromVk(Keys.VK_MBUTTON));
        Assert.Equal(4, GHubApi.EventNumberFromVk(Keys.VK_XBUTTON1));
        Assert.Equal(5, GHubApi.EventNumberFromVk(Keys.VK_XBUTTON2));
    }

    [Fact]
    public void IsMouseButtonPressed_uses_function_numbering()
    {
        var (vm, io, _) = Load("function check() return IsMouseButtonPressed(3), IsMouseButtonPressed(2) end");
        io.Held.Add(Keys.VK_RBUTTON);
        var r = vm.CallValue(vm.Globals.Get("check"), Array.Empty<object>(), 0);
        Assert.Equal(true, r[0]);  // 3 = right
        Assert.Equal(false, r[1]); // 2 = middle
    }

    [Fact]
    public void Typical_rapid_click_script_runs()
    {
        var (vm, io, ctx) = Load(@"
            EnablePrimaryMouseButtonEvents(true)
            function OnEvent(event, arg)
                OutputLogMessage(""event = %s, arg = %d\n"", event, arg)
                if event == ""MOUSE_BUTTON_PRESSED"" and arg == 5 then
                    for i = 1, 3 do
                        PressMouseButton(1)
                        Sleep(10)
                        ReleaseMouseButton(1)
                        Sleep(math.random(20, 30))
                    end
                end
            end");
        Assert.True(ctx.PrimaryEvents);
        vm.CallGlobal("OnEvent", "MOUSE_BUTTON_PRESSED", 5.0, "mouse");
        Assert.Equal(6, io.Did.Count);
        Assert.Equal("event = MOUSE_BUTTON_PRESSED, arg = 5", io.Logs[0]);
        Assert.InRange(io.Slept, 3 * 30 - 1, 3 * 40 + 1);
    }

    [Fact]
    public void Keys_by_name_and_scancode()
    {
        var (_, io, _) = Load("PressKey(\"lshift\", \"a\") ReleaseKey(\"lshift\", \"a\") PressAndReleaseKey(0x1e)");
        // lshift = 0x2a, a = 0x1e
        Assert.Equal(new[] { "key 2a down", "key 1e down", "key 2a up", "key 1e up", "key 1e down", "key 1e up" }, io.Did);
        var ex = Assert.Throws<LuaException>(() => Load("PressKey(\"notakey\")"));
        Assert.Contains("notakey", ex.Message);
    }

    [Fact]
    public void Extended_keys_get_the_extended_bit()
    {
        Assert.Equal(0x11C, Keys.ScanOfName("numenter"));
        Assert.Equal(0x148, Keys.ScanOfName("up"));
        Assert.Equal(0x11D, Keys.ScanOfName("rctrl"));
        Assert.Equal(0x1C, Keys.ScanOfName("enter"));
    }

    [Fact]
    public void Mouse_movement_wheel_and_position()
    {
        var (vm, io, _) = Load("MoveMouseRelative(5, -3) MoveMouseTo(32767, 0) MoveMouseWheel(-2) x, y = GetMousePosition()");
        Assert.Equal(new[] { "move 5,-3", "moveto 32767,0", "wheel -2" }, io.Did);
        Assert.Equal(100.0, vm.Globals.Get("x"));
        Assert.Equal(200.0, vm.Globals.Get("y"));
    }

    [Fact]
    public void Modifiers_locks_mkeys_and_hardware_only_functions()
    {
        var (vm, io, _) = Load(@"
            function f() return IsModifierPressed(""shift""), IsModifierPressed(""lctrl""), IsKeyLockOn(""capslock"") end
            SetMKeyState(2)
            m = GetMKeyState()
            SetBacklightColor(255, 0, 0)
            OutputLCDMessage(""hi"")
            t = GetRunningTime()
            d = GetDate(""%Y"")");
        io.Held.Add(0x10);
        var r = vm.CallValue(vm.Globals.Get("f"), Array.Empty<object>(), 0);
        Assert.Equal(new object[] { true, false, true }, r);
        Assert.Equal(2.0, vm.Globals.Get("m"));
        Assert.IsType<double>(vm.Globals.Get("t"));
        Assert.Equal(DateTime.Now.Year.ToString(), vm.Globals.Get("d"));
    }

    [Fact]
    public void Sleep_stops_when_the_script_is_stopped()
    {
        var (vm, io, _) = Load("function OnEvent() Sleep(100000) end");
        vm.Stop();
        Assert.Throws<LuaStopped>(() => vm.CallGlobal("OnEvent", "PROFILE_ACTIVATED", 0.0, ""));
    }

    [Fact]
    public void The_page_examples_load_without_errors()
    {
        var src = File.ReadAllText(Path.Combine(Repo.Root, "src", "PulseOptimizer", "Pages", "MacrosPage.xaml.cs"));
        var examples = System.Text.RegularExpressions.Regex.Matches(src, "const string Example\\w+ =\\s*@\"((?:[^\"]|\"\")*)\";");
        Assert.True(examples.Count >= 3);
        foreach (System.Text.RegularExpressions.Match m in examples)
        {
            var (vm, _, _) = Load(m.Groups[1].Value.Replace("\"\"", "\""));
            Assert.IsAssignableFrom<LuaFunction>(vm.Globals.Get("OnEvent"));
        }
    }
}

public class MacroPolicyTests
{
    [Fact]
    public void Mouse_movement_is_blocked_in_competitive_shooters_only()
    {
        Assert.Equal("VALORANT", MacroPolicy.ShooterIn(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "explorer", "valorant-win64-shipping" }));
        Assert.Equal("Counter-Strike 2", MacroPolicy.ShooterIn(new HashSet<string> { "cs2" }));
        Assert.Equal("Fortnite", MacroPolicy.ShooterIn(new HashSet<string> { "FortniteClient-Win64-Shipping" }));
        Assert.Null(MacroPolicy.ShooterIn(new HashSet<string> { "RobloxPlayerBeta", "League of Legends", "Minecraft" }));
    }

    [Fact]
    public void Anti_cheats_are_recognised()
    {
        Assert.Equal("Riot Vanguard", MacroPolicy.AntiCheatIn(new HashSet<string> { "vgc" }));
        Assert.Equal("BattlEye", MacroPolicy.AntiCheatIn(new HashSet<string> { "BEService" }));
        Assert.Null(MacroPolicy.AntiCheatIn(new HashSet<string> { "RobloxPlayerBeta" }));
    }

    [Fact]
    public void Anti_cheat_games_stay_off_unless_the_user_allows_them()
    {
        Assert.False(new MacroOptions().AllowWithAntiCheat);
        Assert.Equal(0x1B, new MacroOptions().PanicKey);
        Assert.False(new MacroOptions().AllowMoveInShooters); // movement in shooters stays paused unless the user turns it on
    }

    [Fact]
    public void Knows_which_macros_move_the_mouse()
    {
        Assert.True(MacroPolicy.MovesMouse(new MacroDef { Kind = "sequence", Steps = { new MacroStep { Do = "move", X = 5 } } }));
        Assert.False(MacroPolicy.MovesMouse(new MacroDef { Kind = "sequence", Steps = { new MacroStep { Do = "tap", Key = 0x45 } } }));
        Assert.True(MacroPolicy.MovesMouse(new MacroDef { Kind = "script", Script = "MoveMouseRelative(1, 0)" }));
        Assert.False(MacroPolicy.MovesMouse(new MacroDef { Kind = "clicker" }));
    }

    [Fact]
    public void The_old_click_macro_carries_over()
    {
        var old = new MacroSettings { Mode = "butterfly", Cps = 18, TriggerKey = 0x06, Activation = "toggle", OnlyInGame = true };
        var m = MacroPolicy.FromOldClicker(old);
        Assert.Equal("clicker", m.Kind);
        Assert.Same(old, m.Clicker);
        Assert.Equal(0x06, m.TriggerKey);
        Assert.Equal("toggle", m.Mode);
        Assert.Equal("game", m.Where);
    }

    [Fact]
    public void Modifiers_must_match_when_set()
    {
        var m = new MacroDef { Ctrl = true };
        Assert.True(MacroPolicy.ModifiersMatch(m, vk => vk == 0x11));
        Assert.False(MacroPolicy.ModifiersMatch(m, _ => false));
        Assert.True(MacroPolicy.ModifiersMatch(new MacroDef(), _ => false));
    }

    [Fact]
    public void Humanized_waits_stay_in_range()
    {
        var rng = new Random(3);
        for (int i = 0; i < 1000; i++)
        {
            double w = MacroPolicy.StepWait(100, 20, 0.1, rng);
            Assert.InRange(w, 90, 132.0001);
        }
        Assert.Equal(100, MacroPolicy.StepWait(100, 0, 0, rng));
    }

    [Fact]
    public void Macros_save_and_load()
    {
        var list = new List<MacroDef>
        {
            new() { Name = "Farm", Kind = "sequence", TriggerKey = 0x05, Steps = { new MacroStep { Do = "tap", Key = 0x57, Ms = 30 }, new MacroStep { Do = "wait", Ms = 100, RandomMs = 20 } } },
            new() { Name = "Script", Kind = "script", Script = "function OnEvent() end", GKeys = { 0x7C } },
        };
        var back = System.Text.Json.JsonSerializer.Deserialize<List<MacroDef>>(System.Text.Json.JsonSerializer.Serialize(list));
        Assert.Equal(2, back[0].Steps.Count);
        Assert.Equal(20, back[0].Steps[1].RandomMs);
        Assert.Equal(0x7C, back[1].GKeys[0]);
    }
}

public class MacroRecorderTests
{
    [Fact]
    public void Quick_presses_become_taps_and_gaps_become_waits()
    {
        var ev = new List<(double, int, bool, int)>
        {
            (0, 0x45, true, 0), (40, 0x45, false, 0),        // E tapped for 40 ms
            (240, Keys.VK_RBUTTON, true, 0), (260, Keys.VK_RBUTTON, false, 0),
            (300, 0, false, -1),                              // scroll down
            (400, 0x57, true, 0), (500, 0x20, true, 0), (520, 0x20, false, 0), (900, 0x57, false, 0),
        };
        var steps = MacroRecorder.ToSteps(ev);
        Assert.Equal(new[] { "tap", "wait", "click", "wait", "scroll", "wait", "down", "wait", "tap", "wait", "up" }, steps.Select(s => s.Do));
        Assert.Equal(40, steps[0].Ms);
        Assert.Equal(200, steps[1].Ms);
        Assert.Equal(1, steps[2].Button);
        Assert.Equal(-1, steps[4].Y);
        Assert.Equal(380, steps[9].Ms);
    }

    [Fact]
    public void The_click_on_Stop_is_left_out()
    {
        var ev = new List<(double, int, bool, int)>
        {
            (0, 0x41, true, 0), (30, 0x41, false, 0),
            (1000, Keys.VK_LBUTTON, true, 0), (1080, Keys.VK_LBUTTON, false, 0),
        };
        var steps = MacroRecorder.ToSteps(ev, dropLastLeftClick: true);
        Assert.Single(steps);
        Assert.Equal("tap", steps[0].Do);
    }

    [Fact]
    public void Keys_held_before_recording_are_ignored()
    {
        var steps = MacroRecorder.ToSteps(new List<(double, int, bool, int)> { (0, 0x10, false, 0), (10, 0x41, true, 0), (30, 0x41, false, 0) });
        Assert.Single(steps);
    }
}
