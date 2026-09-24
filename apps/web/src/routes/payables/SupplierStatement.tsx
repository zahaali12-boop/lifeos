import { Badge, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { recordRoute } from "../../lib/documents";
import { formatDate, formatMoney, localized } from "../../lib/format";
import { kindLabel } from "./shared";

/**
 * A supplier's statement for a period, per document currency: what was owed before it, each document booked and each
 * reversal with the running balance, and what is owed at its end. Document numbers open the document.
 */
export function SupplierStatement({ companyId, partnerId, from, to }: { companyId: string; partnerId: string; from: string; to: string }) {
  const { t } = useTranslation();
  const statement = useQuery({
    queryKey: ["supplier-statement", companyId, partnerId, from, to],
    enabled: Boolean(companyId) && Boolean(partnerId),
    queryFn: async () => unwrap(await api.GET("/api/v1/payables/statement", { params: { query: { companyId, partnerId, ...(from ? { from } : {}), ...(to ? { to } : {}) } } })),
  });

  if (!partnerId) {
    return <p className="mt-3 text-sm text-fg-muted" data-testid="statement-choose">{t("payables.statement.chooseSupplier")}</p>;
  }
  const data = statement.data;
  if (!data) {
    return null;
  }

  return (
    <div className="mt-3 flex flex-col gap-6" data-testid="statement">
      <p className="text-sm">
        <span className="font-medium" dir="auto">{data.partnerCode} · {localized(data.partnerName)}</span>
        <span className="text-fg-muted" dir="ltr"> · {formatDate(data.from)} – {formatDate(data.to)}</span>
      </p>
      {data.currencies.length === 0 ? <p className="text-sm text-fg-muted">{t("payables.statement.empty")}</p> : null}
      {data.currencies.map((c) => (
        <section key={c.currency} className="flex flex-col gap-2" data-testid="statement-currency">
          <div className="grid gap-3 sm:grid-cols-4">
            {([["opening", c.opening], ["increases", c.increases], ["decreases", c.decreases], ["closing", c.closing]] as const).map(([key, value]) => (
              <div key={key} className="rounded-lg border border-border bg-surface p-3">
                <div className="text-xs uppercase tracking-wide text-fg-muted">{t(`payables.statement.${key}`)}</div>
                <div className="tabular text-lg font-semibold" dir="ltr" data-testid={`statement-${key}`}>{formatMoney(value, c.currency)}</div>
              </div>
            ))}
          </div>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("payables.statement.date")}</TableHead>
                <TableHead>{t("purchasing.number")}</TableHead>
                <TableHead>{t("purchasing.kind")}</TableHead>
                <TableHead>{t("purchasing.supplierReference")}</TableHead>
                <TableHead>{t("purchasing.dueDate")}</TableHead>
                <TableHead className="text-end">{t("payables.statement.amount")}</TableHead>
                <TableHead className="text-end">{t("payables.statement.balance")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              <TableRow>
                <TableCell colSpan={6} className="text-fg-muted">{t("payables.statement.broughtForward")}</TableCell>
                <TableNumberCell>{formatMoney(c.opening, c.currency)}</TableNumberCell>
              </TableRow>
              {c.lines.map((line, index) => {
                const route = recordRoute(line.documentType, line.documentId);
                return (
                  <TableRow key={`${line.documentId}:${String(index)}`} data-testid="statement-line">
                    <TableCell dir="ltr">{formatDate(line.date)}</TableCell>
                    <TableCell dir="ltr">
                      {route ? <Link to={route.to} search={route.search} className="text-accent underline-offset-2 hover:underline">{line.documentNumber}</Link> : line.documentNumber}
                    </TableCell>
                    <TableCell>
                      <span className="flex items-center gap-2">
                        {kindLabel(t, line.kind)}
                        {line.reversal ? <Badge tone="danger">{t("payables.statement.reversal")}</Badge> : null}
                      </span>
                    </TableCell>
                    <TableCell dir="ltr">{line.supplierReference ?? ""}</TableCell>
                    <TableCell dir="ltr">{formatDate(line.dueDate)}</TableCell>
                    <TableNumberCell>{formatMoney(line.amount, c.currency)}</TableNumberCell>
                    <TableNumberCell>{formatMoney(line.balance, c.currency)}</TableNumberCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        </section>
      ))}
    </div>
  );
}
