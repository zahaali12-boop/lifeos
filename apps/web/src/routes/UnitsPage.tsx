import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus, Trash2 } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";
import { DataGrid } from "../grid/DataGrid";
import { formatNumber, localized } from "../lib/format";
import { toFormProblem, type FormProblem } from "../lib/problem";
import { FormError, PageHeader, SelectField, TextField } from "./common";
import { DocStatus } from "./inventory/shared";

type Uom = components["schemas"]["UomSummary"];

const families = ["count", "weight", "volume", "length", "area", "time", "other"];

/**
 * Units of measure (roadmap 1.8, A-096): the tenant's units by family and decimal precision, and the conversions between
 * units of one family (a kilogram is 1,000 grams); conversions across families (a carton of 24) belong to each item.
 */
export function UnitsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [unit, setUnit] = useState<{ id: string | null; code: string; nameEn: string; nameAr: string; family: string; precision: string; isActive: boolean; isSystem: boolean } | null>(null);
  const [conversion, setConversion] = useState<{ fromUomId: string; toUomId: string; numerator: string; denominator: string } | null>(null);
  const [convert, setConvert] = useState({ from: "", to: "", value: "1" });
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const uoms = useQuery({ queryKey: ["uoms"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/uoms")) });
  const conversions = useQuery({ queryKey: ["uom-conversions"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/uom-conversions")) });
  const converted = useQuery({
    queryKey: ["uom-convert", convert.from, convert.to, convert.value],
    enabled: Boolean(convert.from && convert.to && convert.value.trim()),
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/organization/uom-conversions/convert", { params: { query: { from: convert.from, to: convert.to, value: convert.value } } })),
  });
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const saveUnit = useMutation({
    mutationFn: async () => {
      if (!unit) {
        return;
      }
      const body = { code: unit.code, name: { en: unit.nameEn, ...(unit.nameAr ? { ar: unit.nameAr } : {}) }, family: unit.family, precision: Number(unit.precision) || 0, isActive: unit.isActive };
      if (unit.id) {
        unwrap(await api.PUT("/api/v1/organization/uoms/{uomId}", { params: { path: { uomId: unit.id } }, body }));
      } else {
        unwrap(await api.POST("/api/v1/organization/uoms", { body }));
      }
    },
    onSuccess: async () => { setUnit(null); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["uoms"] }); },
    onError: fail,
  });
  const saveConversion = useMutation({
    mutationFn: async () => {
      if (!conversion) {
        return;
      }
      unwrap(await api.POST("/api/v1/organization/uom-conversions", { body: { fromUomId: conversion.fromUomId, toUomId: conversion.toUomId, numerator: conversion.numerator, denominator: conversion.denominator || "1" } }));
    },
    onSuccess: async () => { setConversion(null); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["uom-conversions"] }); },
    onError: fail,
  });
  const removeConversion = useMutation({
    mutationFn: async (id: string) => { await api.DELETE("/api/v1/organization/uom-conversions/{conversionId}", { params: { path: { conversionId: id } } }).then(unwrap); },
    onSuccess: async () => { setProblem(null); await queryClient.invalidateQueries({ queryKey: ["uom-conversions"] }); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Uom, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("units.code"), size: 110, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (row) => localized(row.name), header: t("units.name"), size: 200 },
      { id: "family", accessorKey: "family", header: t("units.family"), size: 120, cell: ({ row }) => t(`units.families.${row.original.family}`, { defaultValue: row.original.family }) },
      { id: "precision", accessorKey: "precision", header: t("units.precision"), size: 100 },
      { id: "system", accessorKey: "isSystem", header: t("units.kind"), size: 100, cell: ({ row }) => (row.original.isSystem ? t("units.system") : t("units.custom")) },
      { id: "status", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => <DocStatus status={row.original.isActive ? "active" : "inactive"} /> },
    ],
    [t],
  );
  const active = (uoms.data ?? []).filter((u) => u.isActive);
  const fromUnit = active.find((u) => u.id === conversion?.fromUomId);
  const submitUnit = (event: FormEvent): void => { event.preventDefault(); saveUnit.mutate(); };
  const submitConversion = (event: FormEvent): void => { event.preventDefault(); saveConversion.mutate(); };

  return (
    <>
      <PageHeader
        title={t("nav.units")}
        description={t("units.description")}
        actions={
          <Button onClick={() => { setProblem(null); setUnit({ id: null, code: "", nameEn: "", nameAr: "", family: "count", precision: "0", isActive: true, isSystem: false }); }} data-testid="new-unit">
            <Plus aria-hidden="true" />
            {t("units.new")}
          </Button>
        }
      />
      <DataGrid<Uom> label="nav.units" columns={columns} data={uoms.data ?? []} rowKey={(row) => row.id} loading={uoms.isPending} height={400} onOpen={(row) => { setProblem(null); setUnit({ id: row.id, code: row.code, nameEn: row.name.en ?? "", nameAr: row.name.ar ?? "", family: row.family, precision: String(row.precision), isActive: row.isActive, isSystem: row.isSystem }); }} emptyTitle={t("units.emptyTitle")} emptyDescription={t("units.emptyDescription")} />

      <section className="mt-6 grid gap-6 lg:grid-cols-2">
        <div className="flex flex-col gap-3" data-testid="conversions">
          <div className="flex items-center gap-2">
            <h2 className="text-base font-semibold">{t("units.conversions")}</h2>
            <Button variant="secondary" size="sm" className="ms-auto" onClick={() => { setProblem(null); setConversion({ fromUomId: "", toUomId: "", numerator: "", denominator: "1" }); }} data-testid="new-conversion">
              <Plus aria-hidden="true" />
              {t("units.newConversion")}
            </Button>
          </div>
          <p className="text-sm text-fg-muted">{t("units.conversionsHint")}</p>
          <FormError message={problem && !unit && !conversion ? problem.message : null} />
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("units.conversion")}</TableHead>
                <TableHead />
              </TableRow>
            </TableHeader>
            <TableBody>
              {(conversions.data ?? []).map((c) => (
                <TableRow key={c.id} data-testid="conversion-row">
                  <TableCell>
                    {/* The stored ratio as it is (denominator from = numerator to): exact, no division. */}
                    {formatNumber(c.denominator, { maximumFractionDigits: 9 })} <span dir="ltr">{c.fromCode}</span> = {formatNumber(c.numerator, { maximumFractionDigits: 9 })} <span dir="ltr">{c.toCode}</span>
                  </TableCell>
                  <TableCell>
                    <Button variant="ghost" size="icon" aria-label={t("units.removeConversion", { from: c.fromCode, to: c.toCode })} onClick={() => { removeConversion.mutate(c.id); }}>
                      <Trash2 aria-hidden="true" />
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
        <div className="flex flex-col gap-3" data-testid="converter">
          <h2 className="text-base font-semibold">{t("units.converter")}</h2>
          <div className="grid gap-3 sm:grid-cols-3">
            <Field label={t("units.quantity")}>
              <TextField inputMode="decimal" value={convert.value} onChange={(e) => { setConvert({ ...convert, value: e.target.value }); }} dir="ltr" data-testid="convert-value" />
            </Field>
            <Field label={t("units.from")}>
              <SelectField value={convert.from} onChange={(e) => { setConvert({ ...convert, from: e.target.value }); }} data-testid="convert-from">
                <option value="">—</option>
                {active.map((u) => (
                  <option key={u.id} value={u.id}>
                    {u.code}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("units.to")}>
              <SelectField value={convert.to} onChange={(e) => { setConvert({ ...convert, to: e.target.value }); }} data-testid="convert-to">
                <option value="">—</option>
                {active.map((u) => (
                  <option key={u.id} value={u.id}>
                    {u.code}
                  </option>
                ))}
              </SelectField>
            </Field>
          </div>
          <p className="text-sm" role="status" data-testid="convert-result">
            {converted.data ? <strong><span className="tabular">{formatNumber(Number(converted.data.value), { maximumFractionDigits: 9 })} <span dir="ltr">{converted.data.fromCode}</span> = {formatNumber(Number(converted.data.result), { maximumFractionDigits: 9 })} <span dir="ltr">{converted.data.toCode}</span></span></strong> : converted.isError ? <span className="text-danger">{toFormProblem(converted.error, t("units.noPath")).message}</span> : null}
          </p>
        </div>
      </section>

      <Dialog open={Boolean(unit)} onOpenChange={(isOpen) => { if (!isOpen) { setUnit(null); } }}>
        <DialogContent closeLabel={t("common.close")}>
          {unit ? (
            <form onSubmit={submitUnit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{unit.id ? t("units.edit") : t("units.new")}</DialogTitle>
              </DialogHeader>
              {unit.isSystem ? <Badge tone="info">{t("units.systemHint")}</Badge> : null}
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("units.code")} required error={problem?.fields.code}>
                  <TextField value={unit.code} onChange={(e) => { setUnit({ ...unit, code: e.target.value.toUpperCase() }); }} required dir="ltr" disabled={unit.isSystem} data-testid="unit-code" />
                </Field>
                <Field label={t("units.family")} error={problem?.fields.family}>
                  <SelectField value={unit.family} onChange={(e) => { setUnit({ ...unit, family: e.target.value }); }} disabled={unit.isSystem} data-testid="unit-family">
                    {families.map((f) => (
                      <option key={f} value={f}>
                        {t(`units.families.${f}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("units.nameEn")} required error={problem?.fields.name}>
                  <TextField value={unit.nameEn} onChange={(e) => { setUnit({ ...unit, nameEn: e.target.value }); }} required data-testid="unit-name-en" />
                </Field>
                <Field label={t("units.nameAr")}>
                  <TextField value={unit.nameAr} onChange={(e) => { setUnit({ ...unit, nameAr: e.target.value }); }} dir="rtl" lang="ar" data-testid="unit-name-ar" />
                </Field>
                <Field label={t("units.precision")} description={t("units.precisionHint")} error={problem?.fields.precision}>
                  <TextField inputMode="numeric" value={unit.precision} onChange={(e) => { setUnit({ ...unit, precision: e.target.value }); }} dir="ltr" data-testid="unit-precision" />
                </Field>
              </div>
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={unit.isActive} onChange={(e) => { setUnit({ ...unit, isActive: e.target.checked }); }} />
                {t("common.active")}
              </label>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setUnit(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={saveUnit.isPending} data-testid="save-unit-master">
                  {t("common.save")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(conversion)} onOpenChange={(isOpen) => { if (!isOpen) { setConversion(null); } }}>
        <DialogContent closeLabel={t("common.close")}>
          {conversion ? (
            <form onSubmit={submitConversion} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("units.newConversion")}</DialogTitle>
              </DialogHeader>
              <p className="text-sm text-fg-muted">{t("units.conversionFormHint")}</p>
              <FormError message={problem ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("units.from")} required>
                  <SelectField value={conversion.fromUomId} onChange={(e) => { setConversion({ ...conversion, fromUomId: e.target.value }); }} required data-testid="conversion-from">
                    <option value="">—</option>
                    {active.map((u) => (
                      <option key={u.id} value={u.id}>
                        {u.code} · {localized(u.name)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("units.to")} required>
                  <SelectField value={conversion.toUomId} onChange={(e) => { setConversion({ ...conversion, toUomId: e.target.value }); }} required data-testid="conversion-to">
                    <option value="">—</option>
                    {active.filter((u) => !fromUnit || u.family === fromUnit.family).filter((u) => u.id !== conversion.fromUomId).map((u) => (
                      <option key={u.id} value={u.id}>
                        {u.code} · {localized(u.name)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("itemEditor.numerator")} required>
                  <TextField inputMode="decimal" value={conversion.numerator} onChange={(e) => { setConversion({ ...conversion, numerator: e.target.value }); }} required dir="ltr" data-testid="conversion-numerator" />
                </Field>
                <Field label={t("itemEditor.denominator")}>
                  <TextField inputMode="decimal" value={conversion.denominator} onChange={(e) => { setConversion({ ...conversion, denominator: e.target.value }); }} dir="ltr" />
                </Field>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setConversion(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={saveConversion.isPending} data-testid="save-conversion">
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


