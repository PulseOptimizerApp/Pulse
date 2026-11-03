namespace Pulse.Core;

/// <summary>
/// Turns each finding into exact, numbered steps for this PC: the right Windows menus for Windows 10 or 11,
/// the right graphics driver app, and the right BIOS key and menu names for the motherboard maker.
/// Steps are only added to findings that need action.
/// </summary>
public static class FixGuides
{
    public static void Attach(Finding f, HardwareFacts hw)
    {
        if (f.Severity == Severity.Ok || f.Severity == Severity.Unknown) return;
        var key = f.Id.Split(':')[0];
        if (key.StartsWith("gpu-driver")) key = "gpu-driver";
        switch (key)
        {
            case "mon-refresh": Refresh(f, hw); break;
            case "mon-cable": Cable(f, hw); break;
            case "mon-res": Resolution(f, hw); break;
            case "mon-vrr": AdaptiveSync(f, hw); break;
            case "monitor-port": MonitorPort(f); break;
            case "ram": MemoryProfile(f, hw); break;
            case "ram-channels": Channels(f, hw); break;
            case "ram-mixed": MixedRam(f); break;
            case "cpu-throttle": Throttling(f, hw); break;
            case "dpc": DriverLoad(f); break;
            case "gpu-driver": GpuDriver(f, hw); break;
            case "space": Space(f, hw); break;
            case "startup": Startup(f); break;
            case "defender": Defender(f); break;
            case "power": PowerPlan(f, hw); break;
            case "games-hdd": GamesOnHdd(f); break;
            case "network": Network(f, hw); break;
            case "laptop-power": LaptopPower(f, hw); break;
            case "laptop-gpu": LaptopGpu(f, hw); break;
            case "rebar": ResizableBar(f, hw); break;
            case "windowed-opt": WindowedGames(f); break;
        }
    }

    static string F(Finding f, string k, string fallback = "") => f.Facts.TryGetValue(k, out var v) ? v : fallback;

    static string RefreshPath(HardwareFacts hw) => hw.Windows11
        ? "Settings → System → Display → Advanced display → Choose a refresh rate"
        : "Settings → System → Display → Advanced display settings → Refresh rate (on older Windows 10: Display adapter properties → Monitor tab → Screen refresh rate)";

    static string DriverApp(HardwareFacts hw) => hw.Gpu switch
    {
        GpuVendor.Nvidia => "the NVIDIA app (or NVIDIA Control Panel: right-click the desktop → NVIDIA Control Panel)",
        GpuVendor.Amd => "AMD Software: Adrenalin Edition (right-click the desktop → AMD Software)",
        GpuVendor.Intel => "Intel Graphics Software (or Intel Graphics Command Center)",
        _ => "your graphics driver's control app",
    };

    // ───────── Monitors ─────────

    static void Refresh(Finding f, HardwareFacts hw)
    {
        var hz = F(f, "hz", "the highest");
        f.Steps = new()
        {
            $"Press \"Switch to {hz} Hz\" above. The screen may go black for a second.",
            "Pulse asks you to keep the new setting. If the screen stays black or looks wrong, don't touch anything: it switches back by itself after 15 seconds.",
            $"Prefer to do it yourself? Open {RefreshPath(hw)} and pick {hz} Hz.",
            "In each game, also set the refresh rate (sometimes called \"Refresh Rate\" or \"Max Frame Rate\") to match, because some games keep their own setting.",
        };
        f.Confirm = $"Run the checkup again: this monitor should show {hz} Hz. You can also open testufo.com in a browser, which shows the refresh rate it detects.";
    }

