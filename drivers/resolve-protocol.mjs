// Resolves "@openmouse/protocol" and "@openmouse/protocol/<entry>" to the library's TypeScript sources
// (its package exports point at a dist/ folder we don't build). Works as an esbuild or Bun plugin.
import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";

export function protocolPlugin(root = "vendor/mouse-protocol") {
  const pkg = JSON.parse(readFileSync(resolve(root, "package.json"), "utf8"));
  const map = new Map();
  for (const [key, value] of Object.entries(pkg.exports ?? {})) {
    const target = typeof value === "string" ? value : value.import ?? value.default;
    if (!target) continue;
    map.set("@openmouse/protocol" + (key === "." ? "" : key.slice(1)), resolve(root, target.replace("./dist/", "src/").replace(/\.js$/, ".ts")));
  }
  return {
    name: "openmouse-protocol",
    setup(build) {
      build.onResolve({ filter: /^@openmouse\/protocol(\/.*)?$/ }, (args) => {
        let path = map.get(args.path);
        if (!path) {
          const sub = args.path.slice("@openmouse/protocol/".length);
          path = [resolve(root, "src", sub, "index.ts"), resolve(root, "src", sub + ".ts")].find(existsSync);
        }
        if (!path) return { errors: [{ text: "Unknown @openmouse/protocol entry: " + args.path }] };
        return { path };
      });
    },
  };
}
