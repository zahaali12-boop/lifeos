import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Input, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { Plus } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { rememberRecent } from "../../shell/CommandPalette";
import { Amount } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { DocStatus, Qty } from "./shared";
const types = ["stock", "non_stock", "service", "kit", "assembly"];
const trackings = ["none", "lot", "serial", "lot_and_serial"];
const empty = { code: "", nameEn: "", nameAr: "", type: "stock", baseUom: "PCS", categoryCode: "", brandCode: "", tracking: "none", expiryRequired: false, shelfLifeDays: "", fefo: false, listPrice: "", listPriceCurrency: "", isActive: true };
function toForm(item) {
    return {
        code: item.code,
        nameEn: item.name.en ?? "",
        nameAr: item.name.ar ?? "",
        type: item.type,
        baseUom: item.baseUom,
        categoryCode: item.categoryCode ?? "",
        brandCode: item.brandCode ?? "",
        tracking: item.tracking,
        expiryRequired: item.expiryRequired,
        shelfLifeDays: item.shelfLifeDays === null ? "" : String(item.shelfLifeDays),
        fefo: item.fefo,
        listPrice: item.listPrice === null ? "" : String(item.listPrice),
        listPriceCurrency: item.listPriceCurrency ?? "",
        isActive: item.isActive,
    };
}
/** The item master (roadmap 3.1): search, create and edit items, and see each item's units, barcodes, variants and suppliers. */
export function ItemsPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const navigate = useNavigate();
    const search = useSearch({ strict: false });
    const [query, setQuery] = useState("");
    const [editing, setEditing] = useState(null);
    const [masterOpen, setMasterOpen] = useState(false);
    const [problem, setProblem] = useState(null);
    const openId = search.open;
    const items = useInfiniteQuery({
        queryKey: ["items", query],
        queryFn: async ({ pageParam }) => unwrap(await api.GET("/api/v1/items", { params: { query: { limit: 100, ...(query.trim() ? { q: query.trim() } : {}), ...(pageParam ? { cursor: pageParam } : {}) } } })),
        initialPageParam: "",
        getNextPageParam: (last) => last.nextCursor ?? undefined,
    });
    const item = useQuery({
        queryKey: ["item", openId],
        enabled: Boolean(openId),
        queryFn: async () => unwrap(await api.GET("/api/v1/items/{itemId}", { params: { path: { itemId: openId ?? "" }, query: { expand: "uoms,variants,suppliers" } } })),
    });
    const uoms = useQuery({ queryKey: ["uoms"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/uoms")) });
    const categories = useQuery({ queryKey: ["item-categories"], queryFn: async () => unwrap(await api.GET("/api/v1/items/categories")) });
    const brands = useQuery({ queryKey: ["item-brands"], queryFn: async () => unwrap(await api.GET("/api/v1/items/brands")) });
    const open = (id) => { void navigate({ to: "/inventory/items", search: id ? { open: id } : {} }); };
    const save = useMutation({
        mutationFn: async (input) => {
            const f = input.form;
            const body = {
                code: f.code,
                name: { en: f.nameEn, ...(f.nameAr ? { ar: f.nameAr } : {}) },
                type: f.type,
                baseUom: f.baseUom,
                categoryCode: f.categoryCode || null,
                brandCode: f.brandCode || null,
                tracking: f.tracking,
                expiryRequired: f.expiryRequired,
                shelfLifeDays: f.shelfLifeDays ? Number(f.shelfLifeDays) : null,
                fefo: f.fefo,
                listPrice: f.listPrice || null,
                listPriceCurrency: f.listPriceCurrency || null,
                isActive: f.isActive,
            };
            return input.id
                ? unwrap(await api.PUT("/api/v1/items/{itemId}", { params: { path: { itemId: input.id } }, body }))
                : unwrap(await api.POST("/api/v1/items", { body }));
        },
        onSuccess: async (saved) => {
            setEditing(null);
            setProblem(null);
            rememberRecent({ to: `/inventory/items?open=${saved.id}`, label: `${saved.code} · ${localized(saved.name)}` });
            await queryClient.invalidateQueries({ queryKey: ["items"] });
            await queryClient.invalidateQueries({ queryKey: ["item", saved.id] });
            open(saved.id);
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const columns = useMemo(() => [
        { id: "code", accessorKey: "code", header: t("inventory.items.code"), size: 140, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.code }) },
        { id: "name", accessorFn: (row) => localized(row.name), header: t("inventory.items.name"), size: 300 },
        { id: "type", accessorKey: "type", header: t("inventory.items.type"), size: 100, cell: ({ row }) => t(`inventory.items.types.${row.original.type}`, { defaultValue: row.original.type }) },
        { id: "baseUom", accessorKey: "baseUom", header: t("inventory.items.baseUom"), size: 90 },
        { id: "tracking", accessorKey: "tracking", header: t("inventory.items.tracking"), size: 130, cell: ({ row }) => t(`inventory.items.trackings.${row.original.tracking}`, { defaultValue: row.original.tracking }) },
        { id: "category", accessorKey: "categoryCode", header: t("inventory.items.category"), size: 110 },
        { id: "brand", accessorKey: "brandCode", header: t("inventory.items.brand"), size: 110 },
        { id: "isActive", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => _jsx(Badge, { tone: row.original.isActive ? "success" : "neutral", children: row.original.isActive ? t("common.active") : t("common.inactive") }) },
        { id: "updatedAt", accessorKey: "updatedAt", header: t("common.updated"), size: 130, cell: ({ row }) => formatDate(row.original.updatedAt) },
    ], [t]);
    const rows = items.data?.pages.flatMap((page) => page.items) ?? [];
    const detail = item.data;
    const form = editing?.form;
    const isEdit = editing?.id != null;
    const setForm = (patch) => { setEditing((prev) => (prev ? { ...prev, form: { ...prev.form, ...patch } } : prev)); };
    const submit = (event) => {
        event.preventDefault();
        if (editing) {
            save.mutate(editing);
        }
    };
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.items"), description: t("inventory.items.description"), actions: _jsxs(_Fragment, { children: [_jsx(Button, { variant: "secondary", onClick: () => { setMasterOpen(true); }, "data-testid": "master-data", children: t("inventory.items.masterData") }), _jsxs(Button, { onClick: () => { setProblem(null); setEditing({ id: null, form: empty }); }, "data-testid": "new-item", children: [_jsx(Plus, { "aria-hidden": "true" }), t("inventory.items.new")] })] }) }), _jsx(DataGrid, { label: "nav.items", columns: columns, data: rows, rowKey: (row) => row.id, entityType: "item", loading: items.isPending, onOpen: (row) => { open(row.id); }, emptyTitle: t("inventory.items.emptyTitle"), emptyDescription: t("inventory.items.emptyDescription"), toolbar: _jsx(Input, { type: "search", placeholder: t("inventory.items.searchPlaceholder"), value: query, onChange: (e) => { setQuery(e.target.value); }, className: "w-64", "aria-label": t("common.search"), "data-testid": "item-search" }) }), items.hasNextPage ? (_jsx(Button, { variant: "secondary", className: "mt-3", onClick: () => { void items.fetchNextPage(); }, loading: items.isFetchingNextPage, children: t("common.loadMore") })) : null, _jsx(Dialog, { open: Boolean(openId) && !editing, onOpenChange: (isOpen) => { if (!isOpen) {
                    open(null);
                } }, children: _jsxs(DialogContent, { closeLabel: t("common.close"), className: "max-w-3xl", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", dir: "auto", children: detail ? `${detail.code} · ${localized(detail.name)}` : t("common.loading") }) }), detail ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "item-detail", children: [_jsxs("div", { className: "flex flex-wrap gap-2 text-sm", children: [_jsx(Badge, { tone: detail.isActive ? "success" : "neutral", children: detail.isActive ? t("common.active") : t("common.inactive") }), _jsx(Badge, { children: t(`inventory.items.types.${detail.type}`, { defaultValue: detail.type }) }), _jsx(Badge, { children: t(`inventory.items.trackings.${detail.tracking}`, { defaultValue: detail.tracking }) }), detail.fefo ? _jsx(Badge, { tone: "info", children: "FEFO" }) : null, detail.categoryCode ? _jsxs("span", { className: "text-fg-muted", children: [t("inventory.items.category"), ": ", detail.categoryCode] }) : null, detail.brandCode ? _jsxs("span", { className: "text-fg-muted", children: [t("inventory.items.brand"), ": ", detail.brandCode] }) : null, detail.listPrice !== null ? (_jsxs("span", { className: "text-fg-muted", children: [t("inventory.items.listPrice"), ": ", _jsx(Amount, { value: detail.listPrice }), " ", detail.listPriceCurrency] })) : null] }), _jsxs("section", { children: [_jsx("h3", { className: "mb-1 text-sm font-semibold", children: t("inventory.items.units") }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("inventory.items.unit") }), _jsx(TableHead, { children: t("inventory.items.factor") }), _jsx(TableHead, { children: t("inventory.items.barcodes") })] }) }), _jsx(TableBody, { children: (detail.uoms ?? []).map((u) => (_jsxs(TableRow, { children: [_jsxs(TableCell, { children: [u.uomCode, " ", u.isBase ? _jsx(Badge, { tone: "accent", children: t("inventory.items.base") }) : null] }), _jsxs(TableCell, { children: [_jsx(Qty, { value: u.numerator }), " / ", _jsx(Qty, { value: u.denominator })] }), _jsx(TableCell, { dir: "ltr", children: u.barcodes.map((b) => b.barcode).join(", ") })] }, u.id))) })] })] }), (detail.variants ?? []).length > 0 ? (_jsxs("section", { children: [_jsx("h3", { className: "mb-1 text-sm font-semibold", children: t("inventory.items.variants") }), _jsx("ul", { className: "flex flex-wrap gap-2 text-sm", children: (detail.variants ?? []).map((v) => (_jsxs("li", { className: "rounded-md border border-border px-2 py-1", children: [_jsx("span", { dir: "ltr", children: v.sku }), " ", _jsx("span", { className: "text-fg-muted", children: localized(v.name) })] }, v.id))) })] })) : null, (detail.suppliers ?? []).length > 0 ? (_jsxs("section", { children: [_jsx("h3", { className: "mb-1 text-sm font-semibold", children: t("inventory.items.suppliers") }), _jsx("ul", { className: "text-sm", children: (detail.suppliers ?? []).map((s) => (_jsxs("li", { children: [_jsx("span", { dir: "ltr", children: s.supplierItemCode ?? s.partnerId }), " \u00B7 ", t("inventory.items.leadTime", { days: Number(s.leadTimeDays ?? 0) }), s.isPreferred ? _jsx(Badge, { tone: "accent", className: "ms-2", children: t("inventory.items.preferred") }) : null] }, s.id))) })] })) : null, _jsx(DialogFooter, { children: _jsx(Button, { variant: "secondary", onClick: () => { setProblem(null); setEditing({ id: detail.id, form: toForm(detail) }); }, "data-testid": "edit-item", children: t("inventory.items.edit") }) })] })) : null] }) }), _jsx(Dialog, { open: Boolean(editing), onOpenChange: (isOpen) => { if (!isOpen) {
                    setEditing(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-3xl", children: form ? (_jsxs("form", { onSubmit: submit, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: isEdit ? t("inventory.items.edit") : t("inventory.items.new") }) }), _jsx(FormError, { message: problem && Object.keys(problem.fields).length === 0 ? problem.message : null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-3", children: [_jsx(Field, { label: t("inventory.items.code"), required: true, error: problem?.fields.code, children: _jsx(TextField, { value: form.code, onChange: (e) => { setForm({ code: e.target.value.toUpperCase() }); }, required: true, dir: "ltr", disabled: isEdit, "data-testid": "item-code" }) }), _jsx(Field, { label: t("inventory.items.nameEn"), required: true, error: problem?.fields.name, children: _jsx(TextField, { value: form.nameEn, onChange: (e) => { setForm({ nameEn: e.target.value }); }, required: true, "data-testid": "item-name-en" }) }), _jsx(Field, { label: t("inventory.items.nameAr"), children: _jsx(TextField, { value: form.nameAr, onChange: (e) => { setForm({ nameAr: e.target.value }); }, dir: "rtl", "data-testid": "item-name-ar" }) }), _jsx(Field, { label: t("inventory.items.type"), children: _jsx(SelectField, { value: form.type, onChange: (e) => { setForm({ type: e.target.value }); }, children: types.map((k) => (_jsx("option", { value: k, children: t(`inventory.items.types.${k}`) }, k))) }) }), _jsx(Field, { label: t("inventory.items.baseUom"), required: true, error: problem?.fields.baseUom, children: _jsx(SelectField, { value: form.baseUom, onChange: (e) => { setForm({ baseUom: e.target.value }); }, disabled: isEdit, "data-testid": "item-base-uom", children: (uoms.data ?? []).map((u) => (_jsxs("option", { value: u.code, children: [u.code, " \u00B7 ", localized(u.name)] }, u.id))) }) }), _jsx(Field, { label: t("inventory.items.tracking"), children: _jsx(SelectField, { value: form.tracking, onChange: (e) => { setForm({ tracking: e.target.value }); }, "data-testid": "item-tracking", children: trackings.map((k) => (_jsx("option", { value: k, children: t(`inventory.items.trackings.${k}`) }, k))) }) }), _jsx(Field, { label: t("inventory.items.category"), error: problem?.fields.category, children: _jsxs(SelectField, { value: form.categoryCode, onChange: (e) => { setForm({ categoryCode: e.target.value }); }, children: [_jsx("option", { value: "", children: "\u2014" }), (categories.data ?? []).map((c) => (_jsxs("option", { value: c.code, children: [c.code, " \u00B7 ", localized(c.name)] }, c.id)))] }) }), _jsx(Field, { label: t("inventory.items.brand"), error: problem?.fields.brand, children: _jsxs(SelectField, { value: form.brandCode, onChange: (e) => { setForm({ brandCode: e.target.value }); }, children: [_jsx("option", { value: "", children: "\u2014" }), (brands.data ?? []).map((b) => (_jsxs("option", { value: b.code, children: [b.code, " \u00B7 ", localized(b.name)] }, b.id)))] }) }), _jsx(Field, { label: t("inventory.items.shelfLifeDays"), error: problem?.fields.shelfLifeDays, children: _jsx(TextField, { inputMode: "numeric", value: form.shelfLifeDays, onChange: (e) => { setForm({ shelfLifeDays: e.target.value }); }, dir: "ltr" }) }), _jsx(Field, { label: t("inventory.items.listPrice"), error: problem?.fields.listPrice, children: _jsx(TextField, { inputMode: "decimal", value: form.listPrice, onChange: (e) => { setForm({ listPrice: e.target.value }); }, dir: "ltr" }) }), _jsx(Field, { label: t("inventory.items.listPriceCurrency"), children: _jsx(TextField, { value: form.listPriceCurrency, onChange: (e) => { setForm({ listPriceCurrency: e.target.value.toUpperCase() }); }, dir: "ltr", maxLength: 3 }) }), _jsxs("div", { className: "flex flex-col gap-2 self-end pb-2 text-sm", children: [_jsxs("label", { className: "flex items-center gap-2", children: [_jsx("input", { type: "checkbox", checked: form.expiryRequired, onChange: (e) => { setForm({ expiryRequired: e.target.checked }); } }), t("inventory.items.expiryRequired")] }), _jsxs("label", { className: "flex items-center gap-2", children: [_jsx("input", { type: "checkbox", checked: form.fefo, onChange: (e) => { setForm({ fefo: e.target.checked }); } }), t("inventory.items.fefo")] }), _jsxs("label", { className: "flex items-center gap-2", children: [_jsx("input", { type: "checkbox", checked: form.isActive, onChange: (e) => { setForm({ isActive: e.target.checked }); } }), t("common.active")] })] })] }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setEditing(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: save.isPending, "data-testid": "save-item", children: t("common.save") })] })] })) : null }) }), _jsx(MasterDataDialog, { open: masterOpen, onOpenChange: setMasterOpen })] }));
}
/** Categories and brands: the two lists an item picks from, created here so the master stays configuration, not code. */
function MasterDataDialog({ open, onOpenChange }) {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const [category, setCategory] = useState({ code: "", en: "", ar: "", parentCode: "" });
    const [brand, setBrand] = useState({ code: "", en: "", ar: "" });
    const [problem, setProblem] = useState(null);
    const categories = useQuery({ queryKey: ["item-categories"], queryFn: async () => unwrap(await api.GET("/api/v1/items/categories")) });
    const brands = useQuery({ queryKey: ["item-brands"], queryFn: async () => unwrap(await api.GET("/api/v1/items/brands")) });
    const addCategory = useMutation({
        mutationFn: async () => unwrap(await api.POST("/api/v1/items/categories", { body: { code: category.code, name: { en: category.en, ...(category.ar ? { ar: category.ar } : {}) }, parentCode: category.parentCode || null, isActive: true } })),
        onSuccess: async () => { setCategory({ code: "", en: "", ar: "", parentCode: "" }); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["item-categories"] }); },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const addBrand = useMutation({
        mutationFn: async () => unwrap(await api.POST("/api/v1/items/brands", { body: { code: brand.code, name: { en: brand.en, ...(brand.ar ? { ar: brand.ar } : {}) }, isActive: true } })),
        onSuccess: async () => { setBrand({ code: "", en: "", ar: "" }); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["item-brands"] }); },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    return (_jsx(Dialog, { open: open, onOpenChange: onOpenChange, children: _jsxs(DialogContent, { closeLabel: t("common.close"), className: "max-w-3xl", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: t("inventory.items.masterData") }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs("div", { className: "grid gap-6 sm:grid-cols-2", children: [_jsxs("form", { className: "flex flex-col gap-3", onSubmit: (e) => { e.preventDefault(); addCategory.mutate(); }, children: [_jsx("h3", { className: "text-sm font-semibold", children: t("inventory.items.categories") }), _jsx("ul", { className: "max-h-40 overflow-y-auto text-sm", children: (categories.data ?? []).map((c) => (_jsxs("li", { children: [_jsx("span", { dir: "ltr", children: c.code }), " \u00B7 ", localized(c.name), " ", c.parentCode ? _jsxs("span", { className: "text-fg-subtle", children: ["(", c.parentCode, ")"] }) : null] }, c.id))) }), _jsx(Field, { label: t("inventory.items.code"), required: true, children: _jsx(TextField, { value: category.code, onChange: (e) => { setCategory({ ...category, code: e.target.value.toUpperCase() }); }, required: true, dir: "ltr", "data-testid": "category-code" }) }), _jsx(Field, { label: t("inventory.items.nameEn"), required: true, children: _jsx(TextField, { value: category.en, onChange: (e) => { setCategory({ ...category, en: e.target.value }); }, required: true, "data-testid": "category-name-en" }) }), _jsx(Field, { label: t("inventory.items.nameAr"), children: _jsx(TextField, { value: category.ar, onChange: (e) => { setCategory({ ...category, ar: e.target.value }); }, dir: "rtl" }) }), _jsx(Field, { label: t("inventory.items.parentCategory"), children: _jsxs(SelectField, { value: category.parentCode, onChange: (e) => { setCategory({ ...category, parentCode: e.target.value }); }, children: [_jsx("option", { value: "", children: "\u2014" }), (categories.data ?? []).map((c) => (_jsx("option", { value: c.code, children: c.code }, c.id)))] }) }), _jsx(Button, { type: "submit", variant: "secondary", loading: addCategory.isPending, "data-testid": "save-category", children: t("inventory.items.addCategory") })] }), _jsxs("form", { className: "flex flex-col gap-3", onSubmit: (e) => { e.preventDefault(); addBrand.mutate(); }, children: [_jsx("h3", { className: "text-sm font-semibold", children: t("inventory.items.brands") }), _jsx("ul", { className: "max-h-40 overflow-y-auto text-sm", children: (brands.data ?? []).map((b) => (_jsxs("li", { children: [_jsx("span", { dir: "ltr", children: b.code }), " \u00B7 ", localized(b.name)] }, b.id))) }), _jsx(Field, { label: t("inventory.items.code"), required: true, children: _jsx(TextField, { value: brand.code, onChange: (e) => { setBrand({ ...brand, code: e.target.value.toUpperCase() }); }, required: true, dir: "ltr" }) }), _jsx(Field, { label: t("inventory.items.nameEn"), required: true, children: _jsx(TextField, { value: brand.en, onChange: (e) => { setBrand({ ...brand, en: e.target.value }); }, required: true }) }), _jsx(Field, { label: t("inventory.items.nameAr"), children: _jsx(TextField, { value: brand.ar, onChange: (e) => { setBrand({ ...brand, ar: e.target.value }); }, dir: "rtl" }) }), _jsx(Button, { type: "submit", variant: "secondary", loading: addBrand.isPending, children: t("inventory.items.addBrand") })] })] })] }) }));
}
export { DocStatus as ItemStatus };
