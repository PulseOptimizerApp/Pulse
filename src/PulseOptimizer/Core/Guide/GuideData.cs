namespace Pulse.Core.Guide;

/// <summary>
/// The PC guide: what helps most, what hardware costs, the gear and prebuilt PCs reviewers rate best, and hardware news.
/// Researched on <see cref="Updated"/> from the linked sources; it ships with each Pulse release, so prices age until
/// the next update (the page says how old they are).
/// </summary>
public static class GuideData
{
    public static readonly DateTime Updated = new(2026, 11, 7);

    // ───────── 1. What helps most ─────────

    public sealed record Tip(string Title, string Body, string Impact, string PulsePage, string PulseAction, string SourceName, string SourceUrl);

    public static readonly Tip[] Tips =
    {
        new("Run your monitor at its full refresh rate, from the graphics card",
            "A 144 Hz screen left at 60 Hz, or a cable in the motherboard instead of the graphics card, costs more than any tweak can win back.",
            "Huge", "diagnose", "Check my PC", null, null),
        new("Turn on XMP or EXPO in the BIOS",
            "Without it, RAM runs at its slow fallback speed (DDR4 3,200, DDR5 4,800). It's one switch and one of the biggest free gains in CPU-heavy games.",
            "High", "diagnose", "See BIOS steps", "TechRadar", "https://www.techradar.com/computing/gaming-pcs/ram-crisis-getting-you-down-heres-how-to-upgrade-your-gaming-pc-without-going-broke"),
        new("Enable Resizable BAR",
            "Lets the CPU reach all of the graphics card's memory at once. Usually a few percent, over 15% in some games. In the BIOS: CSM off, Above 4G Decoding on, Re-Size BAR on.",
            "Medium", null, null, "Club386", "https://www.club386.com/resizable-bar-on-or-off/"),
        new("Let shaders build, and stop wiping them",
            "New games compile shaders the first time an effect appears, causing hitches that fade. Let the pre-compile step finish, raise the driver's shader cache (10 GB or Unlimited), and skip cleaner apps that delete it.",
            "High", null, null, "PCGameCheck", "https://pcgamecheck.com/blog/why-is-my-game-stuttering-fix"),
        new("Use G-SYNC or FreeSync with the right frame cap",
            "Variable refresh on, V-Sync on in the driver and off in the game. With Reflex on, the driver caps for you (about 138 FPS at 144 Hz); without it, cap a few FPS under your refresh rate.",
            "High", "games", "Open game checklists", "GearGeeks", "https://geargeeksgaming.com/latency/gsync-fps-cap/"),
        new("Best performance power mode, Game Mode on, background recording off",
            "The Windows settings that still measurably help. On laptops, use Best performance only when plugged in.",
            "Medium", "tweaks", "Open tweaks", "Frozen Tweaks", "https://frozentweaks.com/guides/windows-11-gaming-optimization"),
        new("Games on an SSD, and watch the temperatures",
            "New games stutter badly on hard drives. Drops that start after 5 to 15 minutes usually mean heat: clean the dust, fix airflow, repaste or undervolt.",
            "Medium", "diagnose", "Check my PC", null, null),
        new("Memory Integrity: test before turning it off",
            "Windows turned it on for many PCs with the October 2026 update. On modern CPUs it costs under 2% in 2026 tests; older CPUs lose more. Turning it off removes real protection, so only do it if you measure a difference.",
            "Low", "measure", "Measure it", "TechFuelHQ", "https://techfuelhq.com/gaming/memory-integrity-on-or-off-2026/"),
    };

    public static readonly (string Title, string Body)[] Skip =
    {
        ("Registry cleaners", "No measurable effect on modern Windows, and a real chance of breaking installed software."),
        ("Turning off the page file", "Games and Windows expect one. Without it you get crashes when memory runs out, not speed."),
        ("Old \"FPS boost\" registry packs", "Many 2017-era tweaks no longer do anything on Windows 11 24H2 and later."),
    };

    // ───────── 2. What things cost ─────────

    /// <summary>A graphics card's launch price, best US price now, and lowest price ever seen (Tom's Hardware tracker).</summary>
    public sealed record GpuPrice(string Name, int Msrp, int Now, int Lowest, string Tier);

