// Bundles tools/mouse/bridge.ts with OpenMouse's mouse-protocol sources into one script for Pulse's engine.
// The library imports its own modules by package name ("@openmouse/protocol/lamzu"); those point at its compiled dist/
// through package.json "exports", so here each one is mapped to the matching TypeScript source instead.
// Usage: node bundle.mjs <esbuild install dir> <mouse-protocol checkout> <bridge.ts> <out.js>
import { createRequire } from "node:module";
import fs from "node:fs";
import path from "node:path";

const [installDir, protoDir, entry, outFile] = process.argv.slice(2);
const require = createRequire(path.join(path.resolve(installDir), "node_modules", "noop.js"));
const esbuild = require("esbuild");
const pkg = JSON.parse(fs.readFileSync(path.join(protoDir, "package.json"), "utf8"));

function sourceFor(spec) {
  const sub = spec === "@openmouse/protocol" ? "." : "." + spec.slice("@openmouse/protocol".length);
  const target = pkg.exports?.[sub];
  const file = typeof target === "string" ? target : target?.import ?? target?.default;
  if (!file) return null;
  const src = path.resolve(protoDir, file.replace(/^\.\/dist\//, "./src/").replace(/\.js$/, ".ts"));
  return fs.existsSync(src) ? src : null;
}

await esbuild.build({
  entryPoints: [entry],
  bundle: true,
  format: "iife",
  target: "es2019",
  platform: "neutral",
  minify: true,
  legalComments: "none",
  outfile: outFile,
  plugins: [{
    name: "openmouse-self",
    setup(build) {
      build.onResolve({ filter: /^@openmouse\/protocol(\/.*)?$/ }, (args) => {
        const p = sourceFor(args.path);
        return p ? { path: p } : { errors: [{ text: `No source for ${args.path}` }] };
      });
    },
  }],
});
// Pulse's script engine (Jint) runs `await` by blocking its thread until the promise settles. Every answer from the
// mouse is delivered on that same thread, so a driver awaiting the mouse would wait forever. So no `await` may be
// left: Babel rewrites async functions (and async generators) into plain promise steps (a small state machine), and
// esbuild then refuses the build if any async function, await or generator survived.
let babel = null;
try { babel = require("@babel/core"); } catch { }
if (babel) {
  const plugins = ["@babel/plugin-transform-async-generator-functions", "@babel/plugin-transform-async-to-generator", "@babel/plugin-transform-regenerator"]
    .map((name) => require(name));
  const lowered = babel.transformSync(fs.readFileSync(outFile, "utf8"), {
    babelrc: false, configFile: false, compact: true, comments: false, sourceType: "script", plugins,
  }).code;
  const checked = await esbuild.transform(lowered, {
    minify: true, legalComments: "none", target: "es2019",
    supported: { "async-await": false, "async-generator": false, "for-await": false, generator: false },
  });
  fs.writeFileSync(outFile, checked.code);
} else if (process.env.CI) {
  throw new Error("Babel is needed to remove async/await for Pulse's script engine (npm install it in the build folder).");
} else {
  console.warn("Babel not installed: async/await left in the bundle (the mouse engine will hang on the first await).");
}
console.log(`${outFile}: ${fs.statSync(outFile).size} bytes`);
