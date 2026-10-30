import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate, useSearch } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { ChevronDown, Plus, Trash2 } from "lucide-react";
import { Fragment, useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { add, compare, isDecimal, subtract } from "../../lib/decimal";
import { useCurrencies } from "../../lib/currencies";
import { journalBalance } from "../../lib/journalBalance";
import { formatDate, formatMoney, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { AttachmentsPanel } from "../AttachmentsPanel";
import { RecordDiscussion, RecordHistory } from "../RecordDiscussion";
import { asCustomFieldValues, CustomFieldsFieldset, CustomFieldValuesList, type CustomFieldValues } from "../CustomFieldsFieldset";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { Tabs } from "../inventory/shared";
import { JournalImportDialog } from "./JournalImport";
import { AccountCodeField, costCentre, CostCentreSelect, costCentreText, emptyDetails, LineDetailsEditor, LineDetailsSummary, useJournalReference, type LineDetails } from "./JournalLineDetails";
import { Amount, CompanySelect, StatusBadge, today, useCompanies, useCompanySelection } from "./shared";
import { CurrencyField } from "../CurrencyField";

type Journal = components["schemas"]["ManualJournalSummary"];

interface LineForm extends LineDetails {
  accountCode: string;
  debit: string;
  credit: string;
  showDetails: boolean;
}

const emptyLine = (): LineForm => ({ accountCode: "", debit: "", credit: "", ...emptyDetails(), showDetails: false });

/** A line the user has not touched: no account, no amount, no details. Such lines are left out when saving. */
const isBlankLine = (line: LineForm): boolean =>
  !line.accountCode.trim() && !line.debit.trim() && !line.credit.trim() && !line.description.trim() && !line.subledgerRef && Object.keys(line.dimensions).length === 0;

interface JournalForm {
  kind: string;
  postingDate: string;
  currency: string;
  description: string;
  reference: string;
  autoReverseOn: string;
  customFields: CustomFieldValues;
  lines: LineForm[];
}

const kinds = ["manual", "opening", "accrual", "allocation"];
const statuses = ["", "draft", "pending_approval", "approved", "rejected", "posted", "cancelled"];

function emptyForm(currency: string): JournalForm {
  return { kind: "manual", postingDate: today(), currency, description: "", reference: "", autoReverseOn: "", customFields: {}, lines: [emptyLine(), emptyLine()] };
}

function toForm(journal: Journal): JournalForm {
  return {
    kind: journal.kind,
    postingDate: journal.postingDate,
    currency: journal.currency,
    description: localized(journal.description),
    reference: journal.reference ?? "",
    autoReverseOn: journal.autoReverseOn ?? "",
    customFields: asCustomFieldValues(journal.customFields),
    lines: (journal.lines ?? []).map((line) => ({
      accountCode: line.accountCode,
      debit: compare(line.debit, 0) > 0 ? String(line.debit) : "",
      credit: compare(line.credit, 0) > 0 ? String(line.credit) : "",
      description: localized(line.description),
      dimensions: { ...line.dimensions },
      subledgerType: line.subledgerType ?? "",
      subledgerRef: line.subledgerRef ?? "",
      showDetails: false,
    })),
  };
}

function toRequest(form: JournalForm): components["schemas"]["SaveJournalRequest"] {
  return {
    kind: form.kind,
    postingDate: form.postingDate,
    currency: form.currency,
    // One description, typed in whichever language (or both): every language reads the same text.
    description: { en: form.description.trim(), ar: form.description.trim() },
    reference: form.reference || null,
    autoReverse: form.kind === "accrual" && Boolean(form.autoReverseOn),
    autoReverseOn: form.kind === "accrual" && form.autoReverseOn ? form.autoReverseOn : null,
    rateType: "spot",
    customFields: form.customFields,
    lines: form.lines.filter((line) => !isBlankLine(line)).map((line) => {
      // Like the journal's, a line's description is one text in any language, shown the same in every language.
      const text = line.description.trim();
      const description = text ? { en: text, ar: text } : {};
      return {
        accountCode: line.accountCode.trim(),
        debit: line.debit || "0",
        credit: line.credit || "0",
        dimensions: Object.keys(line.dimensions).length > 0 ? line.dimensions : null,
        subledgerType: line.subledgerRef ? line.subledgerType || null : null,
        subledgerRef: line.subledgerRef || null,
        description: Object.keys(description).length > 0 ? description : null,
      };
    }),
  };
}

/** A form line's number among the lines sent to the server (blank lines are left out), as the server's messages count. */
function sentNumber(lines: LineForm[], index: number): number | null {
  return isBlankLine(lines[index] ?? emptyLine()) ? null : lines.slice(0, index + 1).filter((line) => !isBlankLine(line)).length;
}

/** The side's total as an exact decimal; amounts still being typed (not yet a number) count as nothing. */
function sum(lines: LineForm[], side: "debit" | "credit"): string {
  return add("0", ...lines.map((line) => line[side].trim()).filter(isDecimal));
}

function BalanceNote({ kind, debit, credit, currency, minorUnits }: { kind: string; debit: string | number; credit: string | number; currency: string; minorUnits: number }) {
  const { t } = useTranslation();
  const { state, difference } = journalBalance(kind, debit, credit);
  const amount = formatMoney(difference.replace(/^-/u, ""), currency, minorUnits);
  const tone = state === "balanced" ? "text-success" : state === "unbalanced" ? "text-danger" : "text-fg-muted";
  const text = state === "balanced" ? t("accounting.balanced")
    : state === "empty" ? t("accounting.noAmounts")
    : state === "opening" ? t("accounting.openingDifference", { amount })
    : t("accounting.outOfBalance", { amount });
  return (
    <p role={state === "unbalanced" ? "alert" : "status"} className={`text-sm font-medium ${tone}`} data-testid="journal-balance" data-state={state}>
      {text}
    </p>
  );
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
  const [cancelling, setCancelling] = useState(false);
  const [correcting, setCorrecting] = useState(false);
  const openId = search.open;
  const currencies = useCurrencies();
  /** Amounts show as many decimals as the journal's currency has (3 for IQD, KWD, JOD; 0 for JPY), never a fixed two. */
  const minorUnitsOf = (code: string | undefined): number => Number(currencies.data?.find((c) => c.code === code)?.minorUnits ?? 2);
  // Whether this company sends journals for approval before posting: then a draft is submitted, otherwise posted directly.
  const companySettings = useQuery({
    queryKey: ["settings", companyId],
    enabled: Boolean(companyId),
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/organization/companies/{companyId}/settings", { params: { path: { companyId } } })),
  });
  const approvalRequired = companySettings.data?.find((s) => s.key === "accounting.journals.approval")?.value === "required";
  const journal = useQuery({
    queryKey: ["journal", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/accounting/journals/{journalId}", { params: { path: { journalId: openId ?? "" } } })),
  });
  const reference = useJournalReference(
    companyId,
    company?.chartId,
    (journal.data?.lines ?? []).map((l) => l.accountId),
    (editing?.form.lines ?? []).map((l) => l.accountCode),
  );
  // The cost centre has its own column (the other dimensions stay in each line's details).
  const hasCostCentre = reference.dimensions.some((d) => d.code === costCentre);

  const journals = useInfiniteQuery({
    queryKey: ["journals", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async ({ pageParam }) => unwrap(await api.GET("/api/v1/accounting/companies/{companyId}/journals", { params: { path: { companyId }, query: { limit: 100, ...(status ? { status } : {}), ...(pageParam ? { cursor: pageParam } : {}) } } })),
    initialPageParam: "",
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  const refresh = async (id?: string | null): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["journals"] });
    if (id) {
      await queryClient.invalidateQueries({ queryKey: ["journal", id] });
    }
  };
  const open = (id: string | null): void => {
    // A message, reason or pending cancellation belongs to the journal it was given for, not the next one opened.
    setDetailTab("lines");
    setProblem(null);
    setReason("");
    setCancelling(false);
    setCorrecting(false);
    void navigate({ to: "/accounting/journals", search: id ? { open: id } : {} });
  };

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
      setCancelling(false);
      setCorrecting(false);
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
      { id: "reference", accessorFn: (row) => row.reference ?? "", header: t("accounting.reference"), size: 130, cell: ({ row }) => <span dir="auto">{row.original.reference ?? ""}</span> },
      { id: "total", accessorKey: "totalDebit", header: t("accounting.total"), size: 140, cell: ({ row }) => <Amount value={row.original.totalDebit} minorUnits={minorUnitsOf(row.original.currency)} /> },
      { id: "currency", accessorKey: "currency", header: t("accounting.currency"), size: 90 },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 140, cell: ({ row }) => <StatusBadge status={row.original.status} label={t(`accounting.statuses.${row.original.status}`)} /> },
    ],
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the decimals follow the currency list, which only loads once
    [t, currencies.data],
  );

  const rows = journals.data?.pages.flatMap((page) => page.items) ?? [];
  const detail = journal.data;
  const editable = detail?.status === "draft" || detail?.status === "rejected";
  const detailBalance = detail ? journalBalance(detail.kind, detail.totalDebit, detail.totalCredit).state : "empty";
  const detailMinor = minorUnitsOf(detail?.currency);
  // A posted journal's entry may since have been reversed (an accrual on its date, or by hand in the journal entries).
  const postedEntry = useQuery({
    queryKey: ["journal-entry", detail?.journalEntryId],
    enabled: Boolean(detail?.journalEntryId),
    queryFn: async () => unwrap(await api.GET("/api/v1/accounting/journal-entries/{entryId}", { params: { path: { entryId: detail?.journalEntryId ?? "" } } })),
  });
  const reversedBy = postedEntry.data?.reversedByEntryId ?? null;
  const canGoFurther = detailBalance === "balanced" || detailBalance === "opening";

  const submitForm = (event: FormEvent): void => {
    event.preventDefault();
    if (!editing) {
      return;
    }
    // A line with an amount or details but no account would otherwise vanish on saving, and the journal with it.
    const orphan = editing.form.lines.findIndex((line) => !isBlankLine(line) && !line.accountCode.trim());
    if (orphan >= 0) {
      setProblem({ message: t("accounting.lineWithoutAccount", { line: orphan + 1 }), fields: {}, why: { line: sentNumber(editing.form.lines, orphan) } });
      return;
    }
    save.mutate(editing);
  };
  /** A new line: when the journal is out of balance it starts with the amount that balances it, on the side that does. */
  const addLine = (): void => {
    if (!editing) {
      return;
    }
    const difference = subtract(sum(editing.form.lines, "debit"), sum(editing.form.lines, "credit"));
    const side = compare(difference, 0);
    const line = emptyLine();
    if (editing.form.kind !== "opening" && side !== 0) {
      if (side > 0) {
        line.credit = difference;
      } else {
        line.debit = subtract("0", difference);
      }
    }
    setEditing({ ...editing, form: { ...editing.form, lines: [...editing.form.lines, line] } });
  };
  /** The line the server refused, when it said which: it numbers from 1 among the lines sent (blank lines are not). */
  const problemLine = typeof problem?.why?.line === "number" ? problem.why.line : null;
  /** Problems a field of the editor shows next to itself; any other is shown at the top, never swallowed. */
  const shownFields = ["postingDate", "currency", "autoReverseOn"];
  const editorShowsField = Boolean(problem && Object.keys(problem.fields).some((key) => shownFields.includes(key) || key.startsWith("customFields.")));
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
              <dl className="grid gap-x-6 gap-y-1 text-sm sm:grid-cols-3" data-testid="journal-facts">
                <div>
                  <dt className="text-fg-muted">{t("accounting.currency")}</dt>
                  <dd dir="ltr">{detail.currency}</dd>
                </div>
                <div>
                  <dt className="text-fg-muted">{t("accounting.reference")}</dt>
                  <dd dir="auto">{detail.reference ?? "—"}</dd>
                </div>
                {detail.autoReverse && detail.autoReverseOn ? (
                  <div>
                    <dt className="text-fg-muted">{t("accounting.autoReverseOn")}</dt>
                    <dd>{formatDate(detail.autoReverseOn)}</dd>
                  </div>
                ) : null}
                {reversedBy ? (
                  <div>
                    <dt className="text-fg-muted">{t("accounting.reversed")}</dt>
                    <dd>
                      <Link to="/accounting/journal-entries" search={{ open: reversedBy }} className="underline-offset-2 hover:underline" data-testid="journal-reversed-by">
                        {t("accounting.viewReversal")}
                      </Link>
                    </dd>
                  </div>
                ) : null}
              </dl>
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
                    {hasCostCentre ? <TableHead>{t("accounting.costCentre")}</TableHead> : null}
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
                        <LineDetailsSummary dimensions={Object.fromEntries(Object.entries(line.dimensions).filter(([code]) => code !== costCentre))} subledgerType={line.subledgerType} subledgerRef={line.subledgerRef} description={line.description} reference={reference} />
                      </TableCell>
                      {hasCostCentre ? <TableCell data-testid="journal-line-cost-centre">{costCentreText(line.dimensions, reference)}</TableCell> : null}
                      {/* A line has one side; the other stays empty rather than reading 0.00. */}
                      <TableNumberCell>{compare(line.debit, 0) === 0 ? null : <Amount value={line.debit} minorUnits={detailMinor} />}</TableNumberCell>
                      <TableNumberCell>{compare(line.credit, 0) === 0 ? null : <Amount value={line.credit} minorUnits={detailMinor} />}</TableNumberCell>
                    </TableRow>
                  ))}
                  <TableRow className="font-semibold">
                    <TableCell colSpan={hasCostCentre ? 3 : 2}>{t("accounting.totals")}</TableCell>
                    <TableNumberCell><Amount value={detail.totalDebit} minorUnits={detailMinor} /></TableNumberCell>
                    <TableNumberCell><Amount value={detail.totalCredit} minorUnits={detailMinor} /></TableNumberCell>
                  </TableRow>
                </TableBody>
              </Table>
              {detail.status !== "posted" && detail.status !== "cancelled" ? <BalanceNote kind={detail.kind} debit={detail.totalDebit} credit={detail.totalCredit} currency={detail.currency} minorUnits={detailMinor} /> : null}
              <CustomFieldValuesList entityType="manual_journal" values={detail.customFields} />
              <AttachmentsPanel entityType="manual_journal" entityId={detail.id} />
              </>
              ) : null}
              {detailTab === "discussion" ? <RecordDiscussion entityType="manual_journal" entityId={detail.id} files={false} /> : null}
              {detailTab === "history" ? <RecordHistory entityType="manual_journal" entityId={detail.id} /> : null}
              <FormError message={problem?.message ?? null} />
              {detail.status === "pending_approval" ? (
                <Field label={t("accounting.rejectReason")}>
                  <TextField value={reason} onChange={(e) => { setReason(e.target.value); }} data-testid="journal-reason" />
                </Field>
              ) : null}
              {correcting ? (
                <Field label={t("accounting.correctionReason")} description={t("accounting.correctionHint")} required>
                  <TextField value={reason} onChange={(e) => { setReason(e.target.value); }} required data-testid="journal-reason" />
                </Field>
              ) : null}
              <DialogFooter>
                {editable ? (
                  <Button variant="secondary" onClick={() => { setProblem(null); setEditing({ id: detail.id, form: toForm(detail) }); }} data-testid="edit-journal">
                    {t("accounting.edit")}
                  </Button>
                ) : null}
                {editable && (approvalRequired || detail.status === "rejected") ? (
                  <Button onClick={() => { act.mutate("submit"); }} loading={act.isPending} disabled={!canGoFurther} data-testid="submit-journal">
                    {approvalRequired ? t("accounting.submitForApproval") : t("accounting.submit")}
                  </Button>
                ) : null}
                {detail.status === "pending_approval" ? (
                  <>
                    <Button variant="secondary" onClick={() => { act.mutate("reject"); }} loading={act.isPending} disabled={!reason.trim()} data-testid="reject-journal">
                      {t("accounting.reject")}
                    </Button>
                    <Button onClick={() => { act.mutate("approve"); }} loading={act.isPending} data-testid="approve-journal">
                      {t("accounting.approve")}
                    </Button>
                  </>
                ) : null}
                {(detail.status === "draft" && !approvalRequired) || detail.status === "approved" ? (
                  <Button onClick={() => { act.mutate("post"); }} loading={act.isPending} disabled={!canGoFurther} data-testid="post-journal">
                    {t("accounting.post")}
                  </Button>
                ) : null}
                {detail.status !== "posted" && detail.status !== "cancelled" && !cancelling ? (
                  // Cancelling a journal cannot be undone, and "Cancel" next to a dialog reads as "close": it asks first.
                  <Button variant="secondary" onClick={() => { setCancelling(true); }} data-testid="cancel-journal">
                    {t("accounting.cancelJournal")}
                  </Button>
                ) : null}
                {cancelling ? (
                  <>
                    <span className="me-auto text-sm text-danger" role="alert">{t("accounting.cancelJournalWarning")}</span>
                    <Button variant="secondary" onClick={() => { setCancelling(false); }} data-testid="keep-journal">
                      {t("accounting.keepJournal")}
                    </Button>
                    <Button variant="danger" onClick={() => { act.mutate("cancel"); }} loading={act.isPending} data-testid="confirm-cancel-journal">
                      {t("accounting.confirmCancelJournal")}
                    </Button>
                  </>
                ) : null}
                {detail.status === "posted" && !detail.correctedByJournalId && !reversedBy && !correcting ? (
                  <Button variant="secondary" onClick={() => { setCorrecting(true); }} data-testid="correct-journal">
                    {t("accounting.correct")}
                  </Button>
                ) : null}
                {correcting ? (
                  <>
                    <Button variant="secondary" onClick={() => { setCorrecting(false); setReason(""); }}>
                      {t("accounting.keepJournal")}
                    </Button>
                    <Button onClick={() => { act.mutate("correct"); }} loading={act.isPending} disabled={!reason.trim()} data-testid="confirm-correct-journal">
                      {t("accounting.createCorrection")}
                    </Button>
                  </>
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
              <FormError message={problem && !editorShowsField ? problem.message : null} />
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
                  <CurrencyField value={editing.form.currency} onChange={(code) => { setEditing({ ...editing, form: { ...editing.form, currency: code } }); }} required data-testid="journal-currency" />
                </Field>
                <Field label={t("accounting.description")} required className="sm:col-span-2">
                  <TextField value={editing.form.description} onChange={(e) => { setEditing({ ...editing, form: { ...editing.form, description: e.target.value } }); }} required dir="auto" data-testid="journal-description" />
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
                    {hasCostCentre ? <TableHead>{t("accounting.costCentre")}</TableHead> : null}
                    <TableHead className="text-end">{t("accounting.debit")}</TableHead>
                    <TableHead className="text-end">{t("accounting.credit")}</TableHead>
                    <TableHead />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {editing.form.lines.map((line, index) => {
                    const account = reference.accounts.get(line.accountCode.trim());
                    const extras = Object.keys(line.dimensions).filter((code) => code !== costCentre).length + (line.subledgerRef ? 1 : 0) + (line.description.trim() ? 1 : 0);
                    return (
                    <Fragment key={index}>
                    <TableRow className={problemLine === sentNumber(editing.form.lines, index) ? "bg-danger-soft" : undefined} data-testid={problemLine === sentNumber(editing.form.lines, index) ? "journal-line-problem" : undefined}>
                      <TableCell>
                        <AccountCodeField
                          label={t("accounting.accountOfLine", { line: index + 1 })}
                          value={line.accountCode}
                          accounts={reference.accounts}
                          onChange={(code) => { updateLine(index, { accountCode: code, ...(reference.accounts.get(code.trim())?.subledgerType === line.subledgerType ? {} : { subledgerType: "", subledgerRef: "" }) }); }}
                          testId={`line-account-${String(index)}`}
                        />
                      </TableCell>
                      {hasCostCentre ? (
                        <TableCell>
                          <CostCentreSelect
                            label={t("accounting.costCentreOfLine", { line: index + 1 })}
                            values={line.dimensions}
                            reference={reference}
                            emptyLabel="—"
                            // An account whose rules block the cost centre takes none (the server refuses it too).
                            disabled={(account ? reference.rulesByAccount.get(account.id) ?? [] : []).some((r) => r.dimensionCode === costCentre && r.rule === "blocked")}
                            onChange={(dimensions) => { updateLine(index, { dimensions }); }}
                            testId={`line-dimension-${String(index)}-${costCentre}`}
                          />
                        </TableCell>
                      ) : null}
                      <TableNumberCell>
                        <TextField aria-label={t("accounting.debit")} inputMode="decimal" value={line.debit} onChange={(e) => { updateLine(index, { debit: e.target.value, credit: e.target.value ? "" : line.credit }); }} dir="ltr" className="text-end" data-testid={`line-debit-${index}`} />
                      </TableNumberCell>
                      <TableNumberCell>
                        <TextField aria-label={t("accounting.credit")} inputMode="decimal" value={line.credit} onChange={(e) => { updateLine(index, { credit: e.target.value, debit: e.target.value ? "" : line.debit }); }} dir="ltr" className="text-end" data-testid={`line-credit-${index}`} />
                      </TableNumberCell>
                      <TableCell>
                        <div className="flex items-center">
                          <Button
                            type="button"
                            variant="ghost"
                            size="icon"
                            aria-expanded={line.showDetails}
                            aria-label={t("accounting.lineDetails", { line: index + 1, count: extras })}
                            onClick={() => { updateLine(index, { showDetails: !line.showDetails }); }}
                            data-testid={`line-details-toggle-${index}`}
                          >
                            <ChevronDown aria-hidden="true" className={line.showDetails ? "rotate-180" : undefined} />
                          </Button>
                          {extras > 0 ? <span className="text-xs text-fg-muted" aria-hidden="true">{extras}</span> : null}
                          <Button type="button" variant="ghost" size="icon" aria-label={t("accounting.removeLine")} onClick={() => { setEditing({ ...editing, form: { ...editing.form, lines: editing.form.lines.filter((_, i) => i !== index) } }); }}>
                            <Trash2 aria-hidden="true" />
                          </Button>
                        </div>
                      </TableCell>
                    </TableRow>
                    {line.showDetails ? (
                      <TableRow>
                        <TableCell colSpan={hasCostCentre ? 5 : 4} className="bg-surface-sunken/40">
                          <LineDetailsEditor index={index} account={account} details={line} reference={reference} onChange={(patch) => { updateLine(index, patch); }} />
                        </TableCell>
                      </TableRow>
                    ) : null}
                    </Fragment>
                    );
                  })}
                  <TableRow className="font-semibold">
                    <TableCell>
                      <Button type="button" variant="secondary" onClick={addLine} data-testid="add-line">
                        <Plus aria-hidden="true" />
                        {t("accounting.addLine")}
                      </Button>
                    </TableCell>
                    {hasCostCentre ? <TableCell /> : null}
                    <TableNumberCell><Amount value={sum(editing.form.lines, "debit")} minorUnits={minorUnitsOf(editing.form.currency)} /></TableNumberCell>
                    <TableNumberCell><Amount value={sum(editing.form.lines, "credit")} minorUnits={minorUnitsOf(editing.form.currency)} /></TableNumberCell>
                    <TableCell />
                  </TableRow>
                </TableBody>
              </Table>
              <BalanceNote kind={editing.form.kind} debit={sum(editing.form.lines, "debit")} credit={sum(editing.form.lines, "credit")} currency={editing.form.currency} minorUnits={minorUnitsOf(editing.form.currency)} />
              <CustomFieldsFieldset entityType="manual_journal" values={editing.form.customFields} onChange={(customFields) => { setEditing({ ...editing, form: { ...editing.form, customFields } }); }} errors={problem?.fields} />
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
