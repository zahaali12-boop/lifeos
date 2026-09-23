import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate, useSearch } from "@tanstack/react-router";
import { Plus, Trash2 } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { Amount, CompanySelect, StatusBadge, today, useCompanies, useCompanySelection } from "./shared";
const kinds = ["manual", "opening", "accrual", "allocation"];
const statuses = ["", "draft", "pending_approval", "approved", "rejected", "posted", "cancelled"];
function emptyForm(currency) {
    return { kind: "manual", postingDate: today(), currency, descriptionEn: "", descriptionAr: "", reference: "", autoReverseOn: "", lines: [{ accountCode: "", debit: "", credit: "" }, { accountCode: "", debit: "", credit: "" }] };
}
function toForm(journal) {
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
function toRequest(form) {
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
function sum(lines, side) {
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
    const [editing, setEditing] = useState(null);
    const [reason, setReason] = useState("");
    const [problem, setProblem] = useState(null);
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
    const refresh = async (id) => {
        await queryClient.invalidateQueries({ queryKey: ["journals"] });
        if (id) {
            await queryClient.invalidateQueries({ queryKey: ["journal", id] });
        }
    };
    const open = (id) => { void navigate({ to: "/accounting/journals", search: id ? { open: id } : {} }); };
    const save = useMutation({
        mutationFn: async (input) => input.id
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
        mutationFn: async (action) => {
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
    const columns = useMemo(() => [
        { id: "number", accessorKey: "number", header: t("accounting.number"), size: 150, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.number }) },
        { id: "postingDate", accessorKey: "postingDate", header: t("accounting.date"), size: 120, cell: ({ row }) => formatDate(row.original.postingDate) },
        { id: "kind", accessorKey: "kind", header: t("accounting.kind"), size: 110, cell: ({ row }) => t(`accounting.kinds.${row.original.kind}`) },
        { id: "description", accessorFn: (row) => localized(row.description), header: t("accounting.description"), size: 260 },
        { id: "total", accessorKey: "totalDebit", header: t("accounting.total"), size: 140, cell: ({ row }) => _jsx(Amount, { value: row.original.totalDebit }) },
        { id: "currency", accessorKey: "currency", header: t("accounting.currency"), size: 90 },
        { id: "status", accessorKey: "status", header: t("common.status"), size: 140, cell: ({ row }) => _jsx(StatusBadge, { status: row.original.status, label: t(`accounting.statuses.${row.original.status}`) }) },
    ], [t]);
    const rows = journals.data?.pages.flatMap((page) => page.items) ?? [];
    const detail = journal.data;
    const editable = detail?.status === "draft" || detail?.status === "rejected";
    const submitForm = (event) => {
        event.preventDefault();
        if (editing) {
            save.mutate(editing);
        }
    };
    const updateLine = (index, patch) => {
        if (!editing) {
            return;
        }
        const lines = editing.form.lines.map((line, i) => (i === index ? { ...line, ...patch } : line));
        setEditing({ ...editing, form: { ...editing.form, lines } });
    };
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("accounting.journals"), description: t("accounting.journalsDescription"), actions: _jsxs(Button, { onClick: () => { setProblem(null); setEditing({ id: null, form: emptyForm(company?.functionalCurrency ?? "IQD") }); }, disabled: !companyId, "data-testid": "new-journal", children: [_jsx(Plus, { "aria-hidden": "true" }), t("accounting.newJournal")] }) }), _jsxs("div", { className: "mb-4 grid gap-3 sm:grid-cols-3", children: [_jsx(CompanySelect, { companies: companies.data ?? [], value: companyId, onChange: setCompanyId }), _jsx(Field, { label: t("common.status"), children: _jsx(SelectField, { value: status, onChange: (e) => { setStatus(e.target.value); }, children: statuses.map((s) => (_jsx("option", { value: s, children: s ? t(`accounting.statuses.${s}`) : t("accounting.anyStatus") }, s))) }) })] }), _jsx(DataGrid, { label: "accounting.journals", columns: columns, data: rows, rowKey: (row) => row.id, loading: journals.isPending && Boolean(companyId), emptyTitle: t("accounting.noJournals"), emptyDescription: t("accounting.noJournalsHint"), onOpen: (row) => { open(row.id); } }), journals.hasNextPage ? (_jsx(Button, { variant: "secondary", className: "mt-3", onClick: () => { void journals.fetchNextPage(); }, loading: journals.isFetchingNextPage, children: t("common.loadMore") })) : null, _jsx(Dialog, { open: Boolean(openId) && !editing, onOpenChange: (isOpen) => { if (!isOpen) {
                    open(null);
                } }, children: _jsxs(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", dir: "auto", children: detail ? `${detail.number} · ${formatDate(detail.postingDate)}` : t("common.loading") }) }), detail ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "journal-detail", children: [_jsxs("div", { className: "flex flex-wrap items-center gap-2 text-sm", children: [_jsx(StatusBadge, { status: detail.status, label: t(`accounting.statuses.${detail.status}`) }), _jsx("span", { children: t(`accounting.kinds.${detail.kind}`) }), _jsx("span", { className: "text-fg-muted", children: localized(detail.description) }), detail.journalEntryId ? (_jsx(Link, { to: "/accounting/journal-entries", search: { open: detail.journalEntryId }, className: "underline-offset-2 hover:underline", children: t("accounting.viewEntry") })) : null, detail.correctsJournalId ? (_jsx("button", { type: "button", className: "underline-offset-2 hover:underline", onClick: () => { open(detail.correctsJournalId ?? null); }, children: t("accounting.correctsJournal") })) : null, detail.correctedByJournalId ? (_jsx("button", { type: "button", className: "underline-offset-2 hover:underline", onClick: () => { open(detail.correctedByJournalId ?? null); }, children: t("accounting.correctedBy") })) : null, detail.rejectionReason ? _jsx("span", { className: "text-danger", children: t("accounting.rejectedBecause", { reason: detail.rejectionReason }) }) : null] }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: "#" }), _jsx(TableHead, { children: t("accounting.account") }), _jsx(TableHead, { className: "text-end", children: t("accounting.debit") }), _jsx(TableHead, { className: "text-end", children: t("accounting.credit") })] }) }), _jsxs(TableBody, { children: [(detail.lines ?? []).map((line) => (_jsxs(TableRow, { children: [_jsx(TableCell, { children: String(line.lineNo) }), _jsxs(TableCell, { children: [_jsx("span", { dir: "ltr", children: line.accountCode }), " ", localized(line.accountName)] }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: line.debit }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: line.credit }) })] }, line.id))), _jsxs(TableRow, { className: "font-semibold", children: [_jsx(TableCell, { colSpan: 2, children: t("accounting.totals") }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: detail.totalDebit }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: detail.totalCredit }) })] })] })] }), _jsx(FormError, { message: problem?.message ?? null }), detail.status === "pending_approval" || detail.status === "posted" ? (_jsx(Field, { label: t("common.reason"), children: _jsx(TextField, { value: reason, onChange: (e) => { setReason(e.target.value); }, "data-testid": "journal-reason" }) })) : null, _jsxs(DialogFooter, { children: [editable ? (_jsx(Button, { variant: "secondary", onClick: () => { setProblem(null); setEditing({ id: detail.id, form: toForm(detail) }); }, "data-testid": "edit-journal", children: t("accounting.edit") })) : null, editable ? (_jsx(Button, { variant: "secondary", onClick: () => { act.mutate("submit"); }, loading: act.isPending, "data-testid": "submit-journal", children: t("accounting.submit") })) : null, detail.status === "pending_approval" ? (_jsxs(_Fragment, { children: [_jsx(Button, { variant: "secondary", onClick: () => { act.mutate("reject"); }, loading: act.isPending, children: t("accounting.reject") }), _jsx(Button, { onClick: () => { act.mutate("approve"); }, loading: act.isPending, "data-testid": "approve-journal", children: t("accounting.approve") })] })) : null, detail.status === "draft" || detail.status === "approved" ? (_jsx(Button, { onClick: () => { act.mutate("post"); }, loading: act.isPending, "data-testid": "post-journal", children: t("accounting.post") })) : null, detail.status !== "posted" && detail.status !== "cancelled" ? (_jsx(Button, { variant: "secondary", onClick: () => { act.mutate("cancel"); }, loading: act.isPending, children: t("common.cancel") })) : null, detail.status === "posted" && !detail.correctedByJournalId ? (_jsx(Button, { variant: "secondary", onClick: () => { act.mutate("correct"); }, loading: act.isPending, "data-testid": "correct-journal", children: t("accounting.correct") })) : null] })] })) : null] }) }), _jsx(Dialog, { open: Boolean(editing), onOpenChange: (isOpen) => { if (!isOpen) {
                    setEditing(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: editing ? (_jsxs("form", { onSubmit: submitForm, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: editing.id ? t("accounting.editJournal") : t("accounting.newJournal") }) }), _jsx(FormError, { message: problem && Object.keys(problem.fields).length === 0 ? problem.message : null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-3", children: [_jsx(Field, { label: t("accounting.kind"), children: _jsx(SelectField, { value: editing.form.kind, onChange: (e) => { setEditing({ ...editing, form: { ...editing.form, kind: e.target.value } }); }, children: kinds.map((k) => (_jsx("option", { value: k, children: t(`accounting.kinds.${k}`) }, k))) }) }), _jsx(Field, { label: t("accounting.postingDate"), required: true, error: problem?.fields.postingDate, children: _jsx(TextField, { type: "date", value: editing.form.postingDate, onChange: (e) => { setEditing({ ...editing, form: { ...editing.form, postingDate: e.target.value } }); }, required: true, dir: "ltr", "data-testid": "journal-date" }) }), _jsx(Field, { label: t("accounting.currency"), required: true, error: problem?.fields.currency, children: _jsx(TextField, { value: editing.form.currency, onChange: (e) => { setEditing({ ...editing, form: { ...editing.form, currency: e.target.value.toUpperCase() } }); }, required: true, dir: "ltr", maxLength: 3 }) }), _jsx(Field, { label: t("accounting.descriptionEn"), required: true, children: _jsx(TextField, { value: editing.form.descriptionEn, onChange: (e) => { setEditing({ ...editing, form: { ...editing.form, descriptionEn: e.target.value } }); }, required: true, "data-testid": "journal-description" }) }), _jsx(Field, { label: t("accounting.descriptionAr"), children: _jsx(TextField, { value: editing.form.descriptionAr, onChange: (e) => { setEditing({ ...editing, form: { ...editing.form, descriptionAr: e.target.value } }); }, dir: "rtl" }) }), _jsx(Field, { label: t("accounting.reference"), children: _jsx(TextField, { value: editing.form.reference, onChange: (e) => { setEditing({ ...editing, form: { ...editing.form, reference: e.target.value } }); } }) }), editing.form.kind === "accrual" ? (_jsx(Field, { label: t("accounting.autoReverseOn"), required: true, error: problem?.fields.autoReverseOn, children: _jsx(TextField, { type: "date", value: editing.form.autoReverseOn, onChange: (e) => { setEditing({ ...editing, form: { ...editing.form, autoReverseOn: e.target.value } }); }, required: true, dir: "ltr" }) })) : null] }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("accounting.accountCode") }), _jsx(TableHead, { className: "text-end", children: t("accounting.debit") }), _jsx(TableHead, { className: "text-end", children: t("accounting.credit") }), _jsx(TableHead, {})] }) }), _jsxs(TableBody, { children: [editing.form.lines.map((line, index) => (_jsxs(TableRow, { children: [_jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("accounting.accountCode"), value: line.accountCode, onChange: (e) => { updateLine(index, { accountCode: e.target.value }); }, dir: "ltr", "data-testid": `line-account-${index}` }) }), _jsx(TableNumberCell, { children: _jsx(TextField, { "aria-label": t("accounting.debit"), inputMode: "decimal", value: line.debit, onChange: (e) => { updateLine(index, { debit: e.target.value, credit: e.target.value ? "" : line.credit }); }, dir: "ltr", className: "text-end", "data-testid": `line-debit-${index}` }) }), _jsx(TableNumberCell, { children: _jsx(TextField, { "aria-label": t("accounting.credit"), inputMode: "decimal", value: line.credit, onChange: (e) => { updateLine(index, { credit: e.target.value, debit: e.target.value ? "" : line.debit }); }, dir: "ltr", className: "text-end", "data-testid": `line-credit-${index}` }) }), _jsx(TableCell, { children: _jsx(Button, { type: "button", variant: "ghost", size: "icon", "aria-label": t("accounting.removeLine"), onClick: () => { setEditing({ ...editing, form: { ...editing.form, lines: editing.form.lines.filter((_, i) => i !== index) } }); }, children: _jsx(Trash2, { "aria-hidden": "true" }) }) })] }, index))), _jsxs(TableRow, { className: "font-semibold", children: [_jsx(TableCell, { children: _jsxs(Button, { type: "button", variant: "secondary", onClick: () => { setEditing({ ...editing, form: { ...editing.form, lines: [...editing.form.lines, { accountCode: "", debit: "", credit: "" }] } }); }, "data-testid": "add-line", children: [_jsx(Plus, { "aria-hidden": "true" }), t("accounting.addLine")] }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: sum(editing.form.lines, "debit") }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: sum(editing.form.lines, "credit") }) }), _jsx(TableCell, {})] })] })] }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setEditing(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: save.isPending, "data-testid": "save-journal", children: t("common.save") })] })] })) : null }) })] }));
}
