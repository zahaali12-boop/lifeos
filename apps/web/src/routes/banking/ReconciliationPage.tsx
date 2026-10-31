import { Badge, Button, Dialog, DialogContent, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus, Sparkles, Upload } from "lucide-react";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { useOpenRecord } from "../../lib/documents";
import { formatDate, formatDateTime, formatMoney, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, Tabs, useCompanyContext } from "../inventory/shared";
import { useBankAccounts } from "../payables/shared";
import { num } from "../purchasing/shared";
import { RecordActivity } from "../RecordDiscussion";

type Statement = components["schemas"]["BankStatementSummary"];
type Line = components["schemas"]["StatementLineSummary"];
type Rule = components["schemas"]["ReconciliationRuleSummary"];
type Reconciliation = components["schemas"]["BankReconciliationSummary"];
type Transaction = components["schemas"]["BankTransactionSummary"];

/** Income and expense roles a statement line may be booked to; control accounts are fed by their documents. */
const bookableRoles = ["BankCharges", "InterestIncome", "InterestExpense", "FxGainRealized", "FxLossRealized", "Revenue", "RoundingDifferences", "Suspense"];
const formats = ["csv", "ofx", "mt940", "camt053"];

interface ImportForm { file: File | null; format: string; mappingId: string }

interface RuleForm { id: string | null; nameEn: string; nameAr: string; accountRole: string; direction: string; priority: string; descriptionContains: string; referencePattern: string; amountMin: string; amountMax: string; bankAccountId: string; isActive: boolean }

interface MappingForm { name: string; delimiter: string; hasHeader: boolean; skipRows: string; dateColumn: string; dateFormat: string; amountColumn: string; debitColumn: string; creditColumn: string; descriptionColumn: string; referenceColumn: string; counterpartyColumn: string; decimalSeparator: string; thousandsSeparator: string; negateAmount: boolean }

const tones: Record<string, "neutral" | "info" | "success" | "warning" | "danger"> = { unmatched: "warning", matched: "success", excluded: "neutral", imported: "info", reconciled: "success", open: "info", completed: "success" };

function Status({ status, prefix }: { status: string; prefix: string }) {
  const { t } = useTranslation();
  return <Badge tone={tones[status] ?? "neutral"} data-testid="doc-status">{t(`banking.reconciliation.${prefix}.${status}`, { defaultValue: status })}</Badge>;
}

const emptyRule = (companyBank: string): RuleForm => ({ id: null, nameEn: "", nameAr: "", accountRole: "BankCharges", direction: "out", priority: "100", descriptionContains: "", referencePattern: "", amountMin: "", amountMax: "", bankAccountId: companyBank, isActive: true });
const emptyMapping = (): MappingForm => ({ name: "", delimiter: ",", hasHeader: true, skipRows: "0", dateColumn: "", dateFormat: "yyyy-MM-dd", amountColumn: "", debitColumn: "", creditColumn: "", descriptionColumn: "", referenceColumn: "", counterpartyColumn: "", decimalSeparator: ".", thousandsSeparator: ",", negateAmount: false });

/**
 * Bank reconciliation (roadmap 6.2): statements imported in the bank's format, their lines matched to the book's
 * movements by the matcher or by hand (several movements to one line, a movement booked from a line, a line set
 * aside), the account reconciled at a date once the bank's balance agrees with the book's, the rules that book
 * fees and interest, and the aging of what the bank has not confirmed.
 */
