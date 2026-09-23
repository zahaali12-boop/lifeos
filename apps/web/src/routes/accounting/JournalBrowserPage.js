import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Badge, Button, Dialog, DialogContent, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate, useSearch } from "@tanstack/react-router";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { Amount, CompanySelect, useCompanies, useCompanySelection } from "./shared";
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
    const [problem, setProblem] = useState(null);
    const openId = search.open;
    const entries = useInfiniteQuery({
        queryKey: ["journal-entries", companyId, from, to, number, text, sourceType],
        enabled: Boolean(companyId),
        queryFn: async ({ pageParam }) => unwrap(await api.GET("/api/v1/accounting/companies/{companyId}/journal-entries", { params: { path: { companyId }, query: { limit: 100, ...(from ? { from } : {}), ...(to ? { to } : {}), ...(number ? { number } : {}), ...(text ? { text } : {}), ...(sourceType ? { sourceDocumentType: sourceType } : {}), ...(pageParam ? { cursor: pageParam } : {}) } } })),
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
    const columns = useMemo(() => [
        { id: "number", accessorKey: "number", header: t("accounting.entry"), size: 150, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.number }) },
        { id: "postingDate", accessorKey: "postingDate", header: t("accounting.date"), size: 120, cell: ({ row }) => formatDate(row.original.postingDate) },
        { id: "source", accessorFn: (row) => row.sourceDocumentNumber ?? row.sourceDocumentType, header: t("accounting.source"), size: 160, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.sourceDocumentNumber ?? row.original.sourceDocumentType }) },
        { id: "description", accessorFn: (row) => localized(row.description), header: t("accounting.description"), size: 260 },
        { id: "total", accessorKey: "totalDebitTc", header: t("accounting.total"), size: 140, cell: ({ row }) => _jsx(Amount, { value: row.original.totalDebitTc }) },
        { id: "currency", accessorKey: "currencyTc", header: t("accounting.currency"), size: 90 },
        { id: "flags", accessorFn: (row) => (row.isReversal ? "reversal" : row.reversedByEntryId ? "reversed" : ""), header: t("common.status"), size: 120, cell: ({ row }) => (row.original.isReversal ? _jsx(Badge, { tone: "accent", children: t("accounting.reversal") }) : row.original.reversedByEntryId ? _jsx(Badge, { tone: "neutral", children: t("accounting.reversed") }) : null) },
    ], [t]);
    const rows = entries.data?.pages.flatMap((page) => page.items) ?? [];
    const open = (id) => { void navigate({ to: "/accounting/journal-entries", search: id ? { open: id } : {} }); };
    const detail = entry.data;
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("accounting.journalBrowser"), description: t("accounting.journalBrowserDescription") }), _jsxs("div", { className: "mb-4 grid gap-3 sm:grid-cols-3 lg:grid-cols-6", children: [_jsx(CompanySelect, { companies: companies.data ?? [], value: companyId, onChange: setCompanyId }), _jsx(Field, { label: t("accounting.from"), children: _jsx(TextField, { type: "date", value: from, onChange: (e) => { setFrom(e.target.value); }, dir: "ltr" }) }), _jsx(Field, { label: t("accounting.to"), children: _jsx(TextField, { type: "date", value: to, onChange: (e) => { setTo(e.target.value); }, dir: "ltr" }) }), _jsx(Field, { label: t("accounting.number"), children: _jsx(TextField, { value: number, onChange: (e) => { setNumber(e.target.value); }, dir: "ltr" }) }), _jsx(Field, { label: t("common.search"), children: _jsx(TextField, { type: "search", value: text, onChange: (e) => { setText(e.target.value); } }) }), _jsx(Field, { label: t("accounting.source"), children: _jsxs(SelectField, { value: sourceType, onChange: (e) => { setSourceType(e.target.value); }, children: [_jsx("option", { value: "", children: t("accounting.anySource") }), _jsx("option", { value: "manual_journal", children: t("accounting.manualJournal") }), _jsx("option", { value: "journal_entry", children: t("accounting.directPosting") })] }) })] }), _jsx(DataGrid, { label: "accounting.journalBrowser", columns: columns, data: rows, rowKey: (row) => row.id, loading: entries.isPending && Boolean(companyId), emptyTitle: t("accounting.noEntries"), onOpen: (row) => { open(row.id); } }), entries.hasNextPage ? (_jsx(Button, { variant: "secondary", className: "mt-3", onClick: () => { void entries.fetchNextPage(); }, loading: entries.isFetchingNextPage, children: t("common.loadMore") })) : null, _jsx(Dialog, { open: Boolean(openId), onOpenChange: (isOpen) => { if (!isOpen) {
                    open(null);
                } }, children: _jsxs(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", dir: "auto", children: detail ? `${detail.number} · ${formatDate(detail.postingDate)}` : t("common.loading") }) }), detail ? (_jsxs("div", { className: "flex flex-col gap-4", children: [_jsxs("dl", { className: "grid gap-x-6 gap-y-1 text-sm sm:grid-cols-2", "data-testid": "entry-detail", children: [_jsx("dt", { className: "text-fg-muted", children: t("accounting.source") }), _jsx("dd", { dir: "ltr", children: detail.sourceDocumentType === "manual_journal" ? (_jsx(Link, { to: "/accounting/journals", search: { open: detail.sourceDocumentId }, className: "underline-offset-2 hover:underline", children: detail.sourceDocumentNumber ?? detail.sourceDocumentType })) : ((detail.sourceDocumentNumber ?? detail.sourceDocumentType)) }), _jsx("dt", { className: "text-fg-muted", children: t("accounting.description") }), _jsx("dd", { children: localized(detail.description) }), _jsx("dt", { className: "text-fg-muted", children: t("accounting.currency") }), _jsx("dd", { dir: "ltr", children: `${detail.currencyTc} → ${detail.currencyFc} @ ${String(detail.rateTcFc)}` }), _jsx("dt", { className: "text-fg-muted", children: t("accounting.links") }), _jsx("dd", { children: (detail.links ?? []).length === 0
                                                ? "—"
                                                : (detail.links ?? []).map((link) => (_jsx("button", { type: "button", className: "me-2 underline-offset-2 hover:underline", onClick: () => { open(link.fromEntryId === detail.id ? link.toEntryId : link.fromEntryId); }, children: t(`accounting.relation.${link.relation}`) }, `${link.fromEntryId}-${link.relation}-${link.toEntryId}`))) })] }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: "#" }), _jsx(TableHead, { children: t("accounting.account") }), _jsx(TableHead, { children: t("accounting.role") }), _jsx(TableHead, { className: "text-end", children: t("accounting.debit") }), _jsx(TableHead, { className: "text-end", children: t("accounting.credit") }), _jsx(TableHead, { className: "text-end", children: t("accounting.debitFunctional") }), _jsx(TableHead, { className: "text-end", children: t("accounting.creditFunctional") })] }) }), _jsx(TableBody, { children: (detail.lines ?? []).map((line) => (_jsxs(TableRow, { children: [_jsx(TableCell, { children: String(line.lineNo) }), _jsxs(TableCell, { dir: "ltr", children: [_jsx(Link, { to: "/accounting/ledger", search: { companyId: detail.companyId, accountId: line.accountId, to: detail.postingDate }, className: "underline-offset-2 hover:underline", children: line.accountCode }), " ", localized(line.accountName)] }), _jsx(TableCell, { dir: "ltr", children: line.accountRole }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: line.debitTc }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: line.creditTc }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: line.debitFc }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: line.creditFc }) })] }, line.id))) })] }), !detail.isReversal && !detail.reversedByEntryId ? (_jsxs("form", { className: "flex flex-wrap items-end gap-3", onSubmit: (event) => {
                                        event.preventDefault();
                                        reverse.mutate();
                                    }, children: [_jsx(FormError, { message: problem?.message ?? null }), _jsx(Field, { label: t("accounting.reverseReason"), required: true, children: _jsx(TextField, { value: reason, onChange: (e) => { setReason(e.target.value); }, required: true }) }), _jsx(Button, { type: "submit", variant: "secondary", loading: reverse.isPending, children: t("accounting.reverse") })] })) : null] })) : null] }) })] }));
}
