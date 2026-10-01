import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, CheckCircle2, Lock } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatDate, formatNumber } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, useCompanyContext } from "../inventory/shared";
import { useTaxRegistrations, type TaxReturnDrillDownLine, type TaxReturnPreview } from "./shared";

function startOfMonth(date: Date): string {
  return new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), 1)).toISOString().slice(0, 10);
}
function endOfMonth(date: Date): string {
  return new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth() + 1, 0)).toISOString().slice(0, 10);
}

/** Tax returns (roadmap 5.3d): preview a period's return computed from the tax ledger, reconciled to the books, drill down into a box, and file it. */
export function TaxReturnsPage() {
  const { t } = useTranslation();
  const can = useCan();
  const mayFile = can("tax.return.file");
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const registrations = useTaxRegistrations(companyId);
  const [regimeId, setRegimeId] = useState("");
  const [periodStart, setPeriodStart] = useState(() => startOfMonth(new Date()));
  const [periodEnd, setPeriodEnd] = useState(() => endOfMonth(new Date()));
  const [drillDownBox, setDrillDownBox] = useState<string | null>(null);
  const [reference, setReference] = useState("");
  const [fileProblem, setFileProblem] = useState<FormProblem | null>(null);
  const queryClient = useQueryClient();

  const effectiveRegimeId = regimeId !== "" ? regimeId : ((registrations.data ?? [])[0]?.regimeId ?? "");

  const periods = useQuery({
    queryKey: ["tax-return-periods", companyId, effectiveRegimeId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/tax/returns", { params: { query: effectiveRegimeId ? { companyId, regimeId: effectiveRegimeId } : { companyId } } })),
  });

  const preview = useQuery({
    queryKey: ["tax-return-preview", companyId, effectiveRegimeId, periodStart, periodEnd],
    enabled: Boolean(companyId && effectiveRegimeId && periodStart && periodEnd),
    queryFn: async () => unwrap(await api.GET("/api/v1/tax/returns/preview", { params: { query: { companyId, regimeId: effectiveRegimeId, periodStart, periodEnd } } })),
  });

  const drilldown = useQuery({
    queryKey: ["tax-return-drilldown", companyId, effectiveRegimeId, periodStart, periodEnd, drillDownBox],
    enabled: Boolean(companyId && effectiveRegimeId && drillDownBox),
    queryFn: async () => unwrap(await api.GET("/api/v1/tax/returns/drilldown", { params: { query: { companyId, regimeId: effectiveRegimeId, periodStart, periodEnd, box: drillDownBox ?? "" } } })),
  });

  const file = useMutation({
    mutationFn: async () =>
      unwrap(await api.POST("/api/v1/tax/returns/file", { body: { companyId, regimeId: effectiveRegimeId, periodStart, periodEnd, reference: reference || null } })),
    onSuccess: async () => {
      setFileProblem(null);
      setReference("");
      await Promise.all([queryClient.invalidateQueries({ queryKey: ["tax-return-periods"] }), queryClient.invalidateQueries({ queryKey: ["tax-return-preview"] })]);
    },
    onError: (error) => {
      setFileProblem(toFormProblem(error, t("common.saveFailed")));
    },
  });

  const preview_ = preview.data as TaxReturnPreview | undefined;
  const alreadyFiled = useMemo(
    () => (periods.data ?? []).some((p) => p.status === "filed" && p.periodStart === periodStart && p.periodEnd === periodEnd),
    [periods.data, periodStart, periodEnd],
  );

  return (
    <>
      <PageHeader title={t("nav.taxReturns")} description={t("tax.returnsDescription")} />
      <div className="mt-3 grid gap-3 sm:grid-cols-5">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("tax.regime")}>
          <SelectField value={effectiveRegimeId} onChange={(e) => { setRegimeId(e.target.value); }} data-testid="return-regime">
            {(registrations.data ?? []).map((r) => (
              <option key={r.regimeId} value={r.regimeId}>
                {r.regimeCode}
              </option>
            ))}
          </SelectField>
        </Field>
        <Field label={t("tax.periodStart")}>
          <TextField type="date" value={periodStart} onChange={(e) => { setPeriodStart(e.target.value); }} dir="ltr" data-testid="period-start" />
        </Field>
        <Field label={t("tax.periodEnd")}>
          <TextField type="date" value={periodEnd} onChange={(e) => { setPeriodEnd(e.target.value); }} dir="ltr" data-testid="period-end" />
        </Field>
      </div>

      {!companyId || !effectiveRegimeId ? (
        <p className="mt-4 text-sm text-fg-muted">{t("tax.selectCompanyAndRegime")}</p>
      ) : preview.isLoading ? (
        <p className="mt-4 text-sm text-fg-muted">{t("common.loading")}</p>
      ) : preview_ ? (
        <div className="mt-4 flex flex-col gap-4">
          <div className="flex flex-wrap items-center gap-3 rounded-lg border border-border p-4">
            {preview_.reconciled ? (
              <Badge tone="success" className="inline-flex items-center gap-1">
                <CheckCircle2 aria-hidden="true" className="size-3.5" />
                {t("tax.reconciled")}
              </Badge>
            ) : (
              <Badge tone="danger" className="inline-flex items-center gap-1">
                <AlertTriangle aria-hidden="true" className="size-3.5" />
                {t("tax.notReconciled")}
              </Badge>
            )}
            {alreadyFiled ? (
              <Badge tone="neutral" className="inline-flex items-center gap-1">
                <Lock aria-hidden="true" className="size-3.5" />
                {t("tax.periodFiled")}
              </Badge>
            ) : null}
            <span className="ms-auto text-sm text-fg-muted">{t("tax.netPayable")}</span>
            <span className="text-lg font-semibold tabular" dir="ltr">
              {formatNumber(preview_.netPayable)} {preview_.currency}
            </span>
          </div>

          <div>
            <h3 className="mb-2 font-semibold">{t("tax.boxes")}</h3>
            <Table aria-label={t("tax.boxes")}>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("tax.box")}</TableHead>
                  <TableHead className="text-end">{t("tax.baseAmount")}</TableHead>
                  <TableHead className="text-end">{t("tax.taxAmount")}</TableHead>
                  <TableHead>
                    <span className="sr-only">{t("common.actions")}</span>
                  </TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {preview_.boxes.map((box) => (
                  <TableRow key={box.code} data-testid="box-row">
                    <TableCell dir="ltr">{box.code}</TableCell>
                    <TableCell className="text-end tabular" dir="ltr">{formatNumber(box.baseAmount)}</TableCell>
                    <TableCell className="text-end tabular" dir="ltr">{formatNumber(box.taxAmount)}</TableCell>
                    <TableCell className="text-end">
                      <Button variant="ghost" size="sm" onClick={() => { setDrillDownBox(box.code); }} data-testid="drilldown-box">
                        {t("tax.drillDown")}
                      </Button>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>

          <div>
            <h3 className="mb-2 font-semibold">{t("tax.reconciliation")}</h3>
            <Table aria-label={t("tax.reconciliation")}>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("tax.code")}</TableHead>
                  <TableHead>{t("tax.accountRole")}</TableHead>
                  <TableHead className="text-end">{t("tax.ledgerMovement")}</TableHead>
                  <TableHead className="text-end">{t("tax.entriesMovement")}</TableHead>
                  <TableHead className="text-end">{t("tax.difference")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {preview_.reconciliation.map((row, index) => (
                  <TableRow key={index} data-testid="reconciliation-row">
                    <TableCell dir="ltr">
                      <bdi>{row.taxCode}</bdi>
                    </TableCell>
                    <TableCell dir="ltr">{row.accountRole}</TableCell>
                    <TableCell className="text-end tabular" dir="ltr">{formatNumber(row.ledgerMovement)}</TableCell>
                    <TableCell className="text-end tabular" dir="ltr">{formatNumber(row.entriesMovement)}</TableCell>
                    <TableCell className={`text-end tabular ${Number(row.difference) !== 0 ? "text-danger" : ""}`} dir="ltr">
                      {formatNumber(row.difference)}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>

          {mayFile ? (
            <div className="rounded-lg border border-border p-4">
              <FormError message={fileProblem?.message ?? null} />
              <div className="flex flex-wrap items-end gap-3">
                <Field label={t("tax.reference")}>
                  <TextField value={reference} onChange={(e) => { setReference(e.target.value); }} dir="ltr" data-testid="return-reference" />
                </Field>
                <Button
                  onClick={() => { file.mutate(); }}
                  loading={file.isPending}
                  disabled={!preview_.reconciled || alreadyFiled}
                  data-testid="file-return"
                >
                  <Lock aria-hidden="true" />
                  {t("tax.fileReturn")}
                </Button>
              </div>
            </div>
          ) : null}

          <div>
            <h3 className="mb-2 font-semibold">{t("tax.filedPeriods")}</h3>
            <Table aria-label={t("tax.filedPeriods")}>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("tax.validity")}</TableHead>
                  <TableHead>{t("common.status")}</TableHead>
                  <TableHead>{t("tax.reference")}</TableHead>
                  <TableHead className="text-end">{t("tax.netPayable")}</TableHead>
                  <TableHead>{t("tax.filedAt")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {(periods.data ?? []).map((period) => (
                  <TableRow key={period.id} data-testid="period-row">
                    <TableCell dir="ltr">
                      {formatDate(period.periodStart)} – {formatDate(period.periodEnd)}
                    </TableCell>
                    <TableCell>
                      <Badge tone={period.status === "filed" ? "neutral" : "success"}>{t(`tax.value.${period.status}`)}</Badge>
                    </TableCell>
                    <TableCell dir="ltr">{period.reference ?? "—"}</TableCell>
                    <TableCell className="text-end tabular" dir="ltr">{period.netPayable !== null ? formatNumber(period.netPayable) : "—"}</TableCell>
                    <TableCell dir="ltr">{period.filedAt ? formatDate(period.filedAt) : "—"}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </div>
      ) : null}

      <Dialog open={Boolean(drillDownBox)} onOpenChange={(isOpen) => { if (!isOpen) { setDrillDownBox(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold" dir="ltr">
              {t("tax.drillDownTitle", { box: drillDownBox })}
            </DialogTitle>
          </DialogHeader>
          <Table aria-label={t("tax.drillDownTitle", { box: drillDownBox })}>
            <TableHeader>
              <TableRow>
                <TableHead>{t("tax.document")}</TableHead>
                <TableHead>{t("tax.direction")}</TableHead>
                <TableHead>{t("tax.code")}</TableHead>
                <TableHead className="text-end">{t("tax.currentRate")}</TableHead>
                <TableHead className="text-end">{t("tax.baseAmount")}</TableHead>
                <TableHead className="text-end">{t("tax.taxAmount")}</TableHead>
                <TableHead>{t("common.date")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {((drilldown.data ?? []) as TaxReturnDrillDownLine[]).map((line) => (
                <TableRow key={line.id} data-testid="drilldown-line">
                  <TableCell dir="ltr">
                    {line.sourceDocumentNumber ?? line.sourceDocumentId}
                    {line.isReversal ? <Badge tone="neutral" className="ms-2">{t("tax.reversal")}</Badge> : null}
                  </TableCell>
                  <TableCell>{t(`tax.value.${line.direction}`)}</TableCell>
                  <TableCell dir="ltr">
                    <bdi>{line.taxCode}</bdi>
                  </TableCell>
                  <TableCell className="text-end tabular" dir="ltr">{formatNumber(line.ratePct)}%</TableCell>
                  <TableCell className="text-end tabular" dir="ltr">{formatNumber(line.baseFc)}</TableCell>
                  <TableCell className="text-end tabular" dir="ltr">{formatNumber(line.taxFc)}</TableCell>
                  <TableCell dir="ltr">{formatDate(line.postingDate)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
          <DialogFooter>
            <Button variant="secondary" onClick={() => { setDrillDownBox(null); }}>
              {t("common.close")}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  );
}
