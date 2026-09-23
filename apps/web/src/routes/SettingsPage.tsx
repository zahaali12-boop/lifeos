import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";
import { formatDateTime } from "../lib/format";
import { toFormProblem, type FormProblem } from "../lib/problem";
import { CompanySelect, useCompanies, useCompanySelection } from "./accounting/shared";
import { FormError, PageHeader, SelectField, TextField } from "./common";
import { Tabs } from "./inventory/shared";

type Setting = components["schemas"]["SettingSummary"];

const valueTypes = ["string", "number", "boolean", "json"];

/** The settings the modules read today, shown as plain choices; anything else is edited in the advanced table. */
const knownSettings = [
  { key: "accounting.journals.approval", values: ["", "required"] },
  { key: "inventory.adjustments.approval", values: ["", "required"] },
];

function display(setting: Setting): string {
  const value = setting.value;
  if (setting.valueType === "json") {
    return JSON.stringify(value);
  }
  return typeof value === "string" || typeof value === "number" || typeof value === "boolean" ? String(value) : JSON.stringify(value);
}

function parse(value: string, valueType: string): unknown {
  switch (valueType) {
    case "number":
      return Number(value);
    case "boolean":
      return value === "true";
    case "json":
      return JSON.parse(value) as unknown;
    default:
      return value;
  }
}

/**
 * Settings (roadmap 1.8): per-company and workspace-wide switches the modules read (approval for manual journals and
 * stock adjustments today), and the typed key-value store behind them for anything else an administrator configures.
 */
