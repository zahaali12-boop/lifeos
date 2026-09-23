import { Badge, Button } from "@quicker/ui";
import { ChevronDown, X } from "lucide-react";
import { useId, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import type { components } from "../api/schema";
import { formatNumber } from "../lib/format";
import { TextField } from "./common";

type Permission = components["schemas"]["PermissionDefinition"];
type SodRule = components["schemas"]["SodRuleSummary"];

/** Whether a grant covers a permission key: the key itself, an area or module wildcard (`inventory.count.*`), or `*`. */
export function covers(grant: string, key: string): boolean {
  return grant === "*" || grant === key || (grant.endsWith(".*") && key.startsWith(grant.slice(0, -1)));
}

/** The active segregation-of-duties rules a set of grants would trip, as the server checks them (`*` is reported apart). */
export function sodConflicts(grants: string[], rules: SodRule[]): SodRule[] {
  const scoped = grants.filter((g) => g !== "*");
  return rules.filter((r) => r.isActive && scoped.some((g) => covers(g, r.permissionA)) && scoped.some((g) => covers(g, r.permissionB)));
}

interface Area {
  prefix: string;
  permissions: Permission[];
}

interface Module {
  prefix: string;
  areas: Area[];
  count: number;
}

function group(catalogue: Permission[]): Module[] {
  const modules = new Map<string, Map<string, Permission[]>>();
  for (const permission of [...catalogue].sort((a, b) => a.key.localeCompare(b.key))) {
    const [module = permission.key, area = ""] = permission.key.split(".");
    const areas = modules.get(module) ?? new Map<string, Permission[]>();
    areas.set(area, [...(areas.get(area) ?? []), permission]);
    modules.set(module, areas);
  }
  return [...modules].map(([module, areas]) => ({
    prefix: module,
    areas: [...areas].map(([area, permissions]) => ({ prefix: `${module}.${area}`, permissions })),
    count: [...areas.values()].reduce((sum, list) => sum + list.length, 0),
  }));
}

/**
 * The permission catalogue as a checklist by module and area, with "the whole module" and "the whole area" grants
 * (`inventory.*`, `inventory.count.*`) that cover what is added to them later. A key covered by a wider grant shows
 * checked and locked; grants the catalogue does not list (areas reserved for modules still to come) are kept and
 * listed apart.
 */
export function PermissionPicker({ catalogue, grants, onChange, readOnly = false }: { catalogue: Permission[]; grants: string[]; onChange: (grants: string[]) => void; readOnly?: boolean }) {
  const { t } = useTranslation();
  const searchId = useId();
  const [search, setSearch] = useState("");
  const modules = useMemo(() => group(catalogue), [catalogue]);
  const known = useMemo(() => new Set(catalogue.map((p) => p.key)), [catalogue]);
  const reserved = grants.filter((g) => g !== "*" && !known.has(g) && !catalogue.some((p) => covers(g, p.key)));
  const needle = search.trim().toLowerCase();
  const matches = (p: Permission): boolean => !needle || p.key.includes(needle) || p.description.toLowerCase().includes(needle);
  const coveredBy = (key: string, except?: string): string | undefined => grants.find((g) => g !== key && g !== except && covers(g, key));

  const setWildcard = (prefix: string, on: boolean): void => {
    const wildcard = `${prefix}.*`;
    onChange(on ? [...grants.filter((g) => !g.startsWith(`${prefix}.`)), wildcard] : grants.filter((g) => g !== wildcard));
  };
  const setKey = (key: string, on: boolean): void => {
    onChange(on ? [...grants, key] : grants.filter((g) => g !== key));
  };

  return (
    <div className="flex flex-col gap-3" data-testid="permission-picker">
      <div className="flex flex-wrap items-end gap-3">
        <div className="flex min-w-60 flex-1 flex-col gap-1">
          <label htmlFor={searchId} className="text-sm font-medium">
            {t("roles.searchPermissions")}
          </label>
          <TextField id={searchId} type="search" value={search} onChange={(e) => { setSearch(e.target.value); }} dir="ltr" data-testid="permission-search" />
        </div>
        <label className="flex items-center gap-2 pb-2 text-sm">
          <input type="checkbox" checked={grants.includes("*")} disabled={readOnly} onChange={(e) => { onChange(e.target.checked ? ["*"] : grants.filter((g) => g !== "*")); }} data-testid="grant-all" />
          <span className="font-medium">{t("roles.fullAccess")}</span>
        </label>
      </div>
      {grants.includes("*") ? <p className="rounded-md border border-warning/40 bg-warning-soft p-2 text-sm">{t("roles.fullAccessHint")}</p> : null}

      <div className="flex flex-col gap-2" hidden={grants.includes("*")}>
        {modules.map((module) => {
          const visible = module.areas.map((area) => ({ ...area, permissions: area.permissions.filter(matches) })).filter((area) => area.permissions.length > 0);
          if (visible.length === 0) {
            return null;
          }
          const moduleWildcard = `${module.prefix}.*`;
          const moduleLocked = readOnly || coveredBy(moduleWildcard) !== undefined;
          const granted = module.areas.flatMap((a) => a.permissions).filter((p) => grants.some((g) => covers(g, p.key))).length;
          return (
            <details key={module.prefix} open={Boolean(needle) || undefined} className="group rounded-md border border-border" data-testid={`permission-module-${module.prefix}`}>
              <summary className="flex cursor-pointer list-none items-center gap-3 px-3 py-2 text-sm">
                <ChevronDown className="size-4 transition-transform group-open:rotate-180" aria-hidden="true" />
                <span className="font-medium">{t(`roles.modules.${module.prefix}`, { defaultValue: module.prefix })}</span>
                <span className="text-fg-muted">{t("roles.grantedOf", { granted: formatNumber(granted), total: formatNumber(module.count) })}</span>
              </summary>
              <div className="flex flex-col gap-3 border-t border-border p-3">
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={grants.some((g) => covers(g, moduleWildcard))} disabled={moduleLocked} onChange={(e) => { setWildcard(module.prefix, e.target.checked); }} data-testid={`grant-${moduleWildcard}`} />
                  <span>{t("roles.wholeModule")}</span>
                  <code dir="ltr" className="font-mono text-xs text-fg-muted">{moduleWildcard}</code>
                </label>
                {visible.map((area) => {
                  const areaWildcard = `${area.prefix}.*`;
                  const areaCovered = grants.some((g) => covers(g, areaWildcard));
                  const areaLocked = readOnly || coveredBy(areaWildcard, areaWildcard) !== undefined;
                  return (
                    <fieldset key={area.prefix} className="flex flex-col gap-1.5 border-t border-border pt-2 first-of-type:border-t-0">
                      <legend className="sr-only">{area.prefix}</legend>
                      <label className="flex items-center gap-2 text-sm">
                        <input type="checkbox" checked={areaCovered} disabled={areaLocked} onChange={(e) => { setWildcard(area.prefix, e.target.checked); }} data-testid={`grant-${areaWildcard}`} />
                        <code dir="ltr" className="font-mono text-xs font-semibold">{areaWildcard}</code>
                        <span className="text-fg-muted">{t("roles.wholeArea")}</span>
                      </label>
                      <ul className="grid gap-1 ps-6 sm:grid-cols-2">
                        {area.permissions.map((p) => {
                          const wider = coveredBy(p.key, p.key);
                          return (
                            <li key={p.key}>
                              <label className="flex items-start gap-2 text-sm">
                                <input type="checkbox" className="mt-1" checked={grants.includes(p.key) || wider !== undefined} disabled={readOnly || wider !== undefined} onChange={(e) => { setKey(p.key, e.target.checked); }} data-testid={`grant-${p.key}`} />
                                <span className="flex flex-col">
                                  <span className="flex flex-wrap items-center gap-1">
                                    <code dir="ltr" className="font-mono text-xs">{p.key}</code>
                                    {p.isSensitive ? <Badge tone="warning">{t("roles.sensitive")}</Badge> : null}
                                  </span>
                                  <span className="text-xs text-fg-muted" lang="en" dir="ltr">
                                    {p.description}
                                  </span>
                                  {wider ? <span className="text-xs text-fg-muted">{t("roles.coveredBy", { grant: wider })}</span> : null}
                                </span>
                              </label>
                            </li>
                          );
                        })}
                      </ul>
                    </fieldset>
                  );
                })}
              </div>
            </details>
          );
        })}
      </div>

      {reserved.length > 0 ? (
        <section className="flex flex-col gap-1.5 rounded-md border border-border p-3" data-testid="reserved-grants">
          <h3 className="text-sm font-medium">{t("roles.reserved")}</h3>
          <p className="text-xs text-fg-muted">{t("roles.reservedHint")}</p>
          <ul className="flex flex-wrap gap-1.5">
            {reserved.map((g) => (
              <li key={g}>
                <Badge tone="neutral" dir="ltr">
                  {g}
                  {readOnly ? null : (
                    <Button variant="ghost" size="icon" className="ms-1 size-4" aria-label={t("roles.removeGrant", { grant: g })} onClick={() => { onChange(grants.filter((x) => x !== g)); }}>
                      <X aria-hidden="true" />
                    </Button>
                  )}
                </Badge>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </div>
  );
}
