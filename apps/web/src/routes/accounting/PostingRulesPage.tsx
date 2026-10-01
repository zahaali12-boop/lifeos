import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus, Trash2 } from "lucide-react";
import { useId, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { FormError, PageHeader, SelectField, TextField } from "../common";
import { DocStatus, Tabs } from "../inventory/shared";
import { CompanySelect, today, useCompanies, useCompanySelection } from "./shared";

type Rule = components["schemas"]["PostingRuleSummary"];
type RuleRequest = components["schemas"]["PostingRuleRequest"];
type Group = components["schemas"]["PostingGroupSummary"];

const groupKinds = ["item", "partner_customer", "partner_supplier", "bank", "asset", "tax", "charge"];

interface RuleRow extends RuleRequest {
  key: number;
}

function toRequest(r: Rule): RuleRequest {
  return { accountRole: r.accountRole, accountCode: r.accountCode, documentType: r.documentType, itemPostingGroupId: r.itemPostingGroupId, partnerPostingGroupId: r.partnerPostingGroupId, taxCodeId: r.taxCodeId, warehouseId: r.warehouseId, branchId: r.branchId, bankAccountId: r.bankAccountId, assetCategoryId: r.assetCategoryId, chargeTypeId: r.chargeTypeId };
}

/**
 * Posting rules (roadmap 2.2, POSTING_RULES): each company's posting profile maps every account role the documents
 * post to onto an account of its chart, optionally narrowed by document type, posting group, warehouse or branch (the
 * most specific rule wins). Profiles are versioned: a new version starts from the current rules, is edited, then
 * activated, retiring the previous one. Posting groups (item, customer, supplier, …) are managed on the second tab.
 */
export function PostingRulesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const companies = useCompanies();
  const [companyId, setCompanyId] = useCompanySelection(companies.data);
  const company = companies.data?.find((c) => c.id === companyId);
  const [tab, setTab] = useState("rules");
  const [profileId, setProfileId] = useState("");
  const [rows, setRows] = useState<RuleRow[] | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [versionFrom, setVersionFrom] = useState<string | null>(null);
  const [group, setGroup] = useState<{ id: string | null; kind: string; code: string; nameEn: string; nameAr: string; isActive: boolean } | null>(null);
  const accountListId = useId();
  const roleLabel = (role: string) => t(`accountRoles.${role}`, { defaultValue: role });

  const profiles = useQuery({
    queryKey: ["posting-profiles", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/accounting/companies/{companyId}/posting-profiles", { params: { path: { companyId } } })),
  });
  const selectedId = profileId || (profiles.data?.find((p) => p.isCurrent)?.id ?? profiles.data?.[0]?.id ?? "");
  const profile = useQuery({
    queryKey: ["posting-profile", selectedId],
    enabled: Boolean(selectedId),
    queryFn: async () => unwrap(await api.GET("/api/v1/accounting/posting-profiles/{profileId}", { params: { path: { profileId: selectedId } } })),
  });
  const accounts = useQuery({
    queryKey: ["chart-accounts", company?.chartId],
    enabled: Boolean(company?.chartId),
    queryFn: async () => unwrap(await api.GET("/api/v1/accounting/charts/{chartId}/accounts", { params: { path: { chartId: company?.chartId ?? "" } } })),
  });
  const groups = useQuery({ queryKey: ["posting-groups"], queryFn: async () => unwrap(await api.GET("/api/v1/accounting/posting-groups")) });
  const warehouses = useQuery({
    queryKey: ["warehouses", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses", { params: { query: { companyId } } })),
  });
  const branches = useQuery({
    queryKey: ["branches", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/organization/companies/{companyId}/branches", { params: { path: { companyId } } })),
  });

  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["posting-profiles"] });
    await queryClient.invalidateQueries({ queryKey: ["posting-profile"] });
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const saveRules = useMutation({
    mutationFn: async () => unwrap(await api.PUT("/api/v1/accounting/posting-profiles/{profileId}/rules", { params: { path: { profileId: selectedId } }, body: (rows ?? []).filter((r) => r.accountRole && r.accountCode.trim()).map((r) => ({ accountRole: r.accountRole, accountCode: r.accountCode.trim(), documentType: r.documentType?.trim() ? r.documentType.trim() : null, itemPostingGroupId: r.itemPostingGroupId ?? null, partnerPostingGroupId: r.partnerPostingGroupId ?? null, taxCodeId: r.taxCodeId ?? null, warehouseId: r.warehouseId ?? null, branchId: r.branchId ?? null, bankAccountId: r.bankAccountId ?? null, assetCategoryId: r.assetCategoryId ?? null, chargeTypeId: r.chargeTypeId ?? null })) })),
    onSuccess: async () => { setRows(null); setProblem(null); await refresh(); },
    onError: fail,
  });
  const activate = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/accounting/posting-profiles/{profileId}/activate", { params: { path: { profileId: selectedId } } })),
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });
  const fromChart = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/accounting/companies/{companyId}/posting-profiles/from-chart", { params: { path: { companyId } }, body: null })),
    onSuccess: async (created) => { setProblem(null); setProfileId(created.id); await refresh(); },
    onError: fail,
  });
  const newVersion = useMutation({
    mutationFn: async () => {
      const current = profile.data;
      if (!current) {
        throw new Error("no profile");
      }
      const created = unwrap(await api.POST("/api/v1/accounting/companies/{companyId}/posting-profiles", { params: { path: { companyId } }, body: { code: current.code, name: current.name, validFrom: versionFrom } }));
      unwrap(await api.PUT("/api/v1/accounting/posting-profiles/{profileId}/rules", { params: { path: { profileId: created.id } }, body: (current.rules ?? []).map(toRequest) }));
      return created;
    },
    onSuccess: async (created) => { setVersionFrom(null); setProblem(null); setProfileId(created.id); await refresh(); },
    onError: fail,
  });
  const saveGroup = useMutation({
    mutationFn: async () => {
      if (!group) {
        return;
      }
      const body = { kind: group.kind, code: group.code, name: { en: group.nameEn, ...(group.nameAr ? { ar: group.nameAr } : {}) }, isActive: group.isActive };
      if (group.id) {
        unwrap(await api.PUT("/api/v1/accounting/posting-groups/{groupId}", { params: { path: { groupId: group.id } }, body }));
      } else {
        unwrap(await api.POST("/api/v1/accounting/posting-groups", { body }));
      }
    },
    onSuccess: async () => { setGroup(null); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["posting-groups"] }); },
    onError: fail,
  });

  const detail = profile.data;
  const editable = detail?.status === "draft" || detail?.status === "active";
  const postable = (accounts.data ?? []).filter((a) => !a.isHeader && a.isActive);
  const accountName = (code: string) => {
    const account = postable.find((a) => a.code === code);
    return account ? localized(account.name) : "";
  };
  const groupsOf = (kinds: string[]) => (groups.data ?? []).filter((g) => kinds.includes(g.kind));
  const groupCode = (id: string | null | undefined) => (id ? (groups.data ?? []).find((g) => g.id === id)?.code ?? "?" : null);
  const conditions = (r: Rule | RuleRequest): string[] => {
    const parts: string[] = [];
    if (r.documentType) { parts.push(`${t("postingRules.documentType")}: ${r.documentType}`); }
    if (r.itemPostingGroupId) { parts.push(`${t("postingRules.itemGroup")}: ${groupCode(r.itemPostingGroupId) ?? ""}`); }
    if (r.partnerPostingGroupId) { parts.push(`${t("postingRules.partnerGroup")}: ${groupCode(r.partnerPostingGroupId) ?? ""}`); }
    if (r.warehouseId) { parts.push(`${t("inventory.warehouse")}: ${(warehouses.data ?? []).find((w) => w.id === r.warehouseId)?.code ?? "?"}`); }
    if (r.branchId) { parts.push(`${t("postingRules.branch")}: ${(branches.data ?? []).find((b) => b.id === r.branchId)?.code ?? "?"}`); }
    if (r.bankAccountId) { parts.push(t("postingRules.bankAccount")); }
    if (r.chargeTypeId) { parts.push(t("postingRules.chargeType")); }
    if (r.taxCodeId) { parts.push(t("postingRules.taxCode")); }
    if (r.assetCategoryId) { parts.push(t("postingRules.assetCategory")); }
    return parts;
  };
  const sortedRules = [...(detail?.rules ?? [])].sort((a, b) => roleLabel(a.accountRole).localeCompare(roleLabel(b.accountRole)) || Number(a.specificity) - Number(b.specificity));
  const startEdit = (): void => { setProblem(null); setRows(sortedRules.map((r, i) => ({ ...toRequest(r), key: i }))); };
  const patch = (key: number, change: Partial<RuleRequest>): void => { setRows((prev) => (prev ? prev.map((r) => (r.key === key ? { ...r, ...change } : r)) : prev)); };
  const roles = Object.keys(t("accountRoles", { returnObjects: true })).sort((a, b) => roleLabel(a).localeCompare(roleLabel(b)));
  const submitGroup = (event: FormEvent): void => { event.preventDefault(); saveGroup.mutate(); };
  const submitVersion = (event: FormEvent): void => { event.preventDefault(); newVersion.mutate(); };

  return (
    <>
      <PageHeader
        title={t("nav.postingRules")}
        description={t("postingRules.description")}
        actions={
          tab === "groups" ? (
            <Button onClick={() => { setProblem(null); setGroup({ id: null, kind: "item", code: "", nameEn: "", nameAr: "", isActive: true }); }} data-testid="new-posting-group">
              <Plus aria-hidden="true" />
              {t("postingRules.newGroup")}
            </Button>
          ) : (profiles.data ?? []).length === 0 ? (
            <Button onClick={() => { fromChart.mutate(); }} loading={fromChart.isPending} disabled={!company?.chartId} data-testid="profile-from-chart">
              {t("postingRules.fromChart")}
            </Button>
          ) : (
            <Button variant="secondary" onClick={() => { setProblem(null); setVersionFrom(today()); }} disabled={!detail} data-testid="new-profile-version">
              <Plus aria-hidden="true" />
              {t("postingRules.newVersion")}
            </Button>
          )
        }
      />
      <div className="mb-4 grid gap-3 sm:grid-cols-3">
        <CompanySelect companies={companies.data ?? []} value={companyId} onChange={(id) => { setCompanyId(id); setProfileId(""); setRows(null); }} />
        {tab === "rules" && (profiles.data ?? []).length > 0 ? (
          <Field label={t("postingRules.version")}>
            <SelectField value={selectedId} onChange={(e) => { setProfileId(e.target.value); setRows(null); }} data-testid="profile-select">
              {(profiles.data ?? []).map((p) => (
                <option key={p.id} value={p.id}>
                  {p.code} v{p.version} · {t(`postingRules.statuses.${p.status}`, { defaultValue: p.status })}{p.isCurrent ? ` · ${t("postingRules.current")}` : ""}
                </option>
              ))}
            </SelectField>
          </Field>
        ) : null}
      </div>
      <Tabs
        value={tab}
        onChange={(next) => { setTab(next); setProblem(null); }}
        tabs={[
          { id: "rules", label: t("postingRules.rules"), testId: "tab-rules" },
          { id: "groups", label: t("postingRules.groups"), testId: "tab-groups" },
        ]}
      />
      <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />

      {tab === "rules" ? (
        !company?.chartId ? (
          <p className="text-sm text-fg-muted">{t("postingRules.noChart")}</p>
        ) : (profiles.data ?? []).length === 0 ? (
          <p className="text-sm text-fg-muted">{t("postingRules.noProfile")}</p>
        ) : detail ? (
          <div className="flex flex-col gap-4" data-testid="posting-profile">
            <div className="flex flex-wrap items-center gap-2 text-sm">
              <DocStatus status={detail.status} />
              {detail.isCurrent ? <Badge tone="accent">{t("postingRules.current")}</Badge> : null}
              <span className="text-fg-muted">{t("postingRules.validFrom")}: {formatDate(detail.validFrom)}</span>
              <span className="text-fg-muted">{t("postingRules.ruleCount", { count: detail.ruleCount })}</span>
              <div className="ms-auto flex gap-2">
                {editable && !rows ? (
                  <Button variant="secondary" size="sm" onClick={startEdit} data-testid="edit-rules">
                    {t("postingRules.editRules")}
                  </Button>
                ) : null}
                {detail.status === "draft" && !rows ? (
                  <Button size="sm" onClick={() => { activate.mutate(); }} loading={activate.isPending} data-testid="activate-profile">
                    {t("postingRules.activate")}
                  </Button>
                ) : null}
              </div>
            </div>
            {detail.unresolvedRoles.length > 0 ? (
              <div className="rounded-md border border-warning/40 bg-warning-soft p-3 text-sm" role="status" data-testid="unresolved-roles">
                <p className="font-medium">{t("postingRules.unresolved", { count: detail.unresolvedRoles.length })}</p>
                <p className="mt-1 text-fg-muted">{detail.unresolvedRoles.map(roleLabel).join(" · ")}</p>
              </div>
            ) : null}
            {rows ? (
              <div className="flex flex-col gap-3">
                <p className="text-sm text-fg-muted">{t("postingRules.editHint")}</p>
                <datalist id={accountListId}>
                  {postable.map((a) => (
                    <option key={a.id} value={a.code}>
                      {localized(a.name)}
                    </option>
                  ))}
                </datalist>
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("postingRules.role")}</TableHead>
                      <TableHead>{t("postingRules.account")}</TableHead>
                      <TableHead>{t("postingRules.documentType")}</TableHead>
                      <TableHead>{t("postingRules.itemGroup")}</TableHead>
                      <TableHead>{t("postingRules.partnerGroup")}</TableHead>
                      <TableHead>{t("inventory.warehouse")}</TableHead>
                      <TableHead>{t("postingRules.branch")}</TableHead>
                      <TableHead />
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {rows.map((r) => (
                      <TableRow key={r.key} data-testid="rule-edit-row">
                        <TableCell>
                          <SelectField value={r.accountRole} onChange={(e) => { patch(r.key, { accountRole: e.target.value }); }} aria-label={t("postingRules.role")} className="w-52">
                            <option value="">{t("postingRules.chooseRole")}</option>
                            {roles.map((role) => (
                              <option key={role} value={role}>
                                {roleLabel(role)}
                              </option>
                            ))}
                          </SelectField>
                        </TableCell>
                        <TableCell>
                          <TextField value={r.accountCode} onChange={(e) => { patch(r.key, { accountCode: e.target.value }); }} list={accountListId} dir="ltr" className="w-28" aria-label={t("postingRules.account")} />
                        </TableCell>
                        <TableCell>
                          <TextField value={r.documentType ?? ""} onChange={(e) => { patch(r.key, { documentType: e.target.value }); }} dir="ltr" className="w-36" aria-label={t("postingRules.documentType")} />
                        </TableCell>
                        <TableCell>
                          <SelectField value={r.itemPostingGroupId ?? ""} onChange={(e) => { patch(r.key, { itemPostingGroupId: e.target.value || null }); }} aria-label={t("postingRules.itemGroup")}>
                            <option value="">{t("postingRules.any")}</option>
                            {groupsOf(["item"]).map((g) => (
                              <option key={g.id} value={g.id}>
                                {g.code}
                              </option>
                            ))}
                          </SelectField>
                        </TableCell>
                        <TableCell>
                          <SelectField value={r.partnerPostingGroupId ?? ""} onChange={(e) => { patch(r.key, { partnerPostingGroupId: e.target.value || null }); }} aria-label={t("postingRules.partnerGroup")}>
                            <option value="">{t("postingRules.any")}</option>
                            {groupsOf(["partner_customer", "partner_supplier"]).map((g) => (
                              <option key={g.id} value={g.id}>
                                {g.code}
                              </option>
                            ))}
                          </SelectField>
                        </TableCell>
                        <TableCell>
                          <SelectField value={r.warehouseId ?? ""} onChange={(e) => { patch(r.key, { warehouseId: e.target.value || null }); }} aria-label={t("inventory.warehouse")}>
                            <option value="">{t("postingRules.any")}</option>
                            {(warehouses.data ?? []).map((w) => (
                              <option key={w.id} value={w.id}>
                                {w.code}
                              </option>
                            ))}
                          </SelectField>
                        </TableCell>
                        <TableCell>
                          <SelectField value={r.branchId ?? ""} onChange={(e) => { patch(r.key, { branchId: e.target.value || null }); }} aria-label={t("postingRules.branch")}>
                            <option value="">{t("postingRules.any")}</option>
                            {(branches.data ?? []).map((b) => (
                              <option key={b.id} value={b.id}>
                                {b.code}
                              </option>
                            ))}
                          </SelectField>
                        </TableCell>
                        <TableCell>
                          <Button type="button" variant="ghost" size="icon" aria-label={t("accounting.removeLine")} onClick={() => { setRows((prev) => (prev ? prev.filter((x) => x.key !== r.key) : prev)); }}>
                            <Trash2 aria-hidden="true" />
                          </Button>
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
                <div className="flex flex-wrap gap-2">
                  <Button type="button" variant="secondary" onClick={() => { setRows((prev) => [...(prev ?? []), { key: Date.now(), accountRole: "", accountCode: "" }]); }} data-testid="add-rule">
                    <Plus aria-hidden="true" />
                    {t("postingRules.addRule")}
                  </Button>
                  <Button type="button" variant="secondary" onClick={() => { setRows(null); setProblem(null); }}>
                    {t("common.cancel")}
                  </Button>
                  <Button type="button" onClick={() => { saveRules.mutate(); }} loading={saveRules.isPending} data-testid="save-rules">
                    {t("postingRules.saveRules")}
                  </Button>
                </div>
              </div>
            ) : (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("postingRules.role")}</TableHead>
                    <TableHead>{t("postingRules.account")}</TableHead>
                    <TableHead>{t("postingRules.when")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {sortedRules.map((r) => (
                    <TableRow key={r.id} data-testid="rule-row">
                      <TableCell>
                        {roleLabel(r.accountRole)} <span className="text-xs text-fg-subtle" dir="ltr">{r.accountRole}</span>
                      </TableCell>
                      <TableCell>
                        <span dir="ltr">{r.accountCode}</span> <span className="text-fg-muted">{accountName(r.accountCode)}</span>
                      </TableCell>
                      <TableCell>
                        {conditions(r).length === 0 ? <span className="text-fg-muted">{t("postingRules.always")}</span> : (
                          <div className="flex flex-wrap gap-1">
                            {conditions(r).map((c) => (
                              <Badge key={c}>{c}</Badge>
                            ))}
                          </div>
                        )}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </div>
        ) : null
      ) : (
        <div className="flex flex-col gap-4" data-testid="posting-groups">
          <p className="text-sm text-fg-muted">{t("postingRules.groupsHint")}</p>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("postingRules.groupKind")}</TableHead>
                <TableHead>{t("postingRules.code")}</TableHead>
                <TableHead>{t("postingRules.name")}</TableHead>
                <TableHead>{t("common.status")}</TableHead>
                <TableHead />
              </TableRow>
            </TableHeader>
            <TableBody>
              {(groups.data ?? []).map((g: Group) => (
                <TableRow key={g.id} data-testid="posting-group-row">
                  <TableCell>{t(`postingRules.groupKinds.${g.kind}`, { defaultValue: g.kind })}</TableCell>
                  <TableCell dir="ltr">{g.code}</TableCell>
                  <TableCell>{localized(g.name)}</TableCell>
                  <TableCell><DocStatus status={g.isActive ? "active" : "inactive"} /></TableCell>
                  <TableCell>
                    <Button variant="ghost" size="sm" onClick={() => { setProblem(null); setGroup({ id: g.id, kind: g.kind, code: g.code, nameEn: g.name.en ?? "", nameAr: g.name.ar ?? "", isActive: g.isActive }); }}>
                      {t("common.edit")}
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
          {(groups.data ?? []).length === 0 ? <p className="text-sm text-fg-muted">{t("postingRules.noGroups")}</p> : null}
        </div>
      )}

      <Dialog open={versionFrom !== null} onOpenChange={(isOpen) => { if (!isOpen) { setVersionFrom(null); } }}>
        <DialogContent closeLabel={t("common.close")}>
          <form onSubmit={submitVersion} className="flex flex-col gap-4">
            <DialogHeader>
              <DialogTitle className="text-lg font-semibold">{t("postingRules.newVersion")}</DialogTitle>
            </DialogHeader>
            <p className="text-sm text-fg-muted">{t("postingRules.newVersionHint")}</p>
            <Field label={t("postingRules.validFrom")} required error={problem?.fields.validFrom}>
              <TextField type="date" value={versionFrom ?? ""} onChange={(e) => { setVersionFrom(e.target.value); }} required dir="ltr" data-testid="version-valid-from" />
            </Field>
            <DialogFooter>
              <Button type="button" variant="secondary" onClick={() => { setVersionFrom(null); }}>
                {t("common.cancel")}
              </Button>
              <Button type="submit" loading={newVersion.isPending} data-testid="create-version">
                {t("postingRules.createVersion")}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(group)} onOpenChange={(isOpen) => { if (!isOpen) { setGroup(null); } }}>
        <DialogContent closeLabel={t("common.close")}>
          {group ? (
            <form onSubmit={submitGroup} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{group.id ? t("postingRules.editGroup") : t("postingRules.newGroup")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("postingRules.groupKind")}>
                  <SelectField value={group.kind} onChange={(e) => { setGroup({ ...group, kind: e.target.value }); }} disabled={Boolean(group.id)} data-testid="group-kind">
                    {groupKinds.map((k) => (
                      <option key={k} value={k}>
                        {t(`postingRules.groupKinds.${k}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("postingRules.code")} required error={problem?.fields.code}>
                  <TextField value={group.code} onChange={(e) => { setGroup({ ...group, code: e.target.value.toUpperCase() }); }} required dir="ltr" data-testid="group-code" />
                </Field>
                <Field label={t("postingRules.nameEn")} required error={problem?.fields.name}>
                  <TextField value={group.nameEn} onChange={(e) => { setGroup({ ...group, nameEn: e.target.value }); }} required data-testid="group-name-en" />
                </Field>
                <Field label={t("postingRules.nameAr")}>
                  <TextField value={group.nameAr} onChange={(e) => { setGroup({ ...group, nameAr: e.target.value }); }} dir="rtl" lang="ar" />
                </Field>
              </div>
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={group.isActive} onChange={(e) => { setGroup({ ...group, isActive: e.target.checked }); }} />
                {t("common.active")}
              </label>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setGroup(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={saveGroup.isPending} data-testid="save-posting-group">
                  {t("common.save")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
