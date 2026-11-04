// Parses every ```mermaid block under the given directory with the real Mermaid parser (CI gate for the docs).
import { JSDOM } from "jsdom";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { dirname, isAbsolute, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");

const dom = new JSDOM("<!DOCTYPE html><html><body></body></html>", { pretendToBeVisual: true });
globalThis.window = dom.window;
globalThis.document = dom.window.document;
globalThis.DOMPurify = { sanitize: (x) => x, addHook() {} };
globalThis.getComputedStyle = dom.window.getComputedStyle;

const mermaid = (await import("mermaid")).default;
mermaid.initialize({ startOnLoad: false, securityLevel: "loose" });

function walk(dir, out = []) {
  for (const entry of readdirSync(dir)) {
    const path = join(dir, entry);
    if (statSync(path).isDirectory()) walk(path, out);
    else if (path.endsWith(".md")) out.push(path);
  }
  return out;
}

const requested = process.argv[2] ?? "docs";
const root = isAbsolute(requested) ? requested : resolve(repoRoot, requested);
let total = 0;
let failed = 0;
for (const file of walk(root)) {
  const text = readFileSync(file, "utf8");
  const re = /```mermaid\n([\s\S]*?)```/g;
  let match;
  while ((match = re.exec(text))) {
    total++;
    const line = text.slice(0, match.index).split("\n").length;
    try {
      await mermaid.parse(match[1]);
    } catch (error) {
      failed++;
      console.log(`FAIL ${file}:${line}\n     ${String(error.message ?? error).split("\n").slice(0, 3).join("\n     ")}`);
    }
  }
}
console.log(`${total} diagrams checked, ${failed} failed`);
process.exit(failed ? 1 : 0);
