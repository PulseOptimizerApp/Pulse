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
  // ES2015: esbuild lowers everything newer (optional catch, object rest/spread, class fields, async functions into
  // generators…), leaving Babel only the generators to rewrite (see below).
  target: "es2015",
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
  // Two passes, as Babel's own presets order them: first async functions become generators and the syntax the
  // generator rewrite can't take (destructuring, rest/spread, default parameters, for-of, let/const in loops) is
  // lowered; then the generators become plain state machines.
  const pass = (code, names) => babel.transformSync(code, {
    babelrc: false, configFile: false, compact: true, comments: false, sourceType: "script",
    plugins: names.map((name) => require(name)),
  }).code;
  const lowered = pass(pass(fs.readFileSync(outFile, "utf8"), [
    "@babel/plugin-transform-async-generator-functions",
    "@babel/plugin-transform-async-to-generator",
    "@babel/plugin-transform-object-rest-spread",
    "@babel/plugin-transform-destructuring",
    "@babel/plugin-transform-parameters",
    "@babel/plugin-transform-for-of",
    "@babel/plugin-transform-block-scoping",
    "@babel/plugin-transform-template-literals",
  ]), ["@babel/plugin-transform-regenerator"]);
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
// Pulse embeds it compressed (about a sixth of the size) and unpacks it when the Mouse page first needs it.
const zlib = await import("node:zlib");
fs.writeFileSync(outFile + ".gz", zlib.gzipSync(fs.readFileSync(outFile), { level: 9 }));
console.log(`${outFile}: ${fs.statSync(outFile).size} bytes (${fs.statSync(outFile + ".gz").size} compressed)`);
