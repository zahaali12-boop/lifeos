import { Badge, Button, Dialog, DialogContent, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate, useSearch } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { Amount, CompanySelect, SourceDocument, useCompanies, useCompanySelection } from "./shared";

type Entry = components["schemas"]["JournalEntrySummary"];

/** The journal browser: posted entries newest first with filters, and the entry with its lines and links. */
export function JournalBrowserPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const search = useSearch({ strict: false });
  const companies = useCompanies();
  const [companyId, setCompanyId] = useCompanySelection(companies.data);
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [number, setNumber] = useState("");
  const [text, setText] = useState("");
  const [sourceType, setSourceType] = useState("");
  const [reason, setReason] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const openId = search.open;

  const entries = useInfiniteQuery({
    queryKey: ["journal-entries", companyId, from, to, number, text, sourceType],
    enabled: Boolean(companyId),
    queryFn: async ({ pageParam }) =>
      unwrap(await api.GET("/api/v1/accounting/companies/{companyId}/journal-entries", { params: { path: { companyId }, query: { limit: 100, ...(from ? { from } : {}), ...(to ? { to } : {}), ...(number ? { number } : {}), ...(text ? { text } : {}), ...(sourceType ? { sourceDocumentType: sourceType } : {}), ...(pageParam ? { cursor: pageParam } : {}) } } })),
    initialPageParam: "",
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  const entry = useQuery({
    queryKey: ["journal-entry", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/accounting/journal-entries/{entryId}", { params: { path: { entryId: openId ?? "" } } })),
  });
  const reverse = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/accounting/journal-entries/{entryId}/reverse", { params: { path: { entryId: openId ?? "" } }, body: { reason } })),
    onSuccess: async () => {
      setReason("");
      setProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["journal-entry", openId] });
      await queryClient.invalidateQueries({ queryKey: ["journal-entries"] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<Entry, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("accounting.entry"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "postingDate", accessorKey: "postingDate", header: t("accounting.date"), size: 120, cell: ({ row }) => formatDate(row.original.postingDate) },
      { id: "source", accessorFn: (row) => row.sourceDocumentNumber ?? row.sourceDocumentType, header: t("accounting.source"), size: 160, cell: ({ row }) => <span dir="ltr">{row.original.sourceDocumentNumber ?? row.original.sourceDocumentType}</span> },
      { id: "description", accessorFn: (row) => localized(row.description), header: t("accounting.description"), size: 260 },
      { id: "total", accessorKey: "totalDebitTc", header: t("accounting.total"), size: 140, cell: ({ row }) => <Amount value={row.original.totalDebitTc} /> },
      { id: "currency", accessorKey: "currencyTc", header: t("accounting.currency"), size: 90 },
      { id: "flags", accessorFn: (row) => (row.isReversal ? "reversal" : row.reversedByEntryId ? "reversed" : ""), header: t("common.status"), size: 120, cell: ({ row }) => (row.original.isReversal ? <Badge tone="accent">{t("accounting.reversal")}</Badge> : row.original.reversedByEntryId ? <Badge tone="neutral">{t("accounting.reversed")}</Badge> : null) },
    ],
    [t],
  );

  const rows = entries.data?.pages.flatMap((page) => page.items) ?? [];
  const open = (id: string | null): void => { void navigate({ to: "/accounting/journal-entries", search: id ? { open: id } : {} }); };
  const detail = entry.data;

  return (
    <>
      <PageHeader title={t("accounting.journalBrowser")} description={t("accounting.journalBrowserDescription")} />
      <div className="mb-4 grid gap-3 sm:grid-cols-3 lg:grid-cols-6">
        <CompanySelect companies={companies.data ?? []} value={companyId} onChange={setCompanyId} />
        <Field label={t("accounting.from")}>
          <TextField type="date" value={from} onChange={(e) => { setFrom(e.target.value); }} dir="ltr" />
        </Field>
        <Field label={t("accounting.to")}>
          <TextField type="date" value={to} onChange={(e) => { setTo(e.target.value); }} dir="ltr" />
        </Field>
        <Field label={t("accounting.number")}>
          <TextField value={number} onChange={(e) => { setNumber(e.target.value); }} dir="ltr" />
        </Field>
        <Field label={t("common.search")}>
          <TextField type="search" value={text} onChange={(e) => { setText(e.target.value); }} />
        </Field>
        <Field label={t("accounting.source")}>
          <SelectField value={sourceType} onChange={(e) => { setSourceType(e.target.value); }}>
            <option value="">{t("accounting.anySource")}</option>
            <option value="manual_journal">{t("accounting.manualJournal")}</option>
            <option value="journal_entry">{t("accounting.directPosting")}</option>
          </SelectField>
        </Field>
      </div>
      <DataGrid<Entry> label="accounting.journalBrowser" columns={columns} data={rows} rowKey={(row) => row.id} loading={entries.isPending && Boolean(companyId)} emptyTitle={t("accounting.noEntries")} onOpen={(row) => { open(row.id); }} />
      {entries.hasNextPage ? (
        <Button variant="secondary" className="mt-3" onClick={() => { void entries.fetchNextPage(); }} loading={entries.isFetchingNextPage}>
          {t("common.loadMore")}
        </Button>
      ) : null}
      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { open(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold" dir="auto">
              {detail ? `${detail.number} · ${formatDate(detail.postingDate)}` : t("common.loading")}
            </DialogTitle>
          </DialogHeader>
          {detail ? (
            <div className="flex flex-col gap-4">
              <dl className="grid gap-x-6 gap-y-1 text-sm sm:grid-cols-2" data-testid="entry-detail">
                <dt className="text-fg-muted">{t("accounting.source")}</dt>
                <dd dir="ltr">
                  <SourceDocument type={detail.sourceDocumentType} id={detail.sourceDocumentId} number={detail.sourceDocumentNumber} />
                </dd>
                <dt className="text-fg-muted">{t("accounting.description")}</dt>
                <dd>{localized(detail.description)}</dd>
                <dt className="text-fg-muted">{t("accounting.currency")}</dt>
                <dd dir="ltr">{`${detail.currencyTc} → ${detail.currencyFc} @ ${String(detail.rateTcFc)}`}</dd>
                <dt className="text-fg-muted">{t("accounting.links")}</dt>
                <dd>
                  {(detail.links ?? []).length === 0
                    ? "—"
                    : (detail.links ?? []).map((link) => (
                        <button key={`${link.fromEntryId}-${link.relation}-${link.toEntryId}`} type="button" className="me-2 underline-offset-2 hover:underline" onClick={() => { open(link.fromEntryId === detail.id ? link.toEntryId : link.fromEntryId); }}>
                          {t(`accounting.relation.${link.relation}`)}
                        </button>
                      ))}
                </dd>
              </dl>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>#</TableHead>
                    <TableHead>{t("accounting.account")}</TableHead>
                    <TableHead>{t("accounting.role")}</TableHead>
                    <TableHead className="text-end">{t("accounting.debit")}</TableHead>
                    <TableHead className="text-end">{t("accounting.credit")}</TableHead>
                    <TableHead className="text-end">{t("accounting.debitFunctional")}</TableHead>
                    <TableHead className="text-end">{t("accounting.creditFunctional")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {(detail.lines ?? []).map((line) => (
                    <TableRow key={line.id}>
                      <TableCell>{String(line.lineNo)}</TableCell>
                      <TableCell dir="ltr">
                        <Link to="/accounting/ledger" search={{ companyId: detail.companyId, accountId: line.accountId, to: detail.postingDate }} className="underline-offset-2 hover:underline">
                          {line.accountCode}
                        </Link>{" "}
                        {localized(line.accountName)}
                      </TableCell>
                      <TableCell dir="ltr">{line.accountRole}</TableCell>
                      <TableNumberCell><Amount value={line.debitTc} /></TableNumberCell>
                      <TableNumberCell><Amount value={line.creditTc} /></TableNumberCell>
                      <TableNumberCell><Amount value={line.debitFc} /></TableNumberCell>
                      <TableNumberCell><Amount value={line.creditFc} /></TableNumberCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              {!detail.isReversal && !detail.reversedByEntryId ? (
                <form
                  className="flex flex-wrap items-end gap-3"
                  onSubmit={(event) => {
                    event.preventDefault();
                    reverse.mutate();
                  }}
                >
                  <FormError message={problem?.message ?? null} />
                  <Field label={t("accounting.reverseReason")} required>
                    <TextField value={reason} onChange={(e) => { setReason(e.target.value); }} required />
                  </Field>
                  <Button type="submit" variant="secondary" loading={reverse.isPending}>
                    {t("accounting.reverse")}
                  </Button>
                </form>
              ) : null}
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
