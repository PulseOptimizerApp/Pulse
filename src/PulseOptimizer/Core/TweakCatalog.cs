using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Pulse.Core;

/// <summary>
/// Every tweak Pulse offers, matching the research doc
/// ("Pulse Optimizer — Evidence-Based Tweak Research &amp; Measurement Plan").
/// Rules: reversible, explained in plain English, sourced, and never claims a speed-up that hasn't been measured.
/// Cut on evidence and deliberately absent: MMCSS Games-task priorities (documented as unused), timer-resolution
/// registry tweaks, and DefaultTTL / MaxUserPort / TcpTimedWaitDelay (ignored or irrelevant on current Windows). Also absent on purpose, though popular:
/// forcing MSI interrupts (can stop a PC booting), turning off disk write-cache flushing (can lose data on a power
/// cut), disabling exploit protection, and Smart App Control.
/// </summary>
public static class TweakCatalog
{
    static readonly Lazy<List<Tweak>> _all = new(Build);
    public static List<Tweak> All => _all.Value;

    static RegAction CU(string path, string name, object value, RegistryValueKind kind = RegistryValueKind.DWord, object def = null) =>
        new(RegistryHive.CurrentUser, path, name, value, kind, def);

    const string BackgroundAccess = @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications";

    /// <summary>Store extras that don't need to run in the background (never the Snipping Tool, Phone Link or chat apps).</summary>
    internal static readonly string[] BackgroundExtras =
    {
        "Microsoft.BingWeather_8wekyb3d8bbwe", "Microsoft.BingNews_8wekyb3d8bbwe", "Microsoft.WindowsMaps_8wekyb3d8bbwe",
        "Microsoft.Getstarted_8wekyb3d8bbwe", "Microsoft.GetHelp_8wekyb3d8bbwe", "Microsoft.ZuneVideo_8wekyb3d8bbwe",
        "Microsoft.People_8wekyb3d8bbwe", "Microsoft.MicrosoftSolitaireCollection_8wekyb3d8bbwe",
        "Microsoft.WindowsFeedbackHub_8wekyb3d8bbwe", "Microsoft.MicrosoftOfficeHub_8wekyb3d8bbwe",
    };

    static List<TweakAction> BackgroundAppsOff() => BackgroundExtras
        .SelectMany(app => new TweakAction[] { CU($@"{BackgroundAccess}\{app}", "Disabled", 1), CU($@"{BackgroundAccess}\{app}", "DisabledByUser", 1) })
        .ToList();

    /// <summary>
    /// The first version of "Stop Store apps running in the background" (before 0.3.4). It used Windows' all-apps
    /// switch, which also stops the Snipping Tool (Win+Shift+S shows "ms-screenclip" errors). Kept only so it can be
    /// undone exactly on PCs where it was applied.
    /// </summary>
    internal static Tweak OldBackgroundApps => new()
    {
        Id = "background-apps", Title = "Stop Store apps running in the background (old version)",
        Actions =
        {
            CU(BackgroundAccess, "GlobalUserDisabled", 1, def: 0),
            CU(@"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", 0, def: 1),
        },
    };

    static RegAction LM(string path, string name, object value, RegistryValueKind kind = RegistryValueKind.DWord, object def = null) =>
        new(RegistryHive.LocalMachine, path, name, value, kind, def);

    const string Multimedia = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    // Sources (all opened while researching; see the doc's Sources section).
    const string SrcMmcss = "https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service";
    const string SrcDo = "https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization-reference";
    const string SrcPua = "https://learn.microsoft.com/en-us/defender-endpoint/detect-block-potentially-unwanted-apps-microsoft-defender-antivirus";
    const string SrcCsp = "https://learn.microsoft.com/windows/client-management/mdm/policy-csp-system";
    const string SrcSpi = "https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow";
    const string SrcDvr = "https://www.stigviewer.com/stigs/microsoft_windows_11_v2r8/2026-05-19/finding/V-253399";
    const string SrcHags = "https://devblogs.microsoft.com/directx/wp-json/wp/v2/posts/3049";
    const string SrcWidgets = "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-newsandinterests";
    const string SrcTcpAck = "https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/registry-entry-control-tcp-acknowledgment-behavior";

    static GateResult Yes(string why = "") => new(true, why);
    static GateResult No(string why) => new(false, why);

    static readonly Regex Rtx40Plus = new(@"RTX\s*(40|50|60)\d{2}", RegexOptions.IgnoreCase);

