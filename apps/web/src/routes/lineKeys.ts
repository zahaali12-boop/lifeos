import type { ClipboardEvent, KeyboardEvent } from "react";

/**
 * Keyboard-first line tables (founder programme item 1e): Enter in a line moves down to the same column, and on the
 * last line adds a new one; the up and down arrows walk a column; a paste of rows from a spreadsheet (cells apart
 * by tabs, rows by line breaks) fills the lines from the cell it lands in, adding lines as needed. The editor names its
 * columns in the order they show, marks each input with `cell(index, column)`, and hangs the two handlers on the body.
 */
export interface LineKeys<L> {
  lines: L[];
  onChange: (lines: L[]) => void;
  /** A new, empty line. */
  empty: () => L;
  /** The columns a paste fills, in the order they show on screen. */
  columns: (keyof L & string)[];
  /** How a pasted cell becomes a value of the column (a number without its thousands separators, a code in upper case). */
  parse?: Partial<Record<keyof L & string, (raw: string) => string>>;
}

export interface LineKeyHandlers {
  onKeyDown: (event: KeyboardEvent<HTMLElement>) => void;
  onPaste: (event: ClipboardEvent<HTMLElement>) => void;
  /** The attributes an input carries so the handlers know which cell it is. */
  cell: (index: number, column: string) => { "data-line": string; "data-col": string };
}

/** A number as a spreadsheet writes it (1,250.50 or 1 250,50 in some locales) to the plain form the API takes. */
export function pastedNumber(raw: string): string {
  const text = raw.trim().replace(/[\s\u00a0]/gu, "");
  if (/^-?\d{1,3}(,\d{3})*(\.\d+)?$/u.test(text)) {
    return text.replace(/,/gu, "");
  }
  if (/^-?\d{1,3}(\.\d{3})*(,\d+)?$/u.test(text) && text.includes(",")) {
    return text.replace(/\./gu, "").replace(",", ".");
  }
  return text;
}

export const pastedCode = (raw: string): string => raw.trim().toUpperCase();

/** Rows and cells of what a spreadsheet put on the clipboard; a single plain value is not a paste of rows. */
export function parseRows(text: string): string[][] | null {
  const trimmed = text.replace(/\r\n?/gu, "\n").replace(/\n+$/u, "");
  if (!trimmed.includes("\t") && !trimmed.includes("\n")) {
    return null;
  }
  return trimmed.split("\n").map((row) => row.split("\t"));
}

function cellOf(target: EventTarget | null): { element: HTMLElement; index: number; column: string } | null {
  const element = target instanceof HTMLElement ? target : null;
  const index = Number(element?.dataset.line);
  const column = element?.dataset.col;
  return element && column && Number.isInteger(index) ? { element, index, column } : null;
}

function focusCell(from: HTMLElement, index: number, column: string): boolean {
  const body = from.closest("tbody") ?? from.closest("table") ?? document;
  const next = body.querySelector<HTMLElement>(`[data-line="${String(index)}"][data-col="${column}"]`);
  if (next) {
    next.focus();
    if (next instanceof HTMLInputElement) {
      next.select();
    }
    return true;
  }
  return false;
}

export function lineKeys<L extends object>({ lines, onChange, empty, columns, parse }: LineKeys<L>): LineKeyHandlers {
  const onKeyDown = (event: KeyboardEvent<HTMLElement>): void => {
    // A picker that used the key (its list open) has already said so.
    if (event.defaultPrevented) {
      return;
    }
    const at = cellOf(event.target);
    if (!at) {
      return;
    }
    if (event.key === "Enter") {
      event.preventDefault();
      if (at.index >= lines.length - 1) {
        onChange([...lines, empty()]);
        // The new line is on screen on the next frame.
        window.setTimeout(() => { focusCell(at.element, at.index + 1, at.column); }, 0);
      } else {
        focusCell(at.element, at.index + 1, at.column);
      }
    } else if ((event.key === "ArrowDown" || event.key === "ArrowUp") && at.element instanceof HTMLInputElement) {
      const index = at.index + (event.key === "ArrowDown" ? 1 : -1);
      if (index >= 0 && index < lines.length && focusCell(at.element, index, at.column)) {
        event.preventDefault();
      }
    }
  };

  const onPaste = (event: ClipboardEvent<HTMLElement>): void => {
    const at = cellOf(event.target);
    const rows = parseRows(event.clipboardData.getData("text/plain"));
    if (!at || !rows) {
      return;
    }
    const start = columns.indexOf(at.column as keyof L & string);
    if (start < 0) {
      return;
    }
    event.preventDefault();
    const next = [...lines];
    rows.forEach((cells, r) => {
      const index = at.index + r;
      while (next.length <= index) {
        next.push(empty());
      }
      const line = { ...next[index] } as L;
      cells.forEach((raw, c) => {
        const column = columns[start + c];
        if (column) {
          const parser = parse?.[column];
          (line as Record<string, unknown>)[column] = parser ? parser(raw) : raw.trim();
        }
      });
      next[index] = line;
    });
    onChange(next);
  };

  return { onKeyDown, onPaste, cell: (index, column) => ({ "data-line": String(index), "data-col": column }) };
}
