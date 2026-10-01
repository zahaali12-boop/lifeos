import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field } from "@quicker/ui";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Upload } from "lucide-react";
import { useId, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatNumber } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { FormError, TextareaField } from "../common";

const required = "journal_ref, posting_date, currency, account_code, debit | credit";
const optional = "description, reference, document_date, kind, subledger_type, subledger_ref";

/**
 * Journals from a CSV file or pasted rows, one row per line, grouped into journals by journal_ref. Every journal
 * arrives as a draft to review, submit and post as usual; the file is refused as a whole if a row is wrong.
 */
export function JournalImportDialog({ companyId, open, onOpenChange }: { companyId: string; open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const fileId = useId();
  const [text, setText] = useState("");
  const [created, setCreated] = useState<number | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const run = useMutation({
    mutationFn: async () =>
      unwrap(await api.POST("/api/v1/accounting/companies/{companyId}/journals/import", {
        params: { path: { companyId } },
        body: { journals: [] },
        bodySerializer: () => text,
        headers: { "Content-Type": "text/csv" },
      })),
    onSuccess: async (result) => {
      setProblem(null);
      setCreated(Number(result.created));
      await queryClient.invalidateQueries({ queryKey: ["journals"] });
    },
    onError: (error) => { setCreated(null); setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const close = (isOpen: boolean): void => {
    if (!isOpen) {
      setText("");
      setCreated(null);
      setProblem(null);
    }
    onOpenChange(isOpen);
  };

  return (
    <Dialog open={open} onOpenChange={close}>
      <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
        <div className="flex flex-col gap-4" data-testid="import-journals">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold">{t("journalImport.title")}</DialogTitle>
          </DialogHeader>
          <p className="text-sm text-fg-muted">{t("journalImport.hint")}</p>
          <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs text-fg-muted">
            <dt>{t("journalImport.required")}</dt>
            <dd><code dir="ltr" className="break-all font-mono">{required}</code></dd>
            <dt>{t("journalImport.optional")}</dt>
            <dd><code dir="ltr" className="break-all font-mono">{optional}</code></dd>
          </dl>
          <div className="flex flex-col gap-1">
            <label htmlFor={fileId} className="text-sm font-medium">
              {t("chartTools.file")}
            </label>
            <input id={fileId} type="file" accept=".csv,text/csv" className="text-sm" onChange={(e) => { const file = e.target.files?.[0]; if (file) { void file.text().then(setText); } }} />
          </div>
          <Field label={t("chartTools.orPaste")}>
            <TextareaField value={text} onChange={(e) => { setText(e.target.value); setCreated(null); }} rows={8} dir="ltr" className="font-mono text-xs" data-testid="journal-import-text" />
          </Field>
          <FormError message={problem?.message ?? null} />
          {created !== null ? (
            <p role="status" className="rounded-md border border-success/40 bg-success-soft p-3 text-sm" data-testid="journal-import-result">
              {t("journalImport.created", { count: created, formatted: formatNumber(created) })}
            </p>
          ) : null}
          <DialogFooter>
            <Button type="button" variant="secondary" onClick={() => { close(false); }}>
              {t("common.close")}
            </Button>
            <Button onClick={() => { run.mutate(); }} loading={run.isPending} disabled={!text.trim() || !companyId} data-testid="run-journal-import">
              <Upload aria-hidden="true" />
              {t("chartTools.import")}
            </Button>
          </DialogFooter>
        </div>
      </DialogContent>
    </Dialog>
  );
}
