import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Copy, Plus, Trash2 } from "lucide-react";
import { useId, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";
import { formatDateTime, localized } from "../lib/format";
import { toFormProblem, type FormProblem } from "../lib/problem";
import { FormError, PageHeader, SelectField, TextField } from "./common";
import { DocStatus, Tabs } from "./inventory/shared";

type Policy = components["schemas"]["TenantSecurityPolicy"];

/** The numeric policy fields, with the ranges the server accepts. */
const policyNumbers = [
  { key: "passwordMinLength", min: 8, max: 128 },
  { key: "sessionLifetimeHours", min: 1, max: 2160 },
  { key: "accessTokenMinutes", min: 1, max: 60 },
  { key: "stepUpWindowMinutes", min: 1, max: 240 },
  { key: "lockoutThreshold", min: 3, max: 100 },
  { key: "lockoutMinutes", min: 1, max: 1440 },
] as const;

function list(value: string): string[] {
  return value.split(/[\s,;]+/).map((s) => s.trim()).filter(Boolean);
}

/**
 * Security (roadmap 1.5–1.6): segregation-of-duties rules and the report of members who hold conflicting permissions,
 * API keys for integrations (the secret is shown once), and single sign-on connections to an OpenID Connect provider.
 */
export function SecurityPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const permissionListId = useId();
  const [tab, setTab] = useState("sod");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [rule, setRule] = useState<{ id: string | null; permissionA: string; permissionB: string; severity: string; rationaleEn: string; rationaleAr: string; isActive: boolean } | null>(null);
  const [key, setKey] = useState<{ name: string; scopes: string; expiresAt: string; ipAllowlist: string } | null>(null);
  const [created, setCreated] = useState<{ name: string; key: string } | null>(null);
  const [sso, setSso] = useState<{ id: string | null; code: string; displayName: string; authority: string; clientId: string; clientSecret: string; scopes: string; emailDomains: string; jitProvisioning: boolean; groupClaim: string; groupRoleMap: Record<string, string> | null; isActive: boolean; hasSecret: boolean } | null>(null);
  const [policy, setPolicy] = useState<Policy | null>(null);
  const [exception, setException] = useState<{ ruleId: string; membershipId: string; member: string; reason: string; expiresOn: string } | null>(null);
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const permissions = useQuery({ queryKey: ["permission-catalog"], queryFn: async () => unwrap(await api.GET("/api/v1/meta/permissions")) });
  const rules = useQuery({ queryKey: ["sod-rules"], enabled: tab === "sod", queryFn: async () => unwrap(await api.GET("/api/v1/sod/rules")) });
  const report = useQuery({ queryKey: ["sod-report"], enabled: tab === "sod", queryFn: async () => unwrap(await api.GET("/api/v1/sod/report")) });
  const keys = useQuery({ queryKey: ["api-keys"], enabled: tab === "keys", queryFn: async () => unwrap(await api.GET("/api/v1/api-keys")) });
  const connections = useQuery({ queryKey: ["sso-connections"], enabled: tab === "sso" || tab === "policy", queryFn: async () => unwrap(await api.GET("/api/v1/sso-connections")) });
  const savedPolicy = useQuery({ queryKey: ["tenant-policy"], enabled: tab === "policy", queryFn: async () => unwrap(await api.GET("/api/v1/tenant/policy")) });
  const draft = policy ?? savedPolicy.data ?? null;

  const saveRule = useMutation({
    mutationFn: async () => {
      if (!rule) {
        return;
      }
      const body = { permissionA: rule.permissionA.trim(), permissionB: rule.permissionB.trim(), severity: rule.severity, rationale: { en: rule.rationaleEn, ...(rule.rationaleAr ? { ar: rule.rationaleAr } : {}) }, isActive: rule.isActive };
      if (rule.id) {
        unwrap(await api.PUT("/api/v1/sod/rules/{ruleId}", { params: { path: { ruleId: rule.id } }, body }));
      } else {
        unwrap(await api.POST("/api/v1/sod/rules", { body }));
      }
    },
    onSuccess: async () => { setRule(null); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["sod-rules"] }); await queryClient.invalidateQueries({ queryKey: ["sod-report"] }); },
    onError: fail,
  });
  const grantException = useMutation({
    mutationFn: async () => {
      if (!exception) {
        return;
      }
      unwrap(await api.POST("/api/v1/sod/exceptions", { body: { ruleId: exception.ruleId, membershipId: exception.membershipId, reason: exception.reason, expiresOn: exception.expiresOn || null } }));
    },
    onSuccess: async () => { setException(null); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["sod-report"] }); },
    onError: fail,
  });
  const createKey = useMutation({
    mutationFn: async () => {
      if (!key) {
        throw new Error("no key");
      }
      return unwrap(await api.POST("/api/v1/api-keys", { body: { name: key.name, scopes: list(key.scopes), expiresAt: key.expiresAt ? `${key.expiresAt}T23:59:59Z` : null, ipAllowlist: list(key.ipAllowlist) } }));
    },
    onSuccess: async (result) => { setKey(null); setProblem(null); setCreated({ name: result.name, key: result.key }); await queryClient.invalidateQueries({ queryKey: ["api-keys"] }); },
    onError: fail,
  });
  const revokeKey = useMutation({
    mutationFn: async (keyId: string) => { await api.DELETE("/api/v1/api-keys/{keyId}", { params: { path: { keyId } } }).then(unwrap); },
    onSuccess: async () => { setProblem(null); await queryClient.invalidateQueries({ queryKey: ["api-keys"] }); },
    onError: fail,
  });
  const saveSso = useMutation({
    mutationFn: async () => {
      if (!sso) {
        return;
      }
      const body = { code: sso.code, displayName: sso.displayName, authority: sso.authority, clientId: sso.clientId, clientSecret: sso.clientSecret || null, scopes: sso.scopes, emailDomains: list(sso.emailDomains), jitProvisioning: sso.jitProvisioning, groupClaim: sso.groupClaim || null, groupRoleMap: sso.groupRoleMap, isActive: sso.isActive };
      if (sso.id) {
        unwrap(await api.PUT("/api/v1/sso-connections/{connectionId}", { params: { path: { connectionId: sso.id } }, body }));
      } else {
        unwrap(await api.POST("/api/v1/sso-connections", { body }));
      }
    },
    onSuccess: async () => { setSso(null); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["sso-connections"] }); },
    onError: fail,
  });
  const deleteSso = useMutation({
    mutationFn: async (connectionId: string) => { await api.DELETE("/api/v1/sso-connections/{connectionId}", { params: { path: { connectionId } } }).then(unwrap); },
    onSuccess: async () => { setSso(null); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["sso-connections"] }); },
    onError: fail,
  });
  const savePolicy = useMutation({
    mutationFn: async (body: Policy) => unwrap(await api.PUT("/api/v1/tenant/policy", { body })),
    onSuccess: async () => { setPolicy(null); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["tenant-policy"] }); },
    onError: fail,
  });

  const violations = (report.data ?? []).filter((r) => r.conflicts.length > 0);
  const superUsers = (report.data ?? []).filter((r) => r.isSuperUser);
  const submitRule = (event: FormEvent): void => { event.preventDefault(); saveRule.mutate(); };
  const submitKey = (event: FormEvent): void => { event.preventDefault(); createKey.mutate(); };
  const submitException = (event: FormEvent): void => { event.preventDefault(); grantException.mutate(); };
  const submitSso = (event: FormEvent): void => { event.preventDefault(); saveSso.mutate(); };
  const submitPolicy = (event: FormEvent): void => {
    event.preventDefault();
    if (draft) {
      savePolicy.mutate(draft);
    }
  };
  const activeSso = (connections.data ?? []).some((c) => c.isActive);

  return (
    <>
      <PageHeader
        title={t("nav.security")}
        description={t("security.description")}
        actions={
          tab === "sod" ? (
            <Button onClick={() => { setProblem(null); setRule({ id: null, permissionA: "", permissionB: "", severity: "warn", rationaleEn: "", rationaleAr: "", isActive: true }); }} data-testid="new-sod-rule">
              <Plus aria-hidden="true" />
              {t("security.newRule")}
            </Button>
          ) : tab === "keys" ? (
            <Button onClick={() => { setProblem(null); setKey({ name: "", scopes: "", expiresAt: "", ipAllowlist: "" }); }} data-testid="new-api-key">
              <Plus aria-hidden="true" />
              {t("security.newKey")}
            </Button>
          ) : tab === "policy" ? null : (
            <Button onClick={() => { setProblem(null); setSso({ id: null, code: "", displayName: "", authority: "", clientId: "", clientSecret: "", scopes: "openid profile email", emailDomains: "", jitProvisioning: false, groupClaim: "", groupRoleMap: null, isActive: true, hasSecret: false }); }} data-testid="new-sso">
              <Plus aria-hidden="true" />
              {t("security.newSso")}
            </Button>
          )
        }
      />
      <Tabs
        value={tab}
        onChange={(next) => { setTab(next); setProblem(null); }}
        tabs={[
          { id: "sod", label: t("security.sod"), testId: "tab-sod" },
          { id: "keys", label: t("security.keys"), testId: "tab-keys" },
          { id: "sso", label: t("security.sso"), testId: "tab-sso" },
          { id: "policy", label: t("security.policy"), testId: "tab-policy" },
        ]}
      />
      <FormError message={problem && !rule && !key && !sso && !exception && tab !== "policy" ? problem.message : null} />
      <datalist id={permissionListId}>
        {(permissions.data ?? []).map((p) => (
          <option key={p.key} value={p.key}>
            {p.description}
          </option>
        ))}
      </datalist>

      {tab === "sod" ? (
        <div className="flex flex-col gap-6" data-testid="sod">
          <section className="flex flex-col gap-2">
            <h2 className="text-base font-semibold">{t("security.conflicts")}</h2>
            {violations.length === 0 ? <p className="text-sm text-fg-muted" data-testid="sod-clean">{t("security.noConflicts")}</p> : (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("security.member")}</TableHead>
                    <TableHead>{t("security.conflictingPermissions")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {violations.map((v) => (
                    <TableRow key={v.membershipId} data-testid="sod-violation">
                      <TableCell>
                        {v.displayName} <span className="text-fg-muted" dir="ltr">{v.email}</span> {v.isSuperUser ? <Badge tone="info">{t("security.owner")}</Badge> : null}
                      </TableCell>
                      <TableCell>
                        <ul className="flex flex-col gap-1">
                          {v.conflicts.map((c) => (
                            <li key={c.ruleId} className="flex flex-wrap items-center gap-1 text-sm">
                              <Badge tone={c.severity === "block" ? "danger" : "warning"}>{t(`security.severities.${c.severity}`)}</Badge>
                              <span dir="ltr" className="font-mono text-xs">{c.permissionA}</span> + <span dir="ltr" className="font-mono text-xs">{c.permissionB}</span>
                              {c.hasException ? <Badge>{t("security.excepted")}</Badge> : (
                                <Button variant="ghost" size="sm" onClick={() => { setProblem(null); setException({ ruleId: c.ruleId, membershipId: v.membershipId, member: v.displayName, reason: "", expiresOn: "" }); }} data-testid="grant-exception">
                                  {t("security.grantException")}
                                </Button>
                              )}
                            </li>
                          ))}
                        </ul>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </section>
          {superUsers.length > 0 ? (
            <section className="flex flex-col gap-2" data-testid="sod-superusers">
              <h2 className="text-base font-semibold">{t("security.superUsers")}</h2>
              <p className="text-sm text-fg-muted">{t("security.superUsersHint")}</p>
              <ul className="flex flex-wrap gap-2 text-sm">
                {superUsers.map((u) => (
                  <li key={u.membershipId} className="rounded-md border border-border px-2 py-1">
                    {u.displayName} <span className="text-fg-muted" dir="ltr">{u.email}</span>
                  </li>
                ))}
              </ul>
            </section>
          ) : null}
          <section className="flex flex-col gap-2">
            <h2 className="text-base font-semibold">{t("security.rules")}</h2>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("security.permissionA")}</TableHead>
                  <TableHead>{t("security.permissionB")}</TableHead>
                  <TableHead>{t("security.severity")}</TableHead>
                  <TableHead>{t("security.rationale")}</TableHead>
                  <TableHead>{t("common.status")}</TableHead>
                  <TableHead />
                </TableRow>
              </TableHeader>
              <TableBody>
                {(rules.data ?? []).map((r) => (
                  <TableRow key={r.id} data-testid="sod-rule-row">
                    <TableCell dir="ltr" className="font-mono text-xs">{r.permissionA}</TableCell>
                    <TableCell dir="ltr" className="font-mono text-xs">{r.permissionB}</TableCell>
                    <TableCell><Badge tone={r.severity === "block" ? "danger" : "warning"}>{t(`security.severities.${r.severity}`)}</Badge></TableCell>
                    <TableCell>{localized(r.rationale)} {r.isSystem ? <Badge tone="info">{t("security.system")}</Badge> : null}</TableCell>
                    <TableCell><DocStatus status={r.isActive ? "active" : "inactive"} /></TableCell>
                    <TableCell>
                      <Button variant="ghost" size="sm" onClick={() => { setProblem(null); setRule({ id: r.id, permissionA: r.permissionA, permissionB: r.permissionB, severity: r.severity, rationaleEn: r.rationale.en ?? "", rationaleAr: r.rationale.ar ?? "", isActive: r.isActive }); }}>
                        {t("common.edit")}
                      </Button>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </section>
        </div>
      ) : null}

      {tab === "keys" ? (
        <div className="flex flex-col gap-3" data-testid="api-keys">
          <p className="text-sm text-fg-muted">{t("security.keysHint")}</p>
          {created ? (
            <div className="flex flex-col gap-2 rounded-md border border-success/40 bg-success-soft p-3 text-sm" role="status" data-testid="created-key">
              <p className="font-medium">{t("security.keyCreated", { name: created.name })}</p>
              <div className="flex items-center gap-2">
                <code dir="ltr" className="break-all rounded-sm bg-surface px-2 py-1 font-mono text-xs" data-testid="created-key-value">{created.key}</code>
                <Button variant="ghost" size="icon" aria-label={t("security.copy")} onClick={() => { void navigator.clipboard.writeText(created.key); }}>
                  <Copy aria-hidden="true" />
                </Button>
              </div>
              <p className="text-fg-muted">{t("security.keyOnce")}</p>
            </div>
          ) : null}
          {(keys.data ?? []).length === 0 ? <p className="text-sm text-fg-muted">{t("security.noKeys")}</p> : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("security.keyName")}</TableHead>
                  <TableHead>{t("security.prefix")}</TableHead>
                  <TableHead>{t("security.scopes")}</TableHead>
                  <TableHead>{t("security.expires")}</TableHead>
                  <TableHead>{t("security.lastUsed")}</TableHead>
                  <TableHead>{t("common.status")}</TableHead>
                  <TableHead />
                </TableRow>
              </TableHeader>
              <TableBody>
                {(keys.data ?? []).map((k) => (
                  <TableRow key={k.id} data-testid="api-key-row">
                    <TableCell>{k.name}</TableCell>
                    <TableCell dir="ltr" className="font-mono text-xs">qk_….{k.prefix}…</TableCell>
                    <TableCell>{k.scopes.length === 0 ? <span className="text-fg-muted">{t("security.allOfCreator")}</span> : <span dir="ltr" className="font-mono text-xs">{k.scopes.join(", ")}</span>}</TableCell>
                    <TableCell>{k.expiresAt ? formatDateTime(k.expiresAt) : t("security.never")}</TableCell>
                    <TableCell>{k.lastUsedAt ? formatDateTime(k.lastUsedAt) : "—"}</TableCell>
                    <TableCell><DocStatus status={k.revokedAt ? "revoked" : "active"} /></TableCell>
                    <TableCell>
                      {k.revokedAt ? null : (
                        <Button variant="ghost" size="sm" onClick={() => { revokeKey.mutate(k.id); }} data-testid="revoke-key">
                          {t("security.revoke")}
                        </Button>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </div>
      ) : null}

      {tab === "sso" ? (
        <div className="flex flex-col gap-3" data-testid="sso">
          <p className="text-sm text-fg-muted">{t("security.ssoHint")}</p>
          {(connections.data ?? []).length === 0 ? <p className="text-sm text-fg-muted">{t("security.noSso")}</p> : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("security.ssoCode")}</TableHead>
                  <TableHead>{t("security.displayName")}</TableHead>
                  <TableHead>{t("security.authority")}</TableHead>
                  <TableHead>{t("security.emailDomains")}</TableHead>
                  <TableHead>{t("common.status")}</TableHead>
                  <TableHead />
                </TableRow>
              </TableHeader>
              <TableBody>
                {(connections.data ?? []).map((c) => (
                  <TableRow key={c.id} data-testid="sso-row">
                    <TableCell dir="ltr">{c.code}</TableCell>
                    <TableCell>{c.displayName}</TableCell>
                    <TableCell dir="ltr" className="font-mono text-xs">{c.authority}</TableCell>
                    <TableCell dir="ltr">{c.emailDomains.join(", ")}</TableCell>
                    <TableCell><DocStatus status={c.isActive ? "active" : "inactive"} /></TableCell>
                    <TableCell>
                      <Button variant="ghost" size="sm" onClick={() => { setProblem(null); setSso({ id: c.id, code: c.code, displayName: c.displayName, authority: c.authority, clientId: c.clientId, clientSecret: "", scopes: c.scopes, emailDomains: c.emailDomains.join(", "), jitProvisioning: c.jitProvisioning, groupClaim: c.groupClaim ?? "", groupRoleMap: c.groupRoleMap, isActive: c.isActive, hasSecret: c.hasClientSecret }); }}>
                        {t("common.edit")}
                      </Button>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </div>
      ) : null}

      {tab === "policy" && draft ? (
        <form onSubmit={submitPolicy} className="flex max-w-3xl flex-col gap-4" data-testid="policy">
          <p className="text-sm text-fg-muted">{t("security.policyHint")}</p>
          <div className="grid gap-4 sm:grid-cols-2">
            {policyNumbers.map(({ key: field, min, max }) => (
              <Field key={field} label={t(`security.policyFields.${field}`)} description={t(`security.policyHints.${field}`, { min, max })} error={problem?.code === "tenant.policy_invalid" ? problem.message : undefined}>
                <TextField type="number" min={min} max={max} value={String(draft[field])} onChange={(e) => { setPolicy({ ...draft, [field]: Number(e.target.value) }); }} required dir="ltr" data-testid={`policy-${field}`} />
              </Field>
            ))}
          </div>
          <label className="flex items-start gap-2 text-sm">
            <input type="checkbox" className="mt-1" checked={draft.mfaRequired} onChange={(e) => { setPolicy({ ...draft, mfaRequired: e.target.checked }); }} data-testid="policy-mfaRequired" />
            <span>
              <span className="font-medium">{t("security.policyFields.mfaRequired")}</span>
              <span className="block text-fg-muted">{t("security.policyHints.mfaRequired")}</span>
            </span>
          </label>
          <label className="flex items-start gap-2 text-sm">
            <input type="checkbox" className="mt-1" checked={draft.allowPasswordLogin} onChange={(e) => { setPolicy({ ...draft, allowPasswordLogin: e.target.checked }); }} disabled={draft.allowPasswordLogin && !activeSso} data-testid="policy-allowPasswordLogin" />
            <span>
              <span className="font-medium">{t("security.policyFields.allowPasswordLogin")}</span>
              <span className="block text-fg-muted">{activeSso ? t("security.policyHints.allowPasswordLogin") : t("security.policyHints.allowPasswordLoginNoSso")}</span>
            </span>
          </label>
          {draft.ipAllowlist && draft.ipAllowlist.length > 0 ? (
            <p className="text-sm">
              {t("security.policyFields.ipAllowlist")}: <span dir="ltr" className="font-mono text-xs">{draft.ipAllowlist.join(", ")}</span>
            </p>
          ) : null}
          <FormError message={problem && problem.code !== "tenant.policy_invalid" ? problem.message : null} />
          <div className="flex gap-2">
            <Button type="submit" loading={savePolicy.isPending} disabled={!policy} data-testid="save-policy">
              {t("common.save")}
            </Button>
            {policy ? (
              <Button type="button" variant="secondary" onClick={() => { setPolicy(null); setProblem(null); }}>
                {t("common.cancel")}
              </Button>
            ) : null}
          </div>
        </form>
      ) : null}

      <Dialog open={Boolean(rule)} onOpenChange={(isOpen) => { if (!isOpen) { setRule(null); } }}>
        <DialogContent closeLabel={t("common.close")}>
          {rule ? (
            <form onSubmit={submitRule} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{rule.id ? t("security.editRule") : t("security.newRule")}</DialogTitle>
              </DialogHeader>
              <p className="text-sm text-fg-muted">{t("security.ruleHint")}</p>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("security.permissionA")} required error={problem?.fields.permissionA}>
                  <TextField value={rule.permissionA} onChange={(e) => { setRule({ ...rule, permissionA: e.target.value }); }} list={permissionListId} required dir="ltr" data-testid="sod-permission-a" />
                </Field>
                <Field label={t("security.permissionB")} required error={problem?.fields.permissionB}>
                  <TextField value={rule.permissionB} onChange={(e) => { setRule({ ...rule, permissionB: e.target.value }); }} list={permissionListId} required dir="ltr" data-testid="sod-permission-b" />
                </Field>
                <Field label={t("security.severity")}>
                  <SelectField value={rule.severity} onChange={(e) => { setRule({ ...rule, severity: e.target.value }); }} data-testid="sod-severity">
                    <option value="warn">{t("security.severities.warn")}</option>
                    <option value="block">{t("security.severities.block")}</option>
                  </SelectField>
                </Field>
                <Field label={t("security.rationaleEn")} required error={problem?.fields.rationale}>
                  <TextField value={rule.rationaleEn} onChange={(e) => { setRule({ ...rule, rationaleEn: e.target.value }); }} required data-testid="sod-rationale-en" />
                </Field>
                <Field label={t("security.rationaleAr")}>
                  <TextField value={rule.rationaleAr} onChange={(e) => { setRule({ ...rule, rationaleAr: e.target.value }); }} dir="rtl" lang="ar" />
                </Field>
              </div>
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={rule.isActive} onChange={(e) => { setRule({ ...rule, isActive: e.target.checked }); }} />
                {t("common.active")}
              </label>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setRule(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={saveRule.isPending} data-testid="save-sod-rule">
                  {t("common.save")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(exception)} onOpenChange={(isOpen) => { if (!isOpen) { setException(null); } }}>
        <DialogContent closeLabel={t("common.close")}>
          {exception ? (
            <form onSubmit={submitException} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("security.exceptionTitle", { member: exception.member })}</DialogTitle>
              </DialogHeader>
              <p className="text-sm text-fg-muted">{t("security.exceptionHint")}</p>
              <FormError message={problem?.message ?? null} />
              <Field label={t("common.reason")} required>
                <TextField value={exception.reason} onChange={(e) => { setException({ ...exception, reason: e.target.value }); }} required data-testid="exception-reason" />
              </Field>
              <Field label={t("security.exceptionEnds")}>
                <TextField type="date" value={exception.expiresOn} onChange={(e) => { setException({ ...exception, expiresOn: e.target.value }); }} dir="ltr" data-testid="exception-ends" />
              </Field>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setException(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={grantException.isPending} data-testid="save-exception">
                  {t("security.grantException")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(key)} onOpenChange={(isOpen) => { if (!isOpen) { setKey(null); } }}>
        <DialogContent closeLabel={t("common.close")}>
          {key ? (
            <form onSubmit={submitKey} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("security.newKey")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("security.keyName")} required error={problem?.fields.name}>
                  <TextField value={key.name} onChange={(e) => { setKey({ ...key, name: e.target.value }); }} required data-testid="key-name" />
                </Field>
                <Field label={t("security.expires")}>
                  <TextField type="date" value={key.expiresAt} onChange={(e) => { setKey({ ...key, expiresAt: e.target.value }); }} dir="ltr" data-testid="key-expires" />
                </Field>
              </div>
              <Field label={t("security.scopes")} description={t("security.scopesHint")} error={problem?.fields.scopes}>
                <TextField value={key.scopes} onChange={(e) => { setKey({ ...key, scopes: e.target.value }); }} list={permissionListId} dir="ltr" data-testid="key-scopes" />
              </Field>
              <Field label={t("security.ipAllowlist")} description={t("security.ipAllowlistHint")} error={problem?.fields.ipAllowlist}>
                <TextField value={key.ipAllowlist} onChange={(e) => { setKey({ ...key, ipAllowlist: e.target.value }); }} dir="ltr" />
              </Field>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setKey(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={createKey.isPending} data-testid="create-key">
                  {t("security.createKey")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(sso)} onOpenChange={(isOpen) => { if (!isOpen) { setSso(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          {sso ? (
            <form onSubmit={submitSso} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{sso.id ? t("security.editSso") : t("security.newSso")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("security.ssoCode")} required error={problem?.fields.code}>
                  <TextField value={sso.code} onChange={(e) => { setSso({ ...sso, code: e.target.value.toLowerCase() }); }} required dir="ltr" data-testid="sso-code" />
                </Field>
                <Field label={t("security.displayName")} required error={problem?.fields.displayName}>
                  <TextField value={sso.displayName} onChange={(e) => { setSso({ ...sso, displayName: e.target.value }); }} required data-testid="sso-display-name" />
                </Field>
                <Field label={t("security.authority")} required description={t("security.authorityHint")} error={problem?.fields.authority}>
                  <TextField value={sso.authority} onChange={(e) => { setSso({ ...sso, authority: e.target.value }); }} required dir="ltr" data-testid="sso-authority" />
                </Field>
                <Field label={t("security.clientId")} required error={problem?.fields.clientId}>
                  <TextField value={sso.clientId} onChange={(e) => { setSso({ ...sso, clientId: e.target.value }); }} required dir="ltr" data-testid="sso-client-id" />
                </Field>
                <Field label={t("security.clientSecret")} description={sso.hasSecret ? t("security.secretKept") : undefined} error={problem?.fields.clientSecret}>
                  <TextField type="password" value={sso.clientSecret} onChange={(e) => { setSso({ ...sso, clientSecret: e.target.value }); }} autoComplete="new-password" dir="ltr" data-testid="sso-client-secret" />
                </Field>
                <Field label={t("security.oidcScopes")} error={problem?.fields.scopes}>
                  <TextField value={sso.scopes} onChange={(e) => { setSso({ ...sso, scopes: e.target.value }); }} dir="ltr" />
                </Field>
                <Field label={t("security.emailDomains")} description={t("security.emailDomainsHint")} error={problem?.fields.emailDomains}>
                  <TextField value={sso.emailDomains} onChange={(e) => { setSso({ ...sso, emailDomains: e.target.value }); }} dir="ltr" data-testid="sso-domains" />
                </Field>
                <Field label={t("security.groupClaim")}>
                  <TextField value={sso.groupClaim} onChange={(e) => { setSso({ ...sso, groupClaim: e.target.value }); }} dir="ltr" />
                </Field>
              </div>
              <div className="flex flex-wrap gap-4 text-sm">
                <label className="flex items-center gap-2">
                  <input type="checkbox" checked={sso.jitProvisioning} onChange={(e) => { setSso({ ...sso, jitProvisioning: e.target.checked }); }} />
                  {t("security.jit")}
                </label>
                <label className="flex items-center gap-2">
                  <input type="checkbox" checked={sso.isActive} onChange={(e) => { setSso({ ...sso, isActive: e.target.checked }); }} />
                  {t("common.active")}
                </label>
              </div>
              <DialogFooter>
                {sso.id ? (
                  <Button type="button" variant="ghost" className="me-auto text-danger" onClick={() => { if (sso.id) { deleteSso.mutate(sso.id); } }} loading={deleteSso.isPending} data-testid="delete-sso">
                    <Trash2 aria-hidden="true" />
                    {t("security.deleteSso")}
                  </Button>
                ) : null}
                <Button type="button" variant="secondary" onClick={() => { setSso(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={saveSso.isPending} data-testid="save-sso">
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
