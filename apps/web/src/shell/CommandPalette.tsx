import { Dialog, DialogContent, DialogTitle } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { Command } from "cmdk";
import { Building2, Megaphone, Plus, Search } from "lucide-react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { localized } from "../lib/format";
import { navigation } from "./navigation";
import { describeKeys } from "./useShortcuts";

export interface CommandPaletteProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  permissions: ReadonlySet<string>;
}

const RECENT_KEY = "quicker.recent";

export interface RecentRecord {
  to: string;
  label: string;
  at: number;
}

export function rememberRecent(record: Omit<RecentRecord, "at">): void {
  try {
    const list = (JSON.parse(localStorage.getItem(RECENT_KEY) ?? "[]") as RecentRecord[]).filter((r) => r.to !== record.to);
    list.unshift({ ...record, at: Date.now() });
    localStorage.setItem(RECENT_KEY, JSON.stringify(list.slice(0, 8)));
  } catch {
    // ignore
  }
}

function readRecent(): RecentRecord[] {
  try {
    return JSON.parse(localStorage.getItem(RECENT_KEY) ?? "[]") as RecentRecord[];
  } catch {
    return [];
  }
}

function allowed(permissions: ReadonlySet<string>, permission?: string): boolean {
  return !permission || permissions.has("*") || permissions.has(permission);
}

/** Ctrl/⌘+K: navigation, actions, recent records and a global search over companies (more entities join as they land). */
export function CommandPalette({ open, onOpenChange, permissions }: CommandPaletteProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [query, setQuery] = useState("");
  useEffect(() => {
    if (!open) {
      setQuery("");
    }
  }, [open]);

  const search = useQuery({
    queryKey: ["palette", "companies", query],
    enabled: open && query.trim().length >= 2 && allowed(permissions, "organization.company.read"),
    queryFn: async () => {
      const escaped = query.trim().replace(/'/g, "''");
      return unwrap(await api.GET("/api/v1/organization/companies", { params: { query: { filter: `code like '${escaped}'` } } }));
    },
  });

  const go = (to: string): void => {
    onOpenChange(false);
    void navigate({ to });
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent closeLabel={t("common.close")} className="top-24 translate-y-0 p-0 sm:max-w-xl">
        <DialogTitle className="sr-only">{t("palette.title")}</DialogTitle>
        <Command label={t("palette.title")} shouldFilter={true} className="flex flex-col">
          <div className="flex items-center gap-2 border-b border-border px-3">
            <Search className="size-4 text-fg-subtle" aria-hidden="true" />
            <Command.Input value={query} onValueChange={setQuery} placeholder={t("palette.placeholder")} className="h-11 w-full bg-transparent text-sm outline-none placeholder:text-fg-subtle" />
          </div>
          <Command.List className="max-h-80 overflow-y-auto p-2">
            <Command.Empty className="px-2 py-6 text-center text-sm text-fg-muted">{t("palette.empty")}</Command.Empty>
            {search.data && search.data.length > 0 ? (
              <Command.Group heading={t("palette.groups.companies")} className="[&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-xs [&_[cmdk-group-heading]]:font-semibold [&_[cmdk-group-heading]]:text-fg-muted">
                {search.data.map((company) => (
                  <Command.Item key={company.id} value={`company ${company.code} ${localized(company.legalName)}`} onSelect={() => { go(`/companies?open=${company.id}`); }} className="flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 text-sm data-[selected=true]:bg-surface-sunken">
                    <Building2 className="size-4 text-fg-subtle" aria-hidden="true" />
                    <span className="font-medium">{company.code}</span>
                    <span className="text-fg-muted">{localized(company.legalName)}</span>
                  </Command.Item>
                ))}
              </Command.Group>
            ) : null}
            <Command.Group heading={t("palette.groups.navigation")} className="[&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-xs [&_[cmdk-group-heading]]:font-semibold [&_[cmdk-group-heading]]:text-fg-muted">
              {navigation.filter((item) => allowed(permissions, item.permission)).map((item) => (
                <Command.Item key={item.to} value={`${t(item.label)} ${item.to}`} onSelect={() => { go(item.to); }} className="flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 text-sm data-[selected=true]:bg-surface-sunken">
                  <item.icon className="size-4 text-fg-subtle" aria-hidden="true" />
                  <span>{t(item.label)}</span>
                  <kbd className="ms-auto font-mono text-xs text-fg-subtle" dir="ltr">
                    {describeKeys(item.shortcut, t("shortcuts.then"))}
                  </kbd>
                </Command.Item>
              ))}
            </Command.Group>
            <Command.Group heading={t("palette.groups.actions")} className="[&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-xs [&_[cmdk-group-heading]]:font-semibold [&_[cmdk-group-heading]]:text-fg-muted">
              {allowed(permissions, "organization.company.manage") ? (
                <Command.Item value={t("companies.new")} onSelect={() => { go("/companies?new=1"); }} className="flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 text-sm data-[selected=true]:bg-surface-sunken">
                  <Plus className="size-4 text-fg-subtle" aria-hidden="true" />
                  {t("companies.new")}
                </Command.Item>
              ) : null}
              {allowed(permissions, "collaboration.notification.announce") ? (
                <Command.Item value={t("notifications.announce")} onSelect={() => { go("/notifications?announce=1"); }} className="flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 text-sm data-[selected=true]:bg-surface-sunken">
                  <Megaphone className="size-4 text-fg-subtle" aria-hidden="true" />
                  {t("notifications.announce")}
                </Command.Item>
              ) : null}
            </Command.Group>
            {readRecent().length > 0 ? (
              <Command.Group heading={t("palette.groups.recent")} className="[&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-xs [&_[cmdk-group-heading]]:font-semibold [&_[cmdk-group-heading]]:text-fg-muted">
                {readRecent().map((record) => (
                  <Command.Item key={record.to} value={`recent ${record.label}`} onSelect={() => { go(record.to); }} className="flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 text-sm data-[selected=true]:bg-surface-sunken">
                    {record.label}
                  </Command.Item>
                ))}
              </Command.Group>
            ) : null}
          </Command.List>
        </Command>
      </DialogContent>
    </Dialog>
  );
}
