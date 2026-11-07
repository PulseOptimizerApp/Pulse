# Pulse Optimizer

Pulse is an open-source, evidence-based Windows optimizer. It finds what's actually holding a PC back first, only offers tweaks that Microsoft documents and that fit that PC, measures whether they help in your own game, and can undo every change exactly.

**Official download:** this repository's [Releases](../../releases) page, or the website [pulseoptimizerapp.github.io](https://pulseoptimizerapp.github.io/), which links to it.

> **Pulse moved.** Pulse's first home, `PulseOptimizer-app/PulseOptimizer`, is on hold. This is Pulse's home now. Versions 0.4.11 and older look for updates at the old address, so download 0.4.12 once from here; from then on Pulse updates itself from here (and still checks the old address too, in case it comes back). Pulse never asks you to turn off your antivirus or add an exclusion. If a copy does, it's fake.

## What it does

**Diagnose** checks for real problems before any tweak: a monitor running below its maximum refresh rate, RAM at its slow default speed (XMP/EXPO off), CPU throttling under load, high driver interrupt load, an old graphics driver, low disk space, startup bloat, Defender problems and the Power saver plan.

**Tweaks** each show:
- an evidence label: *Documented by Microsoft*, *Helps only on some PCs*, or *Comfort / privacy — no speed claim*;
- what it does, the exact setting it changes, what to expect, the downsides, and a link to its source;
- whether this PC qualifies. Tweaks that don't fit (for example GPU scheduling on a card without DLSS Frame Generation) are dimmed with the reason.

**Measure** runs a before/after test in your game with Intel's PresentMon: one warm-up run, then runs with the tweak off and on in alternating order, and a verdict of *Helped*, *Hurt* or *No measurable change* based on a 95% confidence range and this PC's normal run-to-run variation. It also measures controller and mouse polling rate, background CPU and boot time.

**Clean up**, **Startup apps** (the same switch as Task Manager, with advice per app), **Security** (Microsoft Defender scans plus a check of every place malware hides to start with Windows, mapped to MITRE ATT&CK), and **Undo & restore** (restore points, undo per tweak or everything, activity log).

**Clips** (opt-in) is an instant replay that can also clip big plays automatically (3Ks, aces, pentas) and show a recap of each match (map, agent or champion, K/D/A, headshots, rank): press a hotkey to save the last moments of your game. The replay waits in memory, recording uses your graphics card's own encoder (NVIDIA NVENC, AMD AMF or Intel Quick Sync) and Windows' screen capture, and clips stay in one folder on your PC. Turning Clips on downloads [FFmpeg](https://ffmpeg.org) (GPL) once from this repository's release page, checked against the SHA-256 built into Pulse; it runs as a separate program. Besides updates, that's the only download Pulse ever makes.

## What it never does
- Never changes a setting without saving the exact previous value first.
- Never uploads anything. It only downloads from its own GitHub releases: FFmpeg when you turn on Clips (below), and new versions of Pulse when you press Install on the update card. The lag checks only send pings. Match recaps read each game's own official data: Counter-Strike 2 and Dota 2 send it to Pulse on your PC, League of Legends serves it on your PC, and for VALORANT Pulse asks Riot for your own last match using the Riot Client's sign-in on your PC.
- Never reads, writes or injects into running games or other processes.
- Never turns off your antivirus or adds exclusions.
- Never applies tweaks that Microsoft documents as unused, or undocumented ones as speed boosts.

Automated tests check these promises on every build (see [SECURITY.md](SECURITY.md)).

It asks for admin because tweaks change system-wide settings, and scans and restore points need it. You can turn on *Skip the admin prompt* (a Windows scheduled task that starts Pulse with admin rights), and turn it off again on the Safety page.

## Tweaks deliberately left out
| Tweak | Why |
| --- | --- |
| MMCSS "GPU Priority" / "SFIO Priority" | Microsoft documents them as "not yet used" / "not used" |
| Timer resolution registry tweaks | Timer resolution has been per-process since Windows 10 2004 |

NetworkThrottlingIndex and TCPNoDelay (Nagle's algorithm) aren't in Microsoft's documentation, but many players ask for them. Pulse offers them as optional, labelled *Helps only on some PCs*, with that explained, and Measure shows whether they help on your PC.

## Verify your download
Every release lists the SHA-256 of `PulseOptimizer.exe`, and carries a **build attestation**: signed proof from GitHub that it was built from this repository's public code. To check it, install the [GitHub CLI](https://cli.github.com) and run:
```
gh attestation verify PulseOptimizer.exe --repo OWNER/REPO
```
Replace `OWNER/REPO` with this repository's name.

## Build it yourself
Double-click `build.bat`. It installs the .NET 8 SDK if needed, builds a single `PulseOptimizer.exe` into `dist`, and downloads PresentMon into `dist\tools`.

## Releasing (maintainers)
1. Push to `main`. The **Build and test** workflow runs the tests and builds the exe on Windows.
2. Create and push a tag like `v0.1.0` (or draft a release with that tag). The workflow publishes the release with `PulseOptimizer.exe`, `SHA256SUMS.txt` and the build attestation.
3. Edit the release notes' "What changed" and add the VirusTotal link.

## Signing
Unsigned apps trigger Windows SmartScreen ("Windows protected your PC"). Signing the exe with a code-signing certificate removes that over time. Add a signing step after "Build single-file exe" in `.github/workflows/build.yml` once you have a certificate.

## Data
Pulse keeps its backups and results in `%LocalAppData%\PulseOptimizer\`: `state.json` (the saved previous value of every changed setting), `measurements.json`, and `captures\` (raw PresentMon CSVs).

## License
MIT. Pulse is not affiliated with Microsoft or Intel. PresentMon is © Intel Corporation, MIT license.