    public const string GpuSource = "Tom's Hardware GPU price tracker, November 2026";
    public const string GpuSourceUrl = "https://www.tomshardware.com/pc-components/gpus/lowest-gpu-prices-tracking";

    public static readonly GpuPrice[] Gpus =
    {
        new("GeForce RTX 5050", 249, 399, 229, "1080p"),
        new("GeForce RTX 5060", 299, 449, 279, "1080p"),
        new("Radeon RX 9060 XT 8GB", 299, 459, 259, "1080p"),
        new("GeForce RTX 5060 Ti 8GB", 379, 499, 319, "1080p"),
        new("Radeon RX 9060 XT 16GB", 349, 659, 349, "1080p / 1440p"),
        new("GeForce RTX 5060 Ti 16GB", 429, 769, 379, "1080p / 1440p"),
        new("Radeon RX 9070 GRE", 549, 569, 499, "1440p"),
        new("Radeon RX 9070", 549, 749, 494, "1440p"),
        new("GeForce RTX 5070", 549, 899, 479, "1440p"),
        new("Radeon RX 9070 XT", 599, 919, 599, "1440p / 4K"),
        new("GeForce RTX 5070 Ti", 749, 1399, 729, "1440p / 4K"),
    };

    public sealed record RamPrice(string Kit, string Now, string Before, bool Ddr5);

    public const string RamSource = "Capital and Compute RAM tracker, November 3, 2026";
    public const string RamSourceUrl = "https://capitalandcompute.net/memory-prices/";

    public static readonly RamPrice[] Ram =
    {
        new("16 GB DDR5 (2×8)", "$297", "", true),
        new("32 GB DDR5 (2×16)", "$429 – $600", "about $95", true),
        new("64 GB DDR5 (2×32)", "$1,360", "about $222", true),
        new("16 GB DDR4 (2×8)", "$130", "", false),
        new("32 GB DDR4 (2×16)", "$129 – $257", "", false),
        new("64 GB DDR4 (2×32)", "$552", "", false),
    };

    // ───────── 3. Best gear ─────────

    public sealed record Pick(string Label, string Name, string Why, string SourceName, string SourceUrl);
    public sealed record GearGroup(string Title, string Glyph, Pick[] Picks);

