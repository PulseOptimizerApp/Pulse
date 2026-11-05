namespace Pulse.Core.Clips;

/// <summary>Everything the user picked on the Clips page. Saved in Pulse's state file.</summary>
public class ClipSettings
{
    /// <summary>Clips is opt-in: off until the user turns it on (that's also when FFmpeg is downloaded).</summary>
    public bool Enabled { get; set; }
    /// <summary>Seconds kept in the instant replay.</summary>
    public int ReplaySeconds { get; set; } = 60;
    /// <summary>"native", "1440", "1080", "720".</summary>
    public string Resolution { get; set; } = "native";
    public int Fps { get; set; } = 60;
    /// <summary>Video bitrate in megabits per second.</summary>
    public int BitrateMbps { get; set; } = 40;
    /// <summary>"h264", "hevc" or "av1" (only offered when the graphics card can encode it).</summary>
    public string Codec { get; set; } = "h264";
    /// <summary>Index of the display to record (0 = the graphics card's first output, usually the main screen).</summary>
    public int Monitor { get; set; }
    public bool RecordCursor { get; set; }
    /// <summary>Stretched resolutions (4:3 on a 16:9 screen) play stretched like in game, instead of in a box.</summary>
    public bool StretchToScreen { get; set; } = true;
    /// <summary>
    /// "game" = record the game's own window (Windows 11; follows it across monitors and display-mode changes, and
    /// Discord or other windows on top aren't recorded), falling back to the whole screen when that doesn't work.
    /// "screen" = always record the whole display picked in <see cref="Monitor"/>.
    /// </summary>
    public string Capture { get; set; } = "game";

    public bool GameAudio { get; set; } = true;
    public int GameVolume { get; set; } = 100;
    public bool Microphone { get; set; }
    public int MicVolume { get; set; } = 100;
    /// <summary>Also put the microphone on its own second audio track (for editing). The first track is always the mix.</summary>
    public bool SeparateMicTrack { get; set; }
    /// <summary>Moves the sound against the picture, in milliseconds (positive = sound later), if a PC's clips are out of sync.</summary>
    public int AudioOffsetMs { get; set; }
    /// <summary>
    /// Record the game, Discord and Spotify on their own sound tracks too (Windows 10 2004+), so each can be muted
    /// in a clip afterwards. The first track is still everything mixed.
    /// </summary>
    public bool AppTracks { get; set; } = true;

    /// <summary>Hotkey to save the replay: modifier flags (MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4) and a virtual-key code.</summary>
    public int HotkeyModifiers { get; set; } = 1;   // Alt
    public int HotkeyKey { get; set; } = 0x77;      // F8 → Alt+F8 (Alt+F10 belongs to NVIDIA's instant replay, F8 alone to Medal)
    public bool HotkeyTakenWarned { get; set; }

    /// <summary>A second clip hotkey with its own length (for example a quick 15 s clip). Off until the user sets it.</summary>
    public bool QuickClip { get; set; }
    public int QuickModifiers { get; set; } = 1;   // Alt
    public int QuickKey { get; set; } = 0x75;      // F6 → Alt+F6
    public int QuickSeconds { get; set; } = 15;

    /// <summary>Screenshot hotkey: saves the current frame as a PNG in the clips folder's Screenshots folder.</summary>
    public bool Screenshot { get; set; } = true;
    public int ScreenshotModifiers { get; set; } = 1; // Alt
    public int ScreenshotKey { get; set; } = 0x76;    // F7 → Alt+F7

    /// <summary>Start/stop a long recording of the whole session (written to disk as it goes).</summary>
    public bool SessionHotkey { get; set; } = true;
    public int SessionModifiers { get; set; } = 1; // Alt
    public int SessionKey { get; set; } = 0x78;    // F9 → Alt+F9

    /// <summary>Bookmark the moment: clips and recordings that include it show a marker on the timeline.</summary>
    public bool BookmarkHotkey { get; set; } = true;
    public int BookmarkModifiers { get; set; } = 1; // Alt
    public int BookmarkKey { get; set; } = 0x7B;    // F12 → Alt+F12

    /// <summary>
    /// Record which keys, mouse and controller buttons are held, so clips can show an input overlay. Only the keys
    /// the overlay shows are kept, only while recording. Off until the user turns it on.
    /// </summary>
    public bool InputOverlay { get; set; }

    /// <summary>Save a clip when you say a phrase (Windows' offline speech engine). Off by default.</summary>
    public bool VoiceClip { get; set; }
    /// <summary>The phrase ("" = the default for the app's language, like "clip that").</summary>
    public string VoicePhrase { get; set; } = "";

    /// <summary>Only keep a replay while a game is running (saves memory and GPU work the rest of the time).</summary>
    public bool OnlyWhileGaming { get; set; } = true;
    /// <summary>
    /// Keep the replay in a temporary file instead of memory (on by default): Pulse then uses almost no memory for it.
    /// Off keeps it in memory, which writes nothing to the drive.
    /// </summary>
    public bool ReplayOnDisk { get; set; } = true;
    /// <summary>
    /// Set by Pulse when this PC couldn't record smoothly at the chosen quality: the largest height and frame rate that
    /// kept up (0 = no limit). Cleared when the user picks a different quality.
    /// </summary>
    public int FitHeight { get; set; }
    public int FitFps { get; set; }
    /// <summary>The light start for PCs with integrated graphics (720p, 30 FPS) has been set once.</summary>
    public bool WeakPcStartApplied { get; set; }

    /// <summary>
    /// The bitrate actually used: the chosen one, capped for the smaller size or frame rate Pulse lowered to (a 720p
    /// 30 FPS clip doesn't need 20 Mbps, and fewer bits is less work for a small graphics chip).
    /// </summary>
    public int EffectiveBitrate()
    {
        if (FitHeight == 0 && FitFps == 0) return BitrateMbps;
        bool slow = FitFps is > 0 and <= 30;
        int cap = FitHeight is > 0 and <= 720 ? (slow ? 8 : 12) : FitHeight is > 0 and <= 1080 ? (slow ? 14 : 20) : (slow ? 20 : 40);
        return Math.Min(BitrateMbps, cap);
    }
    /// <summary>Keep clipping when the Pulse window is closed (Pulse stays in the tray).</summary>
    public bool KeepRunningInTray { get; set; } = true;
    /// <summary>Start Pulse in the tray when you sign in, so clips work without opening Pulse.</summary>
    public bool StartWithWindows { get; set; }
    public bool PlaySound { get; set; } = true;
    public bool ShowSavedPopup { get; set; } = true;
    /// <summary>After you clip in a game, Pulse opens on Clips the next time you switch to it.</summary>
    public bool OpenClipsAfterClip { get; set; } = true;

    /// <summary>"" = Videos\Pulse Clips.</summary>
    public string Folder { get; set; } = "";
    /// <summary>Oldest clips (not favorites) are deleted when the folder grows past this. 0 = no limit.</summary>
    public int StorageLimitGb { get; set; } = 30;
    public List<string> Favorites { get; set; } = new();

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Pulse Clips");

    public string ClipsFolder => string.IsNullOrWhiteSpace(Folder) ? DefaultFolder : Folder;
    public string ScreenshotsFolder => Path.Combine(ClipsFolder, "Screenshots");
}
