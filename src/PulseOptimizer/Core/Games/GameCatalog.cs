namespace Pulse.Core.Games;

/// <summary>What Pulse knows about a game's own low-latency and FPS options.</summary>
public record KnownGame(
    string Id,
    string Name,
    string Short,
    string[] Exes,
    string[] Matches,
    bool Reflex,
    bool AntiLag2,
    string LatencySetting,
    string FullscreenSetting,
    string[] FpsSettings,
    string[] Genres,
    string[] ProSettings = null,
    string ProSource = "",
    string Color1 = "#3B4256",
    string Color2 = "#1A1D26",
    string Note = "")
{
    /// <summary>A game the user added themselves (Pulse has no game-specific notes for it).</summary>
    public bool IsCustom => Id.StartsWith("custom:");
    /// <summary>Uses the general checklist: "Any other game" and games the user added.</summary>
    public bool IsGeneric => Id == "other" || IsCustom;
    /// <summary>The "Add a game" tile.</summary>
    public bool IsAddTile => Id == "add";

    /// <summary>True when an installed program's name is this game ("Rust" matches "Rust", not "RustDesk").</summary>
    public bool IsNamed(string installedName) =>
        !string.IsNullOrEmpty(installedName) && Matches.Any(m =>
            System.Text.RegularExpressions.Regex.IsMatch(installedName, @"(^|[^\p{L}\p{N}])" + System.Text.RegularExpressions.Regex.Escape(m) + @"($|[^\p{L}\p{N}])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));
}

/// <summary>
/// The games Pulse has checklists for. Reflex support comes from NVIDIA's game list; Anti-Lag 2 from AMD's
/// (amd.com/antilag; Marvel Rivals added it in a 2025 patch). Menu names follow the games' current menus
/// and can move with updates, which the page says. FPS advice names the settings reviewers found cost the most frames.
/// </summary>
public static class GameCatalog
{
    public static readonly List<KnownGame> All = new()
    {
        new("valorant", "VALORANT", "V", new[] { "VALORANT-Win64-Shipping.exe" }, new[] { "VALORANT" },
            Reflex: true, AntiLag2: false,
            LatencySetting: "Settings → Video → Graphics Quality → NVIDIA Reflex Low Latency",
            FullscreenSetting: "Settings → Video → General → Display Mode → Fullscreen",
            FpsSettings: new[]
            {
                "Multithreaded Rendering: On. VALORANT leans on the CPU, and this spreads the work over more cores.",
                "Material, Texture and Detail Quality: Low or Medium. Small FPS cost each, but they add up on older cards.",
                "Anti-Aliasing: MSAA 2x or None, and Anisotropic Filtering 4x. MSAA 4x is the most expensive option here.",
                "Limit FPS: off in matches (or set it to the cap from the step above if you use G-SYNC/FreeSync).",
            },
            ProSettings: new[]
            {
                "Display Mode: Fullscreen at your monitor's native resolution (68% of pros play 1920×1080).",
                "Material Quality: Low · Texture Quality: Medium · Detail Quality: Low · UI Quality: Medium.",
                "Vignette, V-Sync, Bloom, Distortion and Cast Shadows: Off. Anti-Aliasing: MSAA 2x. Anisotropic Filtering: 4x.",
                "Improve Clarity and Experimental Sharpening: On (turn Improve Clarity off if it costs you frames).",
                "Mouse: about half of pros use 800 DPI and 40% use 400 DPI; typical eDPI (DPI × sensitivity) is about 240–270. Scoped sensitivity multiplier: 1.",
            },
            ProSource: "ProSettings.net VALORANT guide and pro data, May 2026",
            Genres: new[] { "shooter", "tactical", "competitive" }, Color1: "#FF4655", Color2: "#5A0E17"),
        new("cs2", "Counter-Strike 2", "CS2", new[] { "cs2.exe" }, new[] { "Counter-Strike 2" },
            Reflex: true, AntiLag2: true,
            LatencySetting: "Settings → Video → Advanced Video → NVIDIA Reflex Low Latency (AMD cards: AMD Anti-Lag 2 in the same list)",
            FullscreenSetting: "Settings → Video → Video → Display Mode → Fullscreen",
            FpsSettings: new[]
            {
                "Multisampling Anti-Aliasing Mode: 2x MSAA or None. 8x MSAA costs the most FPS of any setting.",
                "Ambient Occlusion: Disabled, and Shader Detail: Low. Both cost frames without helping you see players.",
                "Boost Player Contrast: Enabled. Makes players easier to spot for almost no FPS cost.",
                "FidelityFX Super Resolution: only if your card struggles; it lowers sharpness.",
            },
            ProSettings: new[]
            {
                "Display Mode: Fullscreen. Many pros play 4:3 at 1280×960 (53%) stretched; native resolution is fine if you prefer it.",
                "Boost Player Contrast: Enabled. Wait for Vertical Sync: Disabled. NVIDIA Reflex: Enabled (or Enabled + Boost).",
                "Multisampling Anti-Aliasing: CMAA2 · Global Shadow Quality: Low · Dynamic Shadows: All (you see enemy shadows) · Model/Texture Detail: Medium.",
                "Shader Detail, Particle Detail: Low · Ambient Occlusion: Disabled · High Dynamic Range: Quality · FidelityFX Super Resolution: Disabled.",
                "Mouse: 400 DPI is most common (66%), then 800 (28%); median eDPI is about 830. Zoom Sensitivity Multiplier: 1.",
            },
            ProSource: "ProSettings.net CS2 guide and pro data, 2026",
            Genres: new[] { "shooter", "tactical", "competitive" }, Color1: "#E8A33B", Color2: "#3B2A10"),
        new("fortnite", "Fortnite", "F", new[] { "FortniteClient-Win64-Shipping.exe" }, new[] { "Fortnite" },
            Reflex: true, AntiLag2: false,
            LatencySetting: "Settings → Video → Latency → NVIDIA Reflex Low Latency",
            FullscreenSetting: "Settings → Video → Display → Window Mode → Fullscreen",
            FpsSettings: new[]
            {
                "Rendering Mode: Performance (Lower Graphical Fidelity). The single biggest FPS gain in Fortnite on low and mid-range PCs; restart the game after changing it.",
                "On a strong card using DirectX 12: turn Nanite, Lumen and Ray Tracing off. They cost far more frames than any other setting.",
                "Upscaling: NVIDIA DLSS (or TSR) on Quality if you need more frames at high resolution.",
                "View Distance: Medium or higher; lowering it barely helps FPS and hides enemies far away.",
            },
            ProSettings: new[]
            {
                "Window Mode: Fullscreen · V-Sync: Off · Frame Rate Limit: Unlimited (or your monitor's refresh rate if you see tearing).",
                "Rendering Mode: Performance · 3D Resolution: 100% · View Distance: Near · Textures: Low · Meshes: Low.",
                "NVIDIA Reflex Low Latency: On + Boost · Latency Markers: Off · Report Performance Stats: Off.",
                "Mouse: most pros stay under 3200 DPI; X sensitivity 7–9% is the most common range, with Y equal to X.",
            },
            ProSource: "ProSettings.net Fortnite guide, 2026",
            Genres: new[] { "battle royale", "shooter", "building" }, Color1: "#2E7BFF", Color2: "#0C1E4A"),
        new("roblox", "Roblox", "R", new[] { "RobloxPlayerBeta.exe" }, new[] { "Roblox Player", "Roblox" },
            Reflex: false, AntiLag2: false,
            LatencySetting: "",
            FullscreenSetting: "Press F11 for fullscreen",
            FpsSettings: new[]
            {
                "Maximum Frame Rate: in Roblox's settings (Esc → Settings), raise it from 60 to match your monitor (120, 144 or 240). Roblox stays at 60 FPS until you change this.",
                "Graphics Mode: Manual, then lower the Graphics Quality bar until the game feels smooth. Busy experiences can drop FPS a lot.",
                "Skip \"FPS unlocker\" programs: the official setting does the same thing without breaking Roblox's rules.",
            },
            Genres: new[] { "sandbox", "social", "casual" }, Color1: "#DADDE3", Color2: "#5A606C"),
        new("overwatch", "Overwatch 2", "OW2", new[] { "Overwatch.exe" }, new[] { "Overwatch" },
            Reflex: true, AntiLag2: false,
            LatencySetting: "Options → Video → NVIDIA Reflex Low Latency",
            FullscreenSetting: "Options → Video → Display Mode → Fullscreen",
            FpsSettings: new[]
            {
                "Graphics Quality: Low, then Render Scale back to 100%. Lower presets drop the render scale, which makes the game blurry.",
                "Dynamic Render Scale: Off, so sharpness doesn't jump around mid-fight.",
                "Shadow Detail, Local Fog Detail and Dynamic Reflections: Low or Off. These are the most expensive effects.",
                "Frame Rate: Custom, with the limit set to the cap from the sync step.",
            },
            ProSettings: new[]
            {
                "Gameplay → Miscellaneous → High Precision Mouse Input: On. This is the first thing pros change.",
                "Display Mode: Fullscreen · Render Scale: Custom 100% · Dynamic Render Scale: Off · Frame Rate: Custom, at your refresh rate.",
                "NVIDIA Reflex: On + Boost · Reduce Buffering: On · V-Sync and Triple Buffering: Off · Field of View: 103.",
                "Texture Quality: High · Texture Filtering: 4x · Model, Shadow, Effect and Local Fog Detail: Medium · Dynamic and Local Reflections: Off.",
            },
            ProSource: "ProSettings.net Overwatch guide, March 2026",
            Genres: new[] { "hero shooter", "shooter", "competitive" }, Color1: "#F99E1A", Color2: "#43290A"),
        new("rivals", "Marvel Rivals", "MR", new[] { "Marvel-Win64-Shipping.exe" }, new[] { "Marvel Rivals" },
            Reflex: true, AntiLag2: true,
            LatencySetting: "Settings → Display or Graphics → Low Latency Mode (NVIDIA Reflex; on AMD cards, AMD Anti-Lag 2)",
            FullscreenSetting: "Settings → Display → Display Mode → Fullscreen",
            FpsSettings: new[]
            {
                "Graphics Quality: Low. Reviewers measured about 140 FPS on Low against 85 on Ultra at 1080p.",
                "Global Illumination and Reflection Quality: lowest (Screen Space). Lumen lighting is the heaviest part of this game.",
                "Shadow Detail and Effects Detail: Low. Ability effects fill the screen in team fights.",
                "Super Resolution: DLSS or FSR on Quality if you need more frames. Frame generation adds delay, so leave it off in competitive play.",
            },
            ProSettings: new[]
            {
                "Native resolution · Limit FPS: On, at your refresh rate · V-Sync: Off · Frame Generation: Off.",
                "Low Latency Mode: On + Boost (NVIDIA) or AMD Anti-Lag 2: On (AMD).",
                "Global Illumination: SSGI, Low · Reflection Quality: Disabled · Model, Post-Processing, Texture, Effects and Foliage: Low · Shadow Detail: Medium (helps you see enemies).",
                "Super Resolution: Quality, sharpening 70–80. Lower render scale only if you're short on frames.",
            },
            ProSource: "ProSettings.net Marvel Rivals guide, 2026",
            Genres: new[] { "hero shooter", "shooter", "competitive" }, Color1: "#E3262E", Color2: "#3E0A0D"),
        new("r6", "Rainbow Six Siege", "R6", new[] { "RainbowSix.exe", "RainbowSix_DX11.exe", "RainbowSixGame.exe" }, new[] { "Rainbow Six" },
            Reflex: true, AntiLag2: false,
            LatencySetting: "Options → Graphics → NVIDIA Reflex Low Latency",
            FullscreenSetting: "Options → Display → Display Mode → Full Screen",
            FpsSettings: new[]
            {
                "Shadow Quality and Reflection Quality: Low. Shadows are the most expensive setting in Siege.",
                "Ambient Occlusion and Lens Effects: Off.",
                "Render Scaling: 100% (or DLSS on Quality on NVIDIA RTX cards) so the game stays sharp.",
                "Texture Quality: as high as your graphics card's memory allows; it costs memory, not FPS.",
            },
            ProSettings: new[]
            {
                "Display Mode: Fullscreen · V-Sync: Off · Dynamic Resolution Scaling: Off · Render Scaling: 100%. Many pros play 4:3 stretched; it's optional.",
                "Texture Quality: Medium · Level of Detail: Medium · Shadow Quality: Low or Off · Shading Quality: Low · Reflection Quality: Low.",
                "Ambient Occlusion and Lens Effects: Off · Anti-Aliasing: T-AA or Off.",
                "Mouse: 400–800 DPI, acceleration off; Shaiiko (BDS) plays 800 DPI with 12/12 sensitivity.",
            },
            ProSource: "siege.gg settings guide and pro configs",
            Genres: new[] { "shooter", "tactical", "competitive" }, Color1: "#8592A6", Color2: "#1D232E"),
        new("apex", "Apex Legends", "AL", new[] { "r5apex.exe", "r5apex_dx12.exe" }, new[] { "Apex Legends" },
            Reflex: true, AntiLag2: false,
            LatencySetting: "Settings → Video → NVIDIA Reflex",
            FullscreenSetting: "Settings → Video → Display Mode → Full Screen",
            FpsSettings: new[]
            {
                "Texture Streaming Budget: Medium or lower if your card has under 8 GB of memory.",
                "Sun Shadow Coverage and Detail: Low; Spot Shadow Detail, Dynamic Spot Shadows and Volumetric Lighting: Disabled. Shadows cost the most here.",
                "Adaptive Resolution FPS Target: 0 (off), so the image doesn't go blurry mid-fight.",
            },
            Genres: new[] { "battle royale", "hero shooter", "shooter" },
            ProSettings: new[]
            {
                "Display Mode: Full Screen at native resolution (77% of pros play 1920×1080) · V-Sync: Off · NVIDIA Reflex: Enabled + Boost.",
                "Field of View: 110 (over half of pros) · FOV Ability Scaling: Disabled · Sprint View Shake: Minimal.",
                "Anti-Aliasing: None · Texture Filtering: Anisotropic 2x · Ambient Occlusion: Disabled · Model, Effects Detail, Impact Marks, Ragdolls: Low.",
                "Mouse: 800 DPI is most common (63%), then 1600 and 400; no pro uses over 2500. ADS sensitivity multiplier: 1.",
            },
            ProSource: "ProSettings.net Apex Legends guide and pro data, 2026",
            Color1: "#DA292A", Color2: "#3A0B0B"),
        new("cod", "Call of Duty", "COD", new[] { "cod.exe" }, new[] { "Call of Duty" },
            Reflex: true, AntiLag2: false,
            LatencySetting: "Settings → Graphics → Display → NVIDIA Reflex Low Latency",
            FullscreenSetting: "Settings → Graphics → Display → Display Mode → Fullscreen Exclusive",
            FpsSettings: new[]
            {
                "On-Demand Texture Streaming: Minimal. It downloads textures while you play and can cost frames and bandwidth.",
                "Screen Space Shadows, Ambient Occlusion, Screen Space Reflections, Tessellation and Volumetric Quality: Off or Low.",
                "Upscaling: FidelityFX CAS sharpening at 100% render resolution; use DLSS Quality only if you need the frames.",
            },
            Genres: new[] { "shooter", "battle royale", "competitive" },
            Note: "Covers Warzone and the current Call of Duty (Black Ops 7, and Modern Warfare 4 from October 23). They share the same settings menus.",
            ProSettings: new[]
            {
                "Display Mode: Fullscreen Exclusive · Refresh rate and resolution: your monitor's maximum · NVIDIA Reflex: On + Boost · V-Sync (Gameplay and Menus): Off.",
                "Custom Frame Rate Limit: your refresh rate · Render Resolution: 100 · Dynamic Resolution: Off · FidelityFX CAS: On, strength about 85.",
                "Texture Resolution: Low · Detail and Shader Quality: Low · Particle Resolution: Very Low · Shadow Quality: Normal · Depth of Field: Off.",
                "Field of View: 100–115 · ADS Field of View: Affected · Weapon Field of View: Wide · World and Weapon Motion Blur: Off · Film Grain: 0.",
                "1st and 3rd Person Camera Movement: Least (50%) · Inverted Flashbang: On.",
            },
            ProSource: "CharlieIntel's Warzone PC settings guide",
            Color1: "#3E7F3E", Color2: "#0E1A10"),
        new("arcraiders", "ARC Raiders", "ARC", new[] { "PioneerGame.exe" }, new[] { "ARC Raiders" },
            Reflex: true, AntiLag2: false,
            LatencySetting: "Settings → Video → NVIDIA Reflex (AMD: AMD Anti-Lag)",
            FullscreenSetting: "Settings → Video → Window Mode → Fullscreen",
            FpsSettings: new[]
            {
                "NVIDIA RTX Global Illumination: Static. The dynamic setting costs a lot of frames.",
                "Foliage: Low; Shadows, Effects and Post Processing: Medium.",
                "Upscaling: NVIDIA DLSS (or FSR 3 on AMD) on Quality or Balanced if you need frames.",
            },
            Genres: new[] { "shooter", "survival", "co-op" },
            ProSettings: new[]
            {
                "Display Resolution: native · V-Sync: Off · AMD Anti-Lag / NVIDIA Reflex: On · Frame Rate Limit: your refresh rate (or uncapped).",
                "Upscaled Resolution: 100% · Field of View: 80 · Motion Blur: Off · View Distance: High.",
                "Anti-Aliasing, Shadows, Effects, Post Processing, Global Illumination Resolution: Medium · Textures: High (lower on low-memory cards) · Foliage: Low.",
                "Mouse Smoothing: Off · Accessibility → Invert Flash Color: On.",
            },
            ProSource: "ProSettings.net ARC Raiders guide",
            Color1: "#E7B53B", Color2: "#2E2410"),
        new("rust", "Rust", "R", new[] { "RustClient.exe" }, new[] { "Rust" },
            Reflex: true, AntiLag2: false,
            LatencySetting: "Options → Graphics → NVIDIA Reflex",
            FullscreenSetting: "Options → Screen → Fullscreen",
            FpsSettings: new[]
            {
                "Grass Quality, Decor Quality, Particle Quality: 0 · Grass Shadows: Off · Water Quality and Reflections: Off.",
                "Shadow Quality: 1 with 2 cascades, or off on weaker PCs · Draw Distance: about 1600.",
                "Install Rust on an SSD: it streams the map from disk and stutters on a hard drive.",
            },
            Genres: new[] { "survival", "shooter" },
            ProSettings: new[]
            {
                "Render Scale: 1.0 · Shader Level: 3 · NVIDIA Reflex: On + Boost · DLSS: Off · Volumetric Clouds: Off.",
                "Texture Quality: Half-Resolution · Anisotropic Filtering: Disabled · Pixel Light Count: 0 · Max Gibs: 0.",
                "Field of View: 90 · Hurt Flash: Off · Show Blood: On.",
                "Steam launch options: -high -graphics.branding 0 -player.eye_blinking false -player.eye_movement false",
            },
            ProSource: "Dignitas' Rust PvP settings guide",
            Color1: "#CD412B", Color2: "#2E0F0A"),
        new("rocketleague", "Rocket League", "RL", new[] { "RocketLeague.exe" }, new[] { "Rocket League" },
            Reflex: false, AntiLag2: false,
            LatencySetting: "",
            FullscreenSetting: "Settings → Video → Display Mode → Fullscreen",
            FpsSettings: new[]
            {
                "Render Quality: High Performance and Render Detail: Performance on weaker PCs; the game runs well on almost anything.",
                "Frame Rate: Uncapped or at your refresh rate · V-Sync: Off.",
            },
            Genres: new[] { "sports", "competitive", "casual" },
            ProSettings: new[]
            {
                "Camera Shake: Off · Field of View: 110 (some use 109).",
                "Distance: 260–280 (270 is most common) · Height: 90–110 (100 is most common) · Angle: -3 to -5.",
                "Stiffness: 0.35–0.70 (low) · Swivel Speed: about 4–7 · Transition Speed: whatever feels right.",
                "Copied from top pros like Zen (110 FOV, 100 height, 270 distance, 0.35 stiffness, 4.0 swivel).",
            },
            ProSource: "esports.net's pro camera settings table",
            Color1: "#2E7FD8", Color2: "#0B1E3A"),
        new("thefinals", "THE FINALS", "TF", new[] { "Discovery.exe" }, new[] { "THE FINALS" },
            Reflex: true, AntiLag2: false,
            LatencySetting: "Settings → Video → NVIDIA Reflex Low Latency",
            FullscreenSetting: "Settings → Video → Window Mode → Fullscreen",
            FpsSettings: new[]
            {
                "NVIDIA RTX Global Illumination: Static. Ray-traced lighting costs a lot of frames.",
                "Texture: Low if you need more FPS; Foliage, Effects, Post-Processing: Low.",
            },
            Genres: new[] { "shooter", "competitive" },
            ProSettings: new[]
            {
                "Window Mode: Fullscreen at native resolution · V-Sync: Off · NVIDIA Reflex: On (On + Boost if it doesn't cost you frames).",
                "Resolution Scaling Method: TAAU at 100% · NVIDIA DLSS: Off (adds a little delay) · Field of View: about 100.",
                "Motion Blur and Lens Distortion: Off · View Distance: Medium · Anti-Aliasing: Low · Shadows: Medium.",
                "Texture: Medium · Effects, Foliage, Post-Processing, Global Illumination Resolution: Low.",
            },
            ProSource: "ProSettings.net THE FINALS guide",
            Color1: "#E8303A", Color2: "#2A0A0E"),
        new("pubg", "PUBG: Battlegrounds", "PUBG", new[] { "TslGame.exe" }, new[] { "PUBG", "PLAYERUNKNOWN" },
            Reflex: true, AntiLag2: false,
            LatencySetting: "Settings → Graphics → NVIDIA Reflex",
            FullscreenSetting: "Settings → Graphics → Display Mode → Full Screen",
            FpsSettings: new[]
            {
                "Shadows, Effects, Foliage: Very Low; View Distance: Low. These cost the most and hide little.",
                "DirectX 11 (or DirectX 11 Enhanced) usually runs smoother than DirectX 12.",
            },
            Genres: new[] { "battle royale", "shooter", "tactical" },
            ProSettings: new[]
            {
                "Display Mode: Full Screen · Render Scale: 100 · Smoothed Framerate and V-Sync: Off · In-game FPS cap: Unlimited (cap only if unstable).",
                "FPP Camera FOV: 103 · Anti-Aliasing: Medium · Post-Processing: Low · Textures: Medium (higher if your card allows).",
                "Shadows, Effects, Foliage: Very Low · View Distance: Low · Sharpen and Motion Blur: Off.",
                "Mouse: 82% of pros use 800 DPI or lower.",
            },
            ProSource: "ProSettings.net PUBG guide",
            Color1: "#F2A900", Color2: "#2E2408"),
        new("bf6", "Battlefield 6", "BF6", new[] { "bf6.exe" }, new[] { "Battlefield 6", "Battlefield™ 6" },
            Reflex: true, AntiLag2: false,
            LatencySetting: "Settings → Display → NVIDIA Reflex Low Latency",
            FullscreenSetting: "Settings → Display → Fullscreen Mode",
            FpsSettings: new[]
            {
                "Shadow Filtering: PCF (High costs a lot) · Screen Space AO & GI: GTAO Low · Terrain Quality: Low if you need frames.",
                "Frame Generation: Off in multiplayer: it adds delay.",
            },
            Genres: new[] { "shooter", "competitive" },
            ProSettings: new[]
            {
                "Fullscreen (borderless is fine too) · V-Sync: Off · NVIDIA Reflex: Enabled + Boost · Future Frame Rendering: Off.",
                "Upscaling and Frame Generation: Off · Dynamic Resolution Scale: Off · Fixed Resolution Scale: 100 · Frame Rate Limiter: Off · Anti-Aliasing: TAA.",
                "Field of View: about 105 · Weapon FOV: Wide · Motion Blur: 0 · Vignette and Film Grain: Off · Reduce Sprint Camera Bobbing: On.",
                "Texture Quality: High (Low/Medium on older cards) · Mesh, Terrain, Undergrowth: Medium · Effects, Volumetric, Post Process: Low · Sun Shadows: Medium.",
            },
            ProSource: "ProSettings.net Battlefield 6 guide",
            Color1: "#F05A28", Color2: "#2B1006"),
        new("deltaforce", "Delta Force", "DF", new[] { "DeltaForceClient-Win64-Shipping.exe" }, new[] { "Delta Force" },
            Reflex: true, AntiLag2: false,
            LatencySetting: "Settings → Display → NVIDIA Reflex",
            FullscreenSetting: "Settings → Display → Display Mode → Fullscreen",
            FpsSettings: new[]
            {
                "Global Illumination, Reflections, Shadows and Shadow Map cost the most: set them Low or Off.",
                "DLSS Frame Generation: Off in Warfare and Extraction: it adds a lot of delay.",
            },
            Genres: new[] { "shooter", "tactical" },
            ProSettings: new[]
            {
                "Display Mode: Fullscreen or Borderless (same delay) · V-Sync: Off · Frame cap in and out of match: your refresh rate.",
                "Super Resolution: DLSS (FSR/XeSS on other cards) on Performance · Frame Generation: Off · Rendering Scale: 80–100.",
                "Field of View: 120 · Anti-Aliasing, Ambient Occlusion, Depth of Field: Off.",
                "Reflections, Texture Filtering, Particles, Scene Details, Global Illumination, Shadows, Post-Processing, Volumetric Fog: Low or Off.",
            },
            ProSource: "ProSettings.net Delta Force guide",
            Color1: "#2FBF71", Color2: "#0B2A1A"),
        new("halo", "Halo Infinite", "HI", new[] { "HaloInfinite.exe" }, new[] { "Halo Infinite" },
            Reflex: false, AntiLag2: false,
            LatencySetting: "",
            FullscreenSetting: "Settings → Video → Window Mode → Fullscreen",
            FpsSettings: new[]
            {
                "Quality Preset: Low is Halo Studios' own competitive recommendation.",
                "Async Compute: test on and off: it helps or hurts depending on your CPU.",
            },
            Genres: new[] { "shooter", "competitive" },
            ProSettings: new[]
            {
                "Halo's official competitive settings: Field of View 100° · Blur, Screen Shake, Exposure, Full Screen Effects: 0% · Speed Lines: Off · Sharpening: 60%.",
                "Resolution Scale: 100% · Minimum Frame Rate: 120 · Maximum Frame Rate: Unlimited · V-Sync: Off.",
                "Quality Preset: Low, then Effects Quality: Medium · Animation Quality: Max · Simulation Quality: Ultra.",
                "Chromatic Aberration, Bloom, Parallax: Off · FPS Counter and Network Statistics: On.",
            },
            ProSource: "Halo Waypoint's recommended competitive settings",
            Color1: "#4E9CD5", Color2: "#0E2236"),
        new("hunt", "Hunt: Showdown 1896", "HS", new[] { "HuntGame.exe" }, new[] { "Hunt: Showdown", "Hunt Showdown" },
            Reflex: true, AntiLag2: false,
            LatencySetting: "Settings → Video → NVIDIA Reflex",
            FullscreenSetting: "Settings → Video → Window Mode → Fullscreen",
            FpsSettings: new[]
            {
                "Foliage/Grass and Volumetric Fog cost the most: set them Low.",
                "Upscaling: DLSS, FSR or XeSS on Quality if you need frames.",
            },
            Genres: new[] { "shooter", "tactical", "horror" },
            ProSettings: new[]
            {
                "Foliage/Grass: Low · Volumetric Fog: Low · Shadows: Medium · View Distance: Medium.",
                "Anti-Aliasing: FXAA · Ambient Occlusion: Medium · Textures: High (Medium under 6 GB of graphics memory).",
                "Post-Processing/Bloom: Low · Screen Space Reflections: Off · Motion Blur: Off · NVIDIA Reflex: On.",
            },
            ProSource: "checkfps.io's Hunt: Showdown 1896 guide",
            Color1: "#B8452B", Color2: "#2A0F09"),
        new("readyornot", "Ready or Not", "RoN", new[] { "ReadyOrNot-Win64-Shipping.exe" }, new[] { "Ready or Not" },
            Reflex: false, AntiLag2: false,
            LatencySetting: "",
            FullscreenSetting: "Settings → Graphics → Screen Mode → Fullscreen",
            FpsSettings: new[]
            {
                "Post Processing and VFX: Low; Shadows: Medium.",
                "DLSS on Quality (newer NVIDIA cards) if you need frames.",
            },
            Genres: new[] { "tactical", "shooter", "co-op" },
            ProSettings: new[]
            {
                "Screen Mode: Fullscreen at native resolution · Resolution Scale: 100% · V-Sync: Off · Frame Limit: Disabled · Motion Blur: Off.",
                "Field of View: 90 · Texture Quality: Epic (lowering it caused stutter in testing) · Shadow Quality: Medium.",
                "Post Processing: Low · VFX: Low · Anti-Aliasing: High · View Distance: High · DLSS: Quality.",
            },
            ProSource: "GameRevolution's Ready or Not PC settings",
            Color1: "#3D5A80", Color2: "#0E1726"),
        new("tf2", "Team Fortress 2", "TF2", new[] { "tf_win64.exe", "tf.exe", "hl2.exe" }, new[] { "Team Fortress 2" },
            Reflex: false, AntiLag2: false,
            LatencySetting: "",
            FullscreenSetting: "Options → Video → Display Mode → Fullscreen",
            FpsSettings: new[]
            {
                "Multicore Rendering: Enabled · Antialiasing: None.",
                "For a big FPS jump, the community mastercomfig config is the standard choice.",
            },
            Genres: new[] { "hero shooter", "shooter" },
            ProSettings: new[]
            {
                "Display Mode: Fullscreen · V-Sync: Disabled · Antialiasing: None · Multicore Rendering: Enabled.",
                "Console: fps_max at or above your refresh rate (under 1000) · m_rawinput 1.",
                "Steam launch options: -novid -nojoy -nosteamcontroller -nohltv -particles 1. Test -vulkan against the default and keep whichever feels smoother.",
                "Don't leave -dxlevel in your launch options permanently, and avoid -dxlevel 81.",
            },
            ProSource: "Tier1Settings' TF2 input-lag guide (based on mastercomfig)",
            Color1: "#CF6A32", Color2: "#2E160A"),
        new("paladins", "Paladins", "PAL", new[] { "Paladins.exe" }, new[] { "Paladins" },
            Reflex: false, AntiLag2: false,
            LatencySetting: "",
            FullscreenSetting: "Settings → Video → Screen Type → Full Screen",
            FpsSettings: new[]
            {
                "Shader Quality is the biggest jump (Medium to High); then lights and shadows.",
                "Resolution Scale: 90 if you need frames, never below 75.",
            },
            Genres: new[] { "hero shooter", "shooter" },
            ProSettings: new[]
            {
                "Use D3D11: On · Screen Type: Full Screen · Resolution Scale: 100 · Anti-Aliasing: Off · Vertical Sync: Disabled.",
                "World Detail: Medium (High adds bloom that can hide enemies) · Shadow Detail: Low · Shader Quality: Low.",
                "Texture Detail: Medium or High (by graphics memory) · Particle Detail: Low or Medium · Field of View: 100–120.",
                "Steam launch option: -USEALLAVAILABLECORES",
            },
            ProSource: "Esports Tales' Paladins FPS guide",
            Color1: "#F5C542", Color2: "#2E2509"),
        new("helldivers2", "Helldivers 2", "HD2", new[] { "helldivers2.exe" }, new[] { "Helldivers 2" },
            Reflex: false, AntiLag2: false,
            LatencySetting: "",
            FullscreenSetting: "Options → Display → Display Mode → Fullscreen",
            FpsSettings: new[]
            {
                "Object Detail, Particles, Reflections, Space Quality, Volumetric Fog: Low · Ambient Occlusion: Off.",
                "Shadows and Lighting: Medium.",
            },
            Genres: new[] { "co-op", "shooter" },
            ProSettings: new[]
            {
                "Display Mode: Fullscreen · Framerate Limit: Off · V-Sync: Off · HDR: Off.",
                "Motion Blur: 0 · Depth of Field and Bloom: Off · Sharpness: 1 · Anti-Aliasing: On.",
                "Texture Quality: Medium · Render Distance: Medium · Shadow Quality: Medium · Lighting Quality: Medium · Terrain Quality: High.",
                "Object Detail, Particle, Reflection, Space Quality, Volumetric Fog: Low · Ambient Occlusion: Off · Screen-Space GI: On.",
            },
            ProSource: "Dexerto's Helldivers 2 PC settings",
            Color1: "#F7D417", Color2: "#2B2506"),
        new("drg", "Deep Rock Galactic", "DRG", new[] { "FSD-Win64-Shipping.exe" }, new[] { "Deep Rock Galactic" },
            Reflex: false, AntiLag2: false,
            LatencySetting: "",
            FullscreenSetting: "Options → Graphics → Window Mode → Fullscreen",
            FpsSettings: new[]
            {
                "Shadow Quality is the biggest single gain, then volumetric lighting/fog.",
                "No DLSS or FSR: Resolution Scale 85–90% is the game's own lever.",
            },
            Genres: new[] { "co-op", "shooter" },
            ProSettings: new[]
            {
                "Shadow Quality: Medium · Volumetric Lighting/Fog: Low · Particle Effects: Medium.",
                "Texture Resolution: High (lower only under 6 GB of graphics memory) · Post Processing: Medium.",
                "View and Vegetation Distance: Medium · Real-Time Reflections: Low · Motion Blur: Off · Keep Ambient Occlusion on (caves look flat without it).",
                "Big swarms are CPU-heavy: graphics settings won't fix those dips.",
            },
            ProSource: "checkfps.io's Deep Rock Galactic guide",
            Color1: "#E89B2A", Color2: "#2B1C07"),
        new("dota2", "Dota 2", "D2", new[] { "dota2.exe" }, new[] { "Dota 2" },
            Reflex: true, AntiLag2: true,
            LatencySetting: "Settings → Video → NVIDIA Reflex (AMD cards: AMD Anti-Lag 2)",
            FullscreenSetting: "Settings → Video → Display Mode → Exclusive fullscreen",
            FpsSettings: new[]
            {
                "Game Screen Render Quality: 100%; lower only if you need frames.",
                "Rendering API: DX11 (try DX9 if DX11 runs worse).",
            },
            Genres: new[] { "strategy", "competitive" },
            ProSettings: new[]
            {
                "Rendering API: DX11 · Game Screen Render Quality: 100% · VSync: Off · Anti-Aliasing: Off.",
                "Texture Quality: Medium · Effects Quality: Low · Shadow Quality: Medium · Compute Shaders: On.",
                "Off: Ambient Occlusion, Additive Light Pass, World Lighting, Normal Maps, Ambient Creatures, Ambient Cloth, Animate Portrait, Specular and Light Blooms, High Quality Water, Atmospheric Fog, High Quality Dashboard, Tree Wind.",
                "Turn off camera shake and other cinematic effects.",
            },
            ProSource: "ProSettings.net Dota 2 guide",
            Color1: "#C23C2A", Color2: "#2A0D09"),
        new("lol", "League of Legends", "LoL", new[] { "League of Legends.exe" }, new[] { "League of Legends" },
            Reflex: false, AntiLag2: false,
            LatencySetting: "",
            FullscreenSetting: "Settings → Video → Window Mode → Full Screen",
            FpsSettings: new[]
            {
                "Character Inking: Off (costs FPS for looks only) · Shadows: Low.",
                "Struggling PC: turn on Low Spec Mode in the client's General settings.",
            },
            Genres: new[] { "strategy", "competitive" },
            ProSettings: new[]
            {
                "Window Mode: Full Screen (Borderless is fine) · Frame Rate Cap: Uncapped (or your refresh rate if it runs worse).",
                "Character and Environment Quality: Medium · Effects Quality: Low · Shadow Quality: Low · Character Inking: Off.",
                "Anti-Aliasing: Off · Wait for Vertical Sync: Off.",
                "Mouse: pros' median is 1600 DPI with in-game mouse and camera sensitivity 50.",
            },
            ProSource: "ProSettings.net League of Legends guide",
            Color1: "#C89B3C", Color2: "#0A1428"),
        new("sf6", "Street Fighter 6", "SF6", new[] { "StreetFighter6.exe" }, new[] { "Street Fighter 6", "Street Fighter™ 6" },
            Reflex: false, AntiLag2: false,
            LatencySetting: "",
            FullscreenSetting: "Options → Graphics → Display Mode",
            FpsSettings: new[]
            {
                "Internal Resolution is the biggest FPS lever (3, or lower).",
                "Fights always run at 60 FPS: settings only change smoothness and stability.",
            },
            Genres: new[] { "fighting", "competitive" },
            ProSettings: new[]
            {
                "Options → Graphics → Input Delay Reduction: On. This is the one latency setting that matters.",
                "V-Sync: Off (use G-SYNC/FreeSync for tearing) · Maximum Frame Rate: 60 for the steadiest pacing (120 for smoother menus and Battle Hub).",
                "Ambient Occlusion, Screen Space Reflections, Motion Blur, Depth of Field, Subsurface Scattering: Off · Bloom: Off or Low.",
                "Shadows: Lowest/Low · Lighting, Effects, Shader Quality: Low · Options → Game → Shader Warming on Startup: On.",
            },
            ProSource: "Hone's Street Fighter 6 settings guide",
            Color1: "#F37021", Color2: "#2E1306"),
        new("tekken8", "Tekken 8", "T8", new[] { "Polaris-Win64-Shipping.exe" }, new[] { "TEKKEN 8" },
            Reflex: false, AntiLag2: false,
            LatencySetting: "",
            FullscreenSetting: "Settings → Graphics → Display Mode → Fullscreen",
            FpsSettings: new[]
            {
                "Fights are locked to 60 FPS: only lower settings if you can't hold a steady 60.",
                "DLSS Quality gives headroom on NVIDIA cards.",
            },
            Genres: new[] { "fighting", "competitive" },
            ProSettings: new[]
            {
                "V-Sync: Off · Frame rate: steady 60 (gameplay is locked to 60; only cutscenes go higher).",
                "Upscaling: DLSS Quality · Graphics: as high as you can keep a locked 60, lowering Shadows and Effects first if it dips.",
            },
            ProSource: "PCGamesN's Tekken 8 PC settings",
            Color1: "#8E44AD", Color2: "#1E0B2A"),
        new("brawlhalla", "Brawlhalla", "BH", new[] { "Brawlhalla.exe" }, new[] { "Brawlhalla" },
            Reflex: false, AntiLag2: false,
            LatencySetting: "",
            FullscreenSetting: "Settings → Display → Fullscreen",
            FpsSettings: new[]
            {
                "Overall Graphics Quality: Low smooths frame times in busy fights.",
                "Close the Discord and capture overlays: they cause micro-stutter.",
            },
            Genres: new[] { "fighting", "casual" },
            ProSettings: new[]
            {
                "V-Sync: Off · FPS Limit: 240 · Overall Graphics Quality: Low · Display Mode: Fullscreen · Render Resolution: native.",
                "NVIDIA: Low Latency Mode Ultra for Brawlhalla (the game has no Reflex) · AMD: Radeon Anti-Lag on, Radeon Chill off.",
                "Rollback netcode: use wired internet: unstable ping feels like stutter even at high FPS.",
            },
            ProSource: "checkfps.io's Brawlhalla guide",
            Color1: "#2E9BD6", Color2: "#0A2233"),
        new("trackmania", "Trackmania", "TM", new[] { "Trackmania.exe" }, new[] { "Trackmania" },
            Reflex: false, AntiLag2: false,
            LatencySetting: "",
            FullscreenSetting: "Settings → Display → Display Mode → Fullscreen",
            FpsSettings: new[]
            {
                "Shadows: Minimum and Shaders: Low are the biggest FPS gains.",
                "GPU/CPU Synchronization: 1 Frame (Immediate for the lowest delay if your FPS can take it).",
            },
            Genres: new[] { "racing", "competitive" },
            ProSettings: new[]
            {
                "Display Mode: Fullscreen · V-Sync: Off · Frame Rate Limit: a cap you can hold · Dynamic Res: Off.",
                "GPU/CPU Synchronization: 1 Frame (try Immediate for the lowest delay).",
                "Customize Advanced Video Settings: On, Preset: Custom · Shaders: Low · Shadows: Minimum · Other Reflections: Off.",
                "Bloom/Lens Flare: None · Motion Blur and Slow-Mo Blur: Off · Nice AA: None · Fast AA: MSAA 2x or None · Texture Filtering: 2x.",
            },
            ProSource: "Hone's Trackmania settings guide",
            Color1: "#1EA7E1", Color2: "#06222E"),
        new("fh5", "Forza Horizon 5", "FH5", new[] { "ForzaHorizon5.exe" }, new[] { "Forza Horizon 5" },
            Reflex: false, AntiLag2: false,
            LatencySetting: "",
            FullscreenSetting: "Settings → Video → Fullscreen",
            FpsSettings: new[]
            {
                "Shadow Quality is the heaviest setting (Extreme costs about 30%), then SSAO (~16%) and Reflections (~13%).",
                "SSR Extreme costs ~18% (High ~5%) · MSAA 4x ~10% (2x ~5%).",
            },
            Genres: new[] { "racing" },
            ProSettings: new[]
            {
                "Shadow Quality: High (not Extreme) · SSAO: Medium or High · Reflection Quality: Medium.",
                "SSR Quality: High (Extreme is much heavier) · MSAA: 2x · World Car Level of Detail: High.",
                "Particle Effects: High · Motion Blur: Off · Ray tracing only affects ForzaVista: leave it off.",
                "Don't use the resolution scale option on PC.",
            },
            ProSource: "PCGamesN's Forza Horizon 5 benchmarks",
            Color1: "#E62E7A", Color2: "#2E0918"),
        new("f125", "F1 25", "F1", new[] { "F1_25.exe" }, new[] { "F1 25", "F1® 25" },
            Reflex: true, AntiLag2: false,
            LatencySetting: "Settings → Graphics → NVIDIA Reflex",
            FullscreenSetting: "Settings → Graphics → Display Mode → Fullscreen",
            FpsSettings: new[]
            {
                "Path tracing and ray tracing cost the most (about 222 → 108 FPS in testing): turn them all off.",
                "DLSS Quality gives the best FPS-to-sharpness trade.",
            },
            Genres: new[] { "racing", "sports" },
            ProSettings: new[]
            {
                "Path Tracing, Ray Reconstruction and every Ray Traced option: Off · NVIDIA Reflex: On.",
                "Anti-Aliasing: NVIDIA DLSS, Quality · Dynamic Resolution: Off.",
                "Lighting, Post Process, Shadows, Particles, Crowd, Mirrors, Weather, Ground Cover, Trees, SSR: High.",
                "Frame Generation adds delay: leave it off for online racing (fine for single-player).",
            },
            ProSource: "PCGamesN's F1 25 PC settings",
            Color1: "#E10600", Color2: "#2A0200"),
        new("fallguys", "Fall Guys", "FG", new[] { "FallGuys_client_game.exe" }, new[] { "Fall Guys" },
            Reflex: false, AntiLag2: false,
            LatencySetting: "",
            FullscreenSetting: "Settings → Video → Window Mode → Fullscreen",
            FpsSettings: new[]
            {
                "Shadow Quality: Low is the biggest gain, then View Distance and Effects.",
            },
            Genres: new[] { "casual", "battle royale" },
            ProSettings: new[]
            {
                "V-Sync: Off (adds delay to your jumps) · Frame Rate Limit: 144 (or your refresh rate).",
                "Shadow Quality: Low · View Distance: Medium · Anti-Aliasing: Medium · Effects: Medium.",
                "Post Processing: Low · Texture Quality: Medium · Motion Blur: Off.",
            },
            ProSource: "checkfps.io's Fall Guys guide",
            Color1: "#FF5EB5", Color2: "#2E0A1F"),
        new("lethalcompany", "Lethal Company", "LC", new[] { "Lethal Company.exe" }, new[] { "Lethal Company" },
            Reflex: false, AntiLag2: false,
            LatencySetting: "",
            FullscreenSetting: "Settings → Display → Fullscreen",
            FpsSettings: new[]
            {
                "Mods are the #1 cause of low FPS: disable the ones you don't need.",
                "Shadow Quality: Low is the biggest in-game gain.",
            },
            Genres: new[] { "co-op", "horror" },
            ProSettings: new[]
            {
                "Remove non-essential Thunderstore mods first · Shadow Quality: Low · Render Resolution: 90.",
                "Texture Quality: Medium · Bloom and Motion Blur: Off · V-Sync: Off.",
                "Stutter on DirectX 12? Add the Steam launch option -force-d3d11.",
            },
            ProSource: "checkfps.io's Lethal Company guide",
            Color1: "#E0412B", Color2: "#2A0B07"),
        new("phasmophobia", "Phasmophobia", "PH", new[] { "Phasmophobia.exe" }, new[] { "Phasmophobia" },
            Reflex: false, AntiLag2: false,
            LatencySetting: "",
            FullscreenSetting: "Settings → Video → Fullscreen",
            FpsSettings: new[]
            {
                "Shadows: Low is the biggest gain.",
                "No DLSS or FSR in this game: lower the Screen Percentage if you need frames.",
            },
            Genres: new[] { "co-op", "horror" },
            ProSettings: new[]
            {
                "Shadows: Low · V-Sync: Off (use a driver FPS limiter) · View Distance: Medium · Anti-Aliasing: FXAA.",
                "Texture Quality: Medium (High with 6 GB+) · Reflections: Low · Bloom/Post-Processing and Motion Blur: Off.",
                "Keep Particle Effects on Medium: moving dust is a gameplay clue.",
            },
            ProSource: "checkfps.io's Phasmophobia guide",
            Color1: "#4CB8A6", Color2: "#0A2420"),
        new("valheim", "Valheim", "VH", new[] { "valheim.exe" }, new[] { "Valheim" },
            Reflex: false, AntiLag2: false,
            LatencySetting: "",
            FullscreenSetting: "Settings → Graphics → Fullscreen",
            FpsSettings: new[]
            {
                "Big bases cost the most: thousands of building pieces tank FPS. Split or simplify them.",
                "Vegetation and Shadows are the biggest settings.",
            },
            Genres: new[] { "survival", "co-op", "sandbox" },
            ProSettings: new[]
            {
                "Vegetation: Medium · Shadows: Low · Draw Distance: Medium · V-Sync: Off.",
                "Particle Quality: Low · SSAO: Off · Water Quality: Low · Motion Blur and Depth of Field: Off · Terrain Shadows: Low.",
                "No DLSS or FSR: lower Render Scale if you still need frames.",
            },
            ProSource: "checkfps.io's Valheim guide",
            Color1: "#5E8C61", Color2: "#152316"),
        new("repo", "R.E.P.O.", "REPO", new[] { "REPO.exe" }, new[] { "R.E.P.O.", "REPO" },
            Reflex: false, AntiLag2: false,
            LatencySetting: "",
            FullscreenSetting: "Settings → Graphics → Fullscreen Mode → Exclusive Fullscreen",
            FpsSettings: new[]
            {
                "Resolution Scale 85 and Shadows Low are the biggest gains.",
            },
            Genres: new[] { "co-op", "horror", "casual" },
            ProSettings: new[]
            {
                "Fullscreen Mode: Exclusive Fullscreen · V-Sync: Off · Resolution Scale: 85.",
                "Shadow Quality: Low · Post-Processing: Low · Anti-Aliasing: FXAA · Ambient Occlusion: Off · Texture Quality: Medium.",
            },
            ProSource: "checkfps.io's R.E.P.O. guide",
            Color1: "#E8B935", Color2: "#2B220A"),
    };

    /// <summary>The tile that opens the "Add a game" picker.</summary>
    public static readonly KnownGame AddTile = new("add", "Add a game", "+", Array.Empty<string>(), Array.Empty<string>(),
        Reflex: false, AntiLag2: false, LatencySetting: "", FullscreenSetting: "", FpsSettings: Array.Empty<string>(), Genres: Array.Empty<string>());

    /// <summary>A game the user added: the general checklist, under the game's own name.</summary>
    public static KnownGame Custom(CustomGame c)
    {
        var initials = string.Concat(c.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).Take(2).Select(w => char.ToUpperInvariant(w[0])));
        return Generic with
        {
            Id = "custom:" + new string(c.Name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()),
            Name = c.Name,
            Short = initials.Length > 0 ? initials : "?",
            Exes = string.IsNullOrEmpty(c.Exe) ? Array.Empty<string>() : new[] { Path.GetFileName(c.Exe) },
            Matches = new[] { c.Name },
        };
    }

    /// <summary>The plan used for any game Pulse doesn't have notes for.</summary>
    public static readonly KnownGame Generic = new("other", "Any other game", "+", Array.Empty<string>(), Array.Empty<string>(),
        Reflex: false, AntiLag2: false, LatencySetting: "", FullscreenSetting: "",
        FpsSettings: new[]
        {
            "Shadows, Global Illumination, Reflections, Volumetric Fog and Ray Tracing usually cost the most FPS. Lower those first.",
            "Textures cost graphics memory, not FPS. Keep them high unless the game warns you about memory.",
            "Upscaling (DLSS, FSR or XeSS) on Quality gives the most extra frames for the least loss in sharpness. Frame generation adds delay.",
        },
        Genres: Array.Empty<string>(),
        Note: "Look in the game's Video or Graphics settings for \"NVIDIA Reflex\", \"AMD Anti-Lag 2\" or \"Low latency\". If it has one, turn it on.");
}

/// <summary>A game the user added from the "Add a game" picker.</summary>
public class CustomGame
{
    public string Name { get; set; }
    public string Exe { get; set; }
}