    static void Cable(Finding f, HardwareFacts hw)
    {
        var conn = F(f, "conn");
        var steps = new List<string>();
        if (conn == "HDMI")
            steps.Add("You're connected over HDMI. If the monitor and graphics card both have a DisplayPort socket, switch to a DisplayPort cable: it's the most reliable way to get high refresh rates.");
        else
            steps.Add("Use DisplayPort if both the monitor and graphics card have it. High-refresh monitors are designed around it.");
        steps.AddRange(new[]
        {
            "Use the cable that came with the monitor, or a certified one: \"DP8K\" certified for DisplayPort, or \"Ultra High Speed\" for HDMI. Old or cheap cables often top out at 60 Hz.",
            "Plug straight into the graphics card. Adapters, docks, KVM switches and USB-C hubs often limit the refresh rate.",
            "Open the monitor's own on-screen menu (the buttons on the monitor) and check the input settings: some monitors ship with DisplayPort set to an older version (like DP 1.1 or 1.2) or have FreeSync/Adaptive-Sync off.",
            $"Run the checkup again. When Windows offers {F(f, "hz", "the full")} Hz, use the \"Switch to\" button that appears.",
        });
        f.Steps = steps;
        f.Confirm = $"The checkup no longer shows this, and the monitor runs at {F(f, "hz", "its full")} Hz.";
    }

    static void Resolution(Finding f, HardwareFacts hw)
    {
        f.Steps = new()
        {
            hw.Windows11 ? "Open Settings → System → Display." : "Open Settings → System → Display.",
            $"Under \"Display resolution\", pick {F(f, "w")}×{F(f, "h")} (marked \"Recommended\").",
            "Choose \"Keep changes\".",
            "If text looks too small afterwards, raise \"Scale\" on the same page instead of lowering the resolution.",
        };
        f.Confirm = "Text looks sharp, and the checkup no longer shows this.";
    }

    static void AdaptiveSync(Finding f, HardwareFacts hw)
    {
        var hz = int.TryParse(F(f, "hz"), out var h) ? h : 144;
        var cap = Math.Max(hz - 3, 30);
        var steps = new List<string>
        {
            "In the monitor's own on-screen menu, make sure FreeSync / Adaptive-Sync is turned on (many monitors ship with it off).",
        };
        switch (hw.Gpu)
        {
            case GpuVendor.Nvidia:
                steps.Add("Right-click the desktop → NVIDIA Control Panel → Display → Set up G-SYNC.");
                steps.Add("Tick \"Enable G-SYNC, G-SYNC Compatible\", choose \"Enable for full screen mode\", select this monitor, tick \"Enable settings for the selected display model\" if shown, and press Apply.");
                steps.Add("Go to 3D Settings → Manage 3D settings and set Vertical sync to On. (With G-SYNC on, this removes tearing without the usual V-Sync delay.)");
                steps.Add($"In games: turn V-Sync off and cap the frame rate at {cap} FPS (3 below {hz} Hz). Games with NVIDIA Reflex set this cap for you.");
                break;
            case GpuVendor.Amd:
                steps.Add("Open AMD Software → Settings (gear icon) → Display, and turn AMD FreeSync on.");
                steps.Add($"In games: cap the frame rate at {cap} FPS (3 below {hz} Hz) with the in-game limiter, or Radeon Chill / Frame Rate Target Control in AMD Software.");
                break;
            case GpuVendor.Intel:
                steps.Add("Open Intel Graphics Software → Display, and turn Adaptive Sync on.");
                steps.Add($"In games: cap the frame rate at {cap} FPS (3 below {hz} Hz).");
                break;
            default:
                steps.Add("Turn on G-SYNC or FreeSync in your graphics driver's display settings.");
                steps.Add($"In games: cap the frame rate at {cap} FPS (3 below {hz} Hz).");
                break;
        }
        if (hw.Windows11)
            steps.Add("Also turn on Settings → System → Display → Graphics → Advanced graphics settings → Variable refresh rate, so older DirectX 11 games benefit too.");
        f.Steps = steps;
        f.Confirm = hw.Gpu == GpuVendor.Nvidia
            ? "NVIDIA Control Panel → Display menu (top bar) → tick \"G-SYNC Compatible Indicator\": a G-SYNC label appears in games when it's working. Untick it afterwards."
            : "Many monitors show the live refresh rate in their on-screen menu; with adaptive sync working it follows your FPS in games.";
    }

