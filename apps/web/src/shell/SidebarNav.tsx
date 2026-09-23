import { cn } from "@quicker/ui";
import { Link } from "@tanstack/react-router";
import { ChevronDown, Search } from "lucide-react";
import { useEffect, useId, useState } from "react";
import { useTranslation } from "react-i18next";
import { navigationGroups, type NavigationGroup, type NavigationItem } from "./navigation";

const STORAGE_KEY = "quicker.nav.collapsed";

function readCollapsed(): NavigationGroup[] {
  try {
    const stored = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? "[]") as unknown;
    return Array.isArray(stored) ? navigationGroups.filter((g) => stored.includes(g)) : [];
  } catch {
    return [];
  }
}

function isActive(item: NavigationItem, location: string): boolean {
  return item.to === "/" ? location === "/" : location === item.to || location.startsWith(`${item.to}/`);
}

/**
 * The sidebar: the everyday entries on top, then one section per module. A section can be collapsed (remembered in
 * this browser) and opens again by itself when the current page is in it (reached from the palette or a shortcut);
 * the filter narrows every section at once to the entries whose label matches, and Enter opens the first.
 */
export function SidebarNav({ items, location, onNavigate }: { items: NavigationItem[]; location: string; onNavigate: () => void }) {
  const { t } = useTranslation();
  const filterId = useId();
  const [collapsed, setCollapsed] = useState<NavigationGroup[]>(readCollapsed);
  const [filter, setFilter] = useState("");
  const needle = filter.trim().toLocaleLowerCase();
  const visible = needle ? items.filter((item) => t(item.label).toLocaleLowerCase().includes(needle)) : items;
  const activeGroup = items.find((item) => isActive(item, location))?.group;

  // A change of page reopens its section if it was collapsed; collapsing the current section by hand stays allowed.
  const [seenGroup, setSeenGroup] = useState(activeGroup);
  if (seenGroup !== activeGroup) {
    setSeenGroup(activeGroup);
    if (activeGroup && collapsed.includes(activeGroup)) {
      setCollapsed(collapsed.filter((g) => g !== activeGroup));
    }
  }
  useEffect(() => {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(collapsed));
    } catch {
      // ignore
    }
  }, [collapsed]);
  const toggle = (group: NavigationGroup): void => { setCollapsed(collapsed.includes(group) ? collapsed.filter((g) => g !== group) : [...collapsed, group]); };

  const link = (item: NavigationItem) => {
    const active = isActive(item, location);
    return (
      <li key={item.to}>
        <Link
          to={item.to}
          onClick={onNavigate}
          aria-current={active ? "page" : undefined}
          className={cn("flex h-8 items-center gap-3 rounded-md px-3 text-sm transition-colors", active ? "bg-accent-soft font-medium text-accent" : "text-fg-muted hover:bg-surface-sunken hover:text-fg")}
        >
          <item.icon className="size-4 shrink-0" aria-hidden="true" />
          <span className="truncate">{t(item.label)}</span>
        </Link>
      </li>
    );
  };

  return (
    <nav className="flex flex-1 flex-col overflow-y-auto p-2">
      <div className="relative mb-2">
        <label htmlFor={filterId} className="sr-only">
          {t("shell.filterMenu")}
        </label>
        <Search className="pointer-events-none absolute start-2.5 top-1/2 size-3.5 -translate-y-1/2 text-fg-subtle" aria-hidden="true" />
        <input
          id={filterId}
          type="search"
          value={filter}
          onChange={(e) => { setFilter(e.target.value); }}
          onKeyDown={(e) => {
            const first = visible[0];
            if (e.key === "Enter" && first) {
              e.currentTarget.closest("nav")?.querySelector<HTMLAnchorElement>(`a[href="${first.to}"]`)?.click();
              setFilter("");
            }
          }}
          placeholder={t("shell.filterMenu")}
          className="h-8 w-full rounded-md border border-border bg-canvas ps-8 pe-2 text-sm placeholder:text-fg-subtle focus:outline-none focus-visible:ring-2 focus-visible:ring-accent"
          data-testid="nav-filter"
        />
      </div>
      <ul className="flex flex-col gap-0.5">{visible.filter((item) => !item.group).map(link)}</ul>
      {navigationGroups.map((group) => {
        const entries = visible.filter((item) => item.group === group);
        if (entries.length === 0) {
          return null;
        }
        const open = Boolean(needle) || !collapsed.includes(group);
        const listId = `${filterId}-${group}`;
        return (
          <section key={group} className="mt-3" data-testid={`nav-group-${group}`}>
            <h2>
              <button
                type="button"
                onClick={() => { toggle(group); }}
                aria-expanded={open}
                aria-controls={listId}
                data-testid={`nav-toggle-${group}`}
                className="flex w-full items-center gap-2 rounded-md px-3 py-1 text-xs font-semibold uppercase tracking-wide text-fg-muted hover:text-fg"
              >
                <span className="flex-1 text-start">{t(`nav.groups.${group}`)}</span>
                <ChevronDown className={cn("size-3.5 transition-transform", open ? "" : "-rotate-90 rtl:rotate-90")} aria-hidden="true" />
              </button>
            </h2>
            <ul id={listId} className="mt-0.5 flex flex-col gap-0.5" hidden={!open}>
              {entries.map(link)}
            </ul>
          </section>
        );
      })}
      {visible.length === 0 ? <p className="px-3 py-2 text-sm text-fg-muted">{t("shell.noMenuMatch")}</p> : null}
    </nav>
  );
}
