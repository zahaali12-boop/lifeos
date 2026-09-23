import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate, useSearch } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus, Trash2 } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { AttachmentsPanel } from "../AttachmentsPanel";
import { RecordDiscussion, RecordHistory } from "../RecordDiscussion";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { Tabs } from "../inventory/shared";
import { JournalImportDialog } from "./JournalImport";
import { Amount, CompanySelect, StatusBadge, today, useCompanies, useCompanySelection } from "./shared";

type Journal = components["schemas"]["ManualJournalSummary"];

interface LineForm {
  accountCode: string;
  debit: string;
  credit: string;
}

interface JournalForm {
  kind: string;
  postingDate: string;
  currency: string;
  descriptionEn: string;
  descriptionAr: string;
  reference: string;
  autoReverseOn: string;
  lines: LineForm[];
}

const kinds = ["manual", "opening", "accrual", "allocation"];
const statuses = ["", "draft", "pending_approval", "approved", "rejected", "posted", "cancelled"];

function emptyForm(currency: string): JournalForm {
  return { kind: "manual", postingDate: today(), currency, descriptionEn: "", descriptionAr: "", reference: "", autoReverseOn: "", lines: [{ accountCode: "", debit: "", credit: "" }, { accountCode: "", debit: "", credit: "" }] };
}

function toForm(journal: Journal): JournalForm {
  return {
    kind: journal.kind,
    postingDate: journal.postingDate,
    currency: journal.currency,
    descriptionEn: journal.description.en ?? "",
    descriptionAr: journal.description.ar ?? "",
    reference: journal.reference ?? "",
    autoReverseOn: journal.autoReverseOn ?? "",
    lines: (journal.lines ?? []).map((line) => ({ accountCode: line.accountCode, debit: Number(line.debit) > 0 ? String(line.debit) : "", credit: Number(line.credit) > 0 ? String(line.credit) : "" })),
  };
}

function toRequest(form: JournalForm): components["schemas"]["SaveJournalRequest"] {
  return {
    kind: form.kind,
    postingDate: form.postingDate,
    currency: form.currency,
    description: { en: form.descriptionEn, ...(form.descriptionAr ? { ar: form.descriptionAr } : {}) },
    reference: form.reference || null,
    autoReverse: form.kind === "accrual" && Boolean(form.autoReverseOn),
    autoReverseOn: form.kind === "accrual" && form.autoReverseOn ? form.autoReverseOn : null,
    rateType: "spot",
    lines: form.lines.filter((line) => line.accountCode.trim()).map((line) => ({ accountCode: line.accountCode.trim(), debit: line.debit || "0", credit: line.credit || "0" })),
  };
}

function sum(lines: LineForm[], side: "debit" | "credit"): number {
  return lines.reduce((total, line) => total + (Number(line[side]) || 0), 0);
}

