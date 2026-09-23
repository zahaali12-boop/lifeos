#!/usr/bin/env node
/**
 * Every key in en.json must exist in ar.json with a non-empty value (ASSUMPTIONS A-063: Arabic is required for merge),
 * and ar.json must not carry keys English lacks (dead translations). Messages use ICU syntax ({name}, {count, plural, ...}),
 * so a doubled-brace placeholder would render literally and is refused. Exit 1 with the offending keys otherwise.
 */
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "src", "i18n");
const en = JSON.parse(readFileSync(join(root, "en.json"), "utf8"));
const ar = JSON.parse(readFileSync(join(root, "ar.json"), "utf8"));

function flatten(object, prefix = "") {
  return Object.entries(object).flatMap(([key, value]) =>
    typeof value === "object" && value !== null ? flatten(value, `${prefix}${key}.`) : [[`${prefix}${key}`, value]],
  );
}

const english = new Map(flatten(en));
const arabic = new Map(flatten(ar));
const missing = [...english.keys()].filter((key) => !arabic.has(key) || String(arabic.get(key)).trim() === "");
const stale = [...arabic.keys()].filter((key) => !english.has(key));
const doubled = [...english, ...arabic].filter(([, value]) => /\{\{/.test(String(value))).map(([key]) => key);

if (missing.length > 0 || stale.length > 0 || doubled.length > 0) {
  if (missing.length > 0) console.error(`Missing or empty Arabic translations (${missing.length}):\n  - ${missing.join("\n  - ")}`);
  if (stale.length > 0) console.error(`Arabic keys without an English source (${stale.length}):\n  - ${stale.join("\n  - ")}`);
  if (doubled.length > 0) console.error(`Placeholders in {{double braces}} (ICU uses {single}):\n  - ${[...new Set(doubled)].join("\n  - ")}`);
  process.exit(1);
}
console.log(`i18n: ${english.size} keys, Arabic complete.`);
