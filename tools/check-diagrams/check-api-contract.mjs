#!/usr/bin/env node
/**
 * Breaking-change check between two OpenAPI documents (the contract on the base branch and the one just built).
 *
 *   node check-api-contract.mjs <base.json> <head.json>
 *
 * Fails (exit 1) when the head removes anything a client may depend on: a path, an operation, a response property,
 * a response status, an enum value, or when it adds a required request property or makes a response property
 * nullable-only. Additions are fine. Exit 0 with a summary otherwise; exit 0 with a note when the base is missing
 * (first contract ever).
 *
 * Corrections (ADR-0012): when the published document misdescribed the server (two types once shared one schema
 * name), the corrected contract lists each removed line, verbatim, with its reason in contract-corrections.json next
 * to the head document; exactly those lines are accepted, everything else still fails.
 */
import { readFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";

const [basePath, headPath] = process.argv.slice(2);
if (!basePath || !headPath) {
  console.error("usage: check-api-contract <base.json> <head.json>");
  process.exit(2);
}
if (!existsSync(basePath)) {
  console.log(`No base contract at ${basePath}: nothing to compare (first contract).`);
  process.exit(0);
}

const base = JSON.parse(readFileSync(basePath, "utf8"));
const head = JSON.parse(readFileSync(headPath, "utf8"));
const breaking = [];
const additions = [];

function resolve(doc, schema, seen = new Set()) {
  if (!schema) return schema;
  if (schema.$ref) {
    if (seen.has(schema.$ref)) return {};
    seen.add(schema.$ref);
    const parts = schema.$ref.replace(/^#\//, "").split("/");
    let target = doc;
    for (const part of parts) target = target?.[part];
    return resolve(doc, target, seen);
  }
  return schema;
}

function properties(doc, schema, path, seen = new Set()) {
  const resolved = resolve(doc, schema);
  if (!resolved) return new Map();
  const out = new Map();
  if (resolved.type === "array" || resolved.items) {
    for (const [key, value] of properties(doc, resolved.items, `${path}[]`, seen)) out.set(key, value);
    return out;
  }
  for (const [name, property] of Object.entries(resolved.properties ?? {})) {
    const key = `${path}.${name}`;
    if (seen.has(key)) continue;
    seen.add(key);
    const resolvedProperty = resolve(doc, property);
    out.set(key, { schema: resolvedProperty, required: (resolved.required ?? []).includes(name) });
    if (resolvedProperty?.properties || resolvedProperty?.items) {
      for (const [k, v] of properties(doc, resolvedProperty, key, seen)) out.set(k, v);
    }
  }
  return out;
}

function enumValues(schema) {
  return new Set(schema?.enum ?? []);
}

for (const [path, baseItem] of Object.entries(base.paths ?? {})) {
  const headItem = head.paths?.[path];
  if (!headItem) {
    breaking.push(`path removed: ${path}`);
    continue;
  }
  for (const [method, baseOperation] of Object.entries(baseItem)) {
    if (!["get", "put", "post", "delete", "patch"].includes(method)) continue;
    const headOperation = headItem[method];
    const where = `${method.toUpperCase()} ${path}`;
    if (!headOperation) {
      breaking.push(`operation removed: ${where}`);
      continue;
    }
    // Responses: statuses and properties must survive; enum values may not disappear.
    for (const [status, baseResponse] of Object.entries(baseOperation.responses ?? {})) {
      const headResponse = headOperation.responses?.[status];
      if (!headResponse) {
        breaking.push(`response ${status} removed: ${where}`);
        continue;
      }
      const baseSchema = baseResponse.content?.["application/json"]?.schema;
      const headSchema = headResponse.content?.["application/json"]?.schema;
      if (baseSchema && !headSchema) {
        breaking.push(`response ${status} lost its schema: ${where}`);
        continue;
      }
      const baseProps = properties(base, baseSchema, "$");
      const headProps = properties(head, headSchema, "$");
      for (const [key, value] of baseProps) {
        const headValue = headProps.get(key);
        if (!headValue) {
          breaking.push(`response property removed: ${where} ${status} ${key}`);
          continue;
        }
        for (const v of enumValues(value.schema)) {
          if (!enumValues(headValue.schema).has(v)) breaking.push(`enum value removed: ${where} ${status} ${key} '${v}'`);
        }
        if (value.required && !headValue.required) breaking.push(`response property no longer guaranteed: ${where} ${status} ${key}`);
      }
      for (const key of headProps.keys()) if (!baseProps.has(key)) additions.push(`${where} ${status} ${key}`);
    }
    // Requests: a newly required property, a removed property, or a removed parameter breaks callers.
    const baseBody = baseOperation.requestBody?.content?.["application/json"]?.schema;
    const headBody = headOperation.requestBody?.content?.["application/json"]?.schema;
    if (baseBody || headBody) {
      const baseProps = properties(base, baseBody, "$");
      const headProps = properties(head, headBody, "$");
      for (const [key, value] of headProps) {
        const baseValue = baseProps.get(key);
        if (!baseValue && value.required) breaking.push(`new required request property: ${where} ${key}`);
        if (baseValue && !baseValue.required && value.required) breaking.push(`request property became required: ${where} ${key}`);
      }
      for (const key of baseProps.keys()) if (!headProps.has(key)) breaking.push(`request property removed: ${where} ${key}`);
    }
    const baseParams = new Map((baseOperation.parameters ?? []).map((p) => [`${p.in}:${p.name}`, p]));
    const headParams = new Map((headOperation.parameters ?? []).map((p) => [`${p.in}:${p.name}`, p]));
    for (const [key] of baseParams) if (!headParams.has(key)) breaking.push(`parameter removed: ${where} ${key}`);
    for (const [key, p] of headParams) if (!baseParams.has(key) && p.required) breaking.push(`new required parameter: ${where} ${key}`);
  }
}
for (const path of Object.keys(head.paths ?? {})) if (!base.paths?.[path]) additions.push(`path added: ${path}`);

const correctionsPath = join(dirname(headPath), "contract-corrections.json");
const corrections = existsSync(correctionsPath) ? JSON.parse(readFileSync(correctionsPath, "utf8")) : [];
const acknowledged = new Set(corrections.map((c) => c.change));
const remaining = breaking.filter((b) => !acknowledged.has(b));
const stale = corrections.filter((c) => !breaking.includes(c.change));
if (stale.length > 0) {
  console.log(`API contract: ${stale.length} recorded correction(s) no longer apply and can be removed from ${correctionsPath}:\n  - ${stale.map((c) => c.change).join("\n  - ")}`);
}
if (remaining.length > 0) {
  console.error(`API contract: ${remaining.length} breaking change(s)\n  - ${remaining.join("\n  - ")}`);
  console.error("\nBreaking changes need a new API version or a deprecation window (docs/adr: contract policy); a correction of a misdescribed schema is recorded in contract-corrections.json with its reason.");
  process.exit(1);
}
console.log(`API contract: compatible (${additions.length} addition(s)${breaking.length > 0 ? `, ${breaking.length} recorded correction(s)` : ""}).`);
