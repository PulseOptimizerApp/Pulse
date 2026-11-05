using System.Text.Json;

namespace Pulse.Core;

public class LogEntry
{
    public DateTime Time { get; set; }
    public string Text { get; set; }
    public string Kind { get; set; } // apply, undo, clean, startup, security, restore
    public string TimeText => Time.ToString("MMM d, h:mm tt");
}

public class LatencyCheck
{
    public DateTime Date { get; set; }
    public double AvgFps { get; set; }
    public double Low1Fps { get; set; }
    public double? LatencyMs { get; set; }
    public string LatencyKind { get; set; } = "";
}

public class AppSettings
{
    public bool AutoRestorePoint { get; set; } = true;
    /// <summary>"auto" follows Windows; otherwise a language code from Loc.Languages.</summary>
    public string Language { get; set; } = "auto";
    /// <summary>Look for a newer Pulse on its GitHub releases page (nothing is installed until the user says so).</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>A version the user chose to skip.</summary>
    public string SkippedVersion { get; set; } = "";
}

public class AppState
{
    /// <summary>Tweak id → backups of everything it changed, in order.</summary>
    public Dictionary<string, List<ActionBackup>> Applied { get; set; } = new();
    public Dictionary<string, DateTime> AppliedAt { get; set; } = new();
    /// <summary>One-time fixes already run on this PC (see TweakEngine.Migrate).</summary>
    public List<string> Migrations { get; set; } = new();
    public List<LogEntry> Log { get; set; } = new();
    public DateTime? LastRestorePoint { get; set; }
    public AppSettings Settings { get; set; } = new();
    /// <summary>Times a USB input device was seen disconnecting (gates the USB suspend tweak).</summary>
    public List<DateTime> UsbDropouts { get; set; } = new();
    /// <summary>Game id → checklist step ids the user ticked on the Games page.</summary>
    public Dictionary<string, List<string>> GameChecklist { get; set; } = new();
    /// <summary>Game id → latency checks, newest first.</summary>
    public Dictionary<string, List<LatencyCheck>> GameLatency { get; set; } = new();
    /// <summary>Keep it fresh: the "good as new" snapshot and the latest checks.</summary>
    public Pulse.Core.Fresh.Snapshot Baseline { get; set; }
    public List<Pulse.Core.Fresh.Snapshot> FreshHistory { get; set; } = new();
    public bool WeeklyTuneUp { get; set; }
    /// <summary>Set by the weekly background run; shown once on the next launch.</summary>
    public string PendingReport { get; set; }
    /// <summary>Game id → backup of its settings file from before Pulse applied the pro settings.</summary>
    public Dictionary<string, Pulse.Core.Games.GameConfigBackup> GameConfigBackups { get; set; } = new();
    /// <summary>Games the user added with "Add a game".</summary>
    public List<Pulse.Core.Games.CustomGame> CustomGames { get; set; } = new();
    /// <summary>The Roblox page: launch-through-Pulse and the chosen Roblox settings.</summary>
    public Pulse.Core.Roblox.RobloxSettings Roblox { get; set; } = new();
    /// <summary>The Clips page: instant replay settings and favorite clips.</summary>
    public Pulse.Core.Clips.ClipSettings Clips { get; set; } = new();
    public Pulse.Core.Reaction.ReactionSettings Reaction { get; set; } = new();
    /// <summary>The first version's single click macro. Kept so older settings carry over into <see cref="MacroList"/>.</summary>
    public Pulse.Core.Input.MacroSettings Macros { get; set; } = new();
    /// <summary>The Macros page: every macro (null until the old click macro has been carried over).</summary>
    public List<Pulse.Core.Input.MacroDef> MacroList { get; set; }
    public Pulse.Core.Input.MacroOptions MacroOptions { get; set; } = new();
    public Pulse.Core.Games.GameSessionSettings GameSession { get; set; } = new();
}

