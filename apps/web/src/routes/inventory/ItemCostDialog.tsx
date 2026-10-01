import {
  Button,
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableNumberCell,
  TableRow,
} from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatDate } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Amount, today } from "../accounting/shared";
import { Field, FormError, TextField } from "../common";
import { KeyValues, Qty } from "./shared";

export interface ItemCostTarget {
  itemId: string;
  itemCode: string;
  warehouseId: string | null;
  warehouseCode: string | null;
}

/** The valuation's "item cost" dialog: the panel below for the row's item and warehouse at the report date. */
export function ItemCostDialog({
  companyId,
  asOf,
  target,
  onClose,
}: {
  companyId: string;
  asOf: string;
  target: ItemCostTarget | null;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  return (
    <Dialog
      open={Boolean(target)}
      onOpenChange={(isOpen) => {
        if (!isOpen) {
          onClose();
        }
      }}
    >
      <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
        <DialogHeader>
          <DialogTitle className="text-lg font-semibold" dir="auto">
            {t("itemCost.title")} · {target?.itemCode}
            {target?.warehouseCode ? ` · ${target.warehouseCode}` : ""} · {formatDate(asOf)}
          </DialogTitle>
        </DialogHeader>
        {target ? (
          <ItemCostPanel
            companyId={companyId}
            itemId={target.itemId}
            warehouseId={target.warehouseId}
            asOf={asOf}
          />
        ) : null}
      </DialogContent>
    </Dialog>
  );
}

/** An item's cost in a company (and warehouse) at a date: its method and scope, average, last, expected and standard cost; the standard-cost versions, and a new standard that revalues the stock on hand (roadmap 3.3). */
export function ItemCostPanel({
  companyId,
  itemId,
  warehouseId,
  asOf,
}: {
  companyId: string;
  itemId: string;
  warehouseId: string | null;
  asOf: string;
}) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [form, setForm] = useState({ standardCost: "", effectiveFrom: today(), reason: "" });
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const cost = useQuery({
    queryKey: ["item-cost", companyId, itemId, warehouseId, asOf],
    enabled: Boolean(companyId),
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/inventory/costing/item-cost", {
          params: { query: { companyId, itemId, asOf, ...(warehouseId ? { warehouseId } : {}) } },
        }),
      ),
  });
  const isStandard = cost.data?.costingMethod === "standard";
  const standards = useQuery({
    queryKey: ["standard-costs", companyId, itemId],
    enabled: Boolean(companyId) && isStandard,
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/inventory/costing/standard-costs", {
          params: { query: { companyId, itemId } },
        }),
      ),
  });
  const setStandard = useMutation({
    mutationFn: async () =>
      unwrap(
        await api.POST("/api/v1/inventory/costing/standard-costs", {
          body: {
            companyId,
            itemId,
            standardCost: form.standardCost,
            effectiveFrom: form.effectiveFrom,
            reason: form.reason || null,
          },
        }),
      ),
    onSuccess: async () => {
      setProblem(null);
      setForm({ standardCost: "", effectiveFrom: today(), reason: "" });
      await queryClient.invalidateQueries({ queryKey: ["standard-costs"] });
      await queryClient.invalidateQueries({ queryKey: ["item-cost"] });
      await queryClient.invalidateQueries({ queryKey: ["valuation"] });
      await queryClient.invalidateQueries({ queryKey: ["cost-runs"] });
    },
    onError: (error) => {
      setProblem(toFormProblem(error, t("common.saveFailed")));
    },
  });
  const submit = (event: FormEvent): void => {
    event.preventDefault();
    setStandard.mutate();
  };
  const data = cost.data;

  return data ? (
    <div className="flex flex-col gap-4" data-testid="item-cost">
      <KeyValues
        entries={[
          [
            t("itemCost.method"),
            `${t(`itemCost.methods.${data.costingMethod}`, { defaultValue: data.costingMethod })} · ${t(`itemCost.scopes.${data.costingScope}`, { defaultValue: data.costingScope })}`,
          ],
          [t("inventory.quantity"), <Qty key="q" value={data.quantity} />],
          [t("inventory.valuation.value"), <Amount key="v" value={data.value} />],
          [
            t("inventory.valuation.averageCost"),
            <span key="a" data-testid="item-cost-average">
              <Amount value={data.averageUnitCost} minorUnits={4} />
            </span>,
          ],
          [t("itemCost.lastCost"), <Amount key="l" value={data.lastCost} minorUnits={4} />],
          [t("itemCost.expectedUnitCost"), <Amount key="e" value={data.expectedUnitCost} minorUnits={4} />],
          ...(data.standardCost === null
            ? []
            : [
                [
                  t("itemCost.standardCost"),
                  <span key="s" data-testid="item-cost-standard">
                    <Amount value={data.standardCost} minorUnits={4} />
                  </span>,
                ] as [string, ReactNode],
              ]),
        ]}
      />
      {data.valuationPending ? <p className="text-sm text-warning">{t("itemCost.pending")}</p> : null}
      {isStandard ? (
        <>
          <section>
            <h3 className="mb-1 text-sm font-semibold">{t("itemCost.versions")}</h3>
            {(standards.data ?? []).length === 0 ? (
              <p className="text-sm text-fg-muted">{t("itemCost.noVersions")}</p>
            ) : (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("itemCost.effectiveFrom")}</TableHead>
                    <TableHead className="text-end">{t("itemCost.standardCost")}</TableHead>
                    <TableHead>{t("common.reason")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {(standards.data ?? []).map((v) => (
                    <TableRow key={v.id} data-testid="standard-version">
                      <TableCell>{formatDate(v.effectiveFrom)}</TableCell>
                      <TableNumberCell>
                        <Amount value={v.standardCost} minorUnits={4} />
                      </TableNumberCell>
                      <TableCell>{v.reason ?? ""}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </section>
          <form onSubmit={submit} className="flex flex-col gap-3 rounded-md border border-border p-3">
            <h3 className="text-sm font-semibold">{t("itemCost.newStandard")}</h3>
            <p className="text-sm text-fg-muted">{t("itemCost.newStandardHint")}</p>
            <FormError
              message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null}
            />
            <div className="grid gap-3 sm:grid-cols-3">
              <Field label={t("itemCost.standardCost")} required error={problem?.fields.standardCost}>
                <TextField
                  inputMode="decimal"
                  value={form.standardCost}
                  onChange={(e) => {
                    setForm({ ...form, standardCost: e.target.value });
                  }}
                  required
                  dir="ltr"
                  data-testid="new-standard-cost"
                />
              </Field>
              <Field label={t("itemCost.effectiveFrom")} required error={problem?.fields.effectiveFrom}>
                <TextField
                  type="date"
                  value={form.effectiveFrom}
                  onChange={(e) => {
                    setForm({ ...form, effectiveFrom: e.target.value });
                  }}
                  required
                  dir="ltr"
                  data-testid="new-standard-date"
                />
              </Field>
              <Field label={t("common.reason")}>
                <TextField
                  value={form.reason}
                  onChange={(e) => {
                    setForm({ ...form, reason: e.target.value });
                  }}
                  data-testid="new-standard-reason"
                />
              </Field>
            </div>
            <div>
              <Button type="submit" loading={setStandard.isPending} data-testid="save-standard-cost">
                {t("itemCost.setStandard")}
              </Button>
            </div>
          </form>
        </>
      ) : null}
    </div>
  ) : null;
}
