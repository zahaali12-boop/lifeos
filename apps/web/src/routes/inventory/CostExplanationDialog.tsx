import { Dialog, DialogContent, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatDate, formatDateTime } from "../../lib/format";
import { Amount } from "../accounting/shared";
import { DocStatus, KeyValues, Qty } from "./shared";

/** Why a stock movement cost what it did (roadmap 3.3): its value entries, the layers it consumed or that consumed it, and the cost runs that touched it. */
export function CostExplanationDialog({ sleId, itemCode, onClose }: { sleId: string | null; itemCode?: string | undefined; onClose: () => void }) {
  const { t } = useTranslation();
  const explanation = useQuery({
    queryKey: ["cost-explanation", sleId],
    enabled: Boolean(sleId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/costing/entries/{sleId}", { params: { path: { sleId: sleId ?? "" } } })),
  });
  const data = explanation.data;
  const source = (type: string) => t(`inventory.valuation.sourceDocuments.${type}`, { defaultValue: type });

  return (
    <Dialog open={Boolean(sleId)} onOpenChange={(isOpen) => { if (!isOpen) { onClose(); } }}>
      <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
        <DialogHeader>
          <DialogTitle className="text-lg font-semibold" dir="auto">
            {data ? `${t("inventory.cost.title")} · ${itemCode ? itemCode + " · " : ""}${t(`inventory.entryTypes.${data.entry.entryType}`, { defaultValue: data.entry.entryType })} · ${formatDate(data.entry.postingDate)}` : t("common.loading")}
          </DialogTitle>
        </DialogHeader>
        {data ? (
          <div className="flex flex-col gap-4" data-testid="cost-explanation">
            <KeyValues
              entries={[
                [t("inventory.quantity"), <Qty key="q" value={data.entry.quantity} />],
                [t("inventory.unitCost"), <Amount key="u" value={data.entry.unitCost} minorUnits={4} />],
                [t("inventory.costAmount"), <span key="c" data-testid="explained-cost"><Amount value={data.entry.costAmount} /></span>],
                [t("inventory.cost.basis"), data.entry.costedAtExpected ? t("inventory.cost.atExpected") : t("inventory.cost.atActual")],
                [t("inventory.stock.source"), source(data.entry.sourceDocumentType)],
              ]}
            />

            <section>
              <h3 className="mb-1 text-sm font-semibold">{t("inventory.cost.valueEntries")}</h3>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("inventory.cost.valuationDate")}</TableHead>
                    <TableHead>{t("inventory.cost.valueType")}</TableHead>
                    <TableHead className="text-end">{t("inventory.quantity")}</TableHead>
                    <TableHead className="text-end">{t("inventory.valuation.actual")}</TableHead>
                    <TableHead className="text-end">{t("inventory.valuation.expected")}</TableHead>
                    <TableHead>{t("inventory.cost.accounts")}</TableHead>
                    <TableHead>{t("inventory.stock.source")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {data.valueEntries.map((v) => (
                    <TableRow key={v.id} data-testid="value-entry-row">
                      <TableCell>{formatDate(v.valuationDate)}</TableCell>
                      <TableCell>{t(`inventory.cost.valueTypes.${v.valueType}`, { defaultValue: v.valueType })}</TableCell>
                      <TableNumberCell><Qty value={v.valuedQuantity} /></TableNumberCell>
                      <TableNumberCell><Amount value={v.costAmountActual} /></TableNumberCell>
                      <TableNumberCell><Amount value={v.costAmountExpected} /></TableNumberCell>
                      <TableCell>{t(`accountRoles.${v.accountRole}`, { defaultValue: v.accountRole })} / {t(`accountRoles.${v.offsetRole}`, { defaultValue: v.offsetRole })}</TableCell>
                      <TableCell>{source(v.sourceDocumentType)}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </section>

            {data.applications.length > 0 ? (
              <section>
                <h3 className="mb-1 text-sm font-semibold">{t("inventory.cost.applications")}</h3>
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("inventory.cost.direction")}</TableHead>
                      <TableHead>{t("inventory.stock.entryType")}</TableHead>
                      <TableHead>{t("accounting.date")}</TableHead>
                      <TableHead className="text-end">{t("inventory.quantity")}</TableHead>
                      <TableHead className="text-end">{t("inventory.costAmount")}</TableHead>
                      <TableHead>{t("common.status")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {data.applications.map((a) => (
                      <TableRow key={a.id}>
                        <TableCell>{t(`inventory.cost.directions.${a.direction}`, { defaultValue: a.direction })}</TableCell>
                        <TableCell>{t(`inventory.entryTypes.${a.counterpartEntryType}`, { defaultValue: a.counterpartEntryType })}</TableCell>
                        <TableCell>{formatDate(a.counterpartDate)}</TableCell>
                        <TableNumberCell><Qty value={a.quantity} /></TableNumberCell>
                        <TableNumberCell><Amount value={a.costAmount} /></TableNumberCell>
                        <TableCell>{a.supersededBy ? t("inventory.cost.superseded") : a.isReapplication ? t("inventory.cost.reapplied") : t("inventory.cost.current")}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </section>
            ) : null}

            {data.runs.length > 0 ? (
              <section>
                <h3 className="mb-1 text-sm font-semibold">{t("inventory.valuation.runs")}</h3>
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("inventory.valuation.started")}</TableHead>
                      <TableHead>{t("inventory.valuation.trigger")}</TableHead>
                      <TableHead className="text-end">{t("inventory.valuation.adjusted")}</TableHead>
                      <TableHead>{t("common.status")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {data.runs.map((r) => (
                      <TableRow key={r.id}>
                        <TableCell>{formatDateTime(r.startedAt)}</TableCell>
                        <TableCell>{t(`inventory.valuation.triggerKinds.${r.triggerKind}`, { defaultValue: r.triggerKind })} · {source(r.triggerDocumentType)}</TableCell>
                        <TableNumberCell><Amount value={r.amountAdjusted} /></TableNumberCell>
                        <TableCell><DocStatus status={r.status} /></TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </section>
            ) : null}
          </div>
        ) : null}
      </DialogContent>
    </Dialog>
  );
}
