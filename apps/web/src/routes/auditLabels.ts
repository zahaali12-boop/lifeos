import type { TFunction } from "i18next";
import { formatDate, formatDateTime } from "../lib/format";

type Tone = "neutral" | "accent" | "success" | "warning" | "danger" | "info";

const tones: Record<string, Tone> = {
  created: "success",
  posted: "success",
  approved: "success",
  activated: "success",
  received: "success",
  updated: "accent",
  state_changed: "accent",
  corrected: "warning",
  override: "warning",
  reversed: "warning",
  revealed: "warning",
  deleted: "danger",
  rejected: "danger",
  revoked: "danger",
  cancelled: "danger",
  login_failed: "danger",
  step_up_failed: "danger",
};

/** The badge tone of an audit action; the label always says what it means, the colour only helps scanning. */
export function auditActionTone(action: string): Tone {
  return tones[action] ?? "neutral";
}

/** An audit action in words ("state_changed" → "Status changed"); an action without a label reads as its code. */
export function auditActionLabel(t: TFunction, action: string): string {
  return t(`audit.actions.${action}`, { defaultValue: action.replaceAll("_", " ") });
}

/** A record type in words ("purchase_order" → "Purchase order"); a type without a label reads as its code. */
export function auditEntityLabel(t: TFunction, entityType: string): string {
  return t(`audit.entityTypes.${entityType}`, { defaultValue: entityType });
}

/** Every action and record type with a label, for the explorer's suggestions. */
export function auditVocabulary(t: TFunction): { actions: { code: string; label: string }[]; entityTypes: { code: string; label: string }[] } {
  const listed = (group: "actions" | "entityTypes"): string[] => {
    const labels: unknown = t(`audit.${group}`, { returnObjects: true });
    return typeof labels === "object" && labels !== null ? Object.keys(labels) : [];
  };
  return {
    actions: listed("actions").map((code) => ({ code, label: auditActionLabel(t, code) })),
    entityTypes: listed("entityTypes").map((code) => ({ code, label: auditEntityLabel(t, code) })).sort((a, b) => a.label.localeCompare(b.label)),
  };
}

const dateTime = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/;
const dateOnly = /^\d{4}-\d{2}-\d{2}$/;

/**
 * A value from an audit snapshot as text. Amounts and quantities stay the exact strings the server recorded (never
 * through floating point); timestamps and dates read in the user's locale; anything structured is compact JSON.
 */
export function auditValue(t: TFunction, value: unknown): string {
  if (value === null || value === undefined || value === "") {
    return "—";
  }
  if (typeof value === "boolean") {
    return value ? t("common.yes") : t("common.no");
  }
  if (typeof value === "string") {
    return dateTime.test(value) ? formatDateTime(value) : dateOnly.test(value) ? formatDate(value) : value;
  }
  if (typeof value === "number") {
    return String(value);
  }
  return JSON.stringify(value);
}

/** The field-level changes of an audit event (`{ field: { old, new } }`), in the order the server wrote them. */
export function auditChanges(diff: unknown): { field: string; before: unknown; after: unknown }[] {
  if (!diff || typeof diff !== "object" || Array.isArray(diff)) {
    return [];
  }
  return Object.entries(diff as Record<string, unknown>).map(([field, change]) => {
    const pair = (change ?? {}) as { old?: unknown; new?: unknown };
    return { field, before: pair.old ?? null, after: pair.new ?? null };
  });
}