export function SettingsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const companies = useCompanies();
  const [companyId, setCompanyId] = useCompanySelection(companies.data);
  const [tab, setTab] = useState("company");
  const [editing, setEditing] = useState<{ key: string; valueType: string; value: string; isNew: boolean } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const scope = tab === "company" ? companyId : null;

  const settings = useQuery({
    queryKey: ["settings", scope],
    enabled: tab === "workspace" || Boolean(companyId),
    queryFn: async () =>
      scope
        ? unwrap(await api.GET("/api/v1/organization/companies/{companyId}/settings", { params: { path: { companyId: scope } } }))
        : unwrap(await api.GET("/api/v1/organization/settings")),
  });
  const save = useMutation({
    mutationFn: async (input: { key: string; valueType: string; value: unknown }) =>
      scope
        ? unwrap(await api.PUT("/api/v1/organization/companies/{companyId}/settings/{key}", { params: { path: { companyId: scope, key: input.key } }, body: { value: input.value, valueType: input.valueType } }))
        : unwrap(await api.PUT("/api/v1/organization/settings/{key}", { params: { path: { key: input.key } }, body: { value: input.value, valueType: input.valueType } })),
    onSuccess: async () => { setEditing(null); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["settings"] }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const current = (key: string) => settings.data?.find((s) => s.key === key);
  const submit = (event: FormEvent): void => {
    event.preventDefault();
    if (!editing) {
      return;
    }
    try {
      save.mutate({ key: editing.key.trim(), valueType: editing.valueType, value: parse(editing.value, editing.valueType) });
    } catch {
      setProblem({ message: t("settings.invalidJson"), fields: {} });
    }
  };

  return (
    <>
      <PageHeader
        title={t("nav.settings")}
        description={t("settings.description")}
        actions={
          <Button onClick={() => { setProblem(null); setEditing({ key: "", valueType: "string", value: "", isNew: true }); }} disabled={tab === "company" && !companyId} data-testid="new-setting">
            <Plus aria-hidden="true" />
            {t("settings.new")}
          </Button>
        }
      />
      <Tabs
        value={tab}
        onChange={(next) => { setTab(next); setProblem(null); }}
        tabs={[
          { id: "company", label: t("settings.company"), testId: "tab-company-settings" },
          { id: "workspace", label: t("settings.workspace"), testId: "tab-workspace-settings" },
        ]}
      />
      {tab === "company" ? (
        <div className="mb-4 grid gap-3 sm:grid-cols-3">
          <CompanySelect companies={companies.data ?? []} value={companyId} onChange={setCompanyId} />
        </div>
      ) : null}
      <FormError message={problem && !editing ? problem.message : null} />

      {tab === "company" ? (
        <section className="mb-6 flex flex-col gap-3 rounded-md border border-border p-4" data-testid="known-settings">
          <h2 className="text-base font-semibold">{t("settings.approvals")}</h2>
          <div className="grid gap-4 sm:grid-cols-2">
            {knownSettings.map(({ key, values }) => {
              const setting = current(key);
              const value = typeof setting?.value === "string" ? setting.value : "";
              return (
                <Field key={key} label={t(`settings.known.${key.replace(/\./g, "_")}`)} description={t(`settings.knownHints.${key.replace(/\./g, "_")}`)}>
                  <SelectField value={value} onChange={(e) => { save.mutate({ key, valueType: "string", value: e.target.value }); }} data-testid={`setting-${key}`}>
                    {values.map((v) => (
                      <option key={v} value={v}>
                        {t(`settings.approvalValues.${v || "none"}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
              );
            })}
          </div>
        </section>
      ) : null}

      <section className="flex flex-col gap-3" data-testid="all-settings">
        <h2 className="text-base font-semibold">{t("settings.all")}</h2>
        {(settings.data ?? []).length === 0 ? <p className="text-sm text-fg-muted">{t("settings.none")}</p> : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("settings.key")}</TableHead>
                <TableHead>{t("settings.value")}</TableHead>
                <TableHead>{t("settings.type")}</TableHead>
                <TableHead>{t("settings.updated")}</TableHead>
                <TableHead />
              </TableRow>
            </TableHeader>
            <TableBody>
              {(settings.data ?? []).map((s) => (
                <TableRow key={s.id} data-testid="setting-row">
                  <TableCell dir="ltr" className="font-mono text-xs">{s.key}</TableCell>
                  <TableCell dir="ltr" className="font-mono text-xs">{display(s)}</TableCell>
                  <TableCell>{t(`settings.types.${s.valueType}`, { defaultValue: s.valueType })}</TableCell>
                  <TableCell>{formatDateTime(s.updatedAt)}</TableCell>
                  <TableCell>
                    <Button variant="ghost" size="sm" onClick={() => { setProblem(null); setEditing({ key: s.key, valueType: s.valueType, value: display(s), isNew: false }); }}>
                      {t("common.edit")}
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </section>

      <Dialog open={Boolean(editing)} onOpenChange={(isOpen) => { if (!isOpen) { setEditing(null); } }}>
        <DialogContent closeLabel={t("common.close")}>
          {editing ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{editing.isNew ? t("settings.new") : t("settings.edit")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("settings.key")} required description={t("settings.keyHint")}>
                  <TextField value={editing.key} onChange={(e) => { setEditing({ ...editing, key: e.target.value }); }} required disabled={!editing.isNew} dir="ltr" data-testid="setting-key" />
                </Field>
                <Field label={t("settings.type")}>
                  <SelectField value={editing.valueType} onChange={(e) => { setEditing({ ...editing, valueType: e.target.value }); }} data-testid="setting-type">
                    {valueTypes.map((v) => (
                      <option key={v} value={v}>
                        {t(`settings.types.${v}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
              </div>
              <Field label={t("settings.value")} required>
                {editing.valueType === "boolean" ? (
                  <SelectField value={editing.value || "false"} onChange={(e) => { setEditing({ ...editing, value: e.target.value }); }} data-testid="setting-value">
                    <option value="true">{t("common.yes")}</option>
                    <option value="false">{t("common.no")}</option>
                  </SelectField>
                ) : (
                  <TextField value={editing.value} onChange={(e) => { setEditing({ ...editing, value: e.target.value }); }} inputMode={editing.valueType === "number" ? "decimal" : undefined} dir="ltr" data-testid="setting-value" />
                )}
              </Field>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setEditing(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={save.isPending} data-testid="save-setting">
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
