import { Button, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useInfiniteQuery, useMutation } from "@tanstack/react-query";
import { Link, useNavigate, useSearch } from "@tanstack/react-router";
import { Download } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, TextField } from "../common";
import { Amount, CompanySelect, dimensionFilters, saveFile, today, useCompanies, useCompanySelection, withFilters } from "./shared";

type LedgerItem = components["schemas"]["LedgerItem"];

/** Where a ledger line's source document opens in the app; the API's sourceLink says which document it is. */
export function sourceRoute(item: Pick<LedgerItem, "sourceDocumentType" | "sourceDocumentId" | "entryId">): { to: "/accounting/journals" | "/accounting/journal-entries"; search: Record<string, string> } {
  return item.sourceDocumentType === "manual_journal"
    ? { to: "/accounting/journals", search: { open: item.sourceDocumentId } }
    : { to: "/accounting/journal-entries", search: { open: item.entryId } };
}

/** The account ledger: opening, every line with its running balance and source document, closing; paged with the balance carried across pages. */
export function LedgerPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const search = useSearch({ strict: false });
  const companies = useCompanies();
  const [storedCompanyId, setStoredCompanyId] = useCompanySelection(companies.data);
  const companyId = search.companyId ?? storedCompanyId;
  const [accountCode, setAccountCode] = useState(search.accountCode ?? "");
  const [from, setFrom] = useState(search.from ?? "");
  const [to, setTo] = useState(search.to ?? today());
  const filters = dimensionFilters(search);
  const accountId = search.accountId;
  const basis = search.basis ?? "fc";
  const ready = Boolean(companyId && (accountId ?? search.accountCode));

  const baseQuery = withFilters({ ...(accountId ? { accountId } : { accountCode: search.accountCode ?? "" }), ...(search.from ? { from: search.from } : {}), to: search.to ?? to, basis, limit: 200 }, filters);
  const ledger = useInfiniteQuery({
    queryKey: ["ledger", companyId, baseQuery],
    enabled: ready,
    queryFn: async ({ pageParam }) =>
      unwrap(await api.GET("/api/v1/accounting/companies/{companyId}/reports/ledger", { params: { path: { companyId }, query: { ...baseQuery, ...(pageParam ? { cursor: pageParam } : {}) } } })),
    initialPageParam: "",
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  const exportLedger = useMutation({
    mutationFn: async (format: "csv" | "xlsx") => {
      const result = await api.GET("/api/v1/accounting/companies/{companyId}/reports/ledger", { params: { path: { companyId }, query: { ...baseQuery, format } }, parseAs: "blob" });
      saveFile(unwrap(result), result.response.headers, `ledger.${format}`);
    },
  });

  const run = (event: FormEvent): void => {
    event.preventDefault();
    void navigate({ to: "/accounting/ledger", search: { companyId, accountCode, to, ...(from ? { from } : {}), basis, ...filters } });
  };

  const first = ledger.data?.pages[0];
  const items = ledger.data?.pages.flatMap((page) => page.items) ?? [];
  const problem = ledger.error ? toFormProblem(ledger.error, t("accounting.loadFailed")) : null;

  return (
    <>
      <PageHeader
        title={first ? t("accounting.ledgerOf", { code: first.accountCode, name: localized(first.accountName) }) : t("accounting.ledger")}
        description={t("accounting.ledgerDescription")}
        actions={
          <>
            <Button variant="secondary" onClick={() => { exportLedger.mutate("csv"); }} loading={exportLedger.isPending} disabled={!first}>
              <Download aria-hidden="true" />
              CSV
            </Button>
            <Button variant="secondary" onClick={() => { exportLedger.mutate("xlsx"); }} loading={exportLedger.isPending} disabled={!first}>
              <Download aria-hidden="true" />
              XLSX
            </Button>
          </>
        }
      />
      <form onSubmit={run} className="mb-4 grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
        <CompanySelect companies={companies.data ?? []} value={companyId} onChange={(id) => { setStoredCompanyId(id); void navigate({ to: "/accounting/ledger", search: { ...search, companyId: id } }); }} />
        <Field label={t("accounting.accountCode")} required>
          <TextField value={accountCode} onChange={(e) => { setAccountCode(e.target.value); }} dir="ltr" data-testid="ledger-account" />
        </Field>
        <Field label={t("accounting.from")}>
          <TextField type="date" value={from} onChange={(e) => { setFrom(e.target.value); }} dir="ltr" />
        </Field>
        <Field label={t("accounting.to")} required>
          <TextField type="date" value={to} onChange={(e) => { setTo(e.target.value); }} dir="ltr" />
        </Field>
        <div className="flex items-end">
          <Button type="submit" data-testid="ledger-run">
            {t("accounting.show")}
          </Button>
        </div>
      </form>
      {Object.keys(filters).length > 0 ? (
        <p className="mb-2 text-sm text-fg-muted">
          {t("accounting.filteredBy", { filters: Object.entries(filters).map(([key, value]) => `${key.slice(2)} = ${value.slice(0, 8)}…`).join(", ") })}
        </p>
      ) : null}
      <FormError message={problem?.message ?? null} />
      {first ? (
        <>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("accounting.date")}</TableHead>
                <TableHead>{t("accounting.entry")}</TableHead>
                <TableHead>{t("accounting.source")}</TableHead>
                <TableHead>{t("accounting.description")}</TableHead>
                <TableHead className="text-end">{t("accounting.debit")}</TableHead>
                <TableHead className="text-end">{t("accounting.credit")}</TableHead>
                <TableHead className="text-end">{t("accounting.balance")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              <TableRow className="font-medium">
                <TableCell colSpan={4}>{t("accounting.openingBalance", { currency: first.currency })}</TableCell>
                <TableNumberCell />
                <TableNumberCell />
                <TableNumberCell><Amount value={first.opening} /></TableNumberCell>
              </TableRow>
              {items.map((item) => (
                <TableRow key={item.lineId} data-testid="ledger-line">
                  <TableCell>{formatDate(item.postingDate)}</TableCell>
                  <TableCell dir="ltr">
                    <Link to="/accounting/journal-entries" search={{ open: item.entryId }} className="underline-offset-2 hover:underline">
                      {item.entryNumber}
                    </Link>
                  </TableCell>
                  <TableCell dir="ltr">
                    {item.sourceLink ? (
                      <Link {...sourceRoute(item)} className="underline-offset-2 hover:underline">
                        {item.sourceDocumentNumber ?? item.sourceDocumentType}
                      </Link>
                    ) : (
                      (item.sourceDocumentNumber ?? item.sourceDocumentType)
                    )}
                  </TableCell>
                  <TableCell>{localized(item.description)}</TableCell>
                  <TableNumberCell><Amount value={item.debit} /></TableNumberCell>
                  <TableNumberCell><Amount value={item.credit} /></TableNumberCell>
                  <TableNumberCell><Amount value={item.balance} /></TableNumberCell>
                </TableRow>
              ))}
              {!ledger.hasNextPage ? (
                <TableRow className="font-semibold">
                  <TableCell colSpan={4}>{t("accounting.closingBalance")}</TableCell>
                  <TableNumberCell><Amount value={first.debit} /></TableNumberCell>
                  <TableNumberCell><Amount value={first.credit} /></TableNumberCell>
                  <TableNumberCell><Amount value={first.closing} /></TableNumberCell>
                </TableRow>
              ) : null}
            </TableBody>
          </Table>
          {ledger.hasNextPage ? (
            <Button variant="secondary" className="mt-3" onClick={() => { void ledger.fetchNextPage(); }} loading={ledger.isFetchingNextPage}>
              {t("common.loadMore")}
            </Button>
          ) : null}
        </>
      ) : ready && ledger.isPending ? (
        <p className="text-sm text-fg-muted">{t("common.loading")}</p>
      ) : (
        <p className="text-sm text-fg-muted">{t("accounting.ledgerEmpty")}</p>
      )}
    </>
  );
}
