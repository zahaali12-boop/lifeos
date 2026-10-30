import { cn } from "@quicker/ui";
import { useCallback, useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type ComponentProps, type CSSProperties, type KeyboardEvent, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { useTranslation } from "react-i18next";
import { TextField } from "./common";

/** One entry of a picker: the value written into the field and what the list shows for it. */
export interface ComboOption {
  value: string;
  label?: string;
  detail?: string;
}

type ComboFieldProps = Omit<ComponentProps<typeof TextField>, "value" | "onChange" | "list" | "role"> & {
  value: string;
  onChange: (value: string) => void;
  options: ComboOption[];
  /** Entries shown first, under their own heading (the cash and bank accounts of an account field, say). */
  pinned?: { label: string; values: string[] };
  /** The heading of the full list when something is pinned. */
  allLabel?: string;
  /** Applied to what is typed (upper-casing a code, say). */
  transform?: (raw: string) => string;
  /** A list of values in one field (scopes): the picker completes the last one, after the separator. */
  separator?: string;
  /** Called with what is typed, for a picker whose options come from a search. */
  onQueryChange?: (query: string) => void;
  "data-testid"?: string | undefined;
};

const MAX_SHOWN = 80;
const PREFERRED_HEIGHT = 320;

/** Where the list goes: inside the open dialog (its focus trap and pointer lock cover only its own content), else the page. */
function hostOf(input: HTMLElement): HTMLElement {
  return input.closest<HTMLElement>("[role=dialog]") ?? document.body;
}

/**
 * A text field that opens its list as soon as it is clicked or focused, with the most useful entries on top, and narrows
 * it as you type (code or name). Arrow keys move, Enter picks, Escape closes; typing a known value exactly closes the list,
 * so typing a code still works as before. The value stays plain text: anything can be typed, the list only helps.
 * An ARIA combobox with a listbox (WAI-ARIA 1.2), mirrored right to left.
 */
export function ComboField({ value, onChange, options, pinned, allLabel, transform, separator, onQueryChange, className, onFocus, onBlur, onKeyDown, onMouseDown, "data-testid": testId, ...props }: ComboFieldProps) {
  const { t, i18n } = useTranslation();
  const rtl = i18n.dir() === "rtl";
  const baseId = useId();
  const listId = `${baseId}-list`;
  const wrapper = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const [typed, setTyped] = useState(false);
  const [active, setActive] = useState(0);
  const [place, setPlace] = useState<{ host: HTMLElement; style: CSSProperties } | null>(null);

  const tokens = separator ? value.split(separator) : [value];
  const query = (tokens[tokens.length - 1] ?? "").trim();
  const needle = query.toLocaleLowerCase();
  const byValue = useMemo(() => new Map(options.map((o) => [o.value, o])), [options]);

  // The entries to show: the pinned ones first, then the rest; narrowed only once something has been typed.
  const groups = useMemo(() => {
    const matches = (o: ComboOption): boolean =>
      !typed || !needle || o.value.toLocaleLowerCase().includes(needle) || (o.label ?? "").toLocaleLowerCase().includes(needle) || (o.detail ?? "").toLocaleLowerCase().includes(needle);
    const rank = (o: ComboOption): number => (typed && needle && o.value.toLocaleLowerCase().startsWith(needle) ? 0 : 1);
    const pinnedSet = new Set(pinned?.values ?? []);
    const top = (pinned?.values ?? []).flatMap((v) => { const o = byValue.get(v); return o && matches(o) ? [o] : []; });
    const rest = options.filter((o) => !pinnedSet.has(o.value) && matches(o)).sort((a, b) => rank(a) - rank(b));
    const result: { key: string; label: string | null; options: ComboOption[] }[] = [];
    if (top.length > 0) {
      result.push({ key: "pinned", label: pinned?.label ?? null, options: top });
    }
    if (rest.length > 0) {
      result.push({ key: "all", label: top.length > 0 ? (allLabel ?? null) : null, options: rest.slice(0, MAX_SHOWN) });
    }
    return { result, more: Math.max(0, rest.length - MAX_SHOWN) };
  }, [options, pinned, allLabel, byValue, typed, needle]);
  const flat = useMemo(() => groups.result.flatMap((g) => g.options), [groups]);
  // Shown while there is something to offer; a value typed in full is a choice made, so the list gets out of the way
  // (also when the options arrive after the typing, from a search).
  const visible = open && flat.length > 0 && !(typed && query !== "" && byValue.has(query));

  const input = useCallback(() => wrapper.current?.querySelector("input") ?? null, []);

  // Positioned against the field, below it, or above it when there is not room below.
  const position = useCallback(() => {
    const field = input();
    if (!field) {
      return;
    }
    const host = hostOf(field);
    const rect = field.getBoundingClientRect();
    const inBody = host === document.body;
    const hostRect = inBody ? new DOMRect(-window.scrollX, -window.scrollY, document.documentElement.clientWidth, window.innerHeight) : host.getBoundingClientRect();
    const visibleTop = Math.max(0, inBody ? 0 : hostRect.top);
    const visibleBottom = Math.min(window.innerHeight, inBody ? window.innerHeight : hostRect.bottom);
    const below = visibleBottom - rect.bottom - 8;
    const above = rect.top - visibleTop - 8;
    const up = below < Math.min(PREFERRED_HEIGHT, 200) && above > below;
    const maxHeight = Math.max(120, Math.min(PREFERRED_HEIGHT, up ? above : below));
    const hostWidth = inBody ? document.documentElement.clientWidth : host.clientWidth;
    const width = Math.min(Math.max(rect.width, 288), hostWidth - 16);
    const offsetX = inBody ? window.scrollX : host.scrollLeft - hostRect.left - host.clientLeft;
    const offsetY = inBody ? window.scrollY : host.scrollTop - hostRect.top - host.clientTop;
    const leftEdge = rtl ? rect.right - width : rect.left;
    const left = Math.min(Math.max(leftEdge + offsetX, offsetX + 8), offsetX + hostWidth - width - 8);
    const style: CSSProperties = up
      ? { left, top: rect.top + offsetY - 4, width, maxHeight, transform: "translateY(-100%)" }
      : { left, top: rect.bottom + offsetY + 4, width, maxHeight };
    setPlace({ host, style });
  }, [input, rtl]);

  useLayoutEffect(() => {
    if (visible) {
      position();
    }
  }, [visible, position, flat.length]);
  useEffect(() => {
    if (!visible) {
      return undefined;
    }
    const onMove = (): void => { position(); };
    window.addEventListener("resize", onMove);
    window.addEventListener("scroll", onMove, true);
    return () => {
      window.removeEventListener("resize", onMove);
      window.removeEventListener("scroll", onMove, true);
    };
  }, [visible, position]);
  // Keeps the highlighted entry in view.
  useEffect(() => {
    if (visible) {
      document.getElementById(`${baseId}-opt-${active}`)?.scrollIntoView({ block: "nearest" });
    }
  }, [active, visible, baseId]);

  const show = (): void => {
    setTyped(false);
    setActive(Math.max(0, flat.findIndex((o) => o.value === query)));
    setOpen(true);
  };
  const choose = (option: ComboOption): void => {
    const next = separator ? [...tokens.slice(0, -1), (tokens.length > 1 ? " " : "") + option.value].join(separator) : option.value;
    onChange(next);
    onQueryChange?.("");
    setOpen(false);
    setTyped(false);
  };
  const change = (raw: string): void => {
    const next = transform ? transform(raw) : raw;
    onChange(next);
    const parts = separator ? next.split(separator) : [next];
    const last = (parts[parts.length - 1] ?? "").trim();
    onQueryChange?.(last);
    setTyped(true);
    setActive(0);
    setOpen(true);
  };

  const keyDown = (event: KeyboardEvent<HTMLInputElement>): void => {
    onKeyDown?.(event);
    if (event.defaultPrevented) {
      return;
    }
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      if (!visible) {
        show();
        return;
      }
      const step = event.key === "ArrowDown" ? 1 : -1;
      setActive((i) => (flat.length === 0 ? 0 : (i + step + flat.length) % flat.length));
    } else if (event.key === "Enter" && visible && flat[active]) {
      event.preventDefault();
      choose(flat[active]);
    } else if (event.key === "Escape" && visible) {
      // Closes the list only, not the dialog around it.
      event.preventDefault();
      event.stopPropagation();
      setOpen(false);
    } else if (event.key === "Tab") {
      setOpen(false);
    }
  };

  let index = -1;
  const list: ReactNode = visible && place ? (
    <div
      id={listId}
      role="listbox"
      aria-label={typeof props["aria-label"] === "string" ? props["aria-label"] : undefined}
      onMouseDown={(e) => { e.preventDefault(); }}
      className="absolute z-[60] overflow-y-auto rounded-md border border-border bg-surface py-1 text-sm shadow-lg"
      style={place.style}
      data-testid={testId ? `${testId}-options` : undefined}
    >
      {groups.result.map((group) => (
        <div key={group.key} role="group" aria-labelledby={group.label ? `${baseId}-${group.key}` : undefined}>
          {group.label ? (
            <div id={`${baseId}-${group.key}`} role="presentation" className="px-3 pb-0.5 pt-1.5 text-[11px] font-semibold uppercase tracking-wide text-fg-subtle">
              {group.label}
            </div>
          ) : null}
          {group.options.map((option) => {
            index += 1;
            const i = index;
            const selected = option.value === query;
            return (
              <div
                key={option.value}
                id={`${baseId}-opt-${i}`}
                role="option"
                aria-selected={selected}
                onMouseEnter={() => { setActive(i); }}
                onClick={() => { choose(option); }}
                className={cn("flex cursor-pointer items-baseline gap-2 px-3 py-1.5", i === active ? "bg-surface-sunken" : "", selected ? "font-medium text-accent" : "text-fg")}
              >
                <span className={option.label ? "shrink-0 font-mono text-xs" : "min-w-0 flex-1 truncate"} dir={option.label ? "ltr" : "auto"}>{option.value}</span>
                {option.label ? <span className="min-w-0 flex-1 truncate">{option.label}</span> : null}
                {option.detail ? <span className="shrink-0 text-xs text-fg-subtle">{option.detail}</span> : null}
              </div>
            );
          })}
        </div>
      ))}
      {groups.more > 0 ? <div className="px-3 py-1.5 text-xs text-fg-subtle">{t("common.typeToNarrow", { count: groups.more })}</div> : null}
    </div>
  ) : null;

  return (
    <div ref={wrapper} className={cn("relative", className)}>
      <TextField
        {...props}
        value={value}
        role="combobox"
        data-testid={testId}
        aria-expanded={visible}
        aria-controls={visible ? listId : undefined}
        aria-autocomplete="list"
        aria-activedescendant={visible && flat[active] ? `${baseId}-opt-${active}` : undefined}
        autoComplete="off"
        onChange={(e) => { change(e.target.value); }}
        onFocus={(e) => { onFocus?.(e); show(); }}
        onMouseDown={(e) => { onMouseDown?.(e); if (!visible) { show(); } }}
        onBlur={(e) => { onBlur?.(e); setOpen(false); }}
        onKeyDown={keyDown}
      />
      {list && place ? createPortal(list, place.host) : null}
    </div>
  );
}
