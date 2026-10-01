/**
 * The file name a Content-Disposition header gives: the UTF-8 form (filename*, RFC 6266) when present, since the plain
 * form of a non-English name is only an approximation, else the plain one.
 */
export function fileNameOf(disposition: string): string | null {
  const encoded = /filename\*\s*=\s*(?:UTF-8'[^']*')?([^;]+)/i.exec(disposition)?.[1]?.trim();
  if (encoded) {
    try {
      return decodeURIComponent(encoded.replace(/^"|"$/g, ""));
    } catch {
      // fall through to the plain form
    }
  }
  return /filename\s*=\s*"?([^";]+)"?/i.exec(disposition)?.[1]?.trim() ?? null;
}

/** Saves a download from the API (the bearer token travels with the client, so a plain link would not do). */
export function saveFile(blob: Blob, headers: Headers, fallbackName: string): void {
  const name = fileNameOf(headers.get("content-disposition") ?? "") ?? fallbackName;
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = name;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}
