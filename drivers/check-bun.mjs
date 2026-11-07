// Local check without npm: bun check-bun.mjs  (CI uses build.mjs with esbuild)
import { protocolPlugin } from "./resolve-protocol.mjs";
const r = await Bun.build({ entrypoints: ["host.ts"], target: "browser", minify: true, plugins: [protocolPlugin()] });
if (!r.success) { for (const l of r.logs) console.error(String(l)); process.exit(1); }
const text = await r.outputs[0].text();
await Bun.write("/tmp/mouse-drivers.js", text);
console.log("ok", text.length);
