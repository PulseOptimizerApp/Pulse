# Security and safety

Pulse changes Windows settings, so it has to earn trust. These are its rules, and the tests in `tests/PulseOptimizer.Tests` fail the build if the code ever breaks them:

- **Every change is backed up first.** All registry changes go through one helper that saves the exact previous value, and undo restores it byte-for-byte (`UndoTests.cs`).
- **No network code.** Pulse never uploads or downloads anything itself. Links (driver pages, VirusTotal lookups by file hash) open in your browser (`SafetyTests.cs`).
- **Never touches games.** No reading, writing or injecting into other processes. Frame times come from Intel's PresentMon, which reads Windows' own event tracing.
- **Never weakens your antivirus.** No turning off Defender, no exclusions.
- **Deletes only on the Clean up page,** only the files listed there, after you confirm.
- **No placebo.** Tweaks cut by the research (undocumented or documented as unused) can't come back (`CatalogTests.cs`).

## Report a problem
If you find a bug that breaks one of these rules, or a security issue:

- Use **Report a vulnerability** on this repository's Security tab, so it stays private until it's fixed. Don't open a public issue for it.
- Include what you saw and how to reproduce it.

Confirmed issues are fixed in a new release with a note in the release notes.

## Fake copies
The only official download is this repository's Releases page. Pulse never asks anyone to disable antivirus or add an exclusion. If you find a copy that does, or one whose SHA-256 doesn't match the release, please report it.