    public static readonly GearGroup[] Gear =
    {
        new("Processors", "", new Pick[]
        {
            new("Best for gaming", "AMD Ryzen 7 9800X3D", "Still the one to buy for games.", "PC Gamer", "https://www.pcgamer.com/best-cpu-for-gaming/"),
            new("Fastest", "AMD Ryzen 7 9850X3D", "About 3% faster in games, with about 30% more power draw.", "DropReference", "https://dropreference.com/en/blog/guide/best-gaming-processor"),
            new("Gaming value", "AMD Ryzen 5 7600X3D", "Most of the X3D gaming speed for less.", "G2A News", "https://www.g2a.com/news/features/hardware/best-cpu-for-gaming-in-2026-top-picks-benchmarks-and-buying-guide/"),
            new("Best Intel", "Core Ultra 7 270K Plus", "Best if you also edit or stream; about 20% behind X3D in games.", "DropReference", "https://dropreference.com/en/blog/guide/best-gaming-processor"),
        }),
        new("Graphics cards", "", new Pick[]
        {
            new("Budget", "Radeon RX 9060 XT 8GB / Arc B580", "The sub-$300 picks at launch prices.", "TechSpot", "https://www.techspot.com/bestof/gpu-25-26/"),
            new("Value", "Radeon RX 9060 XT 16GB", "Best value, with 16 GB of video memory.", "PC Gamer", "https://www.pcgamer.com/hardware/graphics-cards/graphics-card-price-watch-deals/"),
            new("1440p", "Radeon RX 9070 / GeForce RTX 5070", "The 1440p sweet spot for most players.", "TechSpot", "https://www.techspot.com/bestof/gpu-25-26/"),
            new("High end", "GeForce RTX 5070 Ti", "About 9% behind the RTX 5080 at 1440p, for less.", "VerdictBits", "https://verdictbits.com/reviews/best-gpus-2026/"),
        }),
        new("Monitors", "", new Pick[]
        {
            new("Best 1440p", "ASUS ROG Swift OLED PG27AQWP-W", "540 Hz OLED, the top pick of OLED specialists.", "TFTCentral", "https://tftcentral.co.uk/recommendations/the-best-oled-gaming-monitors-to-buy-in-2026"),
            new("500 Hz OLED", "MSI MPG 271QR X50 / MAG 272QP X50", "QD-OLED at 500 Hz for less.", "Display Ninja", "https://www.displayninja.com/best-gaming-monitor/"),
            new("Budget OLED", "Alienware AW2726DM", "The cheapest 1440p OLED, around $350.", "TechSpot", "https://www.techspot.com/bestof/oled-monitors-26/"),
        }),
        new("Mice", "", new Pick[]
        {
            new("Best overall", "Logitech G Pro X2 Superstrike", "Haptic buttons with adjustable actuation and rapid trigger.", "Tom's Guide", "https://www.tomsguide.com/round-up/best-gaming-mice"),
            new("Ergonomic", "Razer DeathAdder V4 Pro", "Top pick for right-handed shapes.", "GamesRadar+", "https://www.gamesradar.com/best-gaming-mouse-to-buy/"),
            new("Lightweight", "Corsair Sabre V2 Pro Ultralight", "36 g for $99.99.", "GamesRadar+", "https://www.gamesradar.com/best-gaming-mouse-to-buy/"),
            new("Budget wireless", "Razer Viper V3 HyperSpeed", "Great sensor without the flagship price.", "Trusted Reviews", "https://www.trustedreviews.com/best/best-gaming-mouse-3515423"),
        }),
        new("Keyboards", "", new Pick[]
        {
            new("Best overall", "Wooting 80HE", "Hall effect switches and the best rapid trigger software.", "PC Gamer", "https://www.pcgamer.com/best-gaming-keyboard/"),
            new("Wireless", "Keychron K2 HE", "Rapid trigger without the cable.", "PC Gamer", "https://www.pcgamer.com/best-gaming-keyboard/"),
            new("Budget", "Gamakay × NaughShark NS68", "Hall effect, 8K polling and rapid trigger for about $44.", "Keyboard Tester", "https://keyboardtester.click/blog/best-mechanical-keyboards-for-gaming-2026.php"),
        }),
        new("Headsets and mics", "", new Pick[]
        {
            new("Best sound", "Audeze Maxwell 2", "The best PC gaming headset in lab tests.", "RTINGS", "https://www.rtings.com/headphones/reviews/best/pc-gaming-headsets"),
            new("Best overall", "SteelSeries Arctis Nova Pro Omni", "Top pick at several outlets.", "GamesRadar+", "https://www.gamesradar.com/best-gaming-headsets/"),
            new("Wireless value", "Razer BlackShark V3", "Great sound, low latency, fair price.", "PC Gamer", "https://www.pcgamer.com/best-gaming-headset/"),
            new("Microphone", "Shure MV6", "The best desk mic for gaming.", "PC Gamer", "https://www.pcgamer.com/best-microphone-gaming-streaming/"),
        }),
    };

    // ───────── 4. Prebuilt PCs ─────────

    public sealed record Builder(string Name, string GoodFor, string Example, string Watch, string SourceName, string SourceUrl);

    public static readonly Builder[] Builders =
    {
        new("Corsair Vengeance", "Best build quality", "Ryzen 7 9800X3D + RTX 5070 Ti, 2-year warranty, about $2,499",
            "Premium price", "Tom's Hardware", "https://www.tomshardware.com/best-picks/best-gaming-pcs"),
        new("Lenovo Legion Tower", "Best all-round value, quiet", "Ryzen 7 7800X3D + RTX 5070, 32 GB, about $1,400 – $1,530",
            "Some parts are Lenovo's own", "PCGameCheck", "https://pcgamecheck.com/blog/best-prebuilt-gaming-pc-brands-2026-us"),
        new("HP OMEN", "Service network and cooling", "OMEN 35L / 45L with 9800X3D options",
            "Check which parts are standard", "Your Tech Reviews", "https://yourtechreviews.com/best-prebuilt-gaming-pc-brands/"),
        new("Maingear", "Most configurable", "MG-1, pick every part",
            "Costs more than mass-market brands", "Tom's Hardware", "https://www.tomshardware.com/best-picks/best-gaming-pcs"),
        new("NZXT", "Clean looks, easy upgrades", "BLD and Player series, 2-year warranty",
            "Value varies by config", "Newegg Insider", "https://www.newegg.com/insider/best-prebuilt-gaming-desktops-in-2026"),
        new("Skytech / iBUYPOWER / CyberPowerPC", "Budget and standard parts", "Standard boards, PSUs and cases you can upgrade",
            "Read the exact parts list", "WePC", "https://www.wepc.com/prebuilt/best-prebuilt-gaming-pc/"),
    };

