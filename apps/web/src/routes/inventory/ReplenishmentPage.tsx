import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { Play } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatDateTime, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, DocStatus, KeyValues, Qty, WarehouseSelect, plain, useCompanyContext, useWarehouses } from "./shared";

type Suggestion = components["schemas"]["ReplenishmentSuggestionSummary"];

const statuses = ["open", "accepted", "dismissed", "superseded", "all"];

/** Replenishment (roadmap 3.7): run the planner, read why each suggestion exists, accept it (the purchase order lands with M4) or dismiss it with a reason. */
export function ReplenishmentPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const search = useSearch({ strict: false });
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const warehouses = useWarehouses(companyId);
  const [warehouseId, setWarehouseId] = useState("");
  const [status, setStatus] = useState("open");
  const [asOf, setAsOf] = useState(today());
  const [quantity, setQuantity] = useState("");
  const [note, setNote] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const openId = search.open;

  const suggestions = useQuery({
    queryKey: ["suggestions", companyId, warehouseId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/replenishment/suggestions", { params: { query: { companyId, status, ...(warehouseId ? { warehouseId } : {}) } } })),
  });
  const runs = useQuery({
    queryKey: ["replenishment-runs", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/replenishment/runs", { params: { query: { companyId } } })),
  });
  const detail = suggestions.data?.find((s) => s.id === openId);
  const open = (id: string | null): void => { setQuantity(""); setNote(""); void navigate({ to: "/inventory/replenishment", search: id ? { open: id } : {} }); };
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["suggestions"] });
    await queryClient.invalidateQueries({ queryKey: ["replenishment-runs"] });
  };

  const run = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/inventory/replenishment/run", { body: { companyId, warehouseId: warehouseId || null, asOf: asOf || null } })),
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const decide = useMutation({
    mutationFn: async (action: "accept" | "dismiss") => {
      const suggestionId = openId ?? "";
      return action === "accept"
        ? unwrap(await api.POST("/api/v1/inventory/replenishment/suggestions/{suggestionId}/accept", { params: { path: { suggestionId } }, body: { quantity: quantity || null, note: note || null } }))
        : unwrap(await api.POST("/api/v1/inventory/replenishment/suggestions/{suggestionId}/dismiss", { params: { path: { suggestionId } }, body: { reason: note } }));
    },
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<Suggestion, unknown>[]>(
    () => [
      { id: "item", accessorKey: "itemCode", header: t("inventory.item"), size: 130, cell: ({ row }) => <span dir="ltr">{row.original.itemCode}</span> },
      { id: "name", accessorFn: (row) => localized(row.itemName), header: t("inventory.items.name"), size: 220 },
      { id: "warehouse", accessorKey: "warehouseCode", header: t("inventory.warehouse"), size: 110 },
      { id: "qty", accessorKey: "suggestedQty", header: t("inventory.replenishment.suggested"), size: 120, cell: ({ row }) => <Qty value={row.original.suggestedQty} uom={row.original.baseUom} /> },
      { id: "neededBy", accessorKey: "neededBy", header: t("inventory.replenishment.neededBy"), size: 120, cell: ({ row }) => formatDate(row.original.neededBy) },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => <DocStatus status={row.original.status} /> },
    ],
    [t],
  );

  return (
    <>
      <PageHeader
        title={t("nav.replenishment")}
        description={t("inventory.replenishment.description")}
        actions={
          <Button onClick={() => { run.mutate(); }} disabled={!companyId} loading={run.isPending} data-testid="run-planner">
            <Play aria-hidden="true" />
            {t("inventory.replenishment.runNow")}
          </Button>
        }
      />
      <div className="mb-4 grid gap-3 sm:grid-cols-4">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <WarehouseSelect warehouses={warehouses.data ?? []} value={warehouseId} onChange={setWarehouseId} allowAll />
        <Field label={t("inventory.replenishment.asOf")}>
          <TextField type="date" value={asOf} onChange={(e) => { setAsOf(e.target.value); }} dir="ltr" />
        </Field>
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }}>
            {statuses.map((s) => (
              <option key={s} value={s}>
                {s === "all" ? t("accounting.anyStatus") : t(`inventory.statuses.${s}`)}
              </option>
            ))}
          </SelectField>
        </Field>
      </div>
      <FormError message={problem && !openId ? problem.message : null} />
      <DataGrid<Suggestion> label="nav.replenishment" columns={columns} data={suggestions.data ?? []} rowKey={(row) => row.id} loading={suggestions.isPending && Boolean(companyId)} emptyTitle={t("inventory.replenishment.emptyTitle")} emptyDescription={t("inventory.replenishment.emptyDescription")} onOpen={(row) => { open(row.id); }} height={400} />
      <section className="mt-6">
        <h2 className="mb-2 text-base font-semibold">{t("inventory.replenishment.runs")}</h2>
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("inventory.replenishment.ranAt")}</TableHead>
              <TableHead>{t("inventory.replenishment.asOf")}</TableHead>
              <TableHead className="text-end">{t("inventory.replenishment.checked")}</TableHead>
              <TableHead className="text-end">{t("inventory.replenishment.created")}</TableHead>
              <TableHead className="text-end">{t("inventory.replenishment.refreshed")}</TableHead>
              <TableHead className="text-end">{t("inventory.replenishment.closed")}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {(runs.data ?? []).map((r) => (
              <TableRow key={r.id} data-testid="planner-run">
                <TableCell>{formatDateTime(r.ranAt)}</TableCell>
                <TableCell>{formatDate(r.asOf)}</TableCell>
                <TableNumberCell>{String(r.itemsChecked)}</TableNumberCell>
                <TableNumberCell>{String(r.suggestionsCreated)}</TableNumberCell>
                <TableNumberCell>{String(r.suggestionsRefreshed)}</TableNumberCell>
                <TableNumberCell>{String(r.suggestionsClosed)}</TableNumberCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </section>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { open(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold" dir="auto">
              {detail ? `${detail.itemCode} · ${localized(detail.itemName)} · ${detail.warehouseCode}` : t("common.loading")}
            </DialogTitle>
          </DialogHeader>
          {detail ? (
            <div className="flex flex-col gap-4" data-testid="suggestion-detail">
              <div className="flex flex-wrap items-center gap-2 text-sm">
                <DocStatus status={detail.status} />
                <span>
                  {t("inventory.replenishment.suggested")}: <Qty value={detail.suggestedQty} uom={detail.baseUom} />
                </span>
                {detail.neededBy ? <span className="text-fg-muted">{t("inventory.replenishment.neededBy")}: {formatDate(detail.neededBy)}</span> : null}
                {detail.decisionNote ? <span className="text-fg-muted">{detail.decisionNote}</span> : null}
              </div>
              <section>
                <h3 className="mb-1 text-sm font-semibold">{t("inventory.replenishment.explanation")}</h3>
                <KeyValues entries={Object.entries(detail.explanation as Record<string, unknown>).map(([key, value]) => [t(`inventory.replenishment.why.${key}`, { defaultValue: key }), plain(value)])} />
              </section>
              <FormError message={problem?.message ?? null} />
              {detail.status === "open" ? (
                <div className="grid gap-3 sm:grid-cols-2">
                  <Field label={t("inventory.replenishment.acceptedQty")}>
                    <TextField inputMode="decimal" value={quantity} onChange={(e) => { setQuantity(e.target.value); }} dir="ltr" placeholder={String(detail.suggestedQty)} />
                  </Field>
                  <Field label={t("inventory.replenishment.note")}>
                    <TextField value={note} onChange={(e) => { setNote(e.target.value); }} data-testid="decision-note" />
                  </Field>
                </div>
              ) : null}
              <DialogFooter>
                {detail.status === "open" ? (
                  <>
                    <Button variant="secondary" onClick={() => { decide.mutate("dismiss"); }} loading={decide.isPending} data-testid="dismiss-suggestion">
                      {t("inventory.replenishment.dismiss")}
                    </Button>
                    <Button onClick={() => { decide.mutate("accept"); }} loading={decide.isPending} data-testid="accept-suggestion">
                      {t("inventory.replenishment.accept")}
                    </Button>
                  </>
                ) : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
