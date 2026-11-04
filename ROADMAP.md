# Pulse Optimizer: roadmap and working prompt

Paste everything below the line into a new Claude session to pick the work back up.

---

You're continuing work on **Pulse Optimizer**, a free, open-source (MIT) Windows PC optimizer for gamers, with a large Roblox audience. The repository is `PulseOptimizer-app/PulseOptimizer` on GitHub. The website is `PulseOptimizer-app/pulseoptimizer-app.github.io`, and its download buttons always point to the latest GitHub release. The latest release is **v0.2.9**.

## How the project works (read before changing anything)

- **Stack:** C# / WPF on .NET 8 (`net8.0-windows`), published as one self-contained `PulseOptimizer.exe`. It has no NuGet dependencies, so keep it that way. Windows APIs are called through P/Invoke or COM interop.
- **Layout:** `src/PulseOptimizer/Core` (logic), `Pages` (one XAML page per sidebar item), `Ui` (controls, services, `Loc` translations), `Themes/Theme.xaml` (all shared styles), `Lang/es.json` and `Lang/pt-BR.json`, and `tests/PulseOptimizer.Tests` (xUnit).
- **Building:** a cloud session can't build or run WPF. GitHub Actions (`.github/workflows/build.yml`, `windows-latest`) compiles it and runs the tests. Push a branch, then trigger the workflow with `POST /repos/PulseOptimizer-app/PulseOptimizer/actions/workflows/build.yml/dispatches` with `ref=<branch>` if a push doesn't start it, and read the job log for `error CS`, `error MC` and `[FAIL]`.
- **Workflow:**
  - one branch per piece of work
  - open a PR to `main` and merge only when CI is green
  - never push straight to `main`
  - commit messages end with the Co-Authored-By line the session gives you
