import { Button, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow, cn } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, ChevronRight } from "lucide-react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatNumber, localized } from "../../lib/format";

export type Layout = components["schemas"]["LayoutSummary"];
export type LayoutDefinition = components["schemas"]["LayoutDefinition"];
export type LayoutRow = components["schemas"]["LayoutRowDefinition"];
export type LayoutColumn = components["schemas"]["LayoutColumnDefinition"];
export type LayoutOptions = components["schemas"]["LayoutOptions"];
export type Statement = components["schemas"]["StatementResult"];
export type StatementRow = components["schemas"]["StatementRow"];
export type StatementColumn = components["schemas"]["StatementColumn"];
export type StatementAccountLine = components["schemas"]["StatementAccountLine"];

export const layoutKinds = ["pl", "bs", "tb", "cashflow", "management"] as const;
export const rowTypes = ["header", "accounts", "formula", "blank"] as const;
export const columnTypes = ["period", "ytd", "prior_period", "prior_year", "custom_range", "opening", "debits", "credits", "closing", "variance", "variance_pct"] as const;
export const derivedColumnTypes = ["variance", "variance_pct"] as const;
export const accountTypes = ["asset", "liability", "equity", "revenue", "expense"] as const;

/** The shipped layouts and the tenant's for a company; a shipped one has no id, so its key is its code. */
export function useLayouts(companyId: string) {
  return useQuery({
    queryKey: ["statement-layouts", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/reporting/statements/layouts", { params: { query: { companyId } } })),
  });
}

export function layoutKey(layout: Pick<Layout, "id" | "code">): string {
  return layout.id ?? `shipped:${layout.code}`;
}

/** The run request's layout part from a list key: a tenant layout by id, a shipped one by code. */
export function layoutRef(key: string): { layoutId?: string; layoutCode?: string } {
  return key.startsWith("shipped:") ? { layoutCode: key.slice("shipped:".length) } : { layoutId: key };
}

export function cellText(value: number | string | null | undefined, percent: boolean): string {
  if (value === null || value === undefined || value === "") {
    return "—";
  }
  return percent ? `${formatNumber(value, { maximumFractionDigits: 1 })} %` : formatNumber(value, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

/**
 * A statement as a table: headers, account rows with their accounts folded under them, formulas in bold, one column
 * per layout column; a figure of an account row opens the accounts behind it, an account line opens the ledger.
 */
export function StatementTable({ statement, expanded, onToggle, onDrill, onOpenAccount, testId = "statement-table" }: {
  statement: Statement;
  expanded: Set<string>;
  onToggle: (rowCode: string) => void;
  onDrill?: (row: StatementRow, column: StatementColumn, index: number) => void;
  onOpenAccount?: (account: StatementAccountLine, column: StatementColumn) => void;
  testId?: string;
}) {
  const { t } = useTranslation();
  return (
    <Table data-testid={testId}>
      <caption className="sr-only">{localized(statement.name)}</caption>
      <TableHeader>
        <TableRow>
          <TableHead>{t("statements.line")}</TableHead>
          {statement.columns.map((c) => (
            <TableHead key={c.code} className="text-end">
              <div>{localized(c.label)}</div>
              {c.from && c.to ? <div className="text-[10px] font-normal normal-case tracking-normal text-fg-subtle" dir="ltr">{c.from} → {c.to}</div> : null}
            </TableHead>
          ))}
        </TableRow>
      </TableHeader>
      <TableBody>
        {statement.rows.map((row) => {
          const accounts = row.accounts ?? [];
          const open = expanded.has(row.code);
          const canExpand = accounts.length > 0;
          if (row.rowType === "blank") {
            return <TableRow key={row.code}><TableCell colSpan={statement.columns.length + 1} className="h-4 py-0" /></TableRow>;
          }
          return (
            <StatementRows key={row.code} row={row} statement={statement} open={open} canExpand={canExpand} onToggle={onToggle} onDrill={onDrill} onOpenAccount={onOpenAccount} />
          );
        })}
      </TableBody>
    </Table>
  );
}

function StatementRows({ row, statement, open, canExpand, onToggle, onDrill, onOpenAccount }: {
  row: StatementRow;
  statement: Statement;
  open: boolean;
  canExpand: boolean;
  onToggle: (rowCode: string) => void;
  onDrill?: ((row: StatementRow, column: StatementColumn, index: number) => void) | undefined;
  onOpenAccount?: ((account: StatementAccountLine, column: StatementColumn) => void) | undefined;
}) {
  const { t } = useTranslation();
  const indent = { paddingInlineStart: `${0.75 + Number(row.indent) * 1.25}rem` };
  const isHeader = row.rowType === "header";
  return (
    <>
      <TableRow className={cn(row.bold && "font-semibold", isHeader && "bg-surface-sunken/60")} data-testid={`statement-row-${row.code}`}>
        <TableCell style={indent}>
          {canExpand ? (
            <button type="button" onClick={() => { onToggle(row.code); }} aria-expanded={open} className="inline-flex items-center gap-1 rounded hover:underline" data-testid={`toggle-${row.code}`}>
              {open ? <ChevronDown className="size-3.5 rtl:-scale-x-100" aria-hidden="true" /> : <ChevronRight className="size-3.5 rtl:-scale-x-100" aria-hidden="true" />}
              <span dir="auto">{localized(row.label)}</span>
            </button>
          ) : <span dir="auto">{localized(row.label)}</span>}
        </TableCell>
        {statement.columns.map((column, index) => {
          const value = row.values[index] ?? null;
          const text = isHeader ? "" : cellText(value, column.isPercent);
          const drillable = row.rowType === "accounts" && onDrill && value !== null && !column.isPercent;
          return (
            <TableNumberCell key={column.code} dir="ltr" data-testid={`cell-${row.code}-${column.code}`}>
              {drillable ? (
                <button type="button" className="rounded px-1 text-accent underline-offset-4 hover:underline" onClick={() => { onDrill(row, column, index); }} title={t("statements.drillHint")}>{text}</button>
              ) : text}
            </TableNumberCell>
          );
        })}
      </TableRow>
      {open ? (row.accounts ?? []).map((account) => (
        <TableRow key={account.accountId} className="text-fg-muted" data-testid={`account-${account.code}`}>
          <TableCell style={{ paddingInlineStart: `${2.25 + Number(row.indent) * 1.25}rem` }}>
            <span dir="ltr" className="font-mono text-xs">{account.code}</span> <span dir="auto">{localized(account.name)}</span>
          </TableCell>
          {statement.columns.map((column, index) => {
            const value = account.values[index] ?? null;
            const text = cellText(value, column.isPercent);
            return (
              <TableNumberCell key={column.code} dir="ltr">
                {onOpenAccount && value !== null && !column.isPercent && column.from && column.to ? (
                  <Button variant="link" size="sm" className="h-auto px-1 py-0 text-inherit" onClick={() => { onOpenAccount(account, column); }} title={t("statements.openLedger")}>{text}</Button>
                ) : text}
              </TableNumberCell>
            );
          })}
        </TableRow>
      )) : null}
    </>
  );
}

/** The table as the export takes it: the line and one number per column, account lines under their rows. */
export function statementExport(statement: Statement): { columns: { header: string; type: string }[]; rows: unknown[][] } {
  const columns = [{ header: "", type: "text" }, ...statement.columns.map((c) => ({ header: localized(c.label), type: "number" }))];
  const rows: unknown[][] = [];
  for (const row of statement.rows) {
    if (row.rowType === "blank") {
      continue;
    }
    rows.push([localized(row.label), ...statement.columns.map((_, i) => (row.rowType === "header" ? null : row.values[i] ?? null))]);
    for (const account of row.accounts ?? []) {
      rows.push([`  ${account.code} ${localized(account.name)}`, ...statement.columns.map((_, i) => account.values[i] ?? null)]);
    }
  }
  return { columns, rows };
}
