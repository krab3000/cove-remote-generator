// Fails the build when the bundle imports anything but Cove's runtime modules: a bare "react" import
// would load a second React and break hooks inside the host tree.
import { readFileSync } from "node:fs";

const code = readFileSync(new URL("../dist/ui.mjs", import.meta.url), "utf8");
const specifiers = [...code.matchAll(/(?:^|[;\s}])(?:import|export)\s*(?:[^'"]*?\sfrom\s*)?["']([^"']+)["']/gm)].map((m) => m[1]);
const dynamic = [...code.matchAll(/import\(\s*["']([^"']+)["']\s*\)/g)].map((m) => m[1]);
const bad = [...specifiers, ...dynamic].filter((s) => !s.startsWith("@cove/runtime/"));

if (bad.length > 0) {
  console.error(`ui.mjs imports modules Cove does not provide: ${[...new Set(bad)].join(", ")}`);
  process.exit(1);
}
console.log(`ui.mjs ok (${(code.length / 1024).toFixed(1)} KiB, imports: ${[...new Set(specifiers)].join(", ")})`);