    public static readonly string[] PrebuiltChecks =
    {
        "Two RAM sticks (2×16 GB), not one: a single stick halves memory bandwidth.",
        "A named power supply brand with an 80 Plus Gold rating.",
        "Standard motherboard and case sizes, so you can upgrade later.",
        "The exact graphics card model, not \"RTX 50 series\".",
        "At least a 1-year warranty with parts and labor.",
    };

    // ───────── 5. News ─────────

    public sealed record NewsItem(string Tag, bool Hot, string Title, string Body, string SourceName, string SourceUrl);

    public static readonly NewsItem[] News =
    {
        new("RAM", true, "RAM costs four to seven times more than a year ago",
            "AI data centers are buying most of the world's memory. A 32 GB DDR5 kit went from about $59 in April 2025 to $429 on November 3, 2026.",
            "Capital and Compute", "https://capitalandcompute.net/memory-prices/history/"),
        new("RAM", true, "The worst may still be ahead",
            "Team Group's CEO said the tightest stretch is Q4 2026 to Q2 2027, with balance in late 2028 or later.",
            "Tech Insider", "https://tech-insider.org/team-group-ddr5-chip-price-452-percent-shortage-2028-2026/"),
        new("Storage", false, "SSD prices are climbing too",
            "Some SSD contract prices are up 303% in a year, with another 15 to 20% expected this quarter.",
            "Notebookcheck", "https://www.notebookcheck.net/SSD-prices-explode-by-303-in-a-year-as-AI-fuels-NAND-flash-shortage.1433261.0.html"),
        new("GPU", true, "NVIDIA pulled the RTX 5090 and 5080 from its store",
            "Street prices passed twice launch price. AMD is reportedly planning its fourth Radeon price rise this year.",
            "Tom's Hardware", "https://www.tomshardware.com/pc-components/gpus/news"),
        new("GPU", false, "AMD outsold NVIDIA on Amazon US",
            "A first, per sales tracker TechEpiphany on November 3, as GeForce prices climbed. That's one retailer, not the whole market.",
            "ShiftDelete", "https://shiftdelete.net/en/amd-outsells-nvidia"),
        new("Coming", false, "Next graphics cards: likely 2028",
            "Leakers say NVIDIA's RTX 60 and most of AMD's RDNA 5 won't arrive until 2028. Neither company has confirmed a date.",
            "PC Guide", "https://www.pcguide.com/news/rtx-60-series-faces-massive-delay-says-leak-and-amd-might-only-launch-one-rdna-5-gpu-next-year/"),
        new("Coming", false, "Next CPUs: early 2027",
            "AMD Zen 6 and Intel Nova Lake desktop chips are expected around CES 2027. Zen 6 keeps the AM5 socket.",
            "Club386", "https://www.club386.com/6-exciting-pc-hardware-launches-mark-your-calendars/"),
        new("Windows", false, "Windows 11 26H2 is out",
            "A small update released September 29 that resets support to 24 months on Home and Pro.",
            "TechSpot", "https://www.techspot.com/news/114418-most-users-can-now-upgrade-windows-11-26h2.html"),
    };

    public static readonly string[] UpgradeTips =
    {
        "Get the free speed first: XMP/EXPO, Resizable BAR and your monitor's refresh rate.",
        "Buy RAM as a matched pair. DDR4 and DDR5 don't mix, and adding two more sticks can slow a PC down.",
        "Used RAM and graphics cards can be cheaper than new; buy SSDs new, because they wear out.",
        "A prebuilt PC can be the better deal right now: their prices have risen less than the parts.",
        "Upgrade what isn't in short supply: CPUs, monitors, mice and keyboards.",
    };
}
