## Download
Download **PulseOptimizer.exe** below and run it. It's a single file: nothing to unzip or install.

This page is the only official download. If you got Pulse anywhere else, delete it and download it here.

Pulse never asks you to turn off your antivirus or add an exclusion. If a copy asks you to, it's fake.

Windows may show "Windows protected your PC" because Pulse isn't code-signed yet. Click **More info → Run anyway**, after checking the SHA-256 below if you want to be sure.

## What's in this release
First public release.
- **Diagnose:** finds real problems first: monitor plugged into the motherboard or below its max refresh rate, single-channel or slow RAM, games on a hard drive, Wi-Fi lag, CPU throttling, high driver load, old graphics drivers, low disk space, startup bloat, and Defender problems.
- **Tweaks:** documented, reversible settings, each labelled by evidence and only offered when your PC qualifies. Tweaks Microsoft documents as unused are left out on purpose.
- **Measure:** before/after tests in your own game with Intel's PresentMon, plus controller and mouse polling, background CPU and boot time.
- **Clean up, Startup apps, Security** (Microsoft Defender scans and a startup/persistence check) and **Undo & restore**.

This is an early release: please report anything that looks wrong on the Issues tab.

## Verify your download
**SHA-256**
```
{{SHA256SUMS}}
```

**Build proof.** This release was built by GitHub Actions from this repository's public code. To check it yourself, install the [GitHub CLI](https://cli.github.com) and run:
```
gh attestation verify PulseOptimizer.exe --repo {{REPO}}
```

**VirusTotal:** (paste the VirusTotal link for PulseOptimizer.exe here)

Pulse is open source and is not affiliated with Microsoft or Intel.