export function ReconciliationPage() {
  const { t } = useTranslation();
  const can = useCan();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const canManage = can("banking.reconciliation.manage");
  const [tab, setTab] = useState("statements");
  const [bankAccountId, setBankAccountId] = useState("");
  const [openId, setOpenId] = useOpenRecord("/banking/reconciliation");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [importForm, setImportForm] = useState<ImportForm>({ file: null, format: "", mappingId: "" });
  const [importKey, setImportKey] = useState(0);
  const [selectedLine, setSelectedLine] = useState<string | null>(null);
  const [chosen, setChosen] = useState<string[]>([]);
  const [bookRole, setBookRole] = useState("BankCharges");
  const [excludeReason, setExcludeReason] = useState("");
  const [asOf, setAsOf] = useState(today);
  const [statementBalance, setStatementBalance] = useState("");
  const [agingAsOf, setAgingAsOf] = useState(today);
  const [rule, setRule] = useState<RuleForm | null>(null);
  const [mapping, setMapping] = useState<MappingForm | null>(null);
  const [openReconciliationId, setOpenReconciliationId] = useState<string | null>(null);

  const banks = useBankAccounts(companyId);
  const bankList = (banks.data ?? []).filter((b) => b.kind === "bank");
  useEffect(() => {
    if (bankList.length > 0 && !bankList.some((b) => b.id === bankAccountId)) {
      setBankAccountId(bankList[0]?.id ?? "");
    }
  }, [bankList, bankAccountId]);
  const bank = bankList.find((b) => b.id === bankAccountId);
  const currency = bank?.currency ?? "";

  const statements = useQuery({
    queryKey: ["bank-statements", bankAccountId],
    enabled: Boolean(bankAccountId),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/reconciliation/statements", { params: { query: { bankAccountId } } })),
  });
  const statementDetail = useQuery({
    queryKey: ["bank-statement", openId],
    enabled: Boolean(openId),
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/reconciliation/statements/{statementId}", { params: { path: { statementId: openId ?? "" } } })),
  });
  const mappings = useQuery({ queryKey: ["statement-mappings"], queryFn: async () => unwrap(await api.GET("/api/v1/banking/reconciliation/mappings")) });
  const unmatchedLines = useQuery({
    queryKey: ["statement-lines", bankAccountId],
    enabled: Boolean(bankAccountId),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/reconciliation/lines", { params: { query: { bankAccountId } } })),
  });
  const unmatchedTransactions = useQuery({
    queryKey: ["unmatched-transactions", bankAccountId],
    enabled: Boolean(bankAccountId),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/reconciliation/transactions", { params: { query: { bankAccountId } } })),
  });
  const rules = useQuery({
    queryKey: ["reconciliation-rules", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/reconciliation/rules", { params: { query: { companyId } } })),
  });
  const balanceGiven = /^-?\d+(\.\d+)?$/u.test(statementBalance.trim());
  const report = useQuery({
    queryKey: ["reconciliation-report", bankAccountId, asOf, balanceGiven ? statementBalance.trim() : ""],
    enabled: Boolean(bankAccountId) && tab === "reconcile" && /^\d{4}-\d{2}-\d{2}$/u.test(asOf),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/reconciliation/report", { params: { query: { bankAccountId, asOf, ...(balanceGiven ? { statementBalance: Number(statementBalance) } : {}) } } })),
  });
  const reconciliations = useQuery({
    queryKey: ["bank-reconciliations", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/reconciliation/reconciliations", { params: { query: { companyId } } })),
  });
  const aging = useQuery({
    queryKey: ["unreconciled-aging", companyId, agingAsOf],
    enabled: Boolean(companyId) && tab === "aging" && /^\d{4}-\d{2}-\d{2}$/u.test(agingAsOf),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/reconciliation/aging", { params: { query: { companyId, asOf: agingAsOf } } })),
  });

  const refresh = async (): Promise<void> => {
    await Promise.all([["bank-statements"], ["bank-statement"], ["statement-lines"], ["unmatched-transactions"], ["reconciliation-report"], ["bank-reconciliations"], ["unreconciled-aging"], ["cheques"], ["cheque"], ["cheque-deposits"], ["bank-accounts"], ["bank-account"], ["activities"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setNotice(null); setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const ok = (message: string | null = null): void => { setProblem(null); setNotice(message); };

  const importStatement = useMutation({
    mutationFn: async (f: ImportForm) => {
      const form = new FormData();
      form.append("bankAccountId", bankAccountId);
      if (f.file) { form.append("file", f.file); }
      if (f.format) { form.append("format", f.format); }
      if (f.mappingId) { form.append("mappingId", f.mappingId); }
      return unwrap(await api.POST("/api/v1/banking/reconciliation/statements/import", { body: { bankAccountId, file: "" }, bodySerializer: () => form }));
    },
    onSuccess: async (made) => { ok(t("banking.reconciliation.import.imported", { lines: made.lineCount, skipped: made.skippedCount })); setImportForm({ file: null, format: "", mappingId: "" }); setImportKey((k) => k + 1); await refresh(); setOpenId(made.id); },
    onError: fail,
  });
  const autoMatch = useMutation({
    mutationFn: async (statementId: string | null) => unwrap(statementId
      ? await api.POST("/api/v1/banking/reconciliation/statements/{statementId}/auto-match", { params: { path: { statementId } } })
      : await api.POST("/api/v1/banking/reconciliation/auto-match", { params: { query: { bankAccountId } } })),
    onSuccess: async (result) => { ok(t("banking.reconciliation.statements.autoMatchResult", { matched: result.matched, created: result.created, cheques: result.chequesCleared, remaining: result.remaining })); await refresh(); },
    onError: fail,
  });
  const lineAction = useMutation({
    mutationFn: async (input: { lineId: string; action: "match" | "unmatch" | "create" | "exclude" | "include" }) => {
      const params = { path: { lineId: input.lineId } };
      switch (input.action) {
        case "match": return unwrap(await api.POST("/api/v1/banking/reconciliation/lines/{lineId}/match", { params, body: { bankTransactionIds: chosen } }));
        case "unmatch": return unwrap(await api.POST("/api/v1/banking/reconciliation/lines/{lineId}/unmatch", { params }));
        case "create": return unwrap(await api.POST("/api/v1/banking/reconciliation/lines/{lineId}/create", { params, body: { accountRole: bookRole, description: null } }));
        case "exclude": return unwrap(await api.POST("/api/v1/banking/reconciliation/lines/{lineId}/exclude", { params, body: { reason: excludeReason || null } }));
        case "include": return unwrap(await api.POST("/api/v1/banking/reconciliation/lines/{lineId}/include", { params }));
      }
    },
    onSuccess: async (line) => { ok(); if (line.status !== "unmatched") { setSelectedLine(null); setChosen([]); } await refresh(); },
    onError: fail,
  });
  const deleteStatement = useMutation({
    mutationFn: async (statementId: string) => { await api.DELETE("/api/v1/banking/reconciliation/statements/{statementId}", { params: { path: { statementId } } }).then(unwrap); },
    onSuccess: async () => { ok(); setOpenId(null); await refresh(); },
    onError: fail,
  });
  const saveRule = useMutation({
    mutationFn: async (f: RuleForm) => {
      const body = {
        companyId,
        name: { en: f.nameEn, ar: f.nameAr },
        accountRole: f.accountRole,
        priority: num(f.priority, 100),
        direction: f.direction,
        conditions: { descriptionContains: f.descriptionContains.split(",").map((w) => w.trim()).filter(Boolean), referencePattern: f.referencePattern || null, amountMin: f.amountMin ? num(f.amountMin) : null, amountMax: f.amountMax ? num(f.amountMax) : null },
        bankAccountId: f.bankAccountId || null,
        isActive: f.isActive,
      };
      return unwrap(f.id ? await api.PUT("/api/v1/banking/reconciliation/rules/{ruleId}", { params: { path: { ruleId: f.id } }, body }) : await api.POST("/api/v1/banking/reconciliation/rules", { body }));
    },
    onSuccess: async () => { ok(); setRule(null); await queryClient.invalidateQueries({ queryKey: ["reconciliation-rules"] }); },
    onError: fail,
  });
  const deleteRule = useMutation({
    mutationFn: async (ruleId: string) => { await api.DELETE("/api/v1/banking/reconciliation/rules/{ruleId}", { params: { path: { ruleId } } }).then(unwrap); },
    onSuccess: async () => { ok(); setRule(null); await queryClient.invalidateQueries({ queryKey: ["reconciliation-rules"] }); },
    onError: fail,
  });
  const saveMapping = useMutation({
    mutationFn: async (f: MappingForm) => unwrap(await api.POST("/api/v1/banking/reconciliation/mappings", {
      body: {
        name: f.name,
        settings: {
          delimiter: f.delimiter || ",", hasHeader: f.hasHeader, skipRows: num(f.skipRows), dateColumn: f.dateColumn || null, dateFormat: f.dateFormat || "yyyy-MM-dd",
          amountColumn: f.amountColumn || null, debitColumn: f.debitColumn || null, creditColumn: f.creditColumn || null, descriptionColumn: f.descriptionColumn || null, referenceColumn: f.referenceColumn || null, counterpartyColumn: f.counterpartyColumn || null,
          decimalSeparator: f.decimalSeparator || ".", thousandsSeparator: f.thousandsSeparator || null, negateAmount: f.negateAmount,
        },
        isActive: true,
      },
    })),
    onSuccess: async (made) => { ok(); setMapping(null); setImportForm((f) => ({ ...f, mappingId: made.id, format: "csv" })); await queryClient.invalidateQueries({ queryKey: ["statement-mappings"] }); },
    onError: fail,
  });
  const reconcile = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/banking/reconciliation/reconciliations", { body: { bankAccountId, asOf, statementBalance: report.data?.statementBalance ?? 0 } })),
    onSuccess: async (made) => { ok(); await refresh(); setOpenReconciliationId(made.id); },
    onError: fail,
  });
  const complete = useMutation({
    mutationFn: async (reconciliationId: string) => unwrap(await api.POST("/api/v1/banking/reconciliation/reconciliations/{reconciliationId}/complete", { params: { path: { reconciliationId } } })),
    onSuccess: async () => { ok(); await refresh(); },
    onError: fail,
  });

  const statementColumns = useMemo<ColumnDef<Statement, unknown>[]>(
    () => [
      { id: "file", accessorKey: "fileName", header: t("banking.reconciliation.statements.file"), size: 220, cell: ({ row }) => <span dir="ltr">{row.original.fileName}</span> },
      { id: "status", accessorFn: (r) => t(`banking.reconciliation.statementStatuses.${r.status}`), header: t("common.status"), size: 110, cell: ({ row }) => <Status status={row.original.status} prefix="statementStatuses" /> },
      { id: "format", accessorKey: "format", header: t("banking.reconciliation.statements.format"), size: 90, cell: ({ row }) => <span dir="ltr">{row.original.format.toUpperCase()}</span> },
      { id: "from", accessorKey: "fromDate", header: t("banking.reconciliation.statements.from"), size: 110, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.fromDate)}</span> },
      { id: "to", accessorKey: "toDate", header: t("banking.reconciliation.statements.to"), size: 110, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.toDate)}</span> },
      { id: "closing", accessorKey: "closingBalance", header: t("banking.reconciliation.statements.closing"), size: 130, cell: ({ row }) => <span className="tabular" dir="ltr">{row.original.closingBalance == null ? "—" : formatMoney(row.original.closingBalance, currency)}</span> },
      { id: "lines", accessorKey: "lineCount", header: t("banking.reconciliation.statements.lines"), size: 80, cell: ({ row }) => <span className="tabular" dir="ltr">{String(row.original.lineCount)}</span> },
      { id: "matched", accessorKey: "matchedCount", header: t("banking.reconciliation.statements.matched"), size: 90, cell: ({ row }) => <span className="tabular" dir="ltr">{String(row.original.matchedCount)}</span> },
      { id: "unmatched", accessorKey: "unmatchedCount", header: t("banking.reconciliation.statements.unmatched"), size: 90, cell: ({ row }) => <span className="tabular" dir="ltr">{String(row.original.unmatchedCount)}</span> },
      { id: "imported", accessorKey: "importedAt", header: t("banking.reconciliation.statements.importedAt"), size: 150, cell: ({ row }) => <span dir="ltr">{formatDateTime(row.original.importedAt)}</span> },
    ],
    [t, currency],
  );
  const lineColumns = useMemo<ColumnDef<Line, unknown>[]>(
    () => [
      { id: "date", accessorKey: "bookingDate", header: t("banking.reconciliation.lines.date"), size: 110, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.bookingDate)}</span> },
      { id: "amount", accessorKey: "amount", header: t("banking.reconciliation.lines.amount"), size: 130, meta: { aggregate: "sum" }, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.amount, currency)}</span> },
      { id: "description", accessorKey: "description", header: t("banking.reconciliation.lines.description"), size: 320, cell: ({ row }) => <span dir="auto">{row.original.description}</span> },
      { id: "counterparty", accessorKey: "counterparty", header: t("banking.reconciliation.lines.counterparty"), size: 160, cell: ({ row }) => <span dir="auto">{row.original.counterparty ?? "—"}</span> },
      { id: "reference", accessorKey: "reference", header: t("banking.reconciliation.lines.reference"), size: 140, cell: ({ row }) => <span dir="ltr">{row.original.reference ?? "—"}</span> },
    ],
    [t, currency],
  );
  const ruleColumns = useMemo<ColumnDef<Rule, unknown>[]>(
    () => [
      { id: "priority", accessorKey: "priority", header: t("banking.reconciliation.rules.priority"), size: 80, cell: ({ row }) => <span className="tabular" dir="ltr">{String(row.original.priority)}</span> },
      { id: "name", accessorFn: (r) => localized(r.name), header: t("common.name"), size: 220, cell: ({ row }) => <span dir="auto">{localized(row.original.name)}</span> },
      { id: "direction", accessorFn: (r) => t(`banking.reconciliation.directions.${r.direction}`), header: t("banking.reconciliation.rules.direction"), size: 100 },
      { id: "words", accessorFn: (r) => (r.conditions.descriptionContains ?? []).join(", "), header: t("banking.reconciliation.rules.descriptionContains"), size: 220, cell: ({ row }) => <span dir="auto">{(row.original.conditions.descriptionContains ?? []).join(", ") || "—"}</span> },
      { id: "role", accessorFn: (r) => t(`accountRoles.${r.accountRole ?? ""}`, { defaultValue: r.accountRole ?? "" }), header: t("banking.reconciliation.rules.role"), size: 180 },
      { id: "active", accessorFn: (r) => (r.isActive ? t("common.active") : t("common.inactive")), header: t("common.status"), size: 90 },
    ],
    [t],
  );
  const reconciliationColumns = useMemo<ColumnDef<Reconciliation, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("banking.reconciliation.reconcile.number"), size: 140, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "status", accessorFn: (r) => t(`banking.reconciliation.reconciliationStatuses.${r.status}`), header: t("common.status"), size: 110, cell: ({ row }) => <Status status={row.original.status} prefix="reconciliationStatuses" /> },
      { id: "bank", accessorKey: "bankAccountCode", header: t("nav.bankAccounts"), size: 100, cell: ({ row }) => <span dir="ltr">{row.original.bankAccountCode}</span> },
      { id: "asOf", accessorKey: "asOf", header: t("banking.reconciliation.reconcile.asOf"), size: 110, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.asOf)}</span> },
      { id: "statement", accessorKey: "statementBalance", header: t("banking.reconciliation.reconcile.statementBalance"), size: 140, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.statementBalance, currency)}</span> },
      { id: "book", accessorKey: "bookBalance", header: t("banking.reconciliation.reconcile.bookBalance"), size: 140, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.bookBalance, currency)}</span> },
      { id: "difference", accessorKey: "difference", header: t("banking.reconciliation.reconcile.difference"), size: 130, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.difference, currency)}</span> },
      { id: "completed", accessorKey: "completedAt", header: t("banking.reconciliation.reconcile.completedAt"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.completedAt ? formatDateTime(row.original.completedAt) : "—"}</span> },
    ],
    [t, currency],
  );

  const s = statementDetail.data;
  const openReconciliation = (reconciliations.data ?? []).find((r) => r.id === openReconciliationId) ?? (statementDetail.isError ? (reconciliations.data ?? []).find((r) => r.id === openId) : undefined);
  const line = (unmatchedLines.data ?? []).find((l) => l.id === selectedLine) ?? null;
  const candidates = unmatchedTransactions.data ?? [];
  const chosenSum = candidates.filter((c) => chosen.includes(c.id)).reduce((sum, c) => sum + Number(c.amountTc), 0);
  const sumsMatch = line !== null && chosen.length > 0 && Math.abs(chosenSum - Number(line.amount)) < 0.0000005;
  const pickLine = (id: string): void => { setProblem(null); setSelectedLine(id); setChosen([]); setExcludeReason(""); setTab("matching"); setOpenId(null); };
  const submitImport = (event: FormEvent): void => { event.preventDefault(); importStatement.mutate(importForm); };
  const submitRule = (event: FormEvent): void => { event.preventDefault(); if (rule) { saveRule.mutate(rule); } };
  const submitMapping = (event: FormEvent): void => { event.preventDefault(); if (mapping) { saveMapping.mutate(mapping); } };
  const editRule = (r: Rule): void => {
    setProblem(null);
    setRule({ id: r.id, nameEn: r.name.en ?? "", nameAr: r.name.ar ?? "", accountRole: r.accountRole ?? "BankCharges", direction: r.direction, priority: String(r.priority), descriptionContains: (r.conditions.descriptionContains ?? []).join(", "), referencePattern: r.conditions.referencePattern ?? "", amountMin: r.conditions.amountMin == null ? "" : String(r.conditions.amountMin), amountMax: r.conditions.amountMax == null ? "" : String(r.conditions.amountMax), bankAccountId: r.bankAccountId ?? "", isActive: r.isActive });
  };
  const money = (value: number | string): string => formatMoney(value, currency);

  const lineRows = (rows: Line[], withActions: boolean) => (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>{t("banking.reconciliation.lines.date")}</TableHead>
          <TableHead>{t("banking.reconciliation.lines.amount")}</TableHead>
          <TableHead>{t("banking.reconciliation.lines.description")}</TableHead>
          <TableHead>{t("banking.reconciliation.lines.reference")}</TableHead>
          <TableHead>{t("common.status")}</TableHead>
          <TableHead>{t("banking.reconciliation.lines.matches")}</TableHead>
          {withActions && canManage ? <TableHead>{t("common.actions")}</TableHead> : null}
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((l) => (
          <TableRow key={l.id} data-testid="statement-line" data-status={l.status}>
            <TableCell dir="ltr">{formatDate(l.bookingDate)}</TableCell>
            <TableCell className="tabular" dir="ltr">{money(l.amount)}</TableCell>
            <TableCell dir="auto">{l.description}{l.counterparty ? ` · ${l.counterparty}` : ""}</TableCell>
            <TableCell dir="ltr">{l.reference ?? "—"}</TableCell>
            <TableCell><Status status={l.status} prefix="lineStatuses" />{l.excludeReason ? <span className="ms-2 text-xs text-fg-muted" dir="auto">{l.excludeReason}</span> : null}</TableCell>
            <TableCell>
              {l.matches.length === 0 ? "—" : (
                <ul className="flex flex-col gap-0.5 text-xs">
                  {l.matches.map((m) => (
                    <li key={m.id} dir="auto">
                      {t(`banking.reconciliation.matchKinds.${m.matchKind}`, { defaultValue: m.matchKind })} · <span dir="ltr">{formatDate(m.transactionDate)}</span> · {t(`banking.transactionKinds.${m.transactionKind}`, { defaultValue: m.transactionKind })} · <span className="tabular" dir="ltr">{money(m.transactionAmount)}</span>{m.transactionReference ? <> · <span dir="ltr">{m.transactionReference}</span></> : null}
                    </li>
                  ))}
                </ul>
              )}
            </TableCell>
            {withActions && canManage ? (
              <TableCell>
                <div className="flex flex-wrap gap-1">
                  {l.status === "unmatched" ? <Button size="sm" variant="secondary" onClick={() => { pickLine(l.id); }} data-testid="line-match">{t("banking.reconciliation.lines.actions.match")}</Button> : null}
                  {l.status === "matched" ? <Button size="sm" variant="secondary" onClick={() => { lineAction.mutate({ lineId: l.id, action: "unmatch" }); }} data-testid="line-unmatch">{t("banking.reconciliation.lines.actions.unmatch")}</Button> : null}
                  {l.status === "excluded" ? <Button size="sm" variant="secondary" onClick={() => { lineAction.mutate({ lineId: l.id, action: "include" }); }} data-testid="line-include">{t("banking.reconciliation.lines.actions.include")}</Button> : null}
                </div>
              </TableCell>
            ) : null}
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );

  return (
    <>
      <PageHeader
        title={t("nav.reconciliation")}
        description={t("banking.reconciliation.description")}
        actions={canManage && tab === "rules" ? (
          <Button onClick={() => { setProblem(null); setRule(emptyRule("")); }} disabled={!companyId} data-testid="new-rule">
            <Plus aria-hidden="true" />
            {t("banking.reconciliation.rules.newRule")}
          </Button>
        ) : canManage && (tab === "statements" || tab === "matching") ? (
          <Button variant="secondary" onClick={() => { autoMatch.mutate(null); }} disabled={!bankAccountId} loading={autoMatch.isPending} data-testid="auto-match-account">
            <Sparkles aria-hidden="true" />
            {t("banking.reconciliation.matching.autoMatchAccount")}
          </Button>
        ) : null}
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        {tab !== "rules" && tab !== "aging" ? (
          <Field label={t("banking.reconciliation.bankAccount")}>
            <SelectField value={bankAccountId} onChange={(e) => { setBankAccountId(e.target.value); setSelectedLine(null); setChosen([]); }} data-testid="reconciliation-bank">
              {bankList.length === 0 ? <option value="">{t("banking.reconciliation.chooseBank")}</option> : null}
              {bankList.map((b) => <option key={b.id} value={b.id}>{b.code} · {localized(b.name)} · {b.currency}</option>)}
            </SelectField>
          </Field>
        ) : null}
        {tab === "reconcile" ? (
          <>
            <Field label={t("banking.reconciliation.reconcile.asOf")}>
              <TextField type="date" value={asOf} onChange={(e) => { setAsOf(e.target.value); }} dir="ltr" data-testid="reconcile-as-of" />
            </Field>
            <Field label={t("banking.reconciliation.reconcile.statementBalance")} description={t("banking.reconciliation.reconcile.statementBalanceHelp")}>
              <TextField inputMode="decimal" value={statementBalance} onChange={(e) => { setStatementBalance(e.target.value); }} dir="ltr" data-testid="reconcile-statement-balance" />
            </Field>
          </>
        ) : null}
        {tab === "aging" ? (
          <Field label={t("banking.reconciliation.aging.asOf")}>
            <TextField type="date" value={agingAsOf} onChange={(e) => { setAgingAsOf(e.target.value); }} dir="ltr" data-testid="aging-as-of" />
          </Field>
        ) : null}
      </div>
      <Tabs
        tabs={[
          { id: "statements", label: t("banking.reconciliation.tabs.statements"), testId: "tab-statements" },
          { id: "matching", label: t("banking.reconciliation.tabs.matching"), testId: "tab-matching" },
          { id: "reconcile", label: t("banking.reconciliation.tabs.reconcile"), testId: "tab-reconcile" },
          { id: "rules", label: t("banking.reconciliation.tabs.rules"), testId: "tab-rules" },
          { id: "aging", label: t("banking.reconciliation.tabs.aging"), testId: "tab-aging" },
        ]}
        value={tab}
        onChange={(id) => { setTab(id); setProblem(null); setNotice(null); }}
      />
      <FormError message={!openId && !rule && !mapping ? problem?.message ?? null : null} />
      {notice ? <p role="status" className="mb-3 rounded-md border border-border bg-bg-subtle px-3 py-2 text-sm" data-testid="reconciliation-notice">{notice}</p> : null}

      {tab === "statements" ? (
        <>
          {canManage ? (
            <form onSubmit={submitImport} className="mb-4 flex flex-wrap items-end gap-3 rounded-md border border-border p-3" data-testid="import-form">
              <Field label={t("banking.reconciliation.import.file")} required>
                <TextField key={importKey} type="file" accept=".csv,.txt,.ofx,.qfx,.xml,.sta,.mt940,.940" onChange={(e) => { setImportForm({ ...importForm, file: e.target.files?.[0] ?? null }); }} required data-testid="statement-file" />
              </Field>
              <Field label={t("banking.reconciliation.import.format")}>
                <SelectField value={importForm.format} onChange={(e) => { setImportForm({ ...importForm, format: e.target.value }); }} data-testid="statement-format">
                  <option value="">{t("banking.reconciliation.import.formatAuto")}</option>
                  {formats.map((f) => <option key={f} value={f}>{f.toUpperCase()}</option>)}
                </SelectField>
              </Field>
              <Field label={t("banking.reconciliation.import.mapping")} description={t("banking.reconciliation.import.mappingHelp")}>
                <SelectField value={importForm.mappingId} onChange={(e) => { setImportForm({ ...importForm, mappingId: e.target.value }); }} data-testid="statement-mapping">
                  <option value="">{t("banking.reconciliation.import.noMapping")}</option>
                  {(mappings.data ?? []).filter((m) => m.isActive).map((m) => <option key={m.id} value={m.id}>{m.name}</option>)}
                </SelectField>
              </Field>
              <Button type="submit" disabled={!bankAccountId || !importForm.file} loading={importStatement.isPending} data-testid="import-statement">
                <Upload aria-hidden="true" />
                {t("banking.reconciliation.import.importButton")}
              </Button>
              <Button type="button" variant="ghost" onClick={() => { setProblem(null); setMapping(emptyMapping()); }} data-testid="new-mapping">{t("banking.reconciliation.mappings.newMapping")}</Button>
            </form>
          ) : null}
          <DataGrid<Statement> label="banking.reconciliation.tabs.statements" columns={statementColumns} data={statements.data ?? []} rowKey={(row) => row.id} loading={statements.isPending && Boolean(bankAccountId)} emptyTitle={t("banking.reconciliation.statements.emptyTitle")} emptyDescription={t("banking.reconciliation.statements.emptyDescription")} onOpen={(row) => { setProblem(null); setOpenId(row.id); }} exportable={false} />
        </>
      ) : null}

      {tab === "matching" ? (
        <div className="flex flex-col gap-4">
          <p className="text-sm text-fg-muted">{t("banking.reconciliation.matching.help")}</p>
          <DataGrid<Line> label="banking.reconciliation.tabs.matching" columns={lineColumns} data={unmatchedLines.data ?? []} rowKey={(row) => row.id} loading={unmatchedLines.isPending && Boolean(bankAccountId)} emptyTitle={t("banking.reconciliation.matching.noLines")} emptyDescription={t("banking.reconciliation.matching.noLinesHelp")} onOpen={(row) => { pickLine(row.id); }} exportable={false} />
          {line ? (
            <section className="flex flex-col gap-3 rounded-md border border-border p-3" data-testid="match-panel">
              <h3 className="text-sm font-semibold" dir="auto">{t("banking.reconciliation.matching.lineTitle", { date: formatDate(line.bookingDate), amount: money(line.amount) })} · {line.description}</h3>
              {candidates.length === 0 ? <p className="text-sm text-fg-muted">{t("banking.reconciliation.matching.noCandidates")}</p> : (
                <ul className="flex flex-col gap-1 text-sm" data-testid="candidate-transactions">
                  {candidates.map((c: Transaction) => (
                    <li key={c.id}>
                      <label className="flex flex-wrap items-center gap-3">
                        <input type="checkbox" checked={chosen.includes(c.id)} onChange={(e) => { setChosen(e.target.checked ? [...chosen, c.id] : chosen.filter((id) => id !== c.id)); }} disabled={!canManage} data-testid={`candidate-${c.reference ?? c.id}`} />
                        <span dir="ltr">{formatDate(c.postingDate)}</span>
                        <span>{t(`banking.transactionKinds.${c.kind}`, { defaultValue: c.kind })}</span>
                        <span className="tabular" dir="ltr">{money(c.amountTc)}</span>
                        <span dir="auto">{c.reference ?? "—"}</span>
                      </label>
                    </li>
                  ))}
                </ul>
              )}
              <p className="text-sm" data-testid="chosen-sum">{t("banking.reconciliation.matching.selectedSum", { sum: money(chosenSum), amount: money(line.amount) })}</p>
              {canManage ? (
                <div className="flex flex-wrap items-end gap-3">
                  <Button onClick={() => { lineAction.mutate({ lineId: line.id, action: "match" }); }} disabled={!sumsMatch} loading={lineAction.isPending} data-testid="confirm-match">{t("banking.reconciliation.matching.matchButton")}</Button>
                  <Field label={t("banking.reconciliation.matching.bookAs")}>
                    <SelectField value={bookRole} onChange={(e) => { setBookRole(e.target.value); }} data-testid="book-role">
                      {bookableRoles.map((r) => <option key={r} value={r}>{t(`accountRoles.${r}`, { defaultValue: r })}</option>)}
                    </SelectField>
                  </Field>
                  <Button variant="secondary" onClick={() => { lineAction.mutate({ lineId: line.id, action: "create" }); }} loading={lineAction.isPending} data-testid="create-from-line">{t("banking.reconciliation.matching.createButton")}</Button>
                  <Field label={t("banking.reconciliation.lines.excludeReason")}>
                    <TextField value={excludeReason} onChange={(e) => { setExcludeReason(e.target.value); }} dir="auto" data-testid="exclude-reason" />
                  </Field>
                  <Button variant="ghost" onClick={() => { lineAction.mutate({ lineId: line.id, action: "exclude" }); }} data-testid="exclude-line">{t("banking.reconciliation.lines.actions.exclude")}</Button>
                </div>
              ) : null}
            </section>
          ) : null}
        </div>
      ) : null}

      {tab === "reconcile" ? (
        <div className="flex flex-col gap-4" data-testid="reconcile-tab">
          {report.data ? (
            <section className="grid gap-4 lg:grid-cols-2">
              <div className="flex flex-col gap-3 rounded-md border border-border p-3" data-testid="reconciliation-report">
                <h3 className="text-sm font-semibold">{t("banking.reconciliation.reconcile.reportTitle", { date: formatDate(report.data.asOf) })}</h3>
                <KeyValues entries={[
                  [t("banking.reconciliation.reconcile.statementBalance"), <span key="sb" className="tabular" dir="ltr">{money(report.data.statementBalance)}</span>],
                  [t("banking.reconciliation.reconcile.depositsInTransit"), <span key="dt" className="tabular" dir="ltr">{money(report.data.depositsInTransit)}</span>],
                  [t("banking.reconciliation.reconcile.outstandingPayments"), <span key="op" className="tabular" dir="ltr">{money(report.data.outstandingPayments)}</span>],
                  [t("banking.reconciliation.reconcile.adjustedBank"), <span key="ab" className="tabular" dir="ltr">{money(report.data.adjustedBankBalance)}</span>],
                  [t("banking.reconciliation.reconcile.bookBalance"), <span key="bb" className="tabular" dir="ltr">{money(report.data.bookBalance)}</span>],
                  [t("banking.reconciliation.reconcile.unmatchedBankItems"), <span key="ub" className="tabular" dir="ltr">{money(report.data.unmatchedBankItems)}</span>],
                  [t("banking.reconciliation.reconcile.difference"), <span key="df" className="tabular font-semibold" dir="ltr" data-testid="report-difference">{money(report.data.difference)}</span>],
                ]} />
                <p className="text-sm" data-testid="report-verdict">{report.data.canComplete ? t("banking.reconciliation.reconcile.balanced") : t("banking.reconciliation.reconcile.notBalanced", { count: report.data.unmatchedLines.length })}</p>
                {canManage ? <div><Button onClick={() => { reconcile.mutate(); }} loading={reconcile.isPending} data-testid="record-reconciliation">{t("banking.reconciliation.reconcile.record")}</Button></div> : null}
              </div>
              <div className="flex flex-col gap-3">
                <section className="flex flex-col gap-2">
                  <h3 className="text-sm font-semibold">{t("banking.reconciliation.reconcile.outstanding")}</h3>
                  {report.data.outstandingTransactions.length === 0 ? <p className="text-sm text-fg-muted">{t("banking.reconciliation.reconcile.nothingOutstanding")}</p> : (
                    <Table>
                      <TableHeader>
                        <TableRow>
                          <TableHead>{t("banking.reconciliation.transactions.date")}</TableHead>
                          <TableHead>{t("banking.reconciliation.transactions.kind")}</TableHead>
                          <TableHead>{t("banking.reconciliation.transactions.amount")}</TableHead>
                          <TableHead>{t("banking.reconciliation.transactions.reference")}</TableHead>
                        </TableRow>
                      </TableHeader>
                      <TableBody>
                        {report.data.outstandingTransactions.map((x) => (
                          <TableRow key={x.id} data-testid="outstanding-row">
                            <TableCell dir="ltr">{formatDate(x.postingDate)}</TableCell>
                            <TableCell>{t(`banking.transactionKinds.${x.kind}`, { defaultValue: x.kind })}</TableCell>
                            <TableCell className="tabular" dir="ltr">{money(x.amountTc)}</TableCell>
                            <TableCell dir="auto">{x.reference ?? "—"}</TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  )}
                </section>
                <section className="flex flex-col gap-2">
                  <h3 className="text-sm font-semibold">{t("banking.reconciliation.reconcile.unmatchedLines")}</h3>
                  {report.data.unmatchedLines.length === 0 ? <p className="text-sm text-fg-muted">{t("banking.reconciliation.reconcile.noUnmatchedLines")}</p> : lineRows(report.data.unmatchedLines, true)}
                </section>
              </div>
            </section>
          ) : null}
          <DataGrid<Reconciliation> label="banking.reconciliation.tabs.reconcile" columns={reconciliationColumns} data={reconciliations.data ?? []} rowKey={(row) => row.id} loading={reconciliations.isPending && Boolean(companyId)} emptyTitle={t("banking.reconciliation.reconcile.emptyTitle")} emptyDescription={t("banking.reconciliation.reconcile.emptyDescription")} onOpen={(row) => { setProblem(null); setOpenReconciliationId(row.id); }} exportable={false} />
        </div>
      ) : null}

      {tab === "rules" ? (
        <>
          <p className="mb-3 text-sm text-fg-muted">{t("banking.reconciliation.rules.help")}</p>
          <DataGrid<Rule> label="banking.reconciliation.tabs.rules" columns={ruleColumns} data={rules.data ?? []} rowKey={(row) => row.id} loading={rules.isPending && Boolean(companyId)} emptyTitle={t("banking.reconciliation.rules.emptyTitle")} emptyDescription={t("banking.reconciliation.rules.emptyDescription")} onOpen={editRule} exportable={false} />
        </>
      ) : null}

      {tab === "aging" ? (
        <div className="flex flex-col gap-3" data-testid="aging-tab">
          <p className="text-sm text-fg-muted">{t("banking.reconciliation.aging.help")}</p>
          {(aging.data ?? []).length === 0 ? <p className="text-sm text-fg-muted">{t("banking.reconciliation.aging.empty")}</p> : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("nav.bankAccounts")}</TableHead>
                  <TableHead>{t("banking.reconciliation.aging.count")}</TableHead>
                  <TableHead>{t("banking.reconciliation.aging.current")}</TableHead>
                  <TableHead>{t("banking.reconciliation.aging.d31to60")}</TableHead>
                  <TableHead>{t("banking.reconciliation.aging.d61to90")}</TableHead>
                  <TableHead>{t("banking.reconciliation.aging.over90")}</TableHead>
                  <TableHead>{t("banking.reconciliation.aging.total")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {(aging.data ?? []).map((r) => (
                  <TableRow key={r.bankAccountId} data-testid="aging-row">
                    <TableCell dir="ltr">{r.bankAccountCode}</TableCell>
                    <TableCell className="tabular" dir="ltr">{String(r.count)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{formatMoney(r.current, r.currency)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{formatMoney(r.days31To60, r.currency)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{formatMoney(r.days61To90, r.currency)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{formatMoney(r.over90, r.currency)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{formatMoney(r.total, r.currency)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </div>
      ) : null}

      <Dialog open={Boolean(openId) && !openReconciliation} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setProblem(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          {s ? (
            <div className="flex flex-col gap-4" data-testid="statement-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{s.fileName}</span>
                  <Status status={s.status} prefix="statementStatuses" />
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="flex flex-wrap items-start justify-between gap-3">
                <KeyValues entries={[
                  [t("nav.bankAccounts"), <span key="b" dir="ltr">{s.bankAccountCode}</span>],
                  [t("banking.reconciliation.statements.format"), <span key="f" dir="ltr">{s.format.toUpperCase()}</span>],
                  [t("banking.reconciliation.statements.period"), <span key="p" dir="ltr">{formatDate(s.fromDate)} – {formatDate(s.toDate)}</span>],
                  [t("banking.reconciliation.statements.opening"), <span key="o" className="tabular" dir="ltr">{s.openingBalance == null ? "—" : money(s.openingBalance)}</span>],
                  [t("banking.reconciliation.statements.closing"), <span key="c" className="tabular" dir="ltr">{s.closingBalance == null ? "—" : money(s.closingBalance)}</span>],
                  [t("banking.reconciliation.statements.counts"), <span key="n" dir="ltr" data-testid="statement-counts">{t("banking.reconciliation.statements.countsValue", { lines: s.lineCount, matched: s.matchedCount, unmatched: s.unmatchedCount, excluded: s.excludedCount, skipped: s.skippedCount })}</span>],
                  [t("banking.reconciliation.statements.importedAt"), <span key="i" dir="ltr">{formatDateTime(s.importedAt)}</span>],
                ]} />
                {canManage && s.status === "imported" ? (
                  <div className="flex flex-wrap gap-2">
                    <Button onClick={() => { autoMatch.mutate(s.id); }} disabled={Number(s.unmatchedCount) === 0} loading={autoMatch.isPending} data-testid="auto-match-statement">
                      <Sparkles aria-hidden="true" />
                      {t("banking.reconciliation.statements.autoMatch")}
                    </Button>
                    <Button variant="ghost" onClick={() => { deleteStatement.mutate(s.id); }} disabled={Number(s.matchedCount) > 0} data-testid="delete-statement">{t("common.delete")}</Button>
                  </div>
                ) : null}
              </div>
              {notice ? <p role="status" className="rounded-md border border-border bg-bg-subtle px-3 py-2 text-sm">{notice}</p> : null}
              {lineRows(s.lines, true)}
              <RecordActivity entityType="bank_statement" entityId={s.id} />
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openReconciliation)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenReconciliationId(null); if (statementDetail.isError) { setOpenId(null); } setProblem(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          {openReconciliation ? (
            <div className="flex flex-col gap-4" data-testid="reconciliation-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{openReconciliation.number}</span>
                  <Status status={openReconciliation.status} prefix="reconciliationStatuses" />
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("nav.bankAccounts"), <span key="b" dir="ltr">{openReconciliation.bankAccountCode}</span>],
                [t("banking.reconciliation.reconcile.asOf"), <span key="d" dir="ltr">{formatDate(openReconciliation.asOf)}</span>],
                [t("banking.reconciliation.reconcile.statementBalance"), <span key="sb" className="tabular" dir="ltr">{money(openReconciliation.statementBalance)}</span>],
                [t("banking.reconciliation.reconcile.depositsInTransit"), <span key="dt" className="tabular" dir="ltr">{money(openReconciliation.depositsInTransit)}</span>],
                [t("banking.reconciliation.reconcile.outstandingPayments"), <span key="op" className="tabular" dir="ltr">{money(openReconciliation.outstandingPayments)}</span>],
                [t("banking.reconciliation.reconcile.bookBalance"), <span key="bb" className="tabular" dir="ltr">{money(openReconciliation.bookBalance)}</span>],
                [t("banking.reconciliation.reconcile.unmatchedBankItems"), <span key="ub" className="tabular" dir="ltr">{money(openReconciliation.unmatchedBankItems)}</span>],
                [t("banking.reconciliation.reconcile.difference"), <span key="df" className="tabular font-semibold" dir="ltr">{money(openReconciliation.difference)}</span>],
                [t("banking.reconciliation.reconcile.completedAt"), <span key="ca" dir="ltr">{openReconciliation.completedAt ? formatDateTime(openReconciliation.completedAt) : "—"}</span>],
              ]} />
              {canManage && openReconciliation.status === "open" ? (
                <div><Button onClick={() => { complete.mutate(openReconciliation.id); }} loading={complete.isPending} data-testid="complete-reconciliation">{t("banking.reconciliation.reconcile.complete")}</Button></div>
              ) : null}
              <RecordActivity entityType="bank_reconciliation" entityId={openReconciliation.id} />
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(rule)} onOpenChange={(isOpen) => { if (!isOpen) { setRule(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {rule ? (
            <form onSubmit={submitRule} className="flex flex-col gap-4" data-testid="rule-form">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{rule.id ? t("banking.reconciliation.rules.editRule") : t("banking.reconciliation.rules.newRule")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("common.nameEn")} required>
                  <TextField value={rule.nameEn} onChange={(e) => { setRule({ ...rule, nameEn: e.target.value }); }} dir="ltr" required data-testid="rule-name-en" />
                </Field>
                <Field label={t("common.nameAr")} required>
                  <TextField value={rule.nameAr} onChange={(e) => { setRule({ ...rule, nameAr: e.target.value }); }} dir="rtl" required data-testid="rule-name-ar" />
                </Field>
                <Field label={t("banking.reconciliation.rules.role")} required>
                  <SelectField value={rule.accountRole} onChange={(e) => { setRule({ ...rule, accountRole: e.target.value }); }} data-testid="rule-role">
                    {bookableRoles.map((r) => <option key={r} value={r}>{t(`accountRoles.${r}`, { defaultValue: r })}</option>)}
                  </SelectField>
                </Field>
                <Field label={t("banking.reconciliation.rules.direction")}>
                  <SelectField value={rule.direction} onChange={(e) => { setRule({ ...rule, direction: e.target.value }); }} data-testid="rule-direction">
                    {["out", "in", "any"].map((d) => <option key={d} value={d}>{t(`banking.reconciliation.directions.${d}`)}</option>)}
                  </SelectField>
                </Field>
                <Field label={t("banking.reconciliation.rules.descriptionContains")} description={t("banking.reconciliation.rules.descriptionContainsHelp")}>
                  <TextField value={rule.descriptionContains} onChange={(e) => { setRule({ ...rule, descriptionContains: e.target.value }); }} dir="auto" data-testid="rule-words" />
                </Field>
                <Field label={t("banking.reconciliation.rules.referencePattern")} description={t("banking.reconciliation.rules.referencePatternHelp")}>
                  <TextField value={rule.referencePattern} onChange={(e) => { setRule({ ...rule, referencePattern: e.target.value }); }} dir="ltr" data-testid="rule-pattern" />
                </Field>
                <Field label={t("banking.reconciliation.rules.amountMin")}>
                  <TextField inputMode="decimal" value={rule.amountMin} onChange={(e) => { setRule({ ...rule, amountMin: e.target.value }); }} dir="ltr" data-testid="rule-amount-min" />
                </Field>
                <Field label={t("banking.reconciliation.rules.amountMax")}>
                  <TextField inputMode="decimal" value={rule.amountMax} onChange={(e) => { setRule({ ...rule, amountMax: e.target.value }); }} dir="ltr" data-testid="rule-amount-max" />
                </Field>
                <Field label={t("banking.reconciliation.rules.priority")} description={t("banking.reconciliation.rules.priorityHelp")}>
                  <TextField inputMode="numeric" value={rule.priority} onChange={(e) => { setRule({ ...rule, priority: e.target.value }); }} dir="ltr" data-testid="rule-priority" />
                </Field>
                <Field label={t("nav.bankAccounts")}>
                  <SelectField value={rule.bankAccountId} onChange={(e) => { setRule({ ...rule, bankAccountId: e.target.value }); }} data-testid="rule-bank">
                    <option value="">{t("banking.reconciliation.rules.anyBank")}</option>
                    {bankList.map((b) => <option key={b.id} value={b.id}>{b.code} · {b.currency}</option>)}
                  </SelectField>
                </Field>
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={rule.isActive} onChange={(e) => { setRule({ ...rule, isActive: e.target.checked }); }} data-testid="rule-active" />
                  {t("common.active")}
                </label>
              </div>
              <div className="flex flex-wrap gap-2">
                <Button type="submit" loading={saveRule.isPending} data-testid="save-rule">{t("common.save")}</Button>
                <Button type="button" variant="secondary" onClick={() => { setRule(null); }}>{t("common.cancel")}</Button>
                {rule.id ? <Button type="button" variant="ghost" onClick={() => { if (rule.id) { deleteRule.mutate(rule.id); } }} data-testid="delete-rule">{t("common.delete")}</Button> : null}
              </div>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(mapping)} onOpenChange={(isOpen) => { if (!isOpen) { setMapping(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {mapping ? (
            <form onSubmit={submitMapping} className="flex flex-col gap-4" data-testid="mapping-form">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("banking.reconciliation.mappings.newMapping")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <p className="text-sm text-fg-muted">{t("banking.reconciliation.mappings.help")}</p>
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label={t("common.name")} required>
                  <TextField value={mapping.name} onChange={(e) => { setMapping({ ...mapping, name: e.target.value }); }} dir="auto" required data-testid="mapping-name" />
                </Field>
                <Field label={t("banking.reconciliation.mappings.delimiter")}>
                  <SelectField value={mapping.delimiter} onChange={(e) => { setMapping({ ...mapping, delimiter: e.target.value }); }} data-testid="mapping-delimiter">
                    <option value=",">,</option>
                    <option value=";">;</option>
                    <option value="tab">{t("banking.reconciliation.mappings.tab")}</option>
                    <option value="|">|</option>
                  </SelectField>
                </Field>
                <Field label={t("banking.reconciliation.mappings.dateFormat")} description={t("banking.reconciliation.mappings.dateFormatHelp")}>
                  <TextField value={mapping.dateFormat} onChange={(e) => { setMapping({ ...mapping, dateFormat: e.target.value }); }} dir="ltr" data-testid="mapping-date-format" />
                </Field>
                <Field label={t("banking.reconciliation.mappings.dateColumn")}>
                  <TextField value={mapping.dateColumn} onChange={(e) => { setMapping({ ...mapping, dateColumn: e.target.value }); }} dir="auto" data-testid="mapping-date-column" />
                </Field>
                <Field label={t("banking.reconciliation.mappings.amountColumn")}>
                  <TextField value={mapping.amountColumn} onChange={(e) => { setMapping({ ...mapping, amountColumn: e.target.value }); }} dir="auto" data-testid="mapping-amount-column" />
                </Field>
                <Field label={t("banking.reconciliation.mappings.debitColumn")}>
                  <TextField value={mapping.debitColumn} onChange={(e) => { setMapping({ ...mapping, debitColumn: e.target.value }); }} dir="auto" data-testid="mapping-debit-column" />
                </Field>
                <Field label={t("banking.reconciliation.mappings.creditColumn")}>
                  <TextField value={mapping.creditColumn} onChange={(e) => { setMapping({ ...mapping, creditColumn: e.target.value }); }} dir="auto" data-testid="mapping-credit-column" />
                </Field>
                <Field label={t("banking.reconciliation.mappings.descriptionColumn")}>
                  <TextField value={mapping.descriptionColumn} onChange={(e) => { setMapping({ ...mapping, descriptionColumn: e.target.value }); }} dir="auto" />
                </Field>
                <Field label={t("banking.reconciliation.mappings.referenceColumn")}>
                  <TextField value={mapping.referenceColumn} onChange={(e) => { setMapping({ ...mapping, referenceColumn: e.target.value }); }} dir="auto" />
                </Field>
                <Field label={t("banking.reconciliation.mappings.counterpartyColumn")}>
                  <TextField value={mapping.counterpartyColumn} onChange={(e) => { setMapping({ ...mapping, counterpartyColumn: e.target.value }); }} dir="auto" />
                </Field>
                <Field label={t("banking.reconciliation.mappings.decimalSeparator")}>
                  <SelectField value={mapping.decimalSeparator} onChange={(e) => { setMapping({ ...mapping, decimalSeparator: e.target.value, thousandsSeparator: e.target.value === "," ? "." : "," }); }}>
                    <option value=".">.</option>
                    <option value=",">,</option>
                  </SelectField>
                </Field>
                <Field label={t("banking.reconciliation.mappings.skipRows")}>
                  <TextField inputMode="numeric" value={mapping.skipRows} onChange={(e) => { setMapping({ ...mapping, skipRows: e.target.value }); }} dir="ltr" />
                </Field>
                <div className="flex flex-col gap-2 text-sm">
                  <label className="flex items-center gap-2"><input type="checkbox" checked={mapping.hasHeader} onChange={(e) => { setMapping({ ...mapping, hasHeader: e.target.checked }); }} data-testid="mapping-has-header" />{t("banking.reconciliation.mappings.hasHeader")}</label>
                  <label className="flex items-center gap-2"><input type="checkbox" checked={mapping.negateAmount} onChange={(e) => { setMapping({ ...mapping, negateAmount: e.target.checked }); }} />{t("banking.reconciliation.mappings.negateAmount")}</label>
                </div>
              </div>
              <div className="flex gap-2">
                <Button type="submit" loading={saveMapping.isPending} data-testid="save-mapping">{t("common.save")}</Button>
                <Button type="button" variant="secondary" onClick={() => { setMapping(null); }}>{t("common.cancel")}</Button>
              </div>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