    static List<Tweak> Build() => new()
    {
        // ═════════════ KEEP: backed by primary sources ═════════════

        new Tweak
        {
            Id = "sys-responsiveness", Category = TweakCategory.Performance, Risk = Risk.Safe,
            Evidence = Evidence.Documented, Proof = ProofMetric.FrameTime, Recommended = true,
            SourceName = "Microsoft Learn: Multimedia Class Scheduler Service", SourceUrl = SrcMmcss,
            Title = "Reserve less CPU for background tasks",
            Summary = "Lowers the share of CPU Windows keeps aside for background work while games and media run.",
            Explain = "Windows' multimedia scheduler (MMCSS) guarantees a percentage of CPU time to low-priority tasks. Microsoft documents that 10 is the lowest accepted value; anything below 10 is treated as 20.",
            Changes = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile → SystemResponsiveness = 10",
            Gain = "Only games and apps that register their threads with MMCSS are affected, so many games won't change. Run a measurement in your game to see if it helps on this PC.",
            Tradeoff = "Background downloads can be slightly slower while you play.",
            // Microsoft doesn't document the default; Pulse always backs up and restores the exact previous value.
            Actions = { LM(Multimedia, "SystemResponsiveness", 10, def: 20) }
        },
        new Tweak
        {
            Id = "game-dvr", Category = TweakCategory.Gaming, Risk = Risk.Safe,
            Evidence = Evidence.Documented, Proof = ProofMetric.FrameTime, Recommended = true,
            SourceName = "Group Policy: Windows Game Recording and Broadcasting", SourceUrl = SrcDvr,
            Title = "Turn off background game recording",
            Summary = "Uses Windows' official policy to stop Xbox Game Bar from recording gameplay in the background.",
            Explain = "Game Bar can keep recording the last few minutes of play at all times, using GPU encoding and disk writes even if you never save a clip. This sets the same Group Policy IT departments use to turn it off.",
            Changes = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR → AllowGameDVR = 0",
            Gain = "Removes background encoding work while you play. Measure it in your game: the effect depends on whether recording was active.",
            Tradeoff = "Game Bar's 'Record that' and screenshots-through-Game-Bar stop working. Medal, ShadowPlay and OBS are unaffected.",
            Actions = { LM(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0) }
        },
        new Tweak
        {
            Id = "delivery-opt", Category = TweakCategory.Network, Risk = Risk.Safe,
            Evidence = Evidence.Documented, Proof = ProofMetric.None, Recommended = true,
            SourceName = "Microsoft Learn: Delivery Optimization reference", SourceUrl = SrcDo,
            Title = "Stop sharing updates with other PCs on your network",
            Summary = "Windows downloads updates only from Microsoft instead of swapping pieces with other PCs on your home network.",
            Explain = "By default Windows shares update files with other PCs on the same network (LAN mode). Setting Download Mode to 'HTTP only' turns that peer sharing off. Internet-wide sharing is a separate mode that isn't on by default.",
            Changes = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization → DODownloadMode = 0 (Windows default: 1, LAN)",
            Gain = "Stops update traffic between PCs in your home. No speed claim for single-PC homes.",
            Tradeoff = "Homes with several PCs download each update once per PC.",
            Actions = { LM(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0) }
        },
        new Tweak
        {
            Id = "pua", Category = TweakCategory.Privacy, Risk = Risk.Safe,
            Evidence = Evidence.Documented, Proof = ProofMetric.None, Recommended = true,
            SourceName = "Microsoft Learn: Detect and block potentially unwanted applications", SourceUrl = SrcPua,
            Title = "Block adware and bundled junk",
            Summary = "Switches Microsoft Defender from only logging unwanted apps to actually blocking them.",
            Explain = "Potentially unwanted apps — adware, browser hijackers, bundled crypto miners, fake 'PC cleaners' — aren't classed as viruses. On current Defender versions this protection runs in Audit mode by default, which logs them but lets them install. Block mode stops them.",
            Changes = "Defender → PUAProtection = 1 (Block). Undo restores your previous mode.",
            Gain = "Fewer junk programs sneaking in with downloads.",
            Tradeoff = "Occasionally blocks a legitimate tool that bundles offers; you can allow it in Windows Security.",
            Gate = () => SecurityGate(),
            Actions = { new PuaAction() }
        },
        new Tweak
        {
            Id = "telemetry", Category = TweakCategory.Privacy, Risk = Risk.Safe,
            Evidence = Evidence.Documented, Proof = ProofMetric.None, Recommended = true,
            SourceName = "Microsoft Learn: Policy CSP – System (AllowTelemetry)", SourceUrl = SrcCsp,
            Title = "Lock diagnostic data to the required minimum",
            Summary = "Makes sure Windows only sends Microsoft the required diagnostic data, and locks that choice.",
            Explain = "Windows can send optional usage data if it was turned on during setup. This sets the policy to the lowest level Home and Pro allow ('required'). Microsoft documents that the 'off' level only works on Enterprise and Education, so Pulse doesn't pretend otherwise.",
            Changes = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection → AllowTelemetry = 1",
            Gain = "Privacy only. If you already chose 'required only', nothing changes.",
            Tradeoff = "Settings will show 'Some settings are managed by your organization' — normal for policy settings.",
            Actions = { LM(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1) }
        },
        new Tweak
        {
            Id = "mouse-accel", Category = TweakCategory.Input, Risk = Risk.Safe,
            Evidence = Evidence.Documented, Proof = ProofMetric.None, Recommended = true,
            SourceName = "Microsoft Learn: SystemParametersInfo (SPI_SETMOUSE)", SourceUrl = SrcSpi,
            Title = "Turn off mouse acceleration",
            Summary = "The cursor moves the same distance for the same hand movement, no matter how fast you move.",
            Explain = "'Enhance pointer precision' speeds the cursor up when you move quickly, so the same physical movement lands in different places. Turning it off gives consistent 1:1 movement. Pulse uses the official Windows API, so it applies instantly.",
            Changes = "SystemParametersInfo SPI_SETMOUSE → thresholds 0, 0, acceleration 0 (Windows default 6, 10, 1)",
            Gain = "Consistent aim and muscle memory. Games using raw input already ignore acceleration; this fixes the desktop and games that don't.",
            Tradeoff = "The cursor may feel slower on the desktop at first; raise pointer speed or DPI if needed.",
            Actions = { new MouseAccelAction() }
        },
        new Tweak
        {
            Id = "sticky-keys", Category = TweakCategory.Input, Risk = Risk.Safe,
            Evidence = Evidence.Documented, Proof = ProofMetric.None, Recommended = true,
            SourceName = "Microsoft Learn: SystemParametersInfo (SPI_SETSTICKYKEYS)", SourceUrl = SrcSpi,
            Title = "Stop the Sticky Keys pop-up",
            Summary = "Pressing Shift five times in a game no longer opens a window that kicks you out of the game.",
            Explain = "This turns off only the keyboard shortcut. Sticky Keys itself is still available in Settings if you need it.",
            Changes = "SystemParametersInfo SPI_SETSTICKYKEYS → clear the hotkey flag",
            Gain = "No accidental alt-tabs mid-match. Applies instantly.",
            Tradeoff = "None, unless you rely on the shortcut for accessibility.",
            Actions = { new StickyKeysHotkeyAction() }
        },

        new Tweak
        {
            Id = "do-bandwidth", Category = TweakCategory.Network, Risk = Risk.Safe,
            Evidence = Evidence.Documented, Proof = ProofMetric.None, Recommended = true,
            SourceName = "Microsoft Learn: Delivery Optimization reference", SourceUrl = SrcDo,
            Title = "Stop update downloads from hogging your connection",
            Summary = "Background Windows and Store update downloads use at most 30% of your bandwidth, leaving the rest for games and calls.",
            Explain = "By default Windows adjusts background update downloads on its own, which can still spike while you play. This sets Microsoft's documented policy for the maximum share of bandwidth background downloads may use. Updates you start yourself aren't limited.",
            Changes = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization → DOPercentageMaxBackgroundBandwidth = 30 (Windows default: 0, automatic)",
            Gain = "Fewer lag spikes on slower connections while updates download in the background. No change on fast connections.",
            Tradeoff = "Large background updates take longer to finish.",
            Actions = { LM(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DOPercentageMaxBackgroundBandwidth", 30) }
        },
        new Tweak
        {
            Id = "widgets", Category = TweakCategory.Privacy, Risk = Risk.Safe,
            Evidence = Evidence.Documented, Proof = ProofMetric.None,
            SourceName = "Microsoft Learn: Policy CSP – NewsAndInterests", SourceUrl = SrcWidgets,
            Title = "Turn off Widgets",
            Summary = "Removes the Widgets board (news, weather, stocks) and its taskbar button in Windows 11.",
            Explain = "Uses Microsoft's \"Allow widgets\" policy. The Widgets board loads news feeds in the background; with this off, it doesn't run at all.",
            Changes = @"HKLM\SOFTWARE\Policies\Microsoft\Dsh → AllowNewsAndInterests = 0 (Windows default: allowed)",
            Gain = "A cleaner taskbar and no news feed running in the background. No speed claim.",
            Tradeoff = "No Widgets board. Weather on the taskbar goes away too.",
            RestartNote = "Sign out needed",
            Gate = () => !SystemInfo.IsWindows11 ? No("Widgets are a Windows 11 feature.")
                : SystemInfo.OsName.Contains("Home", StringComparison.OrdinalIgnoreCase) ? No("Microsoft documents this policy for Pro, Enterprise and Education. Windows Home may ignore it; turn Widgets off from Settings → Personalization → Taskbar instead.")
                : Yes(),
            Actions = { LM(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0) }
        },

        // ═════════════ KEEP WITH CONDITIONS: gated, must be measured ═════════════

        new Tweak
        {
            Id = "hags", Category = TweakCategory.Gaming, Risk = Risk.Moderate,
            Evidence = Evidence.Gated, Proof = ProofMetric.FrameTime,
            SourceName = "Microsoft DirectX blog: Hardware Accelerated GPU Scheduling", SourceUrl = SrcHags,
            Title = "Hardware-accelerated GPU scheduling",
            Summary = "Lets the graphics card schedule its own work. Required for NVIDIA DLSS Frame Generation.",
            Explain = "Microsoft says users 'should not notice any significant changes' from this, and independent benchmarks found no general FPS gain. The real reason to turn it on is that DLSS Frame Generation on RTX 40-series and newer cards needs it.",
            Changes = @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers → HwSchMode = 2",
            Gain = "Unlocks DLSS Frame Generation. No general speed-up expected; measure in your game.",
            Tradeoff = "A few games and capture tools have had bugs with it on.",
            RestartNote = "Restart needed",
            Gate = () => SystemInfo.Gpus.Any(g => Rtx40Plus.IsMatch(g.Name))
                ? Yes("Your RTX card supports DLSS Frame Generation, which needs this on.")
                : No($"Only useful for DLSS Frame Generation on RTX 40-series or newer. Your graphics: {SystemInfo.GpuText}."),
            Actions = { LM(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, def: 1) }
        },
        new Tweak
        {
            Id = "tcp-ack", Category = TweakCategory.Network, Risk = Risk.Moderate,
            Evidence = Evidence.Gated, Proof = ProofMetric.Latency,
            SourceName = "Microsoft Learn: TcpAckFrequency", SourceUrl = SrcTcpAck,
            Title = "Turn off Nagle's algorithm and delayed ACK",
            Summary = "Windows sends and confirms small network packets right away instead of waiting to bundle them.",
            Explain = "By default Windows waits for two packets (or 200 ms) before acknowledging them (delayed ACK), and can hold back small packets to send them together (Nagle's algorithm). Setting TcpAckFrequency and TCPNoDelay to 1 sends and acknowledges each packet immediately. Microsoft advises against changing these without careful study, so Pulse only suggests it for games that use TCP and asks you to measure.",
            Changes = @"For each connected adapter: HKLM\…\Tcpip\Parameters\Interfaces\{id} → TcpAckFrequency = 1 (default 2), TCPNoDelay = 1",
            Gain = "Only games using TCP (some MMOs, Roblox chat and older titles) can benefit. Most modern shooters use UDP, and games usually turn Nagle off themselves, so many won't change.",
            Tradeoff = "Slightly more network traffic.",
            RestartNote = "Restart needed",
            Gate = () => Yes("Only helps TCP-based games. Measure ping and frame times in yours before keeping it."),
            Actions = { new TcpAckAction() }
        },
        new Tweak
        {
            Id = "power-plan", Category = TweakCategory.Performance, Risk = Risk.Moderate,
            Evidence = Evidence.Gated, Proof = ProofMetric.FrameTime,
            Title = "Ultimate Performance power plan",
            Summary = "Keeps the CPU ready to boost instead of saving power between bursts of work.",
            Explain = "The Balanced plan lets the CPU drop to low speeds and ramp back up when needed. Ultimate Performance removes those ramp-up delays. Pulse creates the plan, switches to it, and puts your old plan back on undo.",
            Changes = "Creates the 'Ultimate Performance' plan and makes it active (falls back to 'High performance').",
            Gain = "Can reduce micro-stutter in CPU-heavy games; often no change in average FPS. Measure 1% lows.",
            Tradeoff = "Higher power use and temperatures at idle.",
            Gate = () => !SystemInfo.HasBattery ? Yes()
                : SystemInfo.IsPluggedIn ? Yes("Laptop: only worth it while plugged in.")
                : No("You're on battery. This plan drains battery faster; plug in to use it."),
            Actions = { new PowerPlanAction() }
        },
        new Tweak
        {
            Id = "usb-suspend", Category = TweakCategory.Input, Risk = Risk.Safe,
            Evidence = Evidence.Gated, Proof = ProofMetric.Polling,
            Title = "Stop USB devices from going to sleep",
            Summary = "Keeps controllers, mice and headsets powered so they don't disconnect or wake up late.",
            Explain = "USB selective suspend lets Windows cut power to USB devices it thinks are idle. Waking them can cause a short delay or a disconnect. Pulse suggests this when diagnostics see a device drop out.",
            Changes = "Active power plan → USB selective suspend = Disabled (plugged in and on battery)",
            Gain = "Fixes drop-outs and wake-up lag when they're caused by power saving. No effect otherwise.",
            Tradeoff = "Slightly more power use on laptops.",
            Gate = () => PcDiagnostics.UsbDropoutsSeen
                ? Yes("Diagnostics saw a USB device disconnect recently.")
                : No("Offered when diagnostics see a controller, mouse or headset disconnect."),
            Actions = { new PowerSettingAction("2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", 0, 1) }
        },
        new Tweak
        {
            Id = "sysmain", Category = TweakCategory.Performance, Risk = Risk.Advanced,
            Evidence = Evidence.Gated, Proof = ProofMetric.DiskActivity,
            Title = "Turn off SysMain (Superfetch)",
            Summary = "Stops Windows preloading your most-used apps into memory.",
            Explain = "SysMain helps apps open faster, mostly on hard drives. On SSDs the benefit is small, and on some PCs it causes bursts of disk activity. Microsoft makes no performance claim either way, so measure disk activity before keeping it off.",
            Changes = "Sets the SysMain service to Disabled and stops it.",
            Gain = "Can reduce background disk activity on SSD systems. Measure it.",
            Tradeoff = "Apps may open a little slower.",
            Gate = () => SystemInfo.SystemDriveMedia == "SSD" ? Yes("Windows is on an SSD.")
                : No($"Keep SysMain on: Windows is on a {(SystemInfo.SystemDriveMedia == "HDD" ? "hard drive" : "drive of unknown type")}, where it helps."),
            Actions = { new ServiceAction("SysMain", 2) }
        },
        new Tweak
        {
            Id = "search-index", Category = TweakCategory.Performance, Risk = Risk.Advanced,
            Evidence = Evidence.Gated, Proof = ProofMetric.BackgroundCpu,
            Title = "Turn off Windows Search indexing",
            Summary = "Stops Windows scanning your files in the background to make search faster.",
            Explain = "The indexer keeps a database so Start and File Explorer searches are instant. It pauses during games, but can still cause disk activity.",
            Changes = "Sets the WSearch service to Disabled and stops it.",
            Gain = "Less background CPU and disk use if indexing is active. Measure background CPU.",
            Tradeoff = "File search becomes much slower and less complete. Outlook search can break.",
            Actions = { new ServiceAction("WSearch", 2) }
        },
        new Tweak
        {
            Id = "hibernate", Category = TweakCategory.Storage, Risk = Risk.Moderate,
            Evidence = Evidence.Gated, Proof = ProofMetric.FreeSpace,
            Title = "Turn off hibernation",
            Summary = "Deletes the hidden hibernation file to free space on your C: drive.",
            Explain = "Windows keeps hiberfil.sys, sized to a large part of your RAM, for hibernate and Fast Startup. If you never hibernate, that space is unused.",
            Changes = "Runs powercfg /h off (undo runs powercfg /h on)",
            Gain = $"Frees roughly {Format.Bytes(SystemInfo.TotalRam * 0.4, 0)} on this PC. Storage only, no speed claim.",
            Tradeoff = "No hibernate and no Fast Startup, so cold boots can take a few seconds longer.",
            Gate = () => SystemInfo.HasBattery
                ? No("Laptops use hibernation to save battery when the lid stays closed. Turn it off only if you never use that.")
                : Yes(),
            Actions =
            {
                new CmdAction("powercfg /h off", "powercfg /h on",
                    () => Reg.Equal(Reg.Get(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled"), 0))
            }
        },
        new Tweak
        {
            Id = "game-mode", Category = TweakCategory.Gaming, Risk = Risk.Safe,
            Evidence = Evidence.Gated, Proof = ProofMetric.None, Recommended = true,
            Title = "Turn Game Mode back on",
            Summary = "Restores Windows' default Game Mode if another tool turned it off.",
            Explain = "Game Mode is on by default. Some 'debloat' tools turn it off. This only resets it to Windows' default.",
            Changes = @"HKCU\Software\Microsoft\GameBar → AutoGameModeEnabled = 1",
            Gain = "A reset to default; no speed claim.",
            Tradeoff = "None.",
            Actions = { CU(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, def: 1) }
        },

        new Tweak
        {
            Id = "net-adapter-power", Category = TweakCategory.Network, Risk = Risk.Moderate,
            Evidence = Evidence.Gated, Proof = ProofMetric.Latency,
            Title = "Turn off network adapter power saving",
            Summary = "Stops your Ethernet and Wi-Fi adapters saving power between packets, which can add small delays.",
            Explain = "Network drivers have energy-saving options (Energy-Efficient Ethernet, Green Ethernet, power saving mode) and Windows can switch the adapter off to save power. These are the same switches as Device Manager → Network adapters → your adapter → Properties → Advanced and Power Management. Pulse only changes the ones your driver has, and keeps every old value for undo.",
            Changes = "Each physical network adapter: \"Allow the computer to turn off this device\" off, and energy-saving driver options set to Off. The adapter reconnects for a second.",
            Gain = "Can remove small delays and drop-outs when the adapter wakes from power saving. No change if your adapter doesn't save power. Measure ping.",
            Tradeoff = "Slightly more power use, mostly on laptops. Wake-on-LAN stops working. Your connection drops for a moment while it applies. A network driver update can reset these; apply again after one.",
            Gate = () => !SystemInfo.HasBattery ? Yes() : SystemInfo.IsPluggedIn ? Yes("Laptop: best while plugged in.") : No("You're on battery, where adapter power saving helps battery life. Plug in to use this."),
            Actions =
            {
                new NetAdapterAction(new()
                {
                    // Standard keywords, then Realtek, Intel and Killer power saving. Wake-on-LAN goes too: it keeps
                    // the adapter half-awake for nothing on a gaming PC.
                    ["*EEE"] = "0", ["EEE"] = "0", ["AdvancedEEE"] = "0", ["EEELinkAdvertisement"] = "0",
                    ["EnableGreenEthernet"] = "0", ["GreenEthernet"] = "0", ["PowerSavingMode"] = "0", ["GigaLite"] = "0",
                    ["AutoDisableGigabit"] = "0", ["EnablePME"] = "0", ["ULPMode"] = "0", ["SipsEnabled"] = "0",
                    ["ReduceSpeedOnPowerDown"] = "0", ["AutoPowerSaveModeEnabled"] = "0", ["EnableSavePowerNow"] = "0",
                    ["EnablePowerManagement"] = "0", ["EnableExtraPowerSaving"] = "0", ["DynamicPowerGating"] = "0",
                    ["*SelectiveSuspend"] = "0", ["*DeviceSleepOnDisconnect"] = "0", ["*PMARPOffload"] = "0", ["*PMNSOffload"] = "0",
                    ["*WakeOnMagicPacket"] = "0", ["*WakeOnPattern"] = "0", ["*ModernStandbyWoLMagicPacket"] = "0", ["S5WakeOnLan"] = "0",
                }, powerManagement: true)
            }
        },
        new Tweak
        {
            Id = "net-adapter-latency", Category = TweakCategory.Network, Risk = Risk.Moderate,
            Evidence = Evidence.Gated, Proof = ProofMetric.Latency,
            Title = "Low-latency network adapter settings",
            Summary = "Your network adapter handles each packet right away instead of batching them, and spreads the work across CPU cores.",
            Explain = "Network drivers batch packets to save CPU: interrupt moderation waits to handle several at once, receive segment coalescing merges incoming packets, and large send offload builds big chunks. Great for downloads, but each adds a little delay. This turns them off, turns flow control off, keeps standard-size frames, switches Receive Side Scaling on and raises the receive and transmit buffers within your driver's limits. Only settings your driver has are changed.",
            Changes = "Each physical adapter: Interrupt Moderation, Flow Control, Large Send Offload (IPv4/IPv6), Receive Segment Coalescing (IPv4/IPv6), Intel ITR off · Jumbo Packet = standard · RSS on · Receive Buffers up to 1024, Transmit Buffers up to 512 · Windows' global receive segment coalescing off. The adapter reconnects for a second.",
            Gain = "Can shave a little delay and jitter off each packet, mostly on wired connections. Measure ping and jitter before keeping it.",
            Tradeoff = "Slightly more CPU use during heavy downloads, and big downloads can be a little slower. A network driver update can reset these; apply again after one.",
            Actions =
            {
                new NetAdapterAction(new()
                {
                    ["*InterruptModeration"] = "0", ["ITR"] = "0", ["*FlowControl"] = "0",
                    ["*LsoV2IPv4"] = "0", ["*LsoV2IPv6"] = "0", ["*RscIPv4"] = "0", ["*RscIPv6"] = "0",
                    ["*JumboPacket"] = "min", ["*RSS"] = "1", ["*ReceiveBuffers"] = "cap:1024", ["*TransmitBuffers"] = "cap:512",
                }, rscGlobal: true)
            }
        },
        new Tweak
        {
            Id = "net-throttling", Category = TweakCategory.Network, Risk = Risk.Safe,
            Evidence = Evidence.Gated, Proof = ProofMetric.Latency,
            SourceName = "Microsoft: network throttling during multimedia playback (KB948066)", SourceUrl = "https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/network-throughput-decrease-multimedia",
            Title = "Turn off network throttling during media playback",
            Summary = "Windows stops limiting other network traffic while music or video plays.",
            Explain = "While audio or video plays, Windows' multimedia scheduler limits how many packets other programs may process (about 10 per millisecond), so playback doesn't stutter. Setting NetworkThrottlingIndex to off removes that limit.",
            Changes = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile → NetworkThrottlingIndex = 0xFFFFFFFF (off; default 10)",
            Gain = "Mainly helps fast connections (several hundred Mbps and up) downloading while music or video plays. Game traffic is tiny, so ping usually doesn't change.",
            Tradeoff = "On very old or slow PCs, music could stutter during big downloads.",
            Actions = { LM(Multimedia, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), def: 10) }
        },
        new Tweak
        {
            Id = "priority-separation", Category = TweakCategory.Performance, Risk = Risk.Moderate,
            Evidence = Evidence.Gated, Proof = ProofMetric.FrameTime,
            SourceName = "Microsoft: Win32PrioritySeparation", SourceUrl = "https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-2000-server/cc976120(v=technet.10)",
            Title = "Give the game in front more CPU time",
            Summary = "Windows gives the window you're using (your game) longer, boosted turns on the CPU over background programs.",
            Explain = "Win32PrioritySeparation controls how Windows shares CPU time. Value 0x26 (38) means short, variable time slices with the biggest boost for the program in front. Windows' default (2) is already tuned for programs, so the difference is small and depends on the game.",
            Changes = @"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl → Win32PrioritySeparation = 0x26 (default 2)",
            Gain = "Can smooth frame times when other programs run while you play. Often no change. Measure 1% lows.",
            Tradeoff = "Background work (downloads, encoding, streaming) gets a little less CPU while a game is in front.",
            Actions = { LM(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x26, def: 2) }
        },
        new Tweak
        {
            Id = "dynamic-tick", Category = TweakCategory.Performance, Risk = Risk.Advanced,
            Evidence = Evidence.Gated, Proof = ProofMetric.FrameTime,
            SourceName = "Microsoft Learn: BCDEdit /set", SourceUrl = "https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/bcdedit--set",
            Title = "Windows timer fix (turn off dynamic tick)",
            Summary = "Keeps Windows' system timer ticking steadily instead of pausing it to save power.",
            Explain = "Dynamic tick lets Windows skip timer ticks when the PC is idle, to save power. Turning it off keeps timing steady, which some players find smooths frame pacing. Pulse also removes a forced-HPET setting (useplatformclock) if another tool left it on, since that one usually makes games worse.",
            Changes = "bcdedit /set disabledynamictick yes, and bcdedit /deletevalue useplatformclock if it was set. Undo removes disabledynamictick (Windows' default).",
            Gain = "Mixed results in testing: some PCs get steadier frame times, many see no change. Measure before keeping it.",
            Tradeoff = "Slightly more idle power use. If BitLocker is on, keep your recovery key handy: boot setting changes can ask for it once.",
            RestartNote = "Restart needed",
            Gate = () => SystemInfo.HasBattery ? No("Laptops rely on dynamic tick to save battery. Desktop PCs only.") : Yes(),
            Actions =
            {
                new BcdAction("disabledynamictick", "yes"),
                new CmdAction($"\"{Path.Combine(Environment.SystemDirectory, "bcdedit.exe")}\" /deletevalue {{current}} useplatformclock >nul 2>&1 & exit /b 0", "exit /b 0", () => true),
            }
        },
        new Tweak
        {
            Id = "extra-services", Category = TweakCategory.Performance, Risk = Risk.Moderate,
            Evidence = Evidence.Gated, Proof = ProofMetric.BackgroundCpu,
            Title = "Turn off unneeded background services",
            Summary = "Stops six Windows services most gaming PCs never use: telemetry, error reporting, offline maps, store demo mode and the compatibility helper.",
            Explain = "Connected User Experiences and Telemetry (DiagTrack), WAP push messages (dmwappushservice), Downloaded Maps Manager (MapsBroker), Retail Demo, Windows Error Reporting (WerSvc) and Program Compatibility Assistant (PcaSvc). None of them are needed for games, Windows Update, Wi-Fi, sound or the Store. Windows Hello and the Snipping Tool are left alone.",
            Changes = "Sets DiagTrack, dmwappushservice, MapsBroker, RetailDemo, WerSvc and PcaSvc to Disabled and stops them. Undo puts back each service's previous start type.",
            Gain = "Less background CPU, disk and network use from these services. Usually small; measure background CPU.",
            Tradeoff = "No crash reports sent to Microsoft and no automatic compatibility fixes for old programs. Offline maps don't update.",
            Actions =
            {
                new OptionalServiceAction("DiagTrack", 2), new OptionalServiceAction("dmwappushservice", 3),
                new OptionalServiceAction("MapsBroker", 2), new OptionalServiceAction("RetailDemo", 3),
                new OptionalServiceAction("WerSvc", 3), new OptionalServiceAction("PcaSvc", 2),
            }
        },

        // ═════════════ NO PERFORMANCE CLAIM: comfort & privacy ═════════════

        new Tweak
        {
            Id = "background-apps-2", Category = TweakCategory.Privacy, Risk = Risk.Safe, Evidence = Evidence.NoPerformanceClaim,
            Title = "Stop Store apps running in the background",
            Summary = "Weather, News, Maps, Tips and other extras stop refreshing in the background for your account.",
            Explain = "Only these apps are switched off, one by one, the same as Settings → Apps → the app → Background permissions. " +
                      "Windows' all-apps switch isn't used: it also stops the Snipping Tool (Win+Shift+S). " +
                      "Pulse makes no speed claim for this.",
            Changes = "HKCU\\…\\BackgroundAccessApplications\\<app> → Disabled = 1, DisabledByUser = 1 (Weather, News, Maps, Tips, Get Help, Movies & TV, People, Solitaire, Feedback Hub, Microsoft 365 hub)",
            Gain = "Fewer apps waking up in the background.",
            Tradeoff = "Those apps refresh when you open them. Everything else, including the Snipping Tool, is untouched.",
            Actions = BackgroundAppsOff(),
        },
        new Tweak
        {
            Id = "suggestions", Category = TweakCategory.Privacy, Risk = Risk.Safe, Evidence = Evidence.NoPerformanceClaim,
            Title = "Stop suggested apps, tips and ads",
            Summary = "Stops Windows silently installing promoted apps and showing suggestions in Start and Settings.",
            Explain = "Undocumented per-user values; comfort and privacy only.",
            Changes = "HKCU\\…\\ContentDeliveryManager → SilentInstalledAppsEnabled, SystemPaneSuggestionsEnabled, SoftLandingEnabled and four SubscribedContent values = 0",
            Gain = "A cleaner Start menu, no surprise app installs.",
            Tradeoff = "No Windows tips.",
            Actions =
            {
                CU(Cdm, "SilentInstalledAppsEnabled", 0, def: 1),
                CU(Cdm, "SystemPaneSuggestionsEnabled", 0, def: 1),
                CU(Cdm, "SoftLandingEnabled", 0, def: 1),
                CU(Cdm, "SubscribedContent-338388Enabled", 0, def: 1),
                CU(Cdm, "SubscribedContent-338389Enabled", 0, def: 1),
                CU(Cdm, "SubscribedContent-353694Enabled", 0, def: 1),
                CU(Cdm, "SubscribedContent-353696Enabled", 0, def: 1),
            }
        },
        new Tweak
        {
            Id = "ad-id", Category = TweakCategory.Privacy, Risk = Risk.Safe, Evidence = Evidence.NoPerformanceClaim,
            Title = "Turn off the advertising ID",
            Summary = "Apps can no longer use a Windows ID to track your interests for ads.",
            Explain = "The same switch as Settings → Privacy → General → 'Let apps show me personalized ads'.",
            Changes = @"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo → Enabled = 0",
            Gain = "Privacy.", Tradeoff = "Ads in apps become less personalized, not fewer.",
            Actions = { CU(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, def: 1) }
        },
        new Tweak
        {
            Id = "web-search", Category = TweakCategory.Privacy, Risk = Risk.Safe, Evidence = Evidence.NoPerformanceClaim,
            Title = "Remove web results from Start search",
            Summary = "Start search looks only at your PC instead of also sending what you type to Bing.",
            Explain = "Sets a policy value. Pulse will verify it's honored on your Windows build before claiming anything.",
            Changes = @"HKCU\Software\Policies\Microsoft\Windows\Explorer → DisableSearchBoxSuggestions = 1",
            Gain = "Private Start search.", Tradeoff = "No web results in Start.",
            RestartNote = "Sign out needed",
            Actions = { CU(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1) }
        },
        new Tweak
        {
            Id = "file-ext", Category = TweakCategory.Privacy, Risk = Risk.Safe, Evidence = Evidence.NoPerformanceClaim, Recommended = true,
            Title = "Show file extensions",
            Summary = "Shows the real file type, so a virus named 'photo.jpg.exe' can't pass as a picture.",
            Explain = "The same switch as File Explorer → View → File name extensions.",
            Changes = @"HKCU\…\Explorer\Advanced → HideFileExt = 0",
            Gain = "Easier to spot fake files.", Tradeoff = "None.",
            Actions = { CU(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", 0, def: 1) }
        },
        new Tweak
        {
            Id = "storage-sense", Category = TweakCategory.Storage, Risk = Risk.Safe, Evidence = Evidence.NoPerformanceClaim,
            Title = "Turn on Storage Sense",
            Summary = "Windows clears temp files on its own when your drive runs low.",
            Explain = "The same switch as Settings → System → Storage → Storage Sense.",
            Changes = @"HKCU\…\StorageSense\Parameters\StoragePolicy → 01 = 1",
            Gain = "Storage only.", Tradeoff = "Recycle Bin items older than 30 days are removed by default.",
            Actions = { CU(@"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 1, def: 0) }
        },
        new Tweak
        {
            Id = "menu-delay", Category = TweakCategory.Visual, Risk = Risk.Safe, Evidence = Evidence.NoPerformanceClaim,
            Title = "Make menus open instantly",
            Summary = "Sub-menus open after 20 ms instead of 400 ms.",
            Explain = "Desktop feel only; doesn't affect games.",
            Changes = @"HKCU\Control Panel\Desktop → MenuShowDelay = 20",
            Gain = "Snappier desktop.", Tradeoff = "None.",
            RestartNote = "Sign out needed",
            Actions = { CU(@"Control Panel\Desktop", "MenuShowDelay", "20", RegistryValueKind.String, "400") }
        },
        new Tweak
        {
            Id = "min-anim", Category = TweakCategory.Visual, Risk = Risk.Safe, Evidence = Evidence.NoPerformanceClaim,
            Title = "Turn off minimize and maximize animations",
            Summary = "Windows open and close instantly instead of zooming.",
            Explain = "Desktop feel only.",
            Changes = @"HKCU\Control Panel\Desktop\WindowMetrics → MinAnimate = 0",
            Gain = "Alt-tab feels instant.", Tradeoff = "Less smooth-looking windows.",
            RestartNote = "Sign out needed",
            Actions = { CU(@"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", RegistryValueKind.String, "1") }
        },
        new Tweak
        {
            Id = "transparency", Category = TweakCategory.Visual, Risk = Risk.Safe, Evidence = Evidence.NoPerformanceClaim,
            Title = "Turn off transparency effects",
            Summary = "Taskbar, Start and windows become solid instead of see-through.",
            Explain = "The same switch as Settings → Personalization → Colors → Transparency effects. Might save a little GPU work on integrated graphics; not claimed until measured.",
            Changes = @"HKCU\…\Themes\Personalize → EnableTransparency = 0",
            Gain = "A flatter look.", Tradeoff = "Less visual depth.",
            Actions = { CU(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, def: 1) }
        },
        new Tweak
        {
            Id = "ai-off", Category = TweakCategory.Privacy, Risk = Risk.Safe, Evidence = Evidence.NoPerformanceClaim,
            Title = "Turn off Windows AI features",
            Summary = "Turns off Copilot, Recall, Click to Do, AI in Paint and Notepad, smart clipboard actions and Edge's Copilot sidebar.",
            Explain = "Uses Microsoft's own policies for each feature, the same ones businesses use. Nothing is uninstalled, so undo brings everything back.",
            Changes = @"WindowsCopilot\TurnOffWindowsCopilot = 1 · WindowsAI\DisableAIDataAnalysis = 1, AllowRecallEnablement = 0, DisableClickToDo = 1 · Paint\DisableCocreator, DisableImageCreator, DisableGenerativeFill = 1 · WindowsNotepad\DisableAIFeatures = 1 · SmartClipboard\Disabled = 1 · Edge\HubsSidebarEnabled = 0",
            Gain = "No AI features watching, suggesting or snapshotting in the background. No speed claim.",
            Tradeoff = "Those features go away until you undo this.",
            RestartNote = "Sign out needed",
            Actions =
            {
                CU(@"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1),
                LM(@"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1),
                LM(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1),
                LM(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecallEnablement", 0),
                CU(@"Software\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo", 1),
                LM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableCocreator", 1),
                LM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableImageCreator", 1),
                LM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableGenerativeFill", 1),
                LM(@"SOFTWARE\Policies\WindowsNotepad", "DisableAIFeatures", 1),
                CU(@"Software\Microsoft\Windows\CurrentVersion\SmartActionPlatform\SmartClipboard", "Disabled", 1),
                LM(@"SOFTWARE\Policies\Microsoft\Edge", "HubsSidebarEnabled", 0),
            }
        },
        new Tweak
        {
            Id = "data-collection", Category = TweakCategory.Privacy, Risk = Risk.Safe, Evidence = Evidence.NoPerformanceClaim,
            Title = "Stop extra data collection",
            Summary = "Stops activity history, typing and inking data collection, feedback prompts, app-launch tracking and cloud search.",
            Explain = "The same switches as Settings → Privacy & security (General, Inking & typing, Activity history, Search permissions, Diagnostics & feedback), plus the old Customer Experience Improvement Program.",
            Changes = "HttpAcceptLanguageOptOut = 1 · DisableMFUTracking = 1 · RestrictImplicitInkCollection / RestrictImplicitTextCollection = 1, HarvestContacts = 0 · feedback frequency never · PublishUserActivities = 0 · cloud content search off · CEIPEnable = 0",
            Gain = "Privacy.", Tradeoff = "Typing suggestions learn less from you; no Timeline-style activity history.",
            Actions =
            {
                CU(@"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", 1),
                CU(@"Software\Policies\Microsoft\Windows\EdgeUI", "DisableMFUTracking", 1),
                CU(@"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1, def: 0),
                CU(@"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1, def: 0),
                CU(@"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0, def: 1),
                CU(@"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0),
                LM(@"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0),
                CU(@"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsAADCloudSearchEnabled", 0, def: 1),
                CU(@"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsMSACloudSearchEnabled", 0, def: 1),
                LM(@"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable", 0),
            }
        },
        new Tweak
        {
            Id = "start-clean", Category = TweakCategory.Privacy, Risk = Risk.Safe, Evidence = Evidence.NoPerformanceClaim,
            Title = "Clean up Start",
            Summary = "Removes recommended files and websites, account nags and 'resume on this PC' prompts from Start.",
            Explain = "The same switches as Settings → Personalization → Start, plus the cross-device resume prompt from Phone Link.",
            Changes = @"Explorer\Advanced → Start_IrisRecommendations = 0, Start_AccountNotifications = 0 · CrossDeviceResume\Configuration → IsResumeAllowed = 0",
            Gain = "A calmer Start menu.", Tradeoff = "No suggestions in Start.",
            Actions =
            {
                CU(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_IrisRecommendations", 0, def: 1),
                CU(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_AccountNotifications", 0, def: 1),
                CU(@"Software\Microsoft\Windows\CurrentVersion\CrossDeviceResume\Configuration", "IsResumeAllowed", 0, def: 1),
            }
        },
        new Tweak
        {
            Id = "key-repeat", Category = TweakCategory.Input, Risk = Risk.Safe, Evidence = Evidence.NoPerformanceClaim,
            Title = "Faster key repeat",
            Summary = "Holding a key repeats it sooner, at the fastest rate.",
            Explain = "The same sliders as Control Panel → Keyboard → Repeat delay (shortest) and Repeat rate (fastest).",
            Changes = @"HKCU\Control Panel\Keyboard → KeyboardDelay = 0 (default 1), KeyboardSpeed = 31",
            Gain = "Snappier typing and menu scrolling. Games read keys directly, so no in-game change.",
            Tradeoff = "Easier to type a letter twice by accident at first.",
            RestartNote = "Sign out needed",
            Actions =
            {
                CU(@"Control Panel\Keyboard", "KeyboardDelay", "0", RegistryValueKind.String, "1"),
                CU(@"Control Panel\Keyboard", "KeyboardSpeed", "31", RegistryValueKind.String, "31"),
            }
        },
        new Tweak
        {
            Id = "access-popups", Category = TweakCategory.Input, Risk = Risk.Safe, Evidence = Evidence.NoPerformanceClaim, Recommended = true,
            Title = "Stop the Filter Keys and Toggle Keys pop-ups",
            Summary = "Holding Shift for 8 seconds or Num Lock for 5 no longer opens a window over your game.",
            Explain = "Turns off only the keyboard shortcuts. Both features are still in Settings → Accessibility → Keyboard if you need them.",
            Changes = @"HKCU\Control Panel\Accessibility\Keyboard Response → Flags = 122 (default 126) · ToggleKeys → Flags = 58 (default 62)",
            Gain = "No accidental alt-tabs mid-match.", Tradeoff = "None, unless you use these shortcuts for accessibility.",
            RestartNote = "Sign out needed",
            Actions =
            {
                CU(@"Control Panel\Accessibility\Keyboard Response", "Flags", "122", RegistryValueKind.String, "126"),
                CU(@"Control Panel\Accessibility\ToggleKeys", "Flags", "58", RegistryValueKind.String, "62"),
            }
        },
        new Tweak
        {
            Id = "taskbar-anim", Category = TweakCategory.Visual, Risk = Risk.Safe, Evidence = Evidence.NoPerformanceClaim,
            Title = "Turn off taskbar animations and Peek",
            Summary = "Taskbar buttons stop animating, and pointing at the corner no longer fades all windows.",
            Explain = "Desktop feel only.",
            Changes = @"Explorer\Advanced → TaskbarAnimations = 0 · DWM → EnableAeroPeek = 0",
            Gain = "A snappier taskbar.", Tradeoff = "Less animated look.",
            RestartNote = "Sign out needed",
            Actions =
            {
                CU(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 0, def: 1),
                CU(@"Software\Microsoft\Windows\DWM", "EnableAeroPeek", 0, def: 1),
            }
        },
    };

    static GateResult SecurityGate()
    {
        var r = Shell.PowerShell("(Get-MpComputerStatus).AntivirusEnabled", 30_000);
        return r.Output.Trim().EndsWith("True", StringComparison.OrdinalIgnoreCase)
            ? Yes()
            : No("Microsoft Defender isn't your active antivirus, so this setting would have no effect.");
    }
}
