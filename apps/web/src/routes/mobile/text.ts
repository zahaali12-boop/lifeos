import { i18n } from "../../i18n";

/** A bilingual name in the current language, falling back to English, then to whatever is there. */
export function localized(name: Record<string, string> | null | undefined): string {
  if (!name) {
    return "";
  }
  return name[i18n.language] ?? name.en ?? Object.values(name)[0] ?? "";
}

/** A non-negative decimal typed by hand: digits with an optional fraction, Eastern Arabic digits accepted. */
export function parseQuantity(raw: string): string | null {
  const normalized = raw
    .trim()
    .replace(/[٠-٩]/g, (d) => String(d.charCodeAt(0) - 0x0660))
    .replace(/[٫,]/g, ".");
  return /^\d+(\.\d+)?$/.test(normalized) ? normalized : null;
}
