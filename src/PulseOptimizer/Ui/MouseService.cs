using System.Text.Json;
using Pulse.Core;
using Pulse.Core.Mouse;

namespace Pulse.Ui;

/// <summary>
/// Game profiles and battery alerts for the mouse picked on the Mouse page. When a game starts, its profile's DPI and
/// polling rate go onto the mouse; when it closes, the desktop profile does. The script engine only runs for the few
/// seconds that takes, then stops, so nothing stays in memory between games.
/// </summary>
public static class MouseService
{
    static MouseSettings S => TweakEngine.State.Mouse ??= new MouseSettings();
    static readonly SemaphoreSlim Busy = new(1, 1);
    static Timer _battery;

    public static void Init()
    {
        // Button remaps start with Pulse (only when there are any).
        try { Core.Input.ButtonRemap.Apply(S.Remaps); } catch { }
        GameSessionService.GameChanged += running => _ = Apply(running ? GameSessionService.CurrentGame : null);
        // The battery is checked now and then while a mouse is set up (a few seconds of work every 20 minutes).
        _battery = new Timer(_ => { if (S.Device != null && S.BatteryAlert) _ = Apply(null, onlyBattery: true); }, null, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(20));
    }

    public static void Shutdown() { _battery?.Dispose(); Core.Input.ButtonRemap.Shutdown(); }

    /// <summary>
    /// Puts <paramref name="game"/>'s profile (or the desktop one) on the mouse. Returns what was done, or why not.
    /// With <paramref name="onlyBattery"/>, only reads the battery.
    /// </summary>
    public static async Task<string> Apply(string game, bool onlyBattery = false)
    {
        var profile = onlyBattery ? null : S.For(game);
        if (S.Device == null || (!onlyBattery && (profile == null || profile.Dpi <= 0 && profile.PollingHz <= 0))) return null;
        if (!await Busy.WaitAsync(0)) return null;   // one at a time: a quick game switch just takes the next one
        MouseHost.Hold();
        int id = 0;
        var host = MouseHost.Instance;
        try
        {
            if (host == null) return null;
            var found = JsonSerializer.Deserialize<List<JsonElement>>(await host.Run("scan"))!
                .Where(e => MouseSettings.DeviceId(e.GetProperty("vendorId").GetInt32(), e.GetProperty("productId").GetInt32()) == S.Device)
                .OrderByDescending(e => e.GetProperty("score").GetDouble()).FirstOrDefault();
            if (found.ValueKind != JsonValueKind.Object) return Loc.T("Your mouse isn't connected.");
            var opened = JsonSerializer.Deserialize<JsonElement>(await host.Run("open", found.GetProperty("key").GetString()));
            id = opened.GetProperty("id").GetInt32();
            var methods = opened.GetProperty("methods").EnumerateArray().Select(m => m.GetString()).ToHashSet();
            var status = JsonSerializer.Deserialize<JsonElement>(await host.Run("status", id));
            var done = new List<string>();

            if (profile != null)
            {
                if (profile.Dpi > 0)
                {
                    if (methods.Contains("setDpi")) { await host.Run("call", id, "setDpi", JsonSerializer.Serialize(new[] { profile.Dpi })); done.Add($"{profile.Dpi} DPI"); }
                    else if (methods.Contains("setDpiStageValue") && status.TryGetProperty("activeDpiStage", out var st) && st.ValueKind == JsonValueKind.Number)
                    { await host.Run("call", id, "setDpiStageValue", JsonSerializer.Serialize(new[] { st.GetInt32(), profile.Dpi })); done.Add($"{profile.Dpi} DPI"); }
                }
                if (profile.PollingHz > 0 && methods.Contains("setPollingRate"))
                {
                    await host.Run("call", id, "setPollingRate", JsonSerializer.Serialize(new[] { profile.PollingHz }));
                    done.Add($"{profile.PollingHz} Hz");
                }
                if (done.Count > 0) TweakEngine.AddLog("mouse", $"{game ?? "Desktop"} profile: {string.Join(", ", done)}");
            }

            CheckBattery(status);
            return done.Count > 0 ? string.Join(" · ", done) : null;
        }
        catch (Exception ex)
        {
            TweakEngine.AddLog("mouse", "Couldn't apply the mouse profile: " + ex.Message);
            return ex.Message;
        }
        finally
        {
            if (id != 0) try { await host!.Run("close", id); } catch { }
            MouseHost.Release();
            Busy.Release();
        }
    }

    /// <summary>A popup once when the battery drops below the alert level (again only after it's been charged).</summary>
    static void CheckBattery(JsonElement status)
    {
        if (!status.TryGetProperty("batteryPercent", out var b) || b.ValueKind != JsonValueKind.Number) return;
        int pct = (int)b.GetDouble();
        bool charging = status.TryGetProperty("batteryState", out var bs) && bs.GetString() is "Charging" or "Charging slowly";
        int last = S.LastBattery;
        S.LastBattery = pct;
        if (!S.BatteryAlert || charging) return;
        if (pct <= S.BatteryAlertPercent && (last < 0 || last > S.BatteryAlertPercent))
        {
            var name = status.TryGetProperty("name", out var n) ? n.GetString() : Loc.T("Your mouse");
            ClipToast.Show(Loc.F("{0}: battery {1}%", name, pct), Loc.T("Charge it soon."), false);
        }
        TweakEngine.Save();
    }
}
