import { Button, Field } from "@quicker/ui";
import { useMutation } from "@tanstack/react-query";
import { Download, FileCheck, Upload } from "lucide-react";
import { useId, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { saveText } from "../accounting/ChartTools";
import { FormError, TextareaField } from "../common";

/** What an import sends: the file's bytes with their type (an Excel workbook), or CSV text typed or read from a file. */
export interface ImportUpload {
  body: string | ArrayBuffer;
  contentType: string;
}

export const XLSX_TYPE = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

interface ImportBoxProps<R> {
  /** Test-id prefix. */
  id: string;
  /** The columns the import reads, for the hint and the template file. */
  columns: readonly string[];
  /** One example row for the template file. */
  example: string;
  templateName: string;
  run: (upload: ImportUpload, dryRun: boolean) => Promise<R>;
  summary: (result: R, dryRun: boolean) => ReactNode;
  onImported?: () => void | Promise<void>;
  disabled?: boolean;
  children?: ReactNode;
}

/**
 * One import, the same on every step of the setup wizard (A-173): a template to download, a file to choose (.xlsx
 * or .csv) or rows to paste, "Check" to run the whole file without keeping anything, "Import" to commit; the first
 * wrong row is named, or the counts of what was done.
 */
export function ImportBox<R>({ id, columns, example, templateName, run, summary, onImported, disabled = false, children }: ImportBoxProps<R>) {
  const { t } = useTranslation();
  const fileId = useId();
  const [text, setText] = useState("");
  const [file, setFile] = useState<{ name: string; upload: ImportUpload } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [outcome, setOutcome] = useState<{ result: R; dryRun: boolean } | null>(null);

  const upload = (): ImportUpload | null => (file ? file.upload : text.trim() ? { body: text, contentType: "text/csv" } : null);
  const mutation = useMutation({
    mutationFn: async (dryRun: boolean) => {
      const payload = upload();
      if (!payload) {
        throw new Error(t("setup.import.nothing"));
      }
      return { result: await run(payload, dryRun), dryRun };
    },
    onSuccess: async (done) => {
      setProblem(null);
      setOutcome(done);
      if (!done.dryRun) {
        await onImported?.();
      }
    },
    onError: (error) => { setOutcome(null); setProblem(toFormProblem(error, t("setup.import.failed"))); },
  });

  const chooseFile = (chosen: File | undefined): void => {
    setOutcome(null);
    setProblem(null);
    if (!chosen) {
      setFile(null);
      return;
    }
    const isWorkbook = chosen.name.toLowerCase().endsWith(".xlsx") || chosen.type === XLSX_TYPE;
    if (isWorkbook) {
      void chosen.arrayBuffer().then((bytes) => { setFile({ name: chosen.name, upload: { body: bytes, contentType: XLSX_TYPE } }); });
    } else {
      void chosen.text().then((csv) => { setFile({ name: chosen.name, upload: { body: csv, contentType: "text/csv" } }); });
    }
  };

  return (
    <div className="flex flex-col gap-3" data-testid={`${id}-import`}>
      <p className="text-xs text-fg-muted">
        {t("setup.import.columns")}{" "}
        <code dir="ltr" className="break-all font-mono">{columns.join(", ")}</code>
      </p>
      {children}
      <div className="flex flex-wrap items-end gap-3">
        <div className="flex flex-col gap-1">
          <label htmlFor={fileId} className="text-sm font-medium">{t("setup.import.file")}</label>
          <input id={fileId} type="file" accept={`.csv,.xlsx,text/csv,${XLSX_TYPE}`} className="text-sm" onChange={(e) => { chooseFile(e.target.files?.[0]); }} data-testid={`${id}-file`} />
        </div>
        <Button type="button" variant="ghost" size="sm" onClick={() => { saveText(`${columns.join(",")}\n${example}\n`, `${templateName}.csv`); }} data-testid={`${id}-template`}>
          <Download aria-hidden="true" />
          {t("setup.import.template")}
        </Button>
      </div>
      <Field label={t("setup.import.paste")}>
        <TextareaField value={text} onChange={(e) => { setText(e.target.value); setOutcome(null); setProblem(null); }} rows={5} dir="ltr" className="font-mono text-xs" disabled={Boolean(file)} data-testid={`${id}-text`} />
      </Field>
      <FormError message={problem?.message ?? null} />
      {outcome ? (
        <p role="status" className={outcome.dryRun ? "rounded-md border border-border bg-surface-sunken p-3 text-sm" : "rounded-md border border-success/40 bg-success-soft p-3 text-sm"} data-testid={`${id}-result`}>
          {summary(outcome.result, outcome.dryRun)}
        </p>
      ) : null}
      <div className="flex flex-wrap gap-2">
        <Button type="button" variant="secondary" onClick={() => { mutation.mutate(true); }} loading={mutation.isPending && mutation.variables} disabled={disabled || !upload()} data-testid={`${id}-check`}>
          <FileCheck aria-hidden="true" />
          {t("setup.import.check")}
        </Button>
        <Button type="button" onClick={() => { mutation.mutate(false); }} loading={mutation.isPending && !mutation.variables} disabled={disabled || !upload()} data-testid={`${id}-run`}>
          <Upload aria-hidden="true" />
          {t("setup.import.run")}
        </Button>
      </div>
    </div>
  );
}