    static void MonitorPort(Finding f)
    {
        f.Steps = new()
        {
            "Look at the back of the PC. The motherboard's video ports sit in the upright strip near the USB ports. The graphics card's ports are lower down, in a horizontal row on the card itself.",
            "Unplug the monitor cable from the motherboard port and plug it into a port on the graphics card (DisplayPort if you can).",
            "The screen will go black for a few seconds while Windows switches over. You don't need to turn the PC off.",
            "If the screen stays black, plug back into the old port, update your graphics driver, then try again.",
        };
        f.Confirm = "Run the checkup again: it should say your screen is driven by your graphics card.";
    }

    // ───────── Memory ─────────

    static void MemoryProfile(Finding f, HardwareFacts hw)
    {
        var profile = hw.MemoryProfile;
        var steps = new List<string>
        {
            "Find your memory's rated speed: press \"Look up this RAM\", or check the sticker on the memory stick or its box (for example \"DDR5-6000\").",
            $"Restart the PC and tap {hw.BiosKey} repeatedly as soon as it starts, until the BIOS opens.",
        };
        steps.Add(hw.Board switch
        {
            BoardMaker.Asus => $"On ASUS boards: on the EZ Mode screen, set the \"X.M.P.\" / \"D.O.C.P.\" / \"EXPO\" option to Profile 1. (In Advanced Mode, F7: Ai Tweaker → Ai Overclock Tuner.)",
            BoardMaker.Msi => $"On MSI boards: click the \"XMP\" or \"EXPO\" button at the top of the main screen and choose Profile 1.",
            BoardMaker.Gigabyte => $"On Gigabyte boards: in Easy Mode, set \"X.M.P.\" / \"EXPO\" to Profile 1. (In Advanced Mode, F2: Tweaker → Extreme Memory Profile.)",
            BoardMaker.AsRock => $"On ASRock boards: go to OC Tweaker → DRAM Configuration → \"Load XMP Setting\" or \"Load EXPO Setting\" and choose Profile 1.",
            BoardMaker.Prebuilt => "Prebuilt PCs and most laptops lock memory settings. If you don't see an XMP or EXPO option anywhere, your memory is already running at the speed this PC allows.",
            _ => $"Find the {profile} option (often on the main screen or under a Tweaker / Overclocking / AI Tweaker menu) and choose Profile 1.",
        });
        steps.Add("Press F10 to save and exit, and confirm.");
        steps.Add("If the PC doesn't start up properly, wait: after a few failed starts most boards reset the setting automatically. Then try Profile 2, or update your BIOS and try again.");
        f.Steps = steps;
        f.Confirm = "Run the checkup again: memory speed should match the rated speed.";
    }

    static void Channels(Finding f, HardwareFacts hw)
    {
        bool one = f.Detail.Contains("one memory module");
        if (one)
        {
            f.Steps = new()
            {
                "Find your memory's exact model: press Look up this RAM, or read the label on the stick.",
                "Buy a second stick with the same speed, size and type (ideally the same model). Or buy a two-stick kit and replace this one.",
                hw.Laptop ? "Check your laptop's manual or the maker's website first: some laptops have only one slot, or memory soldered to the board." : "Install it in the slot your motherboard manual recommends for two sticks (usually A2 and B2, the 2nd and 4th slots from the CPU).",
                "Turn the PC off and unplug it before opening it, and touch a metal part of the case first to discharge static.",
            };
        }
        else
        {
            f.Steps = new()
            {
                "Turn the PC off, unplug it, and press the power button once to drain power.",
                "Open the side panel and touch a metal part of the case to discharge static.",
                "Check your motherboard manual's memory section for the two-stick slots (usually A2 and B2: the 2nd and 4th slots counting from the CPU).",
                "Press the clips at the ends of the slots to release the sticks, move them to those slots, and press down firmly until the clips click.",
                "Start the PC. The first start can take a little longer while it re-trains the memory.",
            };
        }
        f.Confirm = "Run the checkup again: it should show the modules on two different channels.";
    }

