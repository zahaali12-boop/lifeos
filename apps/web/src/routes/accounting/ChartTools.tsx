import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Download, Upload } from "lucide-react";
import { useId, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { FormError, SelectField, TextareaField } from "../common";

type ImportResult = components["schemas"]["ImportResult"];

/** Saves text as a file in the browser (a CSV export). */
export function saveText(text: string, fileName: string, type = "text/csv;charset=utf-8"): void {
  const url = URL.createObjectURL(new Blob([text], { type }));
  const link = document.createElement("a");
  link.href = url;
  link.download = fileName;
  document.body.append(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}

/** The chart as CSV, one row per account, in the format the import reads back. */
export function ExportChartButton({ chartId, fileName }: { chartId: string; fileName: string }) {
  const { t } = useTranslation();
  const [problem, setProblem] = useState<string | null>(null);
  const exportCsv = useMutation({
    mutationFn: async () => unwrap(await api.GET("/api/v1/accounting/charts/{chartId}/export", { params: { path: { chartId } }, parseAs: "text" })),
    onSuccess: (csv) => { setProblem(null); saveText(csv, fileName); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed")).message); },
  });
  return (
    <span className="flex items-center gap-2">
      <Button variant="secondary" onClick={() => { exportCsv.mutate(); }} loading={exportCsv.isPending} data-testid="export-chart">
        <Download aria-hidden="true" />
        {t("chartTools.export")}
      </Button>
      {problem ? <span role="alert" className="text-sm text-danger">{problem}</span> : null}
    </span>
  );
}

/**
 * Imports accounts from CSV (the export format) or pasted text: rows are upserted by code, parents may come after
 * their children, and the whole file is refused if one row is wrong, with the reason.
 */
export function ImportChartDialog({ chartId, open, onOpenChange }: { chartId: string; open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const fileId = useId();
  const [text, setText] = useState("");
  const [result, setResult] = useState<ImportResult | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const importCsv = useMutation({
    mutationFn: async () =>
      unwrap(await api.POST("/api/v1/accounting/charts/{chartId}/import", {
        params: { path: { chartId } },
        body: { accounts: [] },
        bodySerializer: () => text,
        headers: { "Content-Type": "text/csv" },
      })),
    onSuccess: async (imported) => { setProblem(null); setResult(imported); await queryClient.invalidateQueries({ queryKey: ["chart", chartId] }); },
    onError: (error) => { setResult(null); setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const close = (isOpen: boolean): void => {
    if (!isOpen) {
      setText("");
      setResult(null);
      setProblem(null);
    }
    onOpenChange(isOpen);
  };

  return (
    <Dialog open={open} onOpenChange={close}>
      <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
        <div className="flex flex-col gap-4" data-testid="import-chart">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold">{t("chartTools.importTitle")}</DialogTitle>
          </DialogHeader>
          <p className="text-sm text-fg-muted">{t("chartTools.importHint")}</p>
          <div className="flex flex-col gap-1">
            <label htmlFor={fileId} className="text-sm font-medium">
              {t("chartTools.file")}
            </label>
            <input
              id={fileId}
              type="file"
              accept=".csv,text/csv"
              className="text-sm"
              onChange={(e) => {
                const file = e.target.files?.[0];
                if (file) {
                  void file.text().then(setText);
                }
              }}
              data-testid="import-file"
            />
          </div>
          <Field label={t("chartTools.orPaste")}>
            <TextareaField value={text} onChange={(e) => { setText(e.target.value); setResult(null); }} rows={8} dir="ltr" className="font-mono text-xs" data-testid="import-text" />
          </Field>
          <FormError message={problem?.message ?? null} />
          {result ? (
            <p role="status" className="rounded-md border border-success/40 bg-success-soft p-3 text-sm" data-testid="import-result">
              {t("chartTools.imported", { created: formatNumber(result.created), updated: formatNumber(result.updated), unchanged: formatNumber(result.unchanged), total: formatNumber(result.total) })}
            </p>
          ) : null}
          <DialogFooter>
            <Button type="button" variant="secondary" onClick={() => { close(false); }}>
              {t("common.close")}
            </Button>
            <Button onClick={() => { importCsv.mutate(); }} loading={importCsv.isPending} disabled={!text.trim()} data-testid="run-import">
              <Upload aria-hidden="true" />
              {t("chartTools.import")}
            </Button>
          </DialogFooter>
        </div>
      </DialogContent>
    </Dialog>
  );
}

/**
 * Statutory mapping: each postable account of the chart to an account of a statutory chart (the Iraqi unified chart,
 * for instance), for statutory reporting. Only the rows changed here are sent; an emptied row removes its mapping.
 */
export function StatutoryMappingDialog({ chartId, open, onOpenChange }: { chartId: string; open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [statutoryCode, setStatutoryCode] = useState("");
  const [changes, setChanges] = useState<Record<string, string>>({});
  const [onlyUnmapped, setOnlyUnmapped] = useState(false);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const statutory = useQuery({ queryKey: ["statutory-charts"], enabled: open, queryFn: async () => unwrap(await api.GET("/api/v1/accounting/statutory-charts")) });
  const selected = statutory.data?.find((s) => s.code === statutoryCode) ?? statutory.data?.[0];
  const mappings = useQuery({
    queryKey: ["mappings", chartId, selected?.code],
    enabled: open && Boolean(selected),
    queryFn: async () => unwrap(await api.GET("/api/v1/accounting/charts/{chartId}/mappings/{statutoryChartCode}", { params: { path: { chartId, statutoryChartCode: selected?.code ?? "" } } })),
  });
  const choices = useMemo(() => selected?.accounts ?? [], [selected]);
  const save = useMutation({
    mutationFn: async () =>
      unwrap(await api.PUT("/api/v1/accounting/charts/{chartId}/mappings/{statutoryChartCode}", {
        params: { path: { chartId, statutoryChartCode: selected?.code ?? "" } },
        body: Object.entries(changes).map(([accountCode, code]) => ({ accountCode, statutoryCode: code })),
      })),
    onSuccess: async () => { setChanges({}); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["mappings", chartId] }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const rows = (mappings.data ?? []).map((m) => ({ ...m, value: changes[m.accountCode] ?? m.statutoryCode ?? "" }));
  const unmapped = rows.filter((r) => !r.value).length;
  const visible = onlyUnmapped ? rows.filter((r) => !r.value) : rows;

  return (
    <Dialog open={open} onOpenChange={(isOpen) => { if (!isOpen) { setChanges({}); setProblem(null); } onOpenChange(isOpen); }}>
      <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
        <div className="flex flex-col gap-4" data-testid="statutory-mapping">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold">{t("chartTools.mappingTitle")}</DialogTitle>
          </DialogHeader>
          <p className="text-sm text-fg-muted">{t("chartTools.mappingHint")}</p>
          <div className="flex flex-wrap items-end gap-4">
            <Field label={t("chartTools.statutoryChart")}>
              <SelectField value={selected?.code ?? ""} onChange={(e) => { setStatutoryCode(e.target.value); setChanges({}); }} data-testid="statutory-chart">
                {(statutory.data ?? []).map((s) => (
                  <option key={s.code} value={s.code}>
                    {s.code} · {localized(s.name)}
                  </option>
                ))}
              </SelectField>
            </Field>
            <label className="flex items-center gap-2 pb-2 text-sm">
              <input type="checkbox" checked={onlyUnmapped} onChange={(e) => { setOnlyUnmapped(e.target.checked); }} data-testid="only-unmapped" />
              {t("chartTools.onlyUnmapped")}
            </label>
            {mappings.data ? (
              <Badge tone={unmapped === 0 ? "success" : "warning"} data-testid="unmapped-count">
                {t("chartTools.unmapped", { count: unmapped })}
              </Badge>
            ) : null}
          </div>
          {selected?.notes ? <p className="text-xs text-fg-muted">{selected.notes}</p> : null}
          <FormError message={problem?.message ?? null} />
          <div className="max-h-[50dvh] overflow-y-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("chartTools.account")}</TableHead>
                  <TableHead>{t("chartTools.statutoryAccount")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {visible.map((row) => (
                  <TableRow key={row.accountId} data-testid="mapping-row">
                    <TableCell>
                      <span dir="ltr" className="font-mono text-xs">{row.accountCode}</span> {localized(row.accountName)}
                    </TableCell>
                    <TableCell>
                      <SelectField value={row.value} onChange={(e) => { setChanges({ ...changes, [row.accountCode]: e.target.value }); }} aria-label={`${t("chartTools.statutoryAccount")} ${row.accountCode}`} data-testid={`mapping-${row.accountCode}`}>
                        <option value="">{t("chartTools.notMapped")}</option>
                        {choices.map((a) => (
                          <option key={a.code} value={a.code}>
                            {"\u00a0".repeat(Math.max(0, Number(a.level) - 1) * 2)}
                            {a.code} · {localized(a.name)}
                          </option>
                        ))}
                      </SelectField>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
          <DialogFooter>
            <Button type="button" variant="secondary" onClick={() => { onOpenChange(false); }}>
              {t("common.close")}
            </Button>
            <Button onClick={() => { save.mutate(); }} loading={save.isPending} disabled={Object.keys(changes).length === 0} data-testid="save-mappings">
              {t("chartTools.saveMappings", { count: Object.keys(changes).length })}
            </Button>
          </DialogFooter>
        </div>
      </DialogContent>
    </Dialog>
  );
}
