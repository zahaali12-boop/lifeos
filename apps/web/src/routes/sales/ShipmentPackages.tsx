import { Button, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation } from "@tanstack/react-query";
import { PackagePlus, Trash2 } from "lucide-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatNumber } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { Field, FormError, SelectField, TextField } from "../common";
import { num, optionalNum, type Shipment } from "./shared";

type Package = components["schemas"]["ShipmentPackageSummary"];

export const packageTypes = ["box", "carton", "pallet", "envelope", "crate", "drum", "bag", "other"] as const;

interface PackageForm {
  key: string;
  packageType: string;
  weightKg: string;
  lengthCm: string;
  widthCm: string;
  heightCm: string;
  trackingNumber: string;
  contents: Record<string, string>;
}

const q = (value: number | string | null | undefined): string => (value === null || value === undefined ? "" : formatNumber(value, { maximumFractionDigits: 3 }));

function fromPackage(p: Package): PackageForm {
  return {
    key: p.id,
    packageType: p.packageType,
    weightKg: p.weightKg === null ? "" : String(p.weightKg),
    lengthCm: p.lengthCm === null ? "" : String(p.lengthCm),
    widthCm: p.widthCm === null ? "" : String(p.widthCm),
    heightCm: p.heightCm === null ? "" : String(p.heightCm),
    trackingNumber: p.trackingNumber ?? "",
    contents: Object.fromEntries(p.contents.map((c) => [c.orderLineId, String(c.quantity)])),
  };
}

const blank = (): PackageForm => ({ key: crypto.randomUUID(), packageType: "carton", weightKg: "", lengthCm: "", widthCm: "", heightCm: "", trackingNumber: "", contents: {} });

