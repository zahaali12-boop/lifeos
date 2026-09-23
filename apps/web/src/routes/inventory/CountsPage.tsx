import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Amount, today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, DocStatus, Qty, WarehouseSelect, useCompanyContext, useReasonCodes, useWarehouses } from "./shared";

type Count = components["schemas"]["CountSummary"];

interface CountForm {
  warehouseId: string;
  scope: string;
  postingDate: string;
  blind: boolean;
  blockMovements: boolean;
  notes: string;
}

const statuses = ["", "planned", "frozen", "counting", "review", "approved", "posted", "cancelled"];
const scopes = ["full", "cycle", "bins", "items"];

/** Counts (roadmap 3.6): plan, freeze, count (here or on the scanner), recount, review the variances with reasons, approve and post. */
export function CountsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const search = useSearch({ strict: false });
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const warehouses = useWarehouses(companyId);
  const reasons = useReasonCodes("count");
  const [status, setStatus] = useState("");
  const [editing, setEditing] = useState<CountForm | null>(null);
  const [entries, setEntries] = useState<Record<string, string>>({});
  const [lineReasons, setLineReasons] = useState<Record<string, string>>({});
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const openId = search.open;

  const counts = useQuery({
    queryKey: ["counts", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/counts", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const sheet = useQuery({
    queryKey: ["count-sheet", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/counts/{countId}/sheet", { params: { path: { countId: openId ?? "" } } })),
  });
  const refresh = async (id?: string | null): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["counts"] });
    if (id) {
      await queryClient.invalidateQueries({ queryKey: ["count-sheet", id] });
    }
  };
  const open = (id: string | null): void => { setEntries({}); void navigate({ to: "/inventory/counts", search: id ? { open: id } : {} }); };

  const save = useMutation({
    mutationFn: async (f: CountForm) => unwrap(await api.POST("/api/v1/inventory/counts", { body: { companyId, warehouseId: f.warehouseId, scope: f.scope, postingDate: f.postingDate || null, blind: f.blind, blockMovements: f.blockMovements, notes: f.notes || null } })),
    onSuccess: async (saved) => {
      setEditing(null);
      setProblem(null);
      await refresh(saved.id);
      open(saved.id);
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const act = useMutation({
    mutationFn: async (action: "freeze" | "review" | "approve" | "post" | "cancel") => {
      const countId = openId ?? "";
      switch (action) {
        case "freeze":
          return unwrap(await api.POST("/api/v1/inventory/counts/{countId}/freeze", { params: { path: { countId } } }));
        case "review":
          return unwrap(await api.POST("/api/v1/inventory/counts/{countId}/review", { params: { path: { countId } } }));
        case "approve":
          return unwrap(await api.POST("/api/v1/inventory/counts/{countId}/approve", { params: { path: { countId } } }));
        case "post":
          return unwrap(await api.POST("/api/v1/inventory/counts/{countId}/post", { params: { path: { countId } } }));
        case "cancel":
          return unwrap(await api.POST("/api/v1/inventory/counts/{countId}/cancel", { params: { path: { countId } } }));
      }
    },
    onSuccess: async () => {
      setProblem(null);
      await refresh(openId);
      await queryClient.invalidateQueries({ queryKey: ["stock"] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const enter = useMutation({
    mutationFn: async () => {
      const lines = sheet.data?.lines ?? [];
      const body = { entries: Object.entries(entries).filter(([, v]) => v.trim() !== "").map(([lineId, countedQty]) => ({ lineId, countedQty, itemId: lines.find((l) => l.id === lineId)?.itemId ?? null })) };
      return unwrap(await api.POST("/api/v1/inventory/counts/{countId}/entries", { params: { path: { countId: openId ?? "" } }, body }));
    },
    onSuccess: async () => {
      setProblem(null);
      setEntries({});
      await refresh(openId);
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const recount = useMutation({
    mutationFn: async (lineId: string) => unwrap(await api.POST("/api/v1/inventory/counts/{countId}/lines/{lineId}/recount", { params: { path: { countId: openId ?? "", lineId } } })),
    onSuccess: async () => { await refresh(openId); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const setReason = useMutation({
    mutationFn: async (input: { lineId: string; reasonCode: string }) => unwrap(await api.PUT("/api/v1/inventory/counts/{countId}/lines/{lineId}/reason", { params: { path: { countId: openId ?? "", lineId: input.lineId } }, body: { reasonCode: input.reasonCode || null } })),
    onSuccess: async () => { await refresh(openId); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<Count, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("accounting.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "warehouse", accessorKey: "warehouseCode", header: t("inventory.warehouse"), size: 120 },
      { id: "scope", accessorKey: "scope", header: t("inventory.counts.scope"), size: 100, cell: ({ row }) => t(`inventory.counts.scopes.${row.original.scope}`, { defaultValue: row.original.scope }) },
      { id: "date", accessorKey: "postingDate", header: t("accounting.date"), size: 110, cell: ({ row }) => formatDate(row.original.postingDate) },
      { id: "lines", accessorKey: "lineCount", header: t("inventory.lines"), size: 80, cell: ({ row }) => `${String(row.original.countedLines)}/${String(row.original.lineCount)}` },
      { id: "variances", accessorKey: "varianceLines", header: t("inventory.counts.variances"), size: 100, cell: ({ row }) => String(row.original.varianceLines) },
      { id: "value", accessorKey: "varianceValue", header: t("inventory.counts.varianceValue"), size: 140, cell: ({ row }) => <Amount value={row.original.varianceValue} /> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => <DocStatus status={row.original.status} /> },
    ],
    [t],
  );

  const detail = sheet.data;
  const count = detail?.count;
  const counting = count?.status === "frozen" || count?.status === "counting";
  const setForm = (patch: Partial<CountForm>): void => { setEditing((prev) => (prev ? { ...prev, ...patch } : prev)); };
  const submitForm = (event: FormEvent): void => {
    event.preventDefault();
    if (editing) {
      save.mutate(editing);
    }
  };

  return (
    <>
      <PageHeader
        title={t("nav.counts")}
        description={t("inventory.counts.description")}
        actions={
          <Button onClick={() => { setProblem(null); setEditing({ warehouseId: warehouses.data?.[0]?.id ?? "", scope: "full", postingDate: today(), blind: false, blockMovements: false, notes: "" }); }} disabled={!companyId} data-testid="new-count">
            <Plus aria-hidden="true" />
            {t("inventory.counts.new")}
          </Button>
        }
      />
      <div className="mb-4 grid gap-3 sm:grid-cols-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }}>
            {statuses.map((s) => (
              <option key={s} value={s}>
                {s ? t(`inventory.statuses.${s}`) : t("accounting.anyStatus")}
              </option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<Count> label="nav.counts" columns={columns} data={counts.data ?? []} rowKey={(row) => row.id} loading={counts.isPending && Boolean(companyId)} emptyTitle={t("inventory.counts.emptyTitle")} emptyDescription={t("inventory.counts.emptyDescription")} onOpen={(row) => { open(row.id); }} />

      <Dialog open={Boolean(openId) && !editing} onOpenChange={(isOpen) => { if (!isOpen) { open(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold" dir="auto">
              {count ? `${count.number} · ${count.warehouseCode}` : t("common.loading")}
            </DialogTitle>
          </DialogHeader>
          {detail && count ? (
            <div className="flex flex-col gap-4" data-testid="count-detail">
              <div className="flex flex-wrap items-center gap-2 text-sm">
                <DocStatus status={count.status} />
                <span>{t(`inventory.counts.scopes.${count.scope}`, { defaultValue: count.scope })}</span>
                {count.blind ? <span className="text-fg-muted">{t("inventory.counts.blind")}</span> : null}
                {count.blockMovements ? <span className="text-fg-muted">{t("inventory.counts.blockMovements")}</span> : null}
                {count.frozenAt ? <span className="text-fg-muted">{t("inventory.counts.frozenAt", { when: formatDate(count.frozenAt) })}</span> : null}
                <span className="text-fg-muted">
                  {t("inventory.counts.varianceValue")}: <Amount value={count.varianceValue} />
                </span>
              </div>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>#</TableHead>
                    <TableHead>{t("inventory.item")}</TableHead>
                    <TableHead>{t("inventory.warehouses.bin")}</TableHead>
                    <TableHead>{t("inventory.stock.lot")}</TableHead>
                    <TableHead className="text-end">{t("inventory.counts.expected")}</TableHead>
                    <TableHead className="text-end">{t("inventory.counts.counted")}</TableHead>
                    <TableHead className="text-end">{t("inventory.counts.variance")}</TableHead>
                    <TableHead>{t("inventory.reason")}</TableHead>
                    <TableHead />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {detail.lines.map((line) => (
                    <TableRow key={line.id} data-testid="count-line">
                      <TableCell>{String(line.lineNo)}</TableCell>
                      <TableCell>
                        <span dir="ltr">{line.itemCode}</span> {localized(line.itemName)}
                        {line.serialNumber ? <span className="text-fg-muted"> · {line.serialNumber}</span> : null}
                      </TableCell>
                      <TableCell>{line.binCode ?? ""}</TableCell>
                      <TableCell>{line.lotNumber ?? ""}</TableCell>
                      <TableNumberCell>{count.blind && counting ? "•••" : <Qty value={line.expectedQty} uom={line.baseUom} />}</TableNumberCell>
                      <TableNumberCell>
                        {counting ? (
                          <TextField aria-label={t("inventory.counts.counted")} inputMode="decimal" value={entries[line.id] ?? (line.countedQty === null ? "" : String(line.countedQty))} onChange={(e) => { setEntries({ ...entries, [line.id]: e.target.value }); }} dir="ltr" className="w-24 text-end" data-testid={`counted-${line.lineNo}`} />
                        ) : (
                          <Qty value={line.countedQty} />
                        )}
                      </TableNumberCell>
                      <TableNumberCell>
                        <Qty value={line.varianceQty} />
                        {line.recountRequested ? <span className="ms-1 text-warning">↻</span> : null}
                      </TableNumberCell>
                      <TableCell>
                        {count.status === "review" && Number(line.varianceQty) !== 0 ? (
                          <SelectField aria-label={t("inventory.reason")} value={lineReasons[line.id] ?? line.reasonCode ?? ""} onChange={(e) => { setLineReasons({ ...lineReasons, [line.id]: e.target.value }); setReason.mutate({ lineId: line.id, reasonCode: e.target.value }); }} data-testid={`reason-${line.lineNo}`}>
                            <option value="">—</option>
                            {(reasons.data ?? []).map((r) => (
                              <option key={r.id} value={r.code}>
                                {r.code}
                              </option>
                            ))}
                          </SelectField>
                        ) : (
                          line.reasonCode ?? ""
                        )}
                      </TableCell>
                      <TableCell>
                        {count.status === "review" || counting ? (
                          <Button type="button" variant="ghost" size="sm" onClick={() => { recount.mutate(line.id); }}>
                            {t("inventory.counts.recount")}
                          </Button>
                        ) : null}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <FormError message={problem?.message ?? null} />
              <DialogFooter>
                {count.status === "planned" ? (
                  <Button onClick={() => { act.mutate("freeze"); }} loading={act.isPending} data-testid="freeze-count">
                    {t("inventory.counts.freeze")}
                  </Button>
                ) : null}
                {counting ? (
                  <>
                    <Button variant="secondary" onClick={() => { enter.mutate(); }} loading={enter.isPending} disabled={Object.keys(entries).length === 0} data-testid="save-entries">
                      {t("inventory.counts.saveEntries")}
                    </Button>
                    <Button onClick={() => { act.mutate("review"); }} loading={act.isPending} data-testid="review-count">
                      {t("inventory.counts.review")}
                    </Button>
                  </>
                ) : null}
                {count.status === "review" ? (
                  <Button onClick={() => { act.mutate("approve"); }} loading={act.isPending} data-testid="approve-count">
                    {t("accounting.approve")}
                  </Button>
                ) : null}
                {count.status === "approved" ? (
                  <Button onClick={() => { act.mutate("post"); }} loading={act.isPending} data-testid="post-count">
                    {t("accounting.post")}
                  </Button>
                ) : null}
                {count.status !== "posted" && count.status !== "cancelled" ? (
                  <Button variant="secondary" onClick={() => { act.mutate("cancel"); }} loading={act.isPending}>
                    {t("common.cancel")}
                  </Button>
                ) : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(editing)} onOpenChange={(isOpen) => { if (!isOpen) { setEditing(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          {editing ? (
            <form onSubmit={submitForm} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("inventory.counts.new")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <WarehouseSelect warehouses={warehouses.data ?? []} value={editing.warehouseId} onChange={(id) => { setForm({ warehouseId: id }); }} required testId="count-warehouse" />
                <Field label={t("inventory.counts.scope")}>
                  <SelectField value={editing.scope} onChange={(e) => { setForm({ scope: e.target.value }); }}>
                    {scopes.map((s) => (
                      <option key={s} value={s}>
                        {t(`inventory.counts.scopes.${s}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("accounting.postingDate")}>
                  <TextField type="date" value={editing.postingDate} onChange={(e) => { setForm({ postingDate: e.target.value }); }} dir="ltr" />
                </Field>
                <Field label={t("inventory.counts.notes")}>
                  <TextField value={editing.notes} onChange={(e) => { setForm({ notes: e.target.value }); }} />
                </Field>
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={editing.blind} onChange={(e) => { setForm({ blind: e.target.checked }); }} />
                  {t("inventory.counts.blind")}
                </label>
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={editing.blockMovements} onChange={(e) => { setForm({ blockMovements: e.target.checked }); }} />
                  {t("inventory.counts.blockMovements")}
                </label>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setEditing(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={save.isPending} data-testid="save-count">
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
