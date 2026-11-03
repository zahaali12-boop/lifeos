import { cn } from "@quicker/ui";
import { Link } from "@tanstack/react-router";
import { ChevronDown, LayoutGrid, Search } from "lucide-react";
import { useCallback, useEffect, useId, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { navigationGroups, navigationSections, type NavigationGroup, type NavigationItem, type NavigationSection } from "./navigation";

/** The entry a path belongs to: the longest entry path it starts with (so a customer's own page belongs to Customers). */
export function activeItem(items: NavigationItem[], location: string): NavigationItem | undefined {
  return items
    .filter((item) => (item.to === "/" ? location === "/" : location === item.to || location.startsWith(`${item.to}/`)))
    .sort((a, b) => b.to.length - a.to.length)[0];
}

/** A module's entries in its sections, in menu order. */
function sectionsOf(items: NavigationItem[], group: NavigationGroup): { section: NavigationSection | null; items: NavigationItem[] }[] {
  const inGroup = items.filter((item) => item.group === group);
  const result: { section: NavigationSection | null; items: NavigationItem[] }[] = navigationSections
    .map((section) => ({ section, items: inGroup.filter((item) => item.section === section) }))
    .filter((s) => s.items.length > 0);
  const loose = inGroup.filter((item) => !item.section);
  return loose.length > 0 ? [...result, { section: null, items: loose }] : result;
}

/**
 * The trader edition's menu (ADR-0035 §2, §23): the screens themselves as links on their own row under the top bar, the
 * current one marked. When the window is too narrow for one line the links wrap onto the next, so every screen is in
 * sight and nothing scrolls sideways.
 */
export function FlatNav({ items, location, onNavigate }: { items: NavigationItem[]; location: string; onNavigate?: () => void }) {
  const { t } = useTranslation();
  const current = activeItem(items, location);
  return (
    <nav aria-label={t("shell.primaryNavigation")} data-main-nav className="flex flex-wrap items-center gap-1" data-testid="flat-nav">
      {items.map((item) => {
        const active = current?.to === item.to;
        return (
          <Link
            key={item.to}
            to={item.to}
            onClick={onNavigate}
            aria-current={active ? "page" : undefined}
            className={cn(
              "inline-flex h-9 shrink-0 items-center gap-1.5 whitespace-nowrap rounded-md px-2.5 text-sm transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-accent",
              active ? "bg-accent-soft font-medium text-accent" : "text-fg-muted hover:bg-surface-sunken hover:text-fg",
            )}
          >
            <item.icon className="size-4" aria-hidden="true" />
            {t(item.label)}
          </Link>
        );
      })}
    </nav>
  );
}

const OPEN_DELAY = 120;
const CLOSE_DELAY = 220;

/** Links of a panel in reading order, and the columns they sit in (for left/right). */
function linksIn(panel: HTMLElement | null): HTMLAnchorElement[] {
  return panel ? [...panel.querySelectorAll<HTMLAnchorElement>("a[href]")] : [];
}

/**
 * The top menu (Odoo style): the "all menus" button opens every module with every screen, by section; next to it the
 * current module's sections, each a drop-down of its screens; outside a module (the dashboard, notifications), the
 * modules themselves are the drop-downs. Menus open on hover or click; arrow keys move within a menu (up and down),
 * between its columns and to the neighbouring menus (left and right, mirrored right to left), Escape closes.
 */
export function TopNav({ items, location, onNavigate }: { items: NavigationItem[]; location: string; onNavigate?: () => void }) {
  const { t, i18n } = useTranslation();
  const baseId = useId();
  const rtl = i18n.dir() === "rtl";
  const current = activeItem(items, location);
  const module = current?.group;
  const [open, setOpen] = useState<string | null>(null);
  const openedBy = useRef<"hover" | "click" | "key" | null>(null);
  const timer = useRef<number | undefined>(undefined);
  const triggers = useRef(new Map<string, HTMLButtonElement>());
  const panels = useRef(new Map<string, HTMLDivElement>());
  const [filter, setFilter] = useState("");

  const menus = useMemo(() => {
    const result: { id: string; label: string; content: "apps" | "module" | "section"; group?: NavigationGroup; section?: NavigationSection | null; items: NavigationItem[] }[] = [{ id: "apps", label: t("shell.apps"), content: "apps", items }];
    if (module) {
      for (const s of sectionsOf(items, module)) {
        result.push({ id: `section-${s.section ?? "more"}`, label: s.section ? t(`nav.sections.${s.section}`) : t(`nav.modules.${module}`), content: "section", group: module, section: s.section, items: s.items });
      }
    } else {
      for (const group of navigationGroups) {
        const inGroup = items.filter((item) => item.group === group);
        if (inGroup.length > 0) {
          result.push({ id: `module-${group}`, label: t(`nav.modules.${group}`), content: "module", group, items: inGroup });
        }
      }
    }
    return result;
  }, [items, module, t]);
  const order = menus.map((m) => m.id);

  const clear = (): void => { window.clearTimeout(timer.current); };
  const show = useCallback((id: string, by: "hover" | "click" | "key") => {
    window.clearTimeout(timer.current);
    openedBy.current = by;
    setOpen(id);
  }, []);
  const close = useCallback(() => {
    window.clearTimeout(timer.current);
    openedBy.current = null;
    setOpen(null);
    setFilter("");
  }, []);
  // A change of page closes whatever was open.
  useEffect(() => { close(); }, [location, close]);
  useEffect(() => () => { window.clearTimeout(timer.current); }, []);
  // A click anywhere else closes the menu.
  useEffect(() => {
    if (!open) {
      return undefined;
    }
    const onPointer = (event: PointerEvent): void => {
      const target = event.target as Node;
      const inside = [...triggers.current.values(), ...panels.current.values()].some((el) => el.contains(target));
      if (!inside) {
        close();
      }
    };
    document.addEventListener("pointerdown", onPointer);
    return () => { document.removeEventListener("pointerdown", onPointer); };
  }, [open, close]);

  const hoverIn = (id: string): void => {
    clear();
    if (open === id) {
      return;
    }
    // Moving across the bar while a menu is open switches at once, as menu bars do.
    timer.current = window.setTimeout(() => { show(id, "hover"); }, open ? 0 : OPEN_DELAY);
  };
  const hoverOut = (): void => {
    clear();
    if (openedBy.current === "hover") {
      timer.current = window.setTimeout(close, CLOSE_DELAY);
    }
  };
  const click = (id: string): void => {
    if (open === id && openedBy.current !== "hover") {
      close();
      return;
    }
    show(id, "click");
  };

  const focusLink = (id: string, which: "first" | "last"): void => {
    window.requestAnimationFrame(() => {
      const panel = panels.current.get(id);
      const filterBox = panel?.querySelector<HTMLInputElement>("input[type=search]");
      if (filterBox && which === "first") {
        filterBox.focus();
        return;
      }
      const links = linksIn(panel ?? null);
      (which === "first" ? links[0] : links[links.length - 1])?.focus();
    });
  };
  const neighbour = (id: string, step: 1 | -1): string => {
    const index = order.indexOf(id);
    return order[(index + step + order.length) % order.length] ?? id;
  };
  const toNeighbour = (id: string, step: 1 | -1, openIt: boolean): void => {
    const next = neighbour(id, step);
    triggers.current.get(next)?.focus();
    if (openIt) {
      show(next, "key");
      focusLink(next, "first");
    }
  };
  // Right is "next" left to right and "previous" right to left.
  const stepOf = (key: string): 1 | -1 | 0 => (key === "ArrowRight" ? (rtl ? -1 : 1) : key === "ArrowLeft" ? (rtl ? 1 : -1) : 0);

  const onTriggerKey = (event: KeyboardEvent<HTMLButtonElement>, id: string): void => {
    const step = stepOf(event.key);
    if (step !== 0) {
      event.preventDefault();
      toNeighbour(id, step, open !== null);
    } else if (event.key === "ArrowDown" || event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      show(id, "key");
      focusLink(id, "first");
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      show(id, "key");
      focusLink(id, "last");
    } else if (event.key === "Escape") {
      close();
    }
  };

  const onPanelKey = (event: KeyboardEvent<HTMLDivElement>, id: string): void => {
    const panel = panels.current.get(id);
    const links = linksIn(panel ?? null);
    const active = document.activeElement as HTMLElement | null;
    const index = links.indexOf(active as HTMLAnchorElement);
    const inFilter = active?.tagName === "INPUT";
    if (event.key === "Escape") {
      event.preventDefault();
      close();
      triggers.current.get(id)?.focus();
      return;
    }
    if (event.key === "ArrowDown") {
      event.preventDefault();
      (inFilter ? links[0] : links[Math.min(index + 1, links.length - 1)])?.focus();
      return;
    }
    if (event.key === "ArrowUp") {
      event.preventDefault();
      if (index <= 0) {
        const filterBox = panel?.querySelector<HTMLInputElement>("input[type=search]");
        (filterBox ?? triggers.current.get(id))?.focus();
      } else {
        links[index - 1]?.focus();
      }
      return;
    }
    if (event.key === "Home" && !inFilter) {
      event.preventDefault();
      links[0]?.focus();
      return;
    }
    if (event.key === "End" && !inFilter) {
      event.preventDefault();
      links[links.length - 1]?.focus();
      return;
    }
    const step = inFilter ? 0 : stepOf(event.key);
    if (step !== 0) {
      event.preventDefault();
      // Within a menu of several columns, left and right move between the columns first.
      const columns = panel ? [...panel.querySelectorAll<HTMLElement>("[data-menu-column]")] : [];
      const column = columns.findIndex((c) => active ? c.contains(active) : false);
      const target = column >= 0 ? columns[column + step] : undefined;
      const first = target ? linksIn(target)[0] : undefined;
      if (first) {
        first.focus();
      } else {
        toNeighbour(id, step, true);
      }
    }
  };

  const itemLink = (item: NavigationItem, testId?: string) => {
    const isCurrent = current?.to === item.to;
    return (
      <li key={item.to}>
        <Link
          to={item.to}
          onClick={() => { close(); onNavigate?.(); }}
          aria-current={isCurrent ? "page" : undefined}
          data-testid={testId}
          className={cn(
            "flex h-8 items-center gap-2.5 rounded-md px-2.5 text-sm outline-none transition-colors focus-visible:ring-2 focus-visible:ring-accent",
            isCurrent ? "bg-accent-soft font-medium text-accent" : "text-fg hover:bg-surface-sunken focus:bg-surface-sunken",
          )}
        >
          <item.icon className="size-4 shrink-0 text-fg-muted" aria-hidden="true" />
          <span className="flex-1 truncate">{t(item.label)}</span>
          <kbd className="hidden font-mono text-[10px] text-fg-subtle xl:inline" dir="ltr" aria-hidden="true">{item.shortcut}</kbd>
        </Link>
      </li>
    );
  };

  const sectionBlock = (heading: ReactNode, entries: NavigationItem[], key: string) => (
    <div key={key} className="min-w-0">
      {heading ? <p className="mb-1 px-2.5 text-[11px] font-semibold uppercase tracking-wide text-fg-subtle">{heading}</p> : null}
      <ul className="flex flex-col gap-0.5">{entries.map((item) => itemLink(item))}</ul>
    </div>
  );

  const needle = filter.trim().toLocaleLowerCase();
  const panelContent = (menu: (typeof menus)[number]): ReactNode => {
    if (menu.content === "section") {
      return <ul className="flex min-w-60 flex-col gap-0.5" data-menu-column>{menu.items.map((item) => itemLink(item))}</ul>;
    }
    if (menu.content === "module" && menu.group) {
      const sections = sectionsOf(menu.items, menu.group);
      return (
        <div className={cn("grid gap-x-4 gap-y-3", sections.length > 2 ? "w-[40rem] grid-cols-3" : sections.length > 1 ? "w-[28rem] grid-cols-2" : "w-64 grid-cols-1")}>
          {sections.map((s) => (
            <div key={s.section ?? "more"} data-menu-column>
              {sectionBlock(s.section ? t(`nav.sections.${s.section}`) : null, s.items, s.section ?? "more")}
            </div>
          ))}
        </div>
      );
    }
    // Every module with every screen, narrowed by the filter (Enter opens the first match).
    const matches = (item: NavigationItem): boolean => !needle || t(item.label).toLocaleLowerCase().includes(needle);
    const everyday = menu.items.filter((item) => !item.group && matches(item));
    const matching = menu.items.filter((item) => item.group && matches(item));
    const groups = navigationGroups.filter((group) => matching.some((item) => item.group === group));
    return (
      <div className="flex w-[min(72rem,calc(100vw-2rem))] flex-col gap-3">
        <div className="relative">
          <Search className="pointer-events-none absolute start-2.5 top-1/2 size-3.5 -translate-y-1/2 text-fg-subtle" aria-hidden="true" />
          <input
            type="search"
            value={filter}
            onChange={(e) => { setFilter(e.target.value); }}
            onKeyDown={(e) => {
              if (e.key === "Enter") {
                e.preventDefault();
                linksIn(panels.current.get("apps") ?? null)[0]?.click();
              }
            }}
            aria-label={t("shell.filterMenu")}
            placeholder={t("shell.filterMenu")}
            className="h-9 w-full rounded-md border border-border bg-canvas ps-8 pe-2 text-sm placeholder:text-fg-subtle focus:outline-none focus-visible:ring-2 focus-visible:ring-accent"
            data-testid="nav-filter"
          />
        </div>
        {everyday.length > 0 ? (
          <ul className="flex flex-wrap gap-1 border-b border-border pb-2" data-menu-column data-testid="apps-everyday">
            {everyday.map((item) => itemLink(item))}
          </ul>
        ) : null}
        {groups.length === 0 && everyday.length === 0 ? <p className="px-2.5 py-2 text-sm text-fg-muted">{t("shell.noMenuMatch")}</p> : null}
        <div className="grid max-h-[70vh] gap-x-6 gap-y-5 overflow-y-auto sm:grid-cols-2 lg:grid-cols-4">
          {groups.map((group) => (
            <section key={group} data-menu-column data-testid={`apps-module-${group}`} aria-labelledby={`${baseId}-${group}`}>
              <h2 id={`${baseId}-${group}`} className={cn("mb-1.5 border-b border-border px-2.5 pb-1 text-sm font-semibold", group === module ? "text-accent" : "text-fg")}>
                {t(`nav.modules.${group}`)}
              </h2>
              <div className="flex flex-col gap-2">
                {sectionsOf(matching, group).map((s) => sectionBlock(s.section && !needle ? t(`nav.sections.${s.section}`) : null, s.items, s.section ?? "more"))}
              </div>
            </section>
          ))}
        </div>
      </div>
    );
  };

  return (
    <nav aria-label={t("shell.primaryNavigation")} data-main-nav className="flex min-w-0 items-center gap-0.5">
      {menus.map((menu, index) => {
        const isOpen = open === menu.id;
        const panelId = `${baseId}-${menu.id}`;
        const isApps = menu.content === "apps";
        const isCurrentModule = menu.content === "module" ? false : menu.items.some((item) => item.to === current?.to);
        return (
          <div key={menu.id} className="relative" onMouseEnter={() => { hoverIn(menu.id); }} onMouseLeave={hoverOut}>
            {index === 1 && module ? (
              <span className="me-1 ms-1 hidden select-none px-1 text-sm font-semibold text-fg xl:inline" data-testid="current-module">{t(`nav.modules.${module}`)}</span>
            ) : null}
            <button
              type="button"
              ref={(el) => { if (el) { triggers.current.set(menu.id, el); } else { triggers.current.delete(menu.id); } }}
              aria-expanded={isOpen}
              aria-controls={panelId}
              aria-label={isApps ? t("shell.apps") : undefined}
              onClick={() => { click(menu.id); }}
              onKeyDown={(e) => { onTriggerKey(e, menu.id); }}
              data-testid={isApps ? "apps-menu" : `menu-${menu.id}`}
              className={cn(
                "inline-flex h-9 items-center gap-1 rounded-md px-2.5 text-sm transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-accent",
                isOpen ? "bg-surface-sunken text-fg" : isCurrentModule ? "font-medium text-accent hover:bg-surface-sunken" : "text-fg-muted hover:bg-surface-sunken hover:text-fg",
              )}
            >
              {isApps ? <LayoutGrid className="size-4" aria-hidden="true" /> : <span className="whitespace-nowrap">{menu.label}</span>}
              {isApps ? null : <ChevronDown className={cn("size-3.5 transition-transform", isOpen ? "rotate-180" : "")} aria-hidden="true" />}
            </button>
            <div
              id={panelId}
              ref={(el) => { if (el) { panels.current.set(menu.id, el); } else { panels.current.delete(menu.id); } }}
              hidden={!isOpen}
              onKeyDown={(e) => { onPanelKey(e, menu.id); }}
              data-testid={isApps ? "apps-panel" : `menu-panel-${menu.id}`}
              className={cn(
                "absolute top-full z-40 mt-1 rounded-lg border border-border bg-surface p-3 shadow-lg",
                // Menus on the far half of the bar open towards the bar's start, so they stay on screen.
                !isApps && index > menus.length / 2 ? "end-0" : "start-0",
              )}
            >
              {isOpen ? panelContent(menu) : null}
            </div>
          </div>
        );
      })}
    </nav>
  );
}
