import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, useCompanyContext, useWarehouses, type Warehouse } from "./shared";

interface WarehouseForm {
  code: string;
  nameEn: string;
  nameAr: string;
  kind: string;
  binsEnabled: boolean;
  allowNegativeStock: string;
  isActive: boolean;
}

const kinds = ["standard", "in_transit", "consignment", "quarantine", "virtual"];
const binKinds = ["storage", "receiving", "shipping", "quarantine", "returns"];
const empty: WarehouseForm = { code: "", nameEn: "", nameAr: "", kind: "standard", binsEnabled: false, allowNegativeStock: "", isActive: true };

function toForm(w: Warehouse): WarehouseForm {
  return { code: w.code, nameEn: w.name.en ?? "", nameAr: w.name.ar ?? "", kind: w.kind, binsEnabled: w.binsEnabled, allowNegativeStock: w.allowNegativeStock === null ? "" : w.allowNegativeStock ? "yes" : "no", isActive: w.isActive };
}

/** Warehouses and bins (roadmap 3.2): the places stock lives, per company, with their bins in pick order. */
export function WarehousesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const search = useSearch({ strict: false });
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const warehouses = useWarehouses(companyId);
  const [editing, setEditing] = useState<{ id: string | null; form: WarehouseForm } | null>(null);
  const [bin, setBin] = useState({ code: "", zone: "", kind: "storage", pickSequence: "0" });
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const openId = search.open;
  const detail = warehouses.data?.find((w) => w.id === openId);
  const bins = useQuery({
    queryKey: ["bins", openId],
    enabled: Boolean(openId) && detail?.binsEnabled === true,
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses/{warehouseId}/bins", { params: { path: { warehouseId: openId ?? "" } } })),
  });
  const open = (id: string | null): void => { void navigate({ to: "/inventory/warehouses", search: id ? { open: id } : {} }); };

  const save = useMutation({
    mutationFn: async (input: { id: string | null; form: WarehouseForm }) => {
      const f = input.form;
      const body = { companyId, code: f.code, name: { en: f.nameEn, ...(f.nameAr ? { ar: f.nameAr } : {}) }, kind: f.kind, binsEnabled: f.binsEnabled, allowNegativeStock: f.allowNegativeStock === "" ? null : f.allowNegativeStock === "yes", isActive: f.isActive };
      return input.id
        ? unwrap(await api.PUT("/api/v1/inventory/warehouses/{warehouseId}", { params: { path: { warehouseId: input.id } }, body }))
        : unwrap(await api.POST("/api/v1/inventory/warehouses", { body }));
    },
    onSuccess: async (saved) => {
      setEditing(null);
      setProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["warehouses"] });
      open(saved.id);
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const addBin = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/inventory/warehouses/{warehouseId}/bins", { params: { path: { warehouseId: openId ?? "" } }, body: { code: bin.code, zone: bin.zone || null, kind: bin.kind, pickSequence: Number(bin.pickSequence) || 0, isActive: true } })),
    onSuccess: async () => {
      setBin({ code: "", zone: "", kind: "storage", pickSequence: "0" });
      setProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["bins", openId] });
      await queryClient.invalidateQueries({ queryKey: ["warehouses"] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<Warehouse, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("inventory.items.code"), size: 130, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (row) => localized(row.name), header: t("inventory.items.name"), size: 260 },
      { id: "kind", accessorKey: "kind", header: t("inventory.warehouses.kind"), size: 130, cell: ({ row }) => t(`inventory.warehouses.kinds.${row.original.kind}`, { defaultValue: row.original.kind }) },
      { id: "bins", accessorKey: "binCount", header: t("inventory.warehouses.bins"), size: 90, cell: ({ row }) => (row.original.binsEnabled ? String(row.original.binCount) : "—") },
      { id: "isActive", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => <Badge tone={row.original.isActive ? "success" : "neutral"}>{row.original.isActive ? t("common.active") : t("common.inactive")}</Badge> },
      { id: "updatedAt", accessorKey: "updatedAt", header: t("common.updated"), size: 130, cell: ({ row }) => formatDate(row.original.updatedAt) },
    ],
    [t],
  );

  const form = editing?.form;
  const isEdit = editing?.id != null;
  const setForm = (patch: Partial<WarehouseForm>): void => { setEditing((prev) => (prev ? { ...prev, form: { ...prev.form, ...patch } } : prev)); };
  const submit = (event: FormEvent): void => {
    event.preventDefault();
    if (editing) {
      save.mutate(editing);
    }
  };

  return (
    <>
      <PageHeader
        title={t("nav.warehouses")}
        description={t("inventory.warehouses.description")}
        actions={
          <Button onClick={() => { setProblem(null); setEditing({ id: null, form: empty }); }} disabled={!companyId} data-testid="new-warehouse">
            <Plus aria-hidden="true" />
            {t("inventory.warehouses.new")}
          </Button>
        }
      />
      <div className="mb-4 grid gap-3 sm:grid-cols-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
      </div>
      <DataGrid<Warehouse> label="nav.warehouses" columns={columns} data={warehouses.data ?? []} rowKey={(row) => row.id} loading={warehouses.isPending && Boolean(companyId)} onOpen={(row) => { open(row.id); }} emptyTitle={t("inventory.warehouses.emptyTitle")} emptyDescription={t("inventory.warehouses.emptyDescription")} />

      <Dialog open={Boolean(openId) && !editing} onOpenChange={(isOpen) => { if (!isOpen) { open(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold" dir="auto">
              {detail ? `${detail.code} · ${localized(detail.name)}` : t("common.loading")}
            </DialogTitle>
          </DialogHeader>
          {detail ? (
            <div className="flex flex-col gap-4" data-testid="warehouse-detail">
              <div className="flex flex-wrap gap-2 text-sm">
                <Badge>{t(`inventory.warehouses.kinds.${detail.kind}`, { defaultValue: detail.kind })}</Badge>
                <Badge tone={detail.binsEnabled ? "accent" : "neutral"}>{detail.binsEnabled ? t("inventory.warehouses.binsEnabled") : t("inventory.warehouses.noBins")}</Badge>
                {detail.allowNegativeStock !== null ? <Badge tone="warning">{detail.allowNegativeStock ? t("inventory.warehouses.negativeAllowed") : t("inventory.warehouses.negativeBlocked")}</Badge> : null}
              </div>
              {detail.binsEnabled ? (
                <>
                  <Table>
                    <TableHeader>
                      <TableRow>
                        <TableHead>{t("inventory.warehouses.bin")}</TableHead>
                        <TableHead>{t("inventory.warehouses.zone")}</TableHead>
                        <TableHead>{t("inventory.warehouses.kind")}</TableHead>
                        <TableHead className="text-end">{t("inventory.warehouses.pickSequence")}</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {(bins.data ?? []).map((b) => (
                        <TableRow key={b.id} data-testid="bin-row">
                          <TableCell dir="ltr">{b.code}</TableCell>
                          <TableCell>{b.zone ?? ""}</TableCell>
                          <TableCell>{t(`inventory.warehouses.binKinds.${b.kind}`, { defaultValue: b.kind })}</TableCell>
                          <TableCell className="text-end">{String(b.pickSequence)}</TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                  <form className="grid items-end gap-3 sm:grid-cols-5" onSubmit={(e) => { e.preventDefault(); addBin.mutate(); }}>
                    <Field label={t("inventory.warehouses.bin")} required>
                      <TextField value={bin.code} onChange={(e) => { setBin({ ...bin, code: e.target.value.toUpperCase() }); }} required dir="ltr" data-testid="bin-code" />
                    </Field>
                    <Field label={t("inventory.warehouses.zone")}>
                      <TextField value={bin.zone} onChange={(e) => { setBin({ ...bin, zone: e.target.value.toUpperCase() }); }} dir="ltr" />
                    </Field>
                    <Field label={t("inventory.warehouses.kind")}>
                      <SelectField value={bin.kind} onChange={(e) => { setBin({ ...bin, kind: e.target.value }); }}>
                        {binKinds.map((k) => (
                          <option key={k} value={k}>
                            {t(`inventory.warehouses.binKinds.${k}`)}
                          </option>
                        ))}
                      </SelectField>
                    </Field>
                    <Field label={t("inventory.warehouses.pickSequence")}>
                      <TextField inputMode="numeric" value={bin.pickSequence} onChange={(e) => { setBin({ ...bin, pickSequence: e.target.value }); }} dir="ltr" />
                    </Field>
                    <Button type="submit" variant="secondary" loading={addBin.isPending} data-testid="add-bin">
                      {t("inventory.warehouses.addBin")}
                    </Button>
                  </form>
                </>
              ) : null}
              <FormError message={problem?.message ?? null} />
              <DialogFooter>
                <Button variant="secondary" onClick={() => { setProblem(null); setEditing({ id: detail.id, form: toForm(detail) }); }} data-testid="edit-warehouse">
                  {t("inventory.warehouses.edit")}
                </Button>
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(editing)} onOpenChange={(isOpen) => { if (!isOpen) { setEditing(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{isEdit ? t("inventory.warehouses.edit") : t("inventory.warehouses.new")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("inventory.items.code")} required error={problem?.fields.code}>
                  <TextField value={form.code} onChange={(e) => { setForm({ code: e.target.value.toUpperCase() }); }} required dir="ltr" data-testid="warehouse-code" />
                </Field>
                <Field label={t("inventory.warehouses.kind")}>
                  <SelectField value={form.kind} onChange={(e) => { setForm({ kind: e.target.value }); }}>
                    {kinds.map((k) => (
                      <option key={k} value={k}>
                        {t(`inventory.warehouses.kinds.${k}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("inventory.items.nameEn")} required error={problem?.fields.name}>
                  <TextField value={form.nameEn} onChange={(e) => { setForm({ nameEn: e.target.value }); }} required data-testid="warehouse-name-en" />
                </Field>
                <Field label={t("inventory.items.nameAr")}>
                  <TextField value={form.nameAr} onChange={(e) => { setForm({ nameAr: e.target.value }); }} dir="rtl" />
                </Field>
                <Field label={t("inventory.warehouses.negativeStock")}>
                  <SelectField value={form.allowNegativeStock} onChange={(e) => { setForm({ allowNegativeStock: e.target.value }); }}>
                    <option value="">{t("inventory.warehouses.negativeCompany")}</option>
                    <option value="no">{t("inventory.warehouses.negativeBlocked")}</option>
                    <option value="yes">{t("inventory.warehouses.negativeAllowed")}</option>
                  </SelectField>
                </Field>
                <div className="flex flex-col gap-2 self-end pb-2 text-sm">
                  <label className="flex items-center gap-2">
                    <input type="checkbox" checked={form.binsEnabled} onChange={(e) => { setForm({ binsEnabled: e.target.checked }); }} data-testid="warehouse-bins" />
                    {t("inventory.warehouses.binsEnabled")}
                  </label>
                  <label className="flex items-center gap-2">
                    <input type="checkbox" checked={form.isActive} onChange={(e) => { setForm({ isActive: e.target.checked }); }} />
                    {t("common.active")}
                  </label>
                </div>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setEditing(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={save.isPending} data-testid="save-warehouse">
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