    static void MixedRam(Finding f)
    {
        f.Steps = new()
        {
            "Mixed sticks run at the speed of the slowest one, and sometimes less stably.",
            "For the best result, use a matched kit (two identical sticks sold together).",
        };
    }

    // ───────── CPU, drivers, storage ─────────

    static void Throttling(Finding f, HardwareFacts hw)
    {
        var steps = new List<string>();
        if (hw.Laptop)
        {
            steps.Add("Plug the laptop in and set Settings → System → Power & battery → Power mode to Best performance.");
            steps.Add("Use it on a hard, flat surface so air can reach the vents underneath. A laptop stand helps a lot.");
            steps.Add("Open your laptop maker's app (for example Armoury Crate, Lenovo Vantage, MSI Center, Alienware Command Center) and choose its Performance or Turbo mode.");
        }
        steps.Add("Check temperatures: install HWiNFO (free, hwinfo.com), choose Sensors-only, and watch the CPU temperature while a game runs. Above about 95 °C means it's overheating.");
        steps.Add("Clean the dust: turn the PC off, take it outside, and blow out the fans and filters with compressed air. Hold the fans still so they don't over-spin.");
        if (!hw.Laptop)
        {
            steps.Add("Check the CPU cooler is firmly mounted and its fan is plugged into the CPU_FAN header on the motherboard.");
            steps.Add("If the cooler is several years old, replacing the thermal paste (or the cooler) usually brings temperatures well down.");
        }
        f.Steps = steps;
        f.Confirm = "Run the checkup again: under load the CPU should run near 100% of its base speed with no meaningful throttling.";
    }

    static void DriverLoad(Finding f)
    {
        f.Steps = new()
        {
            "Download LatencyMon (free, resplendence.com) and run it as administrator.",
            "Press the green Start button and use the PC normally for 5 minutes, then open the Drivers tab and sort by \"Highest execution\".",
            "The driver at the top is the culprit. Common ones: ndis.sys or a Wi-Fi driver (network), Realtek or Nahimic (audio), storport.sys (storage), nvlddmkm.sys (NVIDIA).",
            "Update that driver from the device maker's website, or for your motherboard, from its support page (chipset, LAN and audio drivers).",
            "Uninstall RGB, fan-control and monitoring tools you don't need; they're a frequent cause.",
            "If it's a Wi-Fi or Bluetooth adapter you don't use, disable it in Device Manager.",
        };
        f.Confirm = "Run the checkup again: driver load at idle should be well under 2%.";
    }

    static void GpuDriver(Finding f, HardwareFacts hw)
    {
        f.Steps = hw.Gpu switch
        {
            GpuVendor.Nvidia => new()
            {
                "Open the NVIDIA app (install it from nvidia.com/en-us/software/nvidia-app if you don't have it).",
                "Go to Drivers, and press Download, then Express Installation.",
                "Restart the PC when it finishes.",
            },
            GpuVendor.Amd => new()
            {
                "Right-click the desktop and open AMD Software: Adrenalin Edition.",
                "On the Home tab, press Check for Updates, then Download & Install the Recommended driver.",
                "Restart the PC when it finishes.",
            },
            GpuVendor.Intel => new()
            {
                "Open Intel Graphics Software (or download Intel Driver & Support Assistant from intel.com).",
                "Install the latest graphics driver it offers.",
                "Restart the PC when it finishes.",
            },
            _ => new() { "Download the latest driver from the official site using the button above.", "Restart the PC when it finishes." },
        };
        f.Steps.Add("Only download drivers from the official site or app. Driver-updater tools often bundle adware.");
        f.Confirm = "Run the checkup again: the driver date should be recent.";
    }

