# Notices

Pulse Optimizer is licensed under the GNU Affero General Public License v3.0 or later (see LICENSE).
Releases up to and including v0.4.17 were published under the MIT license.

## Included work

- **OpenMouse mouse-protocol** — gaming mouse protocol codecs and drivers, used unmodified for the Mouse page.
  https://github.com/OpenMouse-Project/mouse-protocol — AGPL-3.0-or-later. Exact commit: `drivers/protocol.lock`.
  The bundled `mouse-drivers.js` is built from that source by `drivers/build.mjs` in CI.
- **RNNoise** — © Xiph.Org Foundation, Jean-Marc Valin; BSD-3-Clause. Built from v0.1.1 by `tools/build-rnnoise.py` in CI
  and used by the Mic studio for noise removal. https://github.com/xiph/rnnoise
- **NAudio** — © Mark Heath and contributors, MIT license. Used by the Mic studio for audio in and out.
- **PresentMon** — © Intel Corporation, MIT license.
- **FFmpeg** — downloaded separately on demand (GPL), run as its own program.
- **Microsoft Edge WebView2 SDK** — © Microsoft, BSD-style license; the WebView2 runtime is part of Windows.

Product names and brands shown on the Mouse page belong to their owners. Pulse is not affiliated with any of them.
