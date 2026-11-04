import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import { Trash2 } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, isApiProblem, unwrap } from "../api";
import type { components } from "../api/schema";
import { formatDate, formatDateTime, localized } from "../lib/format";
import { toFormProblem } from "../lib/problem";
import { useCompanies } from "./accounting/shared";
import { SelectField, TextField } from "./common";

type Member = components["schemas"]["MemberSummary"];
type Role = components["schemas"]["RoleSummary"];
type Conflict = components["schemas"]["SodConflict"];

const scopeTypes = ["company", "branch", "warehouse"] as const;

interface Assign {
  roleId: string;
  /** The companies, branches and warehouses ticked, as "type:id". */
  scopes: string[];
  validFrom: string;
  validTo: string;
}

const emptyAssign = (): Assign => ({ roleId: "", scopes: [], validFrom: "", validTo: "" });

function conflictsOf(error: unknown): Conflict[] {
  if (!isApiProblem(error) || !error.why) {
    return [];
  }
  const { conflicts } = error.why as { conflicts?: unknown };
  return Array.isArray(conflicts) ? (conflicts as Conflict[]) : [];
}

/** The names of the companies, branches and warehouses an assignment can be limited to, by id. */
function useScopeOptions() {
  const companies = useCompanies();
  const warehouses = useQuery({ queryKey: ["warehouses", "all"], retry: false, queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses")) });
  const branches = useQueries({
    queries: (companies.data ?? []).map((c) => ({
      queryKey: ["branches", c.id],
      retry: false,
      queryFn: async () => unwrap(await api.GET("/api/v1/organization/companies/{companyId}/branches", { params: { path: { companyId: c.id } } })),
    })),
  });
  return useMemo(() => {
    const codeOf = new Map((companies.data ?? []).map((c) => [c.id, c.code]));
    return {
      company: (companies.data ?? []).map((c) => ({ id: c.id, label: `${c.code} · ${localized(c.legalName)}` })),
      branch: branches.flatMap((b) => b.data ?? []).map((b) => ({ id: b.id, label: `${codeOf.get(b.companyId) ?? ""} / ${b.code} · ${localized(b.name)}` })),
      warehouse: (warehouses.data ?? []).map((w) => ({ id: w.id, label: `${codeOf.get(w.companyId) ?? ""} / ${w.code} · ${localized(w.name)}` })),
    } satisfies Record<(typeof scopeTypes)[number], { id: string; label: string }[]>;
  }, [companies.data, warehouses.data, branches]);
}

/**
 * A member's access: their roles, each optionally limited to companies, branches or warehouses and to a validity
 * window; the segregation-of-duties conflicts they hold; enable and disable. Assigning a role that creates a
 * conflict is refused for a blocking rule and asks for acknowledgement for a warning, as the server decides.
 */
export function MemberDialog({ member, roles, onClose }: { member: Member | null; roles: Role[]; onClose: () => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const options = useScopeOptions();
  const [assign, setAssign] = useState<Assign>(emptyAssign);
  const [problem, setProblem] = useState<{ message: string; conflicts: Conflict[]; warning: boolean } | null>(null);
  const report = useQuery({ queryKey: ["sod-report"], enabled: member !== null, retry: false, queryFn: async () => unwrap(await api.GET("/api/v1/sod/report")) });
  const own = report.data?.find((r) => r.membershipId === member?.membershipId)?.conflicts ?? [];
  const labels = useMemo(() => new Map(Object.values(options).flat().map((o) => [o.id, o.label])), [options]);

  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["members"] });
    await queryClient.invalidateQueries({ queryKey: ["sod-report"] });
  };
  const fail = (error: unknown): void => {
    const problemCode = isApiProblem(error) ? error.code : undefined;
    setProblem({ message: toFormProblem(error, t("common.saveFailed")).message, conflicts: conflictsOf(error), warning: problemCode === "assignment.sod_warning" });
  };
  const add = useMutation({
    mutationFn: async (acknowledgeWarnings: boolean) => {
      if (!member) {
        return;
      }
      unwrap(await api.POST("/api/v1/users/{membershipId}/assignments", {
        params: { path: { membershipId: member.membershipId } },
        body: {
          roleId: assign.roleId,
          scopes: assign.scopes.length > 0 ? assign.scopes.map((key) => { const [scopeType = "", scopeId = ""] = key.split(":"); return { scopeType, scopeId }; }) : null,
          validFrom: assign.validFrom || null,
          validTo: assign.validTo || null,
          acknowledgeWarnings,
        },
      }));
    },
    onSuccess: async () => { setProblem(null); setAssign(emptyAssign()); await refresh(); },
    onError: fail,
  });
  const unassign = useMutation({
    mutationFn: async (assignmentId: string) => { await api.DELETE("/api/v1/assignments/{assignmentId}", { params: { path: { assignmentId } } }).then(unwrap); },
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });
  const status = useMutation({
    mutationFn: async (enable: boolean) => {
      if (!member) {
        return;
      }
      const path = { params: { path: { membershipId: member.membershipId } } };
      unwrap(await (enable ? api.POST("/api/v1/users/{membershipId}/enable", path) : api.POST("/api/v1/users/{membershipId}/disable", path)));
    },
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });

  const close = (): void => { setProblem(null); setAssign(emptyAssign()); onClose(); };
  const submit = (event: FormEvent): void => { event.preventDefault(); add.mutate(false); };
  const toggleScope = (key: string, on: boolean): void => {
    setAssign({ ...assign, scopes: on ? [...assign.scopes, key] : assign.scopes.filter((k) => k !== key) });
  };

  return (
    <Dialog open={member !== null} onOpenChange={(isOpen) => { if (!isOpen) { close(); } }}>
      <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
        {member ? (
          <div className="flex flex-col gap-4" data-testid="member-dialog">
            <DialogHeader>
              <DialogTitle className="text-lg font-semibold">{member.displayName}</DialogTitle>
            </DialogHeader>
            <div className="flex flex-wrap items-center gap-2 text-sm">
              <span dir="ltr" className="text-fg-muted">{member.email}</span>
              <Badge tone={member.status === "active" ? "success" : member.status === "invited" ? "info" : "neutral"}>{t(`members.status.${member.status}`)}</Badge>
              {member.isOwner ? <Badge tone="accent">{t("members.owner")}</Badge> : null}
              <span className="text-fg-muted">{t("members.mfa")}: {member.hasMfa ? t("common.yes") : t("common.no")}</span>
              <span className="text-fg-muted">{t("members.lastLogin")}: {formatDateTime(member.lastLoginAt)}</span>
            </div>

            {problem ? (
              <div role="alert" className={`rounded-md border p-3 text-sm ${problem.warning ? "border-warning/40 bg-warning-soft" : "border-danger/40 bg-danger-soft"}`} data-testid="assignment-problem">
                <p>{problem.message}</p>
                {problem.conflicts.length > 0 ? (
                  <ul className="mt-1 flex flex-col gap-1">
                    {problem.conflicts.map((c) => (
                      <li key={c.ruleId} className="flex flex-wrap items-center gap-1">
                        <span dir="ltr" className="font-mono text-xs">{c.permissionA}</span> + <span dir="ltr" className="font-mono text-xs">{c.permissionB}</span>
                        <span className="text-fg-muted">{localized(c.rationale)}</span>
                      </li>
                    ))}
                  </ul>
                ) : null}
                {problem.warning ? (
                  <Button size="sm" variant="secondary" className="mt-2" onClick={() => { add.mutate(true); }} loading={add.isPending} data-testid="assign-anyway">
                    {t("members.assignAnyway")}
                  </Button>
                ) : problem.conflicts.length > 0 ? (
                  <p className="mt-1 text-fg-muted">{t("members.blockedHint")}</p>
                ) : null}
              </div>
            ) : null}

            <section className="flex flex-col gap-2">
              <h2 className="text-base font-semibold">{t("members.assignments")}</h2>
              {member.isOwner ? <p className="text-sm text-fg-muted">{t("members.ownerHint")}</p> : null}
              {member.assignments.length === 0 ? <p className="text-sm text-fg-muted">{t("members.noAssignments")}</p> : (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("members.role")}</TableHead>
                      <TableHead>{t("members.scope")}</TableHead>
                      <TableHead>{t("members.validity")}</TableHead>
                      <TableHead />
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {member.assignments.map((a) => {
                      const role = roles.find((r) => r.id === a.roleId);
                      return (
                        <TableRow key={a.id} data-testid="assignment-row">
                          <TableCell>
                            <span dir="ltr">{a.roleCode}</span> {role ? <span className="text-fg-muted">{localized(role.name)}</span> : null}
                          </TableCell>
                          <TableCell>
                            {a.scopes.length === 0 ? t("members.everywhere") : a.scopes.map((s) => `${t(`members.scopeTypes.${s.scopeType}`)}: ${labels.get(s.scopeId) ?? s.scopeId}`).join("; ")}
                          </TableCell>
                          <TableCell>{a.validFrom || a.validTo ? `${a.validFrom ? formatDate(a.validFrom) : "…"} – ${a.validTo ? formatDate(a.validTo) : "…"}` : t("members.always")}</TableCell>
                          <TableCell>
                            <Button variant="ghost" size="icon" aria-label={t("members.removeRole", { role: a.roleCode })} onClick={() => { unassign.mutate(a.id); }} data-testid="remove-assignment">
                              <Trash2 aria-hidden="true" />
                            </Button>
                          </TableCell>
                        </TableRow>
                      );
                    })}
                  </TableBody>
                </Table>
              )}
            </section>

            <form onSubmit={submit} className="flex flex-col gap-3 rounded-md border border-border p-3" data-testid="assign-role">
              <h3 className="text-sm font-semibold">{t("members.assignRole")}</h3>
              <div className="grid gap-3 sm:grid-cols-3">
                <Field label={t("members.role")} required>
                  <SelectField value={assign.roleId} onChange={(e) => { setProblem(null); setAssign({ ...assign, roleId: e.target.value }); }} required data-testid="assign-role-select">
                    <option value="">—</option>
                    {roles.filter((r) => r.isActive && r.code !== "owner").map((r) => (
                      <option key={r.id} value={r.id}>
                        {r.code} · {localized(r.name)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("members.validFrom")}>
                  <TextField type="date" value={assign.validFrom} onChange={(e) => { setAssign({ ...assign, validFrom: e.target.value }); }} dir="ltr" />
                </Field>
                <Field label={t("members.validTo")}>
                  <TextField type="date" value={assign.validTo} onChange={(e) => { setAssign({ ...assign, validTo: e.target.value }); }} dir="ltr" />
                </Field>
              </div>
              <div className="flex flex-col gap-2" data-testid="assign-scopes">
                <p className="text-sm font-medium">{t("members.limitTo")}</p>
                <p className="text-xs text-fg-muted">{t("members.limitToHint")}</p>
                <div className="grid gap-3 sm:grid-cols-3">
                  {scopeTypes.map((type) => (
                    <fieldset key={type} className="flex min-w-0 flex-col gap-1 rounded-md border border-border p-2" data-testid={`assign-scopes-${type}`}>
                      <legend className="px-1 text-xs font-medium text-fg-muted">{t(`members.scopeTypes.${type}`)}</legend>
                      {options[type].length === 0 ? <p className="text-xs text-fg-muted">—</p> : (
                        <div className="flex max-h-40 flex-col gap-1 overflow-y-auto">
                          {options[type].map((o) => {
                            const key = `${type}:${o.id}`;
                            return (
                              <label key={o.id} className="flex items-start gap-2 text-sm">
                                <input type="checkbox" className="mt-1" checked={assign.scopes.includes(key)} onChange={(e) => { toggleScope(key, e.target.checked); }} />
                                <span className="min-w-0 break-words">{o.label}</span>
                              </label>
                            );
                          })}
                        </div>
                      )}
                    </fieldset>
                  ))}
                </div>
              </div>
              <div>
                <Button type="submit" size="sm" loading={add.isPending && !problem?.warning} data-testid="save-assignment">
                  {t("members.assign")}
                </Button>
              </div>
            </form>

            {own.length > 0 ? (
              <section className="flex flex-col gap-1 text-sm" data-testid="member-conflicts">
                <h3 className="font-semibold">{t("members.conflicts")}</h3>
                <ul className="flex flex-col gap-1">
                  {own.map((c) => (
                    <li key={c.ruleId} className="flex flex-wrap items-center gap-1">
                      <Badge tone={c.severity === "block" ? "danger" : "warning"}>{t(`security.severities.${c.severity}`)}</Badge>
                      <span dir="ltr" className="font-mono text-xs">{c.permissionA}</span> + <span dir="ltr" className="font-mono text-xs">{c.permissionB}</span>
                      {c.hasException ? <Badge>{t("security.excepted")}</Badge> : null}
                    </li>
                  ))}
                </ul>
              </section>
            ) : null}

            <DialogFooter>
              {member.isOwner ? null : member.status === "disabled" ? (
                <Button type="button" variant="secondary" className="me-auto" onClick={() => { status.mutate(true); }} loading={status.isPending} data-testid="enable-member">
                  {t("members.enable")}
                </Button>
              ) : (
                <Button type="button" variant="ghost" className="me-auto text-danger" onClick={() => { status.mutate(false); }} loading={status.isPending} data-testid="disable-member">
                  {t("members.disable")}
                </Button>
              )}
              <Button type="button" variant="secondary" onClick={close}>
                {t("common.close")}
              </Button>
            </DialogFooter>
          </div>
        ) : null}
      </DialogContent>
    </Dialog>
  );
}