/// <summary>Applies and undoes tweaks, keeping a backup of every value it changes on disk.</summary>
public static class TweakEngine
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseOptimizer");
    static string StateFile => Path.Combine(DataDir, "state.json");
    static readonly object Gate = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppState State { get; private set; } = Load();

    public static event Action Changed;

    static AppState Load()
    {
        try
        {
            if (File.Exists(StateFile))
                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(StateFile)) ?? new AppState();
        }
        catch
        {
            try { File.Copy(StateFile, StateFile + ".broken", true); } catch { }
        }
        return new AppState();
    }

    public static void Save()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(DataDir);
            var tmp = StateFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(State, Json));
            File.Move(tmp, StateFile, true);
        }
        Changed?.Invoke();
    }

    public static void AddLog(string kind, string text)
    {
        lock (Gate)
        {
            State.Log.Insert(0, new LogEntry { Time = DateTime.Now, Kind = kind, Text = text });
            if (State.Log.Count > 300) State.Log.RemoveRange(300, State.Log.Count - 300);
        }
        Save();
    }

    public static bool AppliedByPulse(Tweak t) => State.Applied.ContainsKey(t.Id);

    /// <summary>
    /// One-time fixes for changes older versions made. Runs at start-up, once per PC.
    /// "snip": the old background-apps tweak switched off all Store apps, which broke the Snipping Tool. It's undone
    /// exactly, and the new version (which only switches off the listed extras) is applied in its place.
    /// </summary>
    public static void Migrate()
    {
        try
        {
            if (State.Migrations.Contains("snip")) return;
            if (State.Applied.ContainsKey("background-apps"))
            {
                Revert(TweakCatalog.OldBackgroundApps);
                AddLog("undo", "Turned Windows' all-apps background switch back on: it was stopping the Snipping Tool (Win+Shift+S)");
                var replacement = TweakCatalog.All.FirstOrDefault(t => t.Id == "background-apps-2");
                if (replacement != null) try { Apply(replacement); } catch { }
            }
            State.Migrations.Add("snip");
            Save();
        }
        catch { }
    }

    /// <summary>Reads the live system to see if a tweak is in effect.</summary>
    public static void Refresh(Tweak t)
    {
        if (AppliedByPulse(t)) { t.IsOn = true; return; }
        try
        {
            var checks = t.Actions.Select(a => a.Check()).ToList();
            t.IsOn = checks.Count > 0 && checks.All(c => c == true);
        }
        catch { t.IsOn = false; }
    }

    public static void Apply(Tweak t)
    {
        if (AppliedByPulse(t)) return;
        var backups = new List<ActionBackup>();
        try
        {
            foreach (var a in t.Actions) backups.Add(a.Apply());
        }
        catch
        {
            // Roll back whatever part already went through so nothing is left half-done.
            for (int i = backups.Count - 1; i >= 0; i--)
                try { t.Actions[i].Revert(backups[i]); } catch { }
            throw;
        }
        lock (Gate)
        {
            State.Applied[t.Id] = backups;
            State.AppliedAt[t.Id] = DateTime.Now;
        }
        try { t.AfterChange?.Invoke(); } catch { }
        AddLog("apply", $"Applied “{t.Title}”");
    }

    public static void Revert(Tweak t)
    {
        State.Applied.TryGetValue(t.Id, out var backups);
        for (int i = t.Actions.Count - 1; i >= 0; i--)
        {
            var b = backups != null && i < backups.Count ? backups[i] : null;
            try { t.Actions[i].Revert(b); } catch { }
        }
        lock (Gate)
        {
            State.Applied.Remove(t.Id);
            State.AppliedAt.Remove(t.Id);
        }
        try { t.AfterChange?.Invoke(); } catch { }
        AddLog("undo", $"Undid “{t.Title}”");
    }

    /// <summary>Creates a Windows System Restore point. Temporarily lifts Windows' once-a-day limit.</summary>
    public static (bool ok, string message) CreateRestorePoint()
    {
        const string script = @"
$k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
$old = (Get-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
Set-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord -ErrorAction SilentlyContinue
try {
  Enable-ComputerRestore -Drive ""$env:SystemDrive\"" -ErrorAction SilentlyContinue
  Checkpoint-Computer -Description 'Pulse Optimizer' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop
  'OK'
} catch {
  'ERR: ' + $_.Exception.Message
} finally {
  if ($null -eq $old) { Remove-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue }
  else { Set-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -Value $old -Type DWord }
}";
        var r = Shell.PowerShell(script, 300_000);
        var o = r.Output.Trim();
        if (o.EndsWith("OK"))
        {
            State.LastRestorePoint = DateTime.Now;
            AddLog("restore", "Created a System Restore point");
            return (true, "Restore point created.");
        }
        var msg = o.Contains("ERR:") ? o[(o.IndexOf("ERR:") + 4)..].Trim() : CmdAction.FirstLine(r.Error) ?? "Unknown error";
        return (false, msg);
    }
}