- **Releases:** the session can't create GitHub releases. The owner publishes them at `/releases/new` with a new tag `vX.Y.Z` on `main`. Give them the tag, title (`Pulse Optimizer X.Y.Z`) and short "What's new" notes to paste. The workflow then builds and attaches `PulseOptimizer.exe`, `SHA256SUMS.txt` and `ffmpeg-win64.zip` (about 5 minutes after publishing).
- **Translations:** the app follows the Windows language (English, Spanish, Portuguese-Brazil). Every new user-facing English string needs an entry in both `Lang` files. `Loc.T` also matches patterns: `"Freed {0}"` translates `"Freed 34.8 MB"`. Text built in code should use `Loc.F("… {0} …", value)`. Game names and in-game menu settings stay in English.
- **Promises that tests enforce** (`SafetyTests.cs`), never weaken them:
  - **No network code**, except the opt-in FFmpeg download in `Core/Clips/Ffmpeg.cs` (from this repo's own release, SHA-256 checked).
  - **No touching other processes' memory.** No injection, no `OpenProcess` or `WriteProcessMemory`.
  - **Deleting files** only happens in the cleaner and Clips' own files.
  - **Registry writes** go through `Reg.cs`, with a backup first.
  - **Defender** is never turned off, and no exclusions are added.
  - **Evidence only:** Pulse only applies documented settings and never sells placebo tweaks.
- **Roblox rules:** Roblox only honors Fast Flags on its official allowlist (since 29 Sep 2025). Don't add flags outside it, don't inject flags into the running client, and don't build "desync" or network flags. That's against Roblox's rules and gets accounts banned by Hyperion.
- **Look and feel:** the owner likes the current look: the dark theme with the cyan-to-purple gradient (`AccentGradient`), glow, hover and entrance animations. Keep that style. Polish is about consistency (type scale 11/12/13/14/15/17/20+, corner radius 12 cards / 10 buttons / 16 chips, card padding 24,20 or 18,14), not about replacing the style. A stripped-down, plain redesign was rejected. Ask for screenshots before redesigning a screen. The goal is a world-class UI system built on this look (roadmap section 2).
- **Testing:** the owner tests on their PC and sends screenshots. Explain what to click to test each change, and say plainly what couldn't be tested.

## Shipped recently (for context)

- **Roblox:**
  - finds Roblox for the logged-in account, in Program Files, and next to Bloxstrap-style launchers
  - clear error messages
  - Play falls back to the previous launcher
- **NVIDIA settings for Roblox only:** a small, NVIDIA-only Profile Inspector, including `.nip` import.
- **Debloat:** a step-by-step cleaning screen with honest results.
- **Translations:** full Spanish and Portuguese, including text that updates live.
- **Clips (beta):**
  - instant replay: FFmpeg ddagrab plus the graphics card's encoder, replay kept in RAM as MPEG-TS
  - game and mic audio through WASAPI
  - library, Medal-style trimmer (filmstrip, handles, frame stepping), Discord copy under 10 MB
  - quick-clip and screenshot hotkeys, tray mode, start with Windows
- **UI polish:** consistent sizes and spacing, dark drop-downs and menus, focus outlines.

## Roadmap

Work top to bottom unless the owner says otherwise. For each item: say the plan in a few lines, build it on a branch, get CI green, merge, and give release notes.

### 1. Verify and fix what's already shipped

1. **Roblox detection in the wild.** Confirm with real users (one reported "Roblox isn't installed" with Roblox installed) that v0.2.5+ finds Roblox, and fix any new cases from their screenshots.
2. **NVIDIA "Roblox only", guaranteed.** If Roblox's NVIDIA profile also lists other programs (for example Roblox Studio, or a shared profile), move `RobloxPlayerBeta.exe` into its own profile before applying, so only the game is affected.
3. **BUG (reported by a user, fix first): Clips records the menu instead of the match, and ignores the clip length.** A user played a Valorant match, saved a clip, and the 2:20 clip showed only the Valorant main menu (the lobby with "Find match"), never the game itself. Likely causes to check, in order:
   - **Frozen frame from exclusive or "optimized" fullscreen:** when a game goes fullscreen with independent flip, Desktop Duplication (ddagrab) can stop getting new frames, and FFmpeg keeps repeating the last frame it saw, which is the menu. Detect this: if the captured frames stop changing for a few seconds while a game is in focus, it's frozen.
   - **Wrong monitor or output:** the display index may not match the DXGI output the game is on (multi-monitor, or a game that moved displays).
   - **Capture restarted or lost:** a resolution or mode change when the match loads (menu → match often switches) can break ddagrab. It must detect this and restart capture on the new mode instead of silently stalling.
   - **Fixes:** restart ddagrab automatically on mode changes and stalls; add Windows Graphics Capture (WGC, the method OBS and Xbox Game Bar use) as a second capture method, used for the game's window when Desktop Duplication can't see it, chosen automatically; show a clear status in Clips ("Recording: Valorant") and warn if the picture hasn't changed. Never use game hooks or injection (anti-cheat like Vanguard would flag it).
   - **Same user, second bug: a 30-second clip came out 2:20 long.** The clip length was ignored, and the clip was even longer than the 2-minute replay. This is probably linked to the menu bug. Where to look in the code:
     - `TsRing` splits the replay at keyframes (`IsKeyframeStart` looks for the MPEG-TS random-access flag). `SaveTo` starts the clip at the last keyframe before the cut-off. If keyframes are missing or not flagged, there is only one group, so every save returns the whole buffer.
     - `Prune` can only drop a group once a newer one exists, so with no new keyframes the buffer also grows forever. That explains 2:20 > 2:00, and it is a memory leak.
     - Possible reasons for missing keyframes: the encoder ignoring `-g` (check AMF/QSV/NVENC and their IDR vs non-IDR I-frames, e.g. NVENC `-forced-idr 1`), or the capture stalling (frozen frame) so the encoder sends only repeats.
     - **Fixes:** force a keyframe every second for every encoder (`-force_key_frames "expr:gte(t,n_forced*1)"` plus each encoder's own IDR option); cap the ring by bytes as well as seconds; if the groups around the cut-off are too far apart, cut to the exact length with FFmpeg (`-ss`) when saving; and log keyframe spacing so a support screenshot shows the problem.
     - **Tests:** a `TsRing` unit test with sparse or missing keyframes (clip length and memory both stay bounded), and a CI check that each encoder's output has a keyframe at least every 2 seconds.
   - **Test** with Valorant, Fortnite, CS2 and Roblox in fullscreen, borderless and windowed, on one and two monitors, including clips saved right after a match starts.
4. **BUG (reported by a user): Diagnose says a 1080p monitor "isn't at its native resolution".** The user's Acer **XV240 F6** is a 24" 1920×1080 high-refresh monitor, running at 1920×1080. Diagnose said its full resolution is 3840×2160 (60 Hz) and told him to switch to it. That advice is wrong and would make things worse.
   - **Cause (in the code):** `MonitorInfo.Native` in `Core/Display.cs` picks the *largest* mode Windows offers. That list includes **NVIDIA DSR / DLDSR** and **AMD VSR** virtual resolutions, and modes the monitor accepts and then downscales (many 1080p monitors take a 4K 60 Hz signal). The check in `PcDiagnostics.Monitors.cs` (#3, "not at native resolution") then fires.
   - **Fix:** take native resolution from the monitor's EDID **preferred timing** (the first detailed timing descriptor, which is the panel's real resolution). Fall back to the largest mode only if EDID can't be read, and in that case don't show this warning at all. Also ignore modes larger than the EDID native, and use the same native value for the refresh-rate checks.
   - **Tests:** unit tests with a 1080p EDID plus 4K DSR modes (no warning), a 1440p panel running 1080p (warning), and a laptop panel.
5. **Clips beta hardening:**
   - audio/video sync (add an offset setting if needed)
   - capture in true exclusive fullscreen and on HDR displays (tone-map to SDR)
   - multi-monitor: the display index vs DXGI output order
   - limits on how many encoder sessions the graphics card allows
   - clear messages when the encoder fails
   - faster first-run encoder test
   - memory use at long replay lengths
6. **Website download gap.** For about 5 minutes after a release is published, the website's Download button gives "not found" while the exe builds. Change the site's script to fall back to the previous release's `PulseOptimizer.exe` when the latest release has no exe yet.
7. **Translation gaps.** Find text that's still English (strings joined with `+`, new strings) and convert it to `Loc.F` patterns with Spanish and Portuguese.
8. **Screen-by-screen polish** from the owner's screenshots, keeping the current style.

### 2. UI system: world-class, with our original look

The owner wants Pulse's UI on the same level as the best apps in the world (think Linear, Arc, Raycast, Discord, Steam's new client, Apple's own apps), while keeping the original Pulse personality: the dark theme, cyan-to-purple gradient, glow and animations. This is not a plain or minimal redesign. Two earlier attempts went wrong in opposite ways: the first looked sloppy, and the "clean" redesign was rejected as boring. The target is polished and premium *and* exciting.

1. **Design tokens first.** Before touching screens, define everything in `Theme.xaml` as named resources and use only those:
   - colors: background layers (app, sidebar, card, raised, hover, pressed), text (primary, secondary, faint, on-accent), borders, accent gradient, status colors (good, warning, danger, info)
   - type scale with fixed roles: display, page title, section title, body, label, caption, number
   - spacing on a 4 px grid (4, 8, 12, 16, 20, 24, 32, 48), corner radii, shadows and glow levels
   - motion: 3 durations (fast 120 ms, normal 200 ms, slow 320 ms) and 2 easing curves, used everywhere
2. **One component library.** Build each control once as a shared style, with all states (normal, hover, pressed, focused, disabled, loading): primary/secondary/ghost/danger buttons, toggle, slider, drop-down, text box, tabs, chips, cards, list rows, stat tiles, progress, toasts, dialogs, tooltips, empty states and skeleton loaders. No page defines its own one-off button or card.
3. **Where the pop goes.** Use the gradient and glow on purpose, not everywhere: the main action on each page, active sidebar item, key numbers (FPS gains, space freed), progress and success moments. Everything else stays calm so these stand out.
4. **Feel.** Smooth page transitions, hover lift on cards, satisfying feedback when something finishes (count-up numbers, a check animation), no layout jumps while loading, and nothing that ever looks frozen.
5. **Apply page by page.** Home, Diagnose, Tweaks, Roblox, Clips, Debloat, Settings: one page per PR, with before/after screenshots for the owner to approve before the next page.
6. **Quality bar.** Every page checked at 100%, 125% and 150% Windows scaling and a small window, in English, Spanish and Portuguese (longer text must not break layouts), with keyboard focus visible and text contrast readable everywhere.
7. **Show it first.** Before building, make a mockup of the new Home page in the new system and get the owner's OK on the direction.

### 3. Clips: get it to Medal level and beyond

1. **Best clip quality on the market (requested).** Clips must look better than Medal, NVIDIA ShadowPlay/App and AMD ReLive at the same file size, and match them at the same speed cost.
   - **Encoder tuning per brand:**
     - NVIDIA NVENC: `p5`–`p7` presets, `-tune hq`, `-rc vbr` with lookahead, spatial and temporal AQ, B-frames as reference, and full-range colour handled correctly
     - AMD AMF: `quality` preset, pre-analysis and VBAQ
     - Intel QSV: `veryslow`/`slower` preset, lookahead and extended bitrate control
     - libx264 fallback: tuned for games
   - **Codecs:** HEVC and AV1 (on RTX 40+/RX 7000+/Arc) as options, with H.264 kept as the "plays everywhere" default. Pick the best one the GPU supports automatically.
   - **Quality presets** with honest labels: *Balanced*, *High*, *Max* (and *Custom*: resolution, FPS up to 240, bitrate/quality value, codec), plus an estimate of the file size per minute and RAM use. Default to a level that looks great without hurting FPS.
   - **Capture at native resolution and FPS** (including 1440p/4K and 144/240 Hz), with optional downscaling done with a high-quality scaler (Lanczos) on the GPU.
   - **Colour accuracy:** correct BT.709 tags and range so clips don't look washed out or too dark in Discord, browsers or players. HDR games get proper tone-mapping to SDR, with optional HDR clips.
   - **Exports keep the quality:** trimming should cut without re-encoding when possible. When re-encoding is needed (the Discord copy, or cutting off a keyframe), use the best settings for the target size, two-pass when time allows.
   - **Audio:** AAC at 192–320 kb/s (or Opus), 48 kHz, no clipping or drift over long replays.
   - **Prove it:** compare Pulse vs Medal vs ShadowPlay on the same scene with screenshots and VMAF/SSIM scores at equal bitrate, and measure the FPS cost of each. Show the owner the results before claiming "best quality".
2. **Lightest clipper on the market (requested, same priority as quality).** Quality must never come from hurting games. Medal and NVIDIA App are known for costing FPS, stutter and background load; Pulse must be clearly lighter while matching or beating their quality.
   - **Performance budget, measured:** with the replay running, average FPS drops less than 2%, 1% lows less than 3%, no added stutter, CPU use under about 2% of one core outside of saving, and RAM equal to the replay buffer plus a small fixed amount. Check this with tests on real games (Roblox, Fortnite, Valorant, CS2) and show the owner the numbers.
   - **Keep work on the GPU:** capture (ddagrab), scaling and encoding stay on the graphics card's video engine, with no copies of frames through the CPU. Run the encoder at low priority so the game always comes first.
   - **Saving doesn't stutter:** saving a clip just copies the RAM buffer to disk at low I/O priority; any re-encode (Discord copy, trims) runs at below-normal priority and pauses while a game is in focus, or waits until the game closes.
   - **Nothing extra in the background:** no web views, no overlay injected into games, no telemetry, one helper process (FFmpeg) only while Clips is on, and the app itself idles near 0% CPU when minimised to the tray.
   - **Smart defaults:** pick the preset per PC from Diagnose (GPU, VRAM, RAM). Weaker PCs get a lighter default automatically, and Pulse warns, in plain words, if a chosen setting will cost FPS on this PC.
   - **Auto-protect:** if Pulse detects the game's FPS dropping because of capture (for example the encoder is maxed out), it lowers the clip settings for that session and tells the user afterwards.
   - **Prove it** together with quality: one comparison table of Pulse vs Medal vs NVIDIA App, showing FPS cost, 1% lows, CPU, RAM and quality score side by side.
3. **Full-screen player (requested).** A full-screen button in the clip player (plus double-click on the video and the `F` key, `Esc` to leave). It keeps the timeline, play/pause, volume and trim handles as an overlay that fades out when the mouse stops and comes back when it moves. It should open on the monitor the app is on, without stretching the video.
4. **Per-app audio tracks (requested).** Record game, Discord, Spotify, browser and microphone as separate tracks, using Windows' process-loopback capture (Windows 10 2004+). The trimmer shows a toggle per app, and exports mix the ones left on. Track 1 is always the full mix, for Discord and players.
5. **Long recording:** a start/stop hotkey for full sessions, with **bookmarks** (a hotkey adds a marker you can jump to).
6. **Voice clipping (opt-in):** a phrase like "Pulse, clip that" saves a clip. It must run fully offline, like Vosk, with its model downloaded only when turned on.
7. **Library:**
   - search and filter by game
   - rename
   - select several clips and delete them together
   - sort by date or size
   - show duration on the cards
8. **Overlay:** a small in-game "Clip saved" indicator for borderless games, with sound kept as the fallback.
9. **Later, if the owner wants (these cost money or need partners):** share links or cloud hosting, and automatic kill clips through game integrations.

### 4. Roblox page additions

1. **Settings per experience:** read the place ID from the Play link and apply different settings per game (max FPS for competitive games, prettier for showcases).
2. **Make Roblox use the graphics card:** set Windows' per-app GPU preference (`UserGpuPreferences`) for `RobloxPlayerBeta.exe`, with Undo. This is the biggest win on laptops.
3. **Game mode while Roblox runs:** a high-performance power plan and paused Windows Update downloads, put back when Roblox closes.
4. **Automatic cleanup:** clear Roblox logs and cache before launch when they're large and Roblox is closed.
5. **Check flags after every update:** read Roblox's log for "Denied local configuration" and tell the user if a flag stopped working.
6. **Connection check:** ping, jitter and spikes to Roblox servers while playing, plus Wi-Fi and background-download detection, with a plain answer: "your PC or internet" vs "Roblox's server".

### 5. New features (best on the market)

1. **Reaction time trainer** (its own page). Pulse already measures PC latency, so it can show "You: 182 ms · Your PC: 24 ms".
   - Training modes: choice reaction, go/no-go (ignore fakes), anticipation timing, and flick-to-target.
   - Training design: random wait times, difficulty that adapts, 5-minute sessions, a warm-up mode, and a progress graph over weeks that marks normal day-to-day variation.
   - Be honest: plain reaction time barely improves with practice; game reaction skills do.
2. **One-click "Optimize for this game":** Diagnose, the right tweaks, the game's pro settings and NVIDIA settings together, then a measured before/after.
3. **In-game overlay:** FPS, 1% lows, frame time graph, CPU/GPU temperature and ping, using PresentMon (already shipped inside Pulse).
4. **Driver manager:** check official NVIDIA, AMD and Intel drivers, offer a clean reinstall, and warn about known bad versions.
5. **Thermal and stutter detective:** log temperatures, clocks and frame times, then explain each stutter in plain words (heat, background app, driver, full drive).
6. **Network check for any game:** ping, jitter and packet loss to the game's servers, cable vs Wi-Fi.
7. **Game mode for every game:** the Roblox game mode, applied to any detected game.
8. **Hardware upgrade advisor:** the best FPS-per-dollar upgrade based on Diagnose (for example a second RAM stick for dual-channel). Honest advice, no affiliate links.
9. **Weekly report:** what changed, new startup apps, FPS trend, and anything that drifted back.

### Never build

- Fast Flags outside Roblox's allowlist, flag injection, or desync/network flags.
- Anything that reads or writes game memory, or hooks games.
- "FPS unlockers", RAM "cleaners", "timer resolution" tools, or other placebo tweaks.
- Anything that turns off antivirus or adds exclusions.

## First message to send in the new session

> Read ROADMAP.md in PulseOptimizer-app/PulseOptimizer and follow it. Start with section [number], item [number], unless I say otherwise. Tell me your plan in a few lines first.
