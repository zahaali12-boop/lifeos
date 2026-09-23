import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";
import { DataGrid } from "../grid/DataGrid";
import { formatDateTime } from "../lib/format";
import { toFormProblem, type FormProblem } from "../lib/problem";
import { FormError, PageHeader, SelectField, TextField } from "./common";
import { CompanyFilter, DocStatus, KeyValues, useCompanyContext } from "./inventory/shared";

type Series = components["schemas"]["SeriesSummary"];

/** The document types the modules number today; an admin may also type another (a series for a type no module uses is simply never picked). */
const documentTypes = [
  "journal_entry", "manual_journal",
  "purchase_requisition", "purchase_rfq", "purchase_agreement", "purchase_order", "purchase_receipt", "purchase_invoice", "landed_cost_document", "purchase_return",
  "payment_proposal", "bank_payment",
  "stock_adjustment", "stock_transfer", "stock_count", "stock_revaluation", "stock_assembly",
];
const resetPolicies = ["never", "yearly", "monthly"];

const emptyForm = { code: "", documentType: "purchase_order", template: "", startNumber: "1", resetPolicy: "yearly", gapless: true, isDefault: false, isActive: true };

/** Numbering series (ADR-0016, roadmap 1.6): which series numbers which documents of a company, its template and counters, and the gapless audit. */
export function NumberingPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [documentType, setDocumentType] = useState("");
  const [editing, setEditing] = useState<Series | "new" | null>(null);
  const [form, setForm] = useState(emptyForm);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);
  const [counter, setCounter] = useState({ periodKey: "", nextNumber: "", reason: "" });
  const [counterProblem, setCounterProblem] = useState<FormProblem | null>(null);
  const typeLabel = (type: string) => t(`numbering.documentTypes.${type}`, { defaultValue: type });

  const series = useQuery({
    queryKey: ["numbering-series", companyId, documentType],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/numbering/series", { params: { query: { companyId, ...(documentType ? { documentType } : {}) } } })),
  });
  const detail = useQuery({
    queryKey: ["numbering-series", "detail", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/numbering/series/{seriesId}", { params: { path: { seriesId: openId ?? "" } } })),
  });
  const gaps = useQuery({
    queryKey: ["numbering-series", "gaps", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/numbering/series/{seriesId}/gaps", { params: { path: { seriesId: openId ?? "" } } })),
  });
  const allocations = useQuery({
    queryKey: ["numbering-series", "allocations", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/numbering/series/{seriesId}/allocations", { params: { path: { seriesId: openId ?? "" }, query: { limit: 20 } } })),
  });

  const save = useMutation({
    mutationFn: async () => {
      const body = { code: form.code, documentType: form.documentType, companyId, template: form.template, startNumber: Number(form.startNumber), resetPolicy: form.resetPolicy, gapless: form.gapless, isDefault: form.isDefault, isActive: form.isActive };
      return editing && editing !== "new"
        ? unwrap(await api.PUT("/api/v1/numbering/series/{seriesId}", { params: { path: { seriesId: editing.id } }, body }))
        : unwrap(await api.POST("/api/v1/numbering/series", { body }));
    },
    onSuccess: async (saved) => {
      setEditing(null);
      setProblem(null);
      setOpenId(saved.id);
      await queryClient.invalidateQueries({ queryKey: ["numbering-series"] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const moveCounter = useMutation({
    mutationFn: async () => unwrap(await api.PUT("/api/v1/numbering/series/{seriesId}/counter", { params: { path: { seriesId: openId ?? "" } }, body: { periodKey: counter.periodKey, nextNumber: Number(counter.nextNumber), reason: counter.reason || null } })),
    onSuccess: async () => {
      setCounter({ periodKey: "", nextNumber: "", reason: "" });
      setCounterProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["numbering-series"] });
    },
    onError: (error) => { setCounterProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<Series, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("numbering.code"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "type", accessorKey: "documentType", header: t("numbering.documentType"), size: 190, cell: ({ row }) => t(`numbering.documentTypes.${row.original.documentType}`, { defaultValue: row.original.documentType }) },
      { id: "template", accessorKey: "template", header: t("numbering.template"), size: 200, cell: ({ row }) => <span dir="ltr" className="font-mono text-xs">{row.original.template}</span> },
      { id: "reset", accessorKey: "resetPolicy", header: t("numbering.resetPolicy"), size: 110, cell: ({ row }) => t(`numbering.resetPolicies.${row.original.resetPolicy}`) },
      { id: "issued", accessorFn: (row) => row.counters.reduce((sum, c) => sum + Number(c.allocated), 0), header: t("numbering.issued"), size: 100 },
      { id: "default", accessorKey: "isDefault", header: t("numbering.default"), size: 90, cell: ({ row }) => (row.original.isDefault ? t("common.yes") : "") },
      { id: "status", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => <DocStatus status={row.original.isActive ? "active" : "inactive"} /> },
    ],
    [t],
  );

  const startEdit = (target: Series | "new"): void => {
    setProblem(null);
    setForm(target === "new" ? emptyForm : { code: target.code, documentType: target.documentType, template: target.template, startNumber: String(target.startNumber), resetPolicy: target.resetPolicy, gapless: target.gapless, isDefault: target.isDefault, isActive: target.isActive });
    setEditing(target);
  };
  const submit = (event: FormEvent): void => {
    event.preventDefault();
    save.mutate();
  };
  const submitCounter = (event: FormEvent): void => {
    event.preventDefault();
    moveCounter.mutate();
  };
  const opened = detail.data;

  return (
    <>
      <PageHeader
        title={t("nav.numbering")}
        description={t("numbering.description")}
        actions={
          <Button onClick={() => { startEdit("new"); }} disabled={!companyId} data-testid="new-series">
            <Plus aria-hidden="true" />
            {t("numbering.new")}
          </Button>
        }
      />
      <div className="mb-4 grid gap-3 sm:grid-cols-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("numbering.documentType")}>
          <SelectField value={documentType} onChange={(e) => { setDocumentType(e.target.value); }} data-testid="series-type-filter">
            <option value="">{t("numbering.allTypes")}</option>
            {documentTypes.map((type) => (
              <option key={type} value={type}>
                {typeLabel(type)}
              </option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<Series> label="nav.numbering" columns={columns} data={series.data ?? []} rowKey={(row) => row.id} loading={series.isPending && Boolean(companyId)} onOpen={(row) => { setOpenId(row.id); }} emptyTitle={t("numbering.emptyTitle")} emptyDescription={t("numbering.emptyDescription")} />

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setCounterProblem(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold" dir="auto">
              {opened ? `${opened.code} · ${typeLabel(opened.documentType)}` : t("common.loading")}
            </DialogTitle>
          </DialogHeader>
          {opened ? (
            <div className="flex flex-col gap-4" data-testid="series-detail">
              <KeyValues
                entries={[
                  [t("numbering.template"), <span key="t" dir="ltr" className="font-mono text-xs">{opened.template}</span>],
                  [t("numbering.startNumber"), String(opened.startNumber)],
                  [t("numbering.resetPolicy"), t(`numbering.resetPolicies.${opened.resetPolicy}`)],
                  [t("numbering.gapless"), opened.gapless ? t("common.yes") : t("common.no")],
                  [t("numbering.default"), opened.isDefault ? t("common.yes") : t("common.no")],
                ]}
              />
              <section>
                <h3 className="mb-1 text-sm font-semibold">{t("numbering.counters")}</h3>
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("numbering.periodKey")}</TableHead>
                      <TableHead className="text-end">{t("numbering.nextNumber")}</TableHead>
                      <TableHead className="text-end">{t("numbering.issued")}</TableHead>
                      <TableHead>{t("numbering.gaps")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {opened.counters.map((c) => {
                      const period = gaps.data?.periods.find((p) => p.periodKey === c.periodKey);
                      return (
                        <TableRow key={c.periodKey} data-testid="counter-row">
                          <TableCell dir="ltr">{c.periodKey || t("numbering.noPeriod")}</TableCell>
                          <TableNumberCell>{String(c.nextNumber)}</TableNumberCell>
                          <TableNumberCell>{String(c.allocated)}</TableNumberCell>
                          <TableCell data-testid="counter-gaps">{period && period.missing.length > 0 ? <span className="text-danger">{period.missing.join(", ")}</span> : t("numbering.noGaps")}</TableCell>
                        </TableRow>
                      );
                    })}
                  </TableBody>
                </Table>
                {opened.counters.length === 0 ? <p className="mt-2 text-sm text-fg-muted">{t("numbering.noCounters")}</p> : null}
              </section>
              <form onSubmit={submitCounter} className="flex flex-col gap-3 rounded-md border border-border p-3">
                <h3 className="text-sm font-semibold">{t("numbering.moveCounter")}</h3>
                <p className="text-sm text-fg-muted">{t("numbering.moveCounterHint")}</p>
                <FormError message={counterProblem?.message ?? null} />
                <div className="grid gap-3 sm:grid-cols-3">
                  <Field label={t("numbering.periodKey")}>
                    <TextField value={counter.periodKey} onChange={(e) => { setCounter({ ...counter, periodKey: e.target.value }); }} dir="ltr" placeholder={opened.counters[0]?.periodKey ?? ""} data-testid="counter-period" />
                  </Field>
                  <Field label={t("numbering.nextNumber")} required>
                    <TextField inputMode="numeric" value={counter.nextNumber} onChange={(e) => { setCounter({ ...counter, nextNumber: e.target.value }); }} required dir="ltr" data-testid="counter-next" />
                  </Field>
                  <Field label={t("common.reason")}>
                    <TextField value={counter.reason} onChange={(e) => { setCounter({ ...counter, reason: e.target.value }); }} data-testid="counter-reason" />
                  </Field>
                </div>
                <div>
                  <Button type="submit" variant="secondary" loading={moveCounter.isPending} data-testid="save-counter">
                    {t("numbering.saveCounter")}
                  </Button>
                </div>
              </form>
              <section>
                <h3 className="mb-1 text-sm font-semibold">{t("numbering.recent")}</h3>
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("numbering.number")}</TableHead>
                      <TableHead>{t("numbering.documentType")}</TableHead>
                      <TableHead>{t("numbering.allocatedAt")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {(allocations.data ?? []).map((a) => (
                      <TableRow key={a.id}>
                        <TableCell dir="ltr">{a.text}</TableCell>
                        <TableCell>{typeLabel(a.documentType)}</TableCell>
                        <TableCell>{formatDateTime(a.allocatedAt)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </section>
              <DialogFooter>
                <Button variant="secondary" onClick={() => { startEdit(opened); setOpenId(null); }} data-testid="edit-series">
                  {t("common.edit")}
                </Button>
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={editing !== null} onOpenChange={(isOpen) => { if (!isOpen) { setEditing(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          <form onSubmit={submit} className="flex flex-col gap-4">
            <DialogHeader>
              <DialogTitle className="text-lg font-semibold">{editing === "new" ? t("numbering.new") : t("numbering.edit")}</DialogTitle>
            </DialogHeader>
            <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label={t("numbering.code")} required error={problem?.fields.code}>
                <TextField value={form.code} onChange={(e) => { setForm({ ...form, code: e.target.value.toUpperCase() }); }} required dir="ltr" data-testid="series-code" />
              </Field>
              <Field label={t("numbering.documentType")} required error={problem?.fields.documentType}>
                <SelectField value={form.documentType} onChange={(e) => { setForm({ ...form, documentType: e.target.value }); }} data-testid="series-type">
                  {documentTypes.map((type) => (
                    <option key={type} value={type}>
                      {typeLabel(type)}
                    </option>
                  ))}
                </SelectField>
              </Field>
              <Field label={t("numbering.template")} required error={problem?.fields.template} description={t("numbering.templateHint")}>
                <TextField value={form.template} onChange={(e) => { setForm({ ...form, template: e.target.value }); }} required dir="ltr" placeholder="PO-{company}-{yyyy}-{seq:5}" data-testid="series-template" />
              </Field>
              <Field label={t("numbering.startNumber")} error={problem?.fields.startNumber}>
                <TextField inputMode="numeric" value={form.startNumber} onChange={(e) => { setForm({ ...form, startNumber: e.target.value }); }} dir="ltr" />
              </Field>
              <Field label={t("numbering.resetPolicy")} error={problem?.fields.resetPolicy}>
                <SelectField value={form.resetPolicy} onChange={(e) => { setForm({ ...form, resetPolicy: e.target.value }); }}>
                  {resetPolicies.map((policy) => (
                    <option key={policy} value={policy}>
                      {t(`numbering.resetPolicies.${policy}`)}
                    </option>
                  ))}
                </SelectField>
              </Field>
              <div className="flex flex-col gap-2 self-end pb-1 text-sm">
                <label className="flex items-center gap-2">
                  <input type="checkbox" checked={form.gapless} onChange={(e) => { setForm({ ...form, gapless: e.target.checked }); }} />
                  {t("numbering.gapless")}
                </label>
                <label className="flex items-center gap-2">
                  <input type="checkbox" checked={form.isDefault} onChange={(e) => { setForm({ ...form, isDefault: e.target.checked }); }} data-testid="series-default" />
                  {t("numbering.default")}
                </label>
                <label className="flex items-center gap-2">
                  <input type="checkbox" checked={form.isActive} onChange={(e) => { setForm({ ...form, isActive: e.target.checked }); }} />
                  {t("numbering.active")}
                </label>
              </div>
            </div>
            <DialogFooter>
              <Button type="button" variant="secondary" onClick={() => { setEditing(null); }}>
                {t("common.cancel")}
              </Button>
              <Button type="submit" loading={save.isPending} data-testid="save-series">
                {t("common.save")}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </>
  );
}