    static void Space(Finding f, HardwareFacts hw)
    {
        f.Steps = new()
        {
            "Open Pulse's Clean up page, tick what you're happy to remove, and press Clean selected.",
            "Open Settings → System → Storage to see what's taking the space, and use \"Cleanup recommendations\" there too.",
            "Uninstall games you no longer play: Settings → Apps → Installed apps, or right-click the game in Steam → Manage → Uninstall.",
            "Move large games to another drive: in Steam, right-click a game → Properties → Installed Files → Move install folder.",
        };
        f.Confirm = "Run the checkup again: at least 15–20% of the drive should be free.";
    }

    static void Startup(Finding f)
    {
        f.Steps = new()
        {
            "Open Pulse's Startup apps page.",
            "Turn off apps marked \"Safe to turn off\". They'll still work when you open them yourself.",
            "Keep apps marked \"Keep on\" (anti-cheat, audio, mouse/keyboard software).",
            "For apps marked \"Check first\", look at the publisher. Unknown or unsigned ones are worth a startup check on the Security page.",
        };
        f.Confirm = "Restart the PC: it should reach a usable desktop faster.";
    }

    static void Defender(Finding f)
    {
        f.Steps = new()
        {
            "Open Windows Security (search for it in Start) → Virus & threat protection.",
            "Under \"Virus & threat protection settings\", press Manage settings and turn Real-time protection on.",
            "Back on the previous screen, under \"Virus & threat protection updates\", press Protection updates → Check for updates.",
            "Run a Quick scan from Pulse's Security page.",
        };
        f.Confirm = "Pulse's Security page shows \"Protected\".";
    }

    static void PowerPlan(Finding f, HardwareFacts hw)
    {
        f.Steps = hw.Windows11
            ? new() { "Open Settings → System → Power & battery.", "Set Power mode to Best performance (when plugged in).", "On a laptop, plug it in while gaming." }
            : new() { "Open Control Panel → Hardware and Sound → Power Options.", "Choose Balanced or High performance instead of Power saver.", "On a laptop, plug it in while gaming." };
        f.Confirm = "Run the checkup again: the power plan should no longer be Power saver.";
    }

    static void GamesOnHdd(Finding f)
    {
        f.Steps = new()
        {
            "Steam: first add a library on your SSD: Steam → Settings → Storage → the drive drop-down → Add Drive → pick the SSD.",
            "Then right-click each game → Properties → Installed Files → Move install folder → choose the SSD. Nothing is re-downloaded.",
            "Epic Games: copy the game's folder to the SSD, then in Epic click Install on the game and choose the SSD folder that contains it. Epic checks the files instead of downloading again. Delete the old copy afterwards.",
            "Battle.net and EA app: use the game's settings (gear icon) → Move or Change location.",
            "Move the games you play most first; small games barely benefit.",
        };
        f.Confirm = "Run the checkup again: the moved games should no longer be listed.";
    }

    static void Network(Finding f, HardwareFacts hw)
    {
        if (f.Title.Contains("Ethernet"))
        {
            f.Steps = new()
            {
                "Replace the cable with a Cat5e or Cat6 cable (printed on the cable). Damaged or very old cables often drop to 100 Mbps.",
                "Try a different port on the router, and make sure the plug clicks in fully.",
                "Check the speed: Settings → Network & internet → Ethernet → Link speed should say 1000/1000 Mbps or more.",
            };
            f.Confirm = "Run the checkup again: the link should show 1 Gbps or more.";
            return;
        }
        f.Steps = new()
        {
            "Best fix: run an Ethernet cable from the router to the PC. Even a long flat cable along the wall beats Wi-Fi for ping.",
            "If a cable isn't possible: connect to your router's 5 GHz or 6 GHz network (often the name ending in \"-5G\"), not 2.4 GHz.",
            "Move the router higher and into the open, or the PC closer to it. Walls, floors and fish tanks block Wi-Fi.",
            "Pause big downloads and streams on other devices while you play.",
            "Still laggy? A powerline adapter pair (network over your home's power wiring) is a good middle option.",
        };
        f.Confirm = "Run the checkup again: it should show a wired connection, or 5 GHz with good signal.";
    }

