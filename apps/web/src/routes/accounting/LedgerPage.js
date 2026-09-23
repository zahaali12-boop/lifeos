import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useInfiniteQuery, useMutation } from "@tanstack/react-query";
import { Link, useNavigate, useSearch } from "@tanstack/react-router";
import { Download } from "lucide-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, TextField } from "../common";
import { Amount, CompanySelect, dimensionFilters, saveFile, today, useCompanies, useCompanySelection, withFilters } from "./shared";
/** Where a ledger line's source document opens in the app; the API's sourceLink says which document it is. */
export function sourceRoute(item) {
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
        queryFn: async ({ pageParam }) => unwrap(await api.GET("/api/v1/accounting/companies/{companyId}/reports/ledger", { params: { path: { companyId }, query: { ...baseQuery, ...(pageParam ? { cursor: pageParam } : {}) } } })),
        initialPageParam: "",
        getNextPageParam: (last) => last.nextCursor ?? undefined,
    });
    const exportLedger = useMutation({
        mutationFn: async (format) => {
            const result = await api.GET("/api/v1/accounting/companies/{companyId}/reports/ledger", { params: { path: { companyId }, query: { ...baseQuery, format } }, parseAs: "blob" });
            saveFile(unwrap(result), result.response.headers, `ledger.${format}`);
        },
    });
    const run = (event) => {
        event.preventDefault();
        void navigate({ to: "/accounting/ledger", search: { companyId, accountCode, to, ...(from ? { from } : {}), basis, ...filters } });
    };
    const first = ledger.data?.pages[0];
    const items = ledger.data?.pages.flatMap((page) => page.items) ?? [];
    const problem = ledger.error ? toFormProblem(ledger.error, t("accounting.loadFailed")) : null;
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: first ? t("accounting.ledgerOf", { code: first.accountCode, name: localized(first.accountName) }) : t("accounting.ledger"), description: t("accounting.ledgerDescription"), actions: _jsxs(_Fragment, { children: [_jsxs(Button, { variant: "secondary", onClick: () => { exportLedger.mutate("csv"); }, loading: exportLedger.isPending, disabled: !first, children: [_jsx(Download, { "aria-hidden": "true" }), "CSV"] }), _jsxs(Button, { variant: "secondary", onClick: () => { exportLedger.mutate("xlsx"); }, loading: exportLedger.isPending, disabled: !first, children: [_jsx(Download, { "aria-hidden": "true" }), "XLSX"] })] }) }), _jsxs("form", { onSubmit: run, className: "mb-4 grid gap-3 sm:grid-cols-2 lg:grid-cols-5", children: [_jsx(CompanySelect, { companies: companies.data ?? [], value: companyId, onChange: (id) => { setStoredCompanyId(id); void navigate({ to: "/accounting/ledger", search: { ...search, companyId: id } }); } }), _jsx(Field, { label: t("accounting.accountCode"), required: true, children: _jsx(TextField, { value: accountCode, onChange: (e) => { setAccountCode(e.target.value); }, dir: "ltr", "data-testid": "ledger-account" }) }), _jsx(Field, { label: t("accounting.from"), children: _jsx(TextField, { type: "date", value: from, onChange: (e) => { setFrom(e.target.value); }, dir: "ltr" }) }), _jsx(Field, { label: t("accounting.to"), required: true, children: _jsx(TextField, { type: "date", value: to, onChange: (e) => { setTo(e.target.value); }, dir: "ltr" }) }), _jsx("div", { className: "flex items-end", children: _jsx(Button, { type: "submit", "data-testid": "ledger-run", children: t("accounting.show") }) })] }), Object.keys(filters).length > 0 ? (_jsx("p", { className: "mb-2 text-sm text-fg-muted", children: t("accounting.filteredBy", { filters: Object.entries(filters).map(([key, value]) => `${key.slice(2)} = ${value.slice(0, 8)}…`).join(", ") }) })) : null, _jsx(FormError, { message: problem?.message ?? null }), first ? (_jsxs(_Fragment, { children: [_jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("accounting.date") }), _jsx(TableHead, { children: t("accounting.entry") }), _jsx(TableHead, { children: t("accounting.source") }), _jsx(TableHead, { children: t("accounting.description") }), _jsx(TableHead, { className: "text-end", children: t("accounting.debit") }), _jsx(TableHead, { className: "text-end", children: t("accounting.credit") }), _jsx(TableHead, { className: "text-end", children: t("accounting.balance") })] }) }), _jsxs(TableBody, { children: [_jsxs(TableRow, { className: "font-medium", children: [_jsx(TableCell, { colSpan: 4, children: t("accounting.openingBalance", { currency: first.currency }) }), _jsx(TableNumberCell, {}), _jsx(TableNumberCell, {}), _jsx(TableNumberCell, { children: _jsx(Amount, { value: first.opening }) })] }), items.map((item) => (_jsxs(TableRow, { "data-testid": "ledger-line", children: [_jsx(TableCell, { children: formatDate(item.postingDate) }), _jsx(TableCell, { dir: "ltr", children: _jsx(Link, { to: "/accounting/journal-entries", search: { open: item.entryId }, className: "underline-offset-2 hover:underline", children: item.entryNumber }) }), _jsx(TableCell, { dir: "ltr", children: item.sourceLink ? (_jsx(Link, { ...sourceRoute(item), className: "underline-offset-2 hover:underline", children: item.sourceDocumentNumber ?? item.sourceDocumentType })) : ((item.sourceDocumentNumber ?? item.sourceDocumentType)) }), _jsx(TableCell, { children: localized(item.description) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: item.debit }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: item.credit }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: item.balance }) })] }, item.lineId))), !ledger.hasNextPage ? (_jsxs(TableRow, { className: "font-semibold", children: [_jsx(TableCell, { colSpan: 4, children: t("accounting.closingBalance") }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: first.debit }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: first.credit }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: first.closing }) })] })) : null] })] }), ledger.hasNextPage ? (_jsx(Button, { variant: "secondary", className: "mt-3", onClick: () => { void ledger.fetchNextPage(); }, loading: ledger.isFetchingNextPage, children: t("common.loadMore") })) : null] })) : ready && ledger.isPending ? (_jsx("p", { className: "text-sm text-fg-muted", children: t("common.loading") })) : (_jsx("p", { className: "text-sm text-fg-muted", children: t("accounting.ledgerEmpty") }))] }));
}