/** The packages of a shipment as read: number, type, size, weight, tracking and what each holds. */
export function PackagesTable({ shipment }: { shipment: Shipment }) {
  const { t } = useTranslation();
  if (shipment.packages.length === 0) {
    return <p className="text-sm text-fg-muted" data-testid="no-packages">{t("packing.none")}</p>;
  }
  return (
    <Table data-testid="packages">
      <TableHeader>
        <TableRow>
          <TableHead>{t("packing.package")}</TableHead>
          <TableHead>{t("packing.type")}</TableHead>
          <TableHead>{t("packing.weight")}</TableHead>
          <TableHead>{t("packing.dimensions")}</TableHead>
          <TableHead>{t("sales.trackingNumber")}</TableHead>
          <TableHead>{t("packing.contents")}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {shipment.packages.map((p) => (
          <TableRow key={p.id} data-testid="package">
            <TableCell dir="ltr">{p.packageNumber}</TableCell>
            <TableCell>{t(`packing.types.${p.packageType}`)}</TableCell>
            <TableCell className="tabular" dir="ltr">
              {p.weightKg === null ? "—" : `${q(p.weightKg)} kg`}
              {p.contentsWeightKg !== null ? <div className="text-xs text-fg-muted">{t("packing.contentsWeight", { weight: q(p.contentsWeightKg) })}</div> : null}
            </TableCell>
            <TableCell className="tabular" dir="ltr">{p.lengthCm && p.widthCm && p.heightCm ? `${q(p.lengthCm)} × ${q(p.widthCm)} × ${q(p.heightCm)} cm` : "—"}</TableCell>
            <TableCell dir="ltr">{p.trackingNumber ?? "—"}</TableCell>
            <TableCell>
              <ul className="text-xs">
                {p.contents.map((c) => (
                  <li key={c.orderLineId} dir="ltr">{c.itemCode} × {q(c.quantity)}</li>
                ))}
              </ul>
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

/**
 * Packing (roadmap 5.5, A-154): the shipment's boxes, pallets and envelopes, each with what it holds. What is sent
 * replaces the packages there were; no line is packed beyond what the shipment carries.
 */
export function PackagesEditor({ shipment, onSaved, onCancel }: { shipment: Shipment; onSaved: () => Promise<void>; onCancel: () => void }) {
  const { t } = useTranslation();
  const [forms, setForms] = useState<PackageForm[]>(() => (shipment.packages.length > 0 ? shipment.packages.map(fromPackage) : [blank()]));
  const [error, setError] = useState<string | null>(null);
  const lines = shipment.lines;

  const packed = (orderLineId: string): number => forms.reduce((sum, f) => sum + num(f.contents[orderLineId] ?? ""), 0);
  const update = (key: string, patch: Partial<PackageForm>): void => { setForms((all) => all.map((f) => (f.key === key ? { ...f, ...patch } : f))); };
  const allInOne = (): void => {
    setForms([{ ...(forms[0] ?? blank()), contents: Object.fromEntries(lines.map((l) => [l.orderLineId, String(l.quantity)])) }]);
  };

  const save = useMutation({
    mutationFn: async () => unwrap(await api.PUT("/api/v1/sales/shipments/{shipmentId}/packages", {
      params: { path: { shipmentId: shipment.id } },
      body: {
        packages: forms.map((f) => ({
          packageType: f.packageType,
          weightKg: optionalNum(f.weightKg),
          lengthCm: optionalNum(f.lengthCm),
          widthCm: optionalNum(f.widthCm),
          heightCm: optionalNum(f.heightCm),
          trackingNumber: f.trackingNumber.trim() || null,
          contents: Object.entries(f.contents).filter(([, v]) => num(v) > 0).map(([orderLineId, v]) => ({ orderLineId, quantity: num(v) })),
        })),
      },
    })),
    onSuccess: async () => { setError(null); await onSaved(); },
    onError: (caught) => { setError(toFormProblem(caught, t("common.saveFailed")).message); },
  });

  return (
    <div className="flex flex-col gap-3 rounded-md border border-border p-3" data-testid="packages-editor">
      <FormError message={error} />
      <div className="flex flex-wrap gap-2">
        <Button type="button" variant="secondary" size="sm" onClick={() => { setForms([...forms, blank()]); }} data-testid="add-package">
          <PackagePlus aria-hidden="true" />
          {t("packing.addPackage")}
        </Button>
        <Button type="button" variant="secondary" size="sm" onClick={allInOne} data-testid="pack-all-in-one">{t("packing.allInOne")}</Button>
      </div>
      {forms.map((f, index) => (
        <fieldset key={f.key} className="flex flex-col gap-2 rounded-md border border-border p-3" data-testid="package-form">
          <legend className="px-1 text-sm font-semibold">{t("packing.packageNo", { no: index + 1 })}</legend>
          <div className="grid gap-2 sm:grid-cols-6">
            <Field label={t("packing.type")}>
              <SelectField value={f.packageType} onChange={(e) => { update(f.key, { packageType: e.target.value }); }} data-testid="package-type">
                {packageTypes.map((type) => <option key={type} value={type}>{t(`packing.types.${type}`)}</option>)}
              </SelectField>
            </Field>
            <Field label={t("packing.weightKg")}>
              <TextField inputMode="decimal" value={f.weightKg} onChange={(e) => { update(f.key, { weightKg: e.target.value }); }} dir="ltr" data-testid="package-weight" />
            </Field>
            <Field label={t("packing.lengthCm")}>
              <TextField inputMode="decimal" value={f.lengthCm} onChange={(e) => { update(f.key, { lengthCm: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("packing.widthCm")}>
              <TextField inputMode="decimal" value={f.widthCm} onChange={(e) => { update(f.key, { widthCm: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("packing.heightCm")}>
              <TextField inputMode="decimal" value={f.heightCm} onChange={(e) => { update(f.key, { heightCm: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("sales.trackingNumber")}>
              <TextField value={f.trackingNumber} onChange={(e) => { update(f.key, { trackingNumber: e.target.value }); }} dir="ltr" data-testid="package-tracking" />
            </Field>
          </div>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("sales.item")}</TableHead>
                <TableHead>{t("packing.shipped")}</TableHead>
                <TableHead>{t("packing.inThisPackage")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {lines.map((l) => (
                <TableRow key={l.orderLineId}>
                  <TableCell dir="ltr">{l.itemCode}</TableCell>
                  <TableCell className="tabular" dir="ltr">{q(l.quantity)} {l.uomCode}</TableCell>
                  <TableCell>
                    <TextField aria-label={t("packing.inThisPackage")} inputMode="decimal" className="w-24" value={f.contents[l.orderLineId] ?? ""} onChange={(e) => { update(f.key, { contents: { ...f.contents, [l.orderLineId]: e.target.value } }); }} dir="ltr" data-testid={`package-qty-${l.itemCode}`} />
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
          {forms.length > 1 ? (
            <Button type="button" variant="ghost" size="sm" className="self-start" onClick={() => { setForms(forms.filter((x) => x.key !== f.key)); }} data-testid="remove-package">
              <Trash2 aria-hidden="true" />
              {t("packing.removePackage")}
            </Button>
          ) : null}
        </fieldset>
      ))}
      <ul className="text-sm" data-testid="packing-summary">
        {lines.map((l) => {
          const left = num(String(l.quantity)) - packed(l.orderLineId);
          return (
            <li key={l.orderLineId} className={left < 0 ? "text-danger" : left > 0 ? "text-fg-muted" : "text-success"} dir="auto">
              {left < 0 ? t("packing.over", { item: l.itemCode, quantity: q(-left) }) : left > 0 ? t("packing.left", { item: l.itemCode, quantity: q(left) }) : t("packing.allPacked", { item: l.itemCode })}
            </li>
          );
        })}
      </ul>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="secondary" onClick={onCancel}>{t("common.cancel")}</Button>
        <Button type="button" onClick={() => { save.mutate(); }} loading={save.isPending} data-testid="save-packages">{t("packing.save")}</Button>
      </div>
    </div>
  );
}