    static void LaptopPower(Finding f, HardwareFacts hw)
    {
        f.Steps = new()
        {
            "Plug the charger in whenever you play.",
            "Open Settings → System → Power & battery and set Power mode to Best performance.",
            "In your laptop maker's app (Armoury Crate, Lenovo Vantage, MSI Center, Omen Gaming Hub...), choose the Performance or Turbo profile.",
        };
        f.Confirm = "Run the checkup again: it should show the laptop plugged in.";
    }

    static void LaptopGpu(Finding f, HardwareFacts hw)
    {
        var steps = new List<string>
        {
            "Open Settings → System → Display → Graphics.",
            "Find your game in the list (or press Browse / Add desktop app and pick the game's .exe), then choose Options → High performance → Save.",
        };
        if (hw.Gpu == GpuVendor.Nvidia)
            steps.Add("Also in NVIDIA Control Panel → Manage 3D settings → Program Settings: pick the game and set \"Preferred graphics processor\" to High-performance NVIDIA processor.");
        steps.Add("For the most FPS, look in your laptop maker's app for a MUX switch: \"Ultimate\" / \"dGPU only\" in Armoury Crate, Hybrid mode off in Lenovo Vantage, \"Discrete Graphics Mode\" in MSI Center. It needs a restart.");
        f.Steps = steps;
        f.Confirm = "Start the game, open Task Manager → Performance: the graphics card (GPU 1) should be the one busy.";
    }

    static void ResizableBar(Finding f, HardwareFacts hw)
    {
        var where = hw.Board switch
        {
            BoardMaker.Asus => "Advanced → PCI Subsystem Settings",
            BoardMaker.Msi => "Settings → Advanced → PCI Subsystem Settings (or the \"Re-Size BAR\" toggle on the main screen)",
            BoardMaker.Gigabyte => "Settings → IO Ports",
            BoardMaker.AsRock => "Advanced → Chipset Configuration",
            _ => "the PCI / PCIe settings (often under Advanced)",
        };
        f.Steps = new()
        {
            hw.Gpu == GpuVendor.Nvidia ? "Check first: NVIDIA Control Panel → System Information (bottom left) → look for \"Resizable BAR: Yes\". If it says Yes, you're done."
                : "Check first: AMD Software → Performance → Tuning, and look for Smart Access Memory. If it's already on, you're done.",
            "Update your motherboard BIOS from the maker's support page first; older versions may not have the option.",
            $"Restart and tap {hw.BiosKey} to open the BIOS, then go to {where}.",
            "Set \"Above 4G Decoding\" to Enabled and \"Re-Size BAR Support\" to Enabled (or Auto).",
            "This needs UEFI boot. If the option is greyed out, CSM (legacy boot) is probably on. Only turn CSM off if Windows is installed in UEFI mode; check first with Start → System Information → BIOS Mode = UEFI.",
            "Press F10 to save and exit.",
        };
        f.Confirm = hw.Gpu == GpuVendor.Nvidia ? "NVIDIA Control Panel → System Information shows \"Resizable BAR: Yes\"." : "AMD Software shows Smart Access Memory as on.";
    }

    static void WindowedGames(Finding f)
    {
        f.Steps = new()
        {
            "Open Settings → System → Display → Graphics.",
            "Press \"Change default graphics settings\" (or \"Advanced graphics settings\").",
            "Turn on \"Optimizations for windowed games\", and \"Variable refresh rate\" if your monitor supports G-SYNC or FreeSync.",
        };
        f.Confirm = "Run the checkup again: this should show as on.";
    }
}
