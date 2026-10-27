// Builds every game in assembly/ into build/*.wasm and checks the one rule a Kickl game must keep:
// it imports nothing. Usage:  npm run build            (all games)
//                             node build.mjs 01-hello  (one game)
import { spawnSync } from "node:child_process";
import { readFileSync, readdirSync, mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const asc = join(here, "node_modules", "assemblyscript", "bin", "asc.js");
mkdirSync(join(here, "build"), { recursive: true });

const entries = readdirSync(join(here, "assembly"))
  .filter((f) => f.endsWith(".ts") && f !== "kickl.ts" && !f.startsWith("_"))
  .map((f) => f.slice(0, -3));
const wanted = process.argv.slice(2).filter((a) => !a.startsWith("--"));
const todo = wanted.length ? wanted : entries;

let failed = false;
for (const name of todo) {
  const out = join(here, "build", name + ".wasm");
  const r = spawnSync(
    process.execPath,
    [
      asc, join("assembly", name + ".ts"), "-o", out,
      "--runtime", "stub",          // no garbage collector: a game allocates what it needs once, in init()
      "--use", "abort=",            // the default abort() is an import; with this a failed check just traps
      "--noAssert", "-O3", "--shrinkLevel", "1",
      "--initialMemory", "4", "--maximumMemory", "128",   // 64 KiB pages: at most 8 MiB, the unclaimed-name limit
    ],
    { cwd: here, stdio: "inherit" },
  );
  if (r.status !== 0) { failed = true; continue; }

  const wasm = new WebAssembly.Module(readFileSync(out));
  const imports = WebAssembly.Module.imports(wasm);
  const exports = new Set(WebAssembly.Module.exports(wasm).map((e) => e.name));
  const missing = ["memory", "kickl_abi", "init", "frame", "framebuffer"].filter((e) => !exports.has(e));
  const size = readFileSync(out).length;
  if (imports.length || missing.length) {
    failed = true;
    console.error(`x ${name}: ${imports.length ? "imports " + imports.map((i) => i.module + "." + i.name).join(", ") : ""} ${missing.length ? "missing exports " + missing.join(", ") : ""}`);
  } else {
    console.log(`ok ${name}.wasm  ${size.toLocaleString()} bytes, no imports`);
  }
}
process.exit(failed ? 1 : 0);