/** Manual journals: the list by status, a line editor, and the lifecycle actions (submit, approve, reject, post, cancel, correct). */
export function JournalsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const search = useSearch({ strict: false });
  const companies = useCompanies();
  const [companyId, setCompanyId] = useCompanySelection(companies.data);
  const company = companies.data?.find((c) => c.id === companyId);
  const [status, setStatus] = useState("");
  const [editing, setEditing] = useState<{ id: string | null; form: JournalForm } | null>(null);
  const [importing, setImporting] = useState(false);
  const [reason, setReason] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [detailTab, setDetailTab] = useState("lines");
  const openId = search.open;

  const journals = useInfiniteQuery({
    queryKey: ["journals", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async ({ pageParam }) => unwrap(await api.GET("/api/v1/accounting/companies/{companyId}/journals", { params: { path: { companyId }, query: { limit: 100, ...(status ? { status } : {}), ...(pageParam ? { cursor: pageParam } : {}) } } })),
    initialPageParam: "",
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  const journal = useQuery({
    queryKey: ["journal", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/accounting/journals/{journalId}", { params: { path: { journalId: openId ?? "" } } })),
  });

  const refresh = async (id?: string | null): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["journals"] });
    if (id) {
      await queryClient.invalidateQueries({ queryKey: ["journal", id] });
    }
  };
  const open = (id: string | null): void => { setDetailTab("lines"); void navigate({ to: "/accounting/journals", search: id ? { open: id } : {} }); };

  const save = useMutation({
    mutationFn: async (input: { id: string | null; form: JournalForm }) =>
      input.id
        ? unwrap(await api.PUT("/api/v1/accounting/journals/{journalId}", { params: { path: { journalId: input.id } }, body: toRequest(input.form) }))
        : unwrap(await api.POST("/api/v1/accounting/companies/{companyId}/journals", { params: { path: { companyId } }, body: toRequest(input.form) })),
    onSuccess: async (saved) => {
      setEditing(null);
      setProblem(null);
      await refresh(saved.id);
      open(saved.id);
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const act = useMutation({
    mutationFn: async (action: "submit" | "approve" | "reject" | "cancel" | "post" | "correct") => {
      const journalId = openId ?? "";
      switch (action) {
        case "submit":
          return unwrap(await api.POST("/api/v1/accounting/journals/{journalId}/submit", { params: { path: { journalId } } }));
        case "approve":
          return unwrap(await api.POST("/api/v1/accounting/journals/{journalId}/approve", { params: { path: { journalId } } }));
        case "reject":
          return unwrap(await api.POST("/api/v1/accounting/journals/{journalId}/reject", { params: { path: { journalId } }, body: { reason } }));
        case "cancel":
          return unwrap(await api.POST("/api/v1/accounting/journals/{journalId}/cancel", { params: { path: { journalId } } }));
        case "post":
          return unwrap(await api.POST("/api/v1/accounting/journals/{journalId}/post", { params: { path: { journalId } } }));
        case "correct":
          return unwrap(await api.POST("/api/v1/accounting/journals/{journalId}/correct", { params: { path: { journalId } }, body: { reason } }));
      }
    },
    onSuccess: async (result) => {
      setProblem(null);
      setReason("");
      await refresh(openId);
      if (result.id !== openId) {
        open(result.id);
      }
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<Journal, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("accounting.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "postingDate", accessorKey: "postingDate", header: t("accounting.date"), size: 120, cell: ({ row }) => formatDate(row.original.postingDate) },
      { id: "kind", accessorKey: "kind", header: t("accounting.kind"), size: 110, cell: ({ row }) => t(`accounting.kinds.${row.original.kind}`) },
      { id: "description", accessorFn: (row) => localized(row.description), header: t("accounting.description"), size: 260 },
      { id: "total", accessorKey: "totalDebit", header: t("accounting.total"), size: 140, cell: ({ row }) => <Amount value={row.original.totalDebit} /> },
      { id: "currency", accessorKey: "currency", header: t("accounting.currency"), size: 90 },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 140, cell: ({ row }) => <StatusBadge status={row.original.status} label={t(`accounting.statuses.${row.original.status}`)} /> },
    ],
    [t],
  );

  const rows = journals.data?.pages.flatMap((page) => page.items) ?? [];
  const detail = journal.data;
  const editable = detail?.status === "draft" || detail?.status === "rejected";

  const submitForm = (event: FormEvent): void => {
    event.preventDefault();
    if (editing) {
      save.mutate(editing);
    }
  };
  const updateLine = (index: number, patch: Partial<LineForm>): void => {
    if (!editing) {
      return;
    }
    const lines = editing.form.lines.map((line, i) => (i === index ? { ...line, ...patch } : line));
    setEditing({ ...editing, form: { ...editing.form, lines } });
  };

  return (
    <>
      <PageHeader
        title={t("accounting.journals")}
        description={t("accounting.journalsDescription")}
        actions={
          <div className="flex flex-wrap gap-2">
            <Button variant="secondary" onClick={() => { setImporting(true); }} disabled={!companyId} data-testid="import-journals-open">
              {t("journalImport.open")}
            </Button>
            <Button onClick={() => { setProblem(null); setEditing({ id: null, form: emptyForm(company?.functionalCurrency ?? "IQD") }); }} disabled={!companyId} data-testid="new-journal">
              <Plus aria-hidden="true" />
              {t("accounting.newJournal")}
            </Button>
          </div>
        }
      />
      <div className="mb-4 grid gap-3 sm:grid-cols-3">
        <CompanySelect companies={companies.data ?? []} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }}>
            {statuses.map((s) => (
              <option key={s} value={s}>
                {s ? t(`accounting.statuses.${s}`) : t("accounting.anyStatus")}
              </option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<Journal> label="accounting.journals" columns={columns} data={rows} rowKey={(row) => row.id} loading={journals.isPending && Boolean(companyId)} emptyTitle={t("accounting.noJournals")} emptyDescription={t("accounting.noJournalsHint")} onOpen={(row) => { open(row.id); }} />
      {journals.hasNextPage ? (
        <Button variant="secondary" className="mt-3" onClick={() => { void journals.fetchNextPage(); }} loading={journals.isFetchingNextPage}>
          {t("common.loadMore")}
        </Button>
      ) : null}

      <Dialog open={Boolean(openId) && !editing} onOpenChange={(isOpen) => { if (!isOpen) { open(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold" dir="auto">
              {detail ? `${detail.number} · ${formatDate(detail.postingDate)}` : t("common.loading")}
            </DialogTitle>
          </DialogHeader>
          {detail ? (
            <div className="flex flex-col gap-4" data-testid="journal-detail">
              <div className="flex flex-wrap items-center gap-2 text-sm">
                <StatusBadge status={detail.status} label={t(`accounting.statuses.${detail.status}`)} />
                <span>{t(`accounting.kinds.${detail.kind}`)}</span>
                <span className="text-fg-muted">{localized(detail.description)}</span>
                {detail.journalEntryId ? (
                  <Link to="/accounting/journal-entries" search={{ open: detail.journalEntryId }} className="underline-offset-2 hover:underline">
                    {t("accounting.viewEntry")}
                  </Link>
                ) : null}
                {detail.correctsJournalId ? (
                  <button type="button" className="underline-offset-2 hover:underline" onClick={() => { open(detail.correctsJournalId ?? null); }}>
                    {t("accounting.correctsJournal")}
                  </button>
                ) : null}
                {detail.correctedByJournalId ? (
                  <button type="button" className="underline-offset-2 hover:underline" onClick={() => { open(detail.correctedByJournalId ?? null); }}>
                    {t("accounting.correctedBy")}
                  </button>
                ) : null}
                {detail.rejectionReason ? <span className="text-danger">{t("accounting.rejectedBecause", { reason: detail.rejectionReason })}</span> : null}
              </div>
              <Tabs
                value={detailTab}
                onChange={setDetailTab}
                tabs={[
                  { id: "lines", label: t("accounting.lines"), testId: "journal-tab-lines" },
                  { id: "discussion", label: t("comments.tab"), testId: "journal-tab-discussion" },
                  { id: "history", label: t("history.tab"), testId: "journal-tab-history" },
                ]}
              />
              {detailTab === "lines" ? (
              <>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>#</TableHead>
                    <TableHead>{t("accounting.account")}</TableHead>
                    <TableHead className="text-end">{t("accounting.debit")}</TableHead>
                    <TableHead className="text-end">{t("accounting.credit")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {(detail.lines ?? []).map((line) => (
                    <TableRow key={line.id}>
                      <TableCell>{String(line.lineNo)}</TableCell>
                      <TableCell>
                        <span dir="ltr">{line.accountCode}</span> {localized(line.accountName)}
                      </TableCell>
                      <TableNumberCell><Amount value={line.debit} /></TableNumberCell>
                      <TableNumberCell><Amount value={line.credit} /></TableNumberCell>
                    </TableRow>
                  ))}
                  <TableRow className="font-semibold">
                    <TableCell colSpan={2}>{t("accounting.totals")}</TableCell>
                    <TableNumberCell><Amount value={detail.totalDebit} /></TableNumberCell>
                    <TableNumberCell><Amount value={detail.totalCredit} /></TableNumberCell>
                  </TableRow>
                </TableBody>
              </Table>
              <AttachmentsPanel entityType="manual_journal" entityId={detail.id} />
              </>
              ) : null}
              {detailTab === "discussion" ? <RecordDiscussion entityType="manual_journal" entityId={detail.id} files={false} /> : null}
              {detailTab === "history" ? <RecordHistory entityType="manual_journal" entityId={detail.id} /> : null}
              <FormError message={problem?.message ?? null} />
              {detail.status === "pending_approval" || detail.status === "posted" ? (
                <Field label={t("common.reason")}>
                  <TextField value={reason} onChange={(e) => { setReason(e.target.value); }} data-testid="journal-reason" />
                </Field>
              ) : null}
              <DialogFooter>
                {editable ? (
                  <Button variant="secondary" onClick={() => { setProblem(null); setEditing({ id: detail.id, form: toForm(detail) }); }} data-testid="edit-journal">
                    {t("accounting.edit")}
                  </Button>
                ) : null}
                {editable ? (
                  <Button variant="secondary" onClick={() => { act.mutate("submit"); }} loading={act.isPending} data-testid="submit-journal">
                    {t("accounting.submit")}
                  </Button>
                ) : null}
                {detail.status === "pending_approval" ? (
                  <>
                    <Button variant="secondary" onClick={() => { act.mutate("reject"); }} loading={act.isPending}>
                      {t("accounting.reject")}
                    </Button>
                    <Button onClick={() => { act.mutate("approve"); }} loading={act.isPending} data-testid="approve-journal">
                      {t("accounting.approve")}
                    </Button>
                  </>
                ) : null}
                {detail.status === "draft" || detail.status === "approved" ? (
                  <Button onClick={() => { act.mutate("post"); }} loading={act.isPending} data-testid="post-journal">
                    {t("accounting.post")}
                  </Button>
                ) : null}
                {detail.status !== "posted" && detail.status !== "cancelled" ? (
                  <Button variant="secondary" onClick={() => { act.mutate("cancel"); }} loading={act.isPending}>
                    {t("common.cancel")}
                  </Button>
                ) : null}
                {detail.status === "posted" && !detail.correctedByJournalId ? (
                  <Button variant="secondary" onClick={() => { act.mutate("correct"); }} loading={act.isPending} data-testid="correct-journal">
                    {t("accounting.correct")}
                  </Button>
                ) : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(editing)} onOpenChange={(isOpen) => { if (!isOpen) { setEditing(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {editing ? (
            <form onSubmit={submitForm} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{editing.id ? t("accounting.editJournal") : t("accounting.newJournal")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label={t("accounting.kind")}>
                  <SelectField value={editing.form.kind} onChange={(e) => { setEditing({ ...editing, form: { ...editing.form, kind: e.target.value } }); }}>
                    {kinds.map((k) => (
                      <option key={k} value={k}>
                        {t(`accounting.kinds.${k}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("accounting.postingDate")} required error={problem?.fields.postingDate}>
                  <TextField type="date" value={editing.form.postingDate} onChange={(e) => { setEditing({ ...editing, form: { ...editing.form, postingDate: e.target.value } }); }} required dir="ltr" data-testid="journal-date" />
                </Field>
                <Field label={t("accounting.currency")} required error={problem?.fields.currency}>
                  <TextField value={editing.form.currency} onChange={(e) => { setEditing({ ...editing, form: { ...editing.form, currency: e.target.value.toUpperCase() } }); }} required dir="ltr" maxLength={3} />
                </Field>
                <Field label={t("accounting.descriptionEn")} required>
                  <TextField value={editing.form.descriptionEn} onChange={(e) => { setEditing({ ...editing, form: { ...editing.form, descriptionEn: e.target.value } }); }} required data-testid="journal-description" />
                </Field>
                <Field label={t("accounting.descriptionAr")}>
                  <TextField value={editing.form.descriptionAr} onChange={(e) => { setEditing({ ...editing, form: { ...editing.form, descriptionAr: e.target.value } }); }} dir="rtl" />
                </Field>
                <Field label={t("accounting.reference")}>
                  <TextField value={editing.form.reference} onChange={(e) => { setEditing({ ...editing, form: { ...editing.form, reference: e.target.value } }); }} />
                </Field>
                {editing.form.kind === "accrual" ? (
                  <Field label={t("accounting.autoReverseOn")} required error={problem?.fields.autoReverseOn}>
                    <TextField type="date" value={editing.form.autoReverseOn} onChange={(e) => { setEditing({ ...editing, form: { ...editing.form, autoReverseOn: e.target.value } }); }} required dir="ltr" />
                  </Field>
                ) : null}
              </div>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("accounting.accountCode")}</TableHead>
                    <TableHead className="text-end">{t("accounting.debit")}</TableHead>
                    <TableHead className="text-end">{t("accounting.credit")}</TableHead>
                    <TableHead />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {editing.form.lines.map((line, index) => (
                    <TableRow key={index}>
                      <TableCell>
                        <TextField aria-label={t("accounting.accountCode")} value={line.accountCode} onChange={(e) => { updateLine(index, { accountCode: e.target.value }); }} dir="ltr" data-testid={`line-account-${index}`} />
                      </TableCell>
                      <TableNumberCell>
                        <TextField aria-label={t("accounting.debit")} inputMode="decimal" value={line.debit} onChange={(e) => { updateLine(index, { debit: e.target.value, credit: e.target.value ? "" : line.credit }); }} dir="ltr" className="text-end" data-testid={`line-debit-${index}`} />
                      </TableNumberCell>
                      <TableNumberCell>
                        <TextField aria-label={t("accounting.credit")} inputMode="decimal" value={line.credit} onChange={(e) => { updateLine(index, { credit: e.target.value, debit: e.target.value ? "" : line.debit }); }} dir="ltr" className="text-end" data-testid={`line-credit-${index}`} />
                      </TableNumberCell>
                      <TableCell>
                        <Button type="button" variant="ghost" size="icon" aria-label={t("accounting.removeLine")} onClick={() => { setEditing({ ...editing, form: { ...editing.form, lines: editing.form.lines.filter((_, i) => i !== index) } }); }}>
                          <Trash2 aria-hidden="true" />
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                  <TableRow className="font-semibold">
                    <TableCell>
                      <Button type="button" variant="secondary" onClick={() => { setEditing({ ...editing, form: { ...editing.form, lines: [...editing.form.lines, { accountCode: "", debit: "", credit: "" }] } }); }} data-testid="add-line">
                        <Plus aria-hidden="true" />
                        {t("accounting.addLine")}
                      </Button>
                    </TableCell>
                    <TableNumberCell><Amount value={sum(editing.form.lines, "debit")} /></TableNumberCell>
                    <TableNumberCell><Amount value={sum(editing.form.lines, "credit")} /></TableNumberCell>
                    <TableCell />
                  </TableRow>
                </TableBody>
              </Table>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setEditing(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={save.isPending} data-testid="save-journal">
                  {t("common.save")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
      <JournalImportDialog companyId={companyId} open={importing} onOpenChange={setImporting} />
    </>
  );
}
