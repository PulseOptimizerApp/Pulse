// Builds Pulse's mouse driver bundle from OpenMouse's mouse-protocol library (AGPL-3.0-or-later),
// pinned to the commit in protocol.lock, into src/PulseOptimizer/Assets/Drivers/mouse-drivers.js.
// Run by CI (and locally): npm ci && npm run build
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync } from "node:fs";
import { build } from "esbuild";
import { protocolPlugin } from "./resolve-protocol.mjs";

const [repo, sha] = readFileSync("protocol.lock", "utf8").trim().split(/\s+/);
const dir = "vendor/mouse-protocol";
if (!existsSync(dir)) {
  mkdirSync(dir, { recursive: true });
  const git = (...a) => execFileSync("git", a, { cwd: dir, stdio: "inherit" });
  git("init", "-q");
  git("remote", "add", "origin", repo);
  git("fetch", "-q", "--depth", "1", "origin", sha);
  git("checkout", "-q", "FETCH_HEAD");
}
mkdirSync("../src/PulseOptimizer/Assets/Drivers", { recursive: true });
await build({
  entryPoints: ["host.ts"],
  bundle: true,
  format: "iife",
  target: "chrome120",
  minify: true,
  legalComments: "inline",
  banner: { js: `/* Pulse mouse drivers. Includes @openmouse/protocol ${sha} (AGPL-3.0-or-later, ${repo}). */` },
  outfile: "../src/PulseOptimizer/Assets/Drivers/mouse-drivers.js",
  plugins: [protocolPlugin(dir)],
  logLevel: "warning",
});
console.log("built mouse-drivers.js from", sha);
