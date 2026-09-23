import { Button, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { add, compare, isDecimal, subtract } from "../../lib/decimal";
import { localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { FormError, SelectField, TextField } from "../common";
import { Qty, useReasonCodes } from "./shared";

type Transfer = components["schemas"]["TransferSummary"];
type Line = components["schemas"]["TransferLineSummary"];

interface LineEntry {
  quantity: string;
  shortage: string;
  reasonCodeId: string;
  note: string;
  toBinId: string;
  /** Serial-tracked lines: which of the shipped serials arrived (the rest are short). */
  missing: string[];
}

/** What is still in transit on a line: shipped less received less already written off, exactly. */
export function outstandingOf(line: Line): string {
  return subtract(line.qtyShipped, line.qtyReceived, line.qtyShortage);
}

function serialsOf(line: Line): string[] {
  return line.serialNumbers ?? [];
}

/**
 * Shipping less than requested, or receiving with differences: per line, the quantity now and, on a receipt, what is
 * short (written off from transit with a reason code, and a note when the code asks for one) and the bin it goes to.
 * Serial-tracked lines are settled by serial: each shipped serial arrived or is missing. Quantities stay decimal
 * strings; the server checks every rule again and names the line it refuses.
 */
export function TransferQuantities({ transfer, mode, date, toWarehouseBins, onDone, onCancel }: { transfer: Transfer; mode: "ship" | "receive"; date: string; toWarehouseBins: boolean; onDone: () => Promise<void>; onCancel: () => void }) {
  const { t } = useTranslation();
  const reasons = useReasonCodes("shortage");
  const bins = useQuery({
    queryKey: ["bins", transfer.toWarehouseId],
    enabled: mode === "receive" && toWarehouseBins,
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses/{warehouseId}/bins", { params: { path: { warehouseId: transfer.toWarehouseId } } })),
  });
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [entries, setEntries] = useState<Record<string, LineEntry>>(() =>
    Object.fromEntries(
      transfer.lines.map((line) => [
        line.id,
        { quantity: mode === "ship" ? String(line.qtyRequested) : outstandingOf(line), shortage: "0", reasonCodeId: "", note: "", toBinId: line.toBinId ?? "", missing: [] },
      ]),
    ),
  );
  const activeReasons = (reasons.data ?? []).filter((r) => r.isActive);
  const set = (lineId: string, patch: Partial<LineEntry>): void => {
    setEntries((current) => {
      const entry = current[lineId];
      return entry ? { ...current, [lineId]: { ...entry, ...patch } } : current;
    });
  };
  // A serial line settled by serial: arrived = shipped serials not marked missing, short = the missing ones.
  const serialLine = (line: Line): boolean => mode === "receive" && serialsOf(line).length > 0 && compare(add(line.qtyReceived, line.qtyShortage), "0") === 0;

  const submit = useMutation({
    mutationFn: async () => {
      const lines = transfer.lines.map((line) => {
        const entry = entries[line.id];
        if (!entry) {
          return { lineId: line.id, quantity: "0", shortage: "0" };
        }
        if (mode === "ship") {
          return { lineId: line.id, quantity: entry.quantity || "0", shortage: "0" };
        }
        if (serialLine(line)) {
          const arrived = serialsOf(line).filter((s) => !entry.missing.includes(s));
          return {
            lineId: line.id,
            quantity: String(arrived.length),
            shortage: String(entry.missing.length),
            serialNumbers: arrived,
            shortageSerialNumbers: entry.missing,
            shortageReasonCodeId: entry.missing.length > 0 ? entry.reasonCodeId || null : null,
            shortageNote: entry.note || null,
            toBinId: entry.toBinId || null,
          };
        }
        const short = compare(entry.shortage || "0", "0") > 0;
        return {
          lineId: line.id,
          quantity: entry.quantity || "0",
          shortage: entry.shortage || "0",
          shortageReasonCodeId: short ? entry.reasonCodeId || null : null,
          shortageNote: short ? entry.note || null : null,
          toBinId: entry.toBinId || null,
        };
      });
      const path = { params: { path: { transferId: transfer.id } } };
      if (mode === "ship") {
        unwrap(await api.POST("/api/v1/inventory/transfers/{transferId}/ship", { ...path, body: { shipDate: date || null, lines } }));
      } else {
        unwrap(await api.POST("/api/v1/inventory/transfers/{transferId}/receive", { ...path, body: { receiveDate: date || null, lines } }));
      }
    },
    onSuccess: async () => { setProblem(null); await onDone(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  // Client-side hints only; the server is the authority. Exact decimal comparisons, never floating point.
  const reasonProblem = (entry: LineEntry): string | null => {
    if (!entry.reasonCodeId) {
      return t("inventory.transfers.reasonNeeded");
    }
    return activeReasons.find((r) => r.id === entry.reasonCodeId)?.requiresNote === true && !entry.note.trim() ? t("inventory.transfers.noteNeeded") : null;
  };
  const lineError = (line: Line): string | null => {
    const entry = entries[line.id];
    if (!entry) {
      return null;
    }
    if (serialLine(line)) {
      return entry.missing.length > 0 ? reasonProblem(entry) : null;
    }
    if (!isDecimal(entry.quantity || "0") || (mode === "receive" && !isDecimal(entry.shortage || "0"))) {
      return t("inventory.transfers.notANumber");
    }
    const limit = mode === "ship" ? String(line.qtyRequested) : outstandingOf(line);
    const total = mode === "ship" ? entry.quantity || "0" : add(entry.quantity || "0", entry.shortage || "0");
    if (compare(total, limit) > 0 || compare(entry.quantity || "0", "0") < 0 || compare(entry.shortage || "0", "0") < 0) {
      return mode === "ship" ? t("inventory.transfers.overShip") : t("inventory.transfers.overReceive");
    }
    return mode === "receive" && compare(entry.shortage || "0", "0") > 0 ? reasonProblem(entry) : null;
  };
  const blocked = transfer.lines.some((line) => lineError(line) !== null);

  return (
    <div className="flex flex-col gap-3 rounded-md border border-border p-3" data-testid={`transfer-${mode}-quantities`}>
      <p className="text-sm text-fg-muted">{mode === "ship" ? t("inventory.transfers.shipHint") : t("inventory.transfers.receiveHint")}</p>
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{t("inventory.item")}</TableHead>
            <TableHead className="text-end">{mode === "ship" ? t("inventory.transfers.requested") : t("inventory.transfers.inTransit")}</TableHead>
            <TableHead>{mode === "ship" ? t("inventory.transfers.shipNow") : t("inventory.transfers.receiveNow")}</TableHead>
            {mode === "receive" ? <TableHead>{t("inventory.transfers.shortage")}</TableHead> : null}
            {mode === "receive" && toWarehouseBins ? <TableHead>{t("inventory.transfers.toBin")}</TableHead> : null}
          </TableRow>
        </TableHeader>
        <TableBody>
          {transfer.lines.map((line) => {
            const entry = entries[line.id];
            if (!entry) {
              return null;
            }
            const outstanding = mode === "ship" ? String(line.qtyRequested) : outstandingOf(line);
            const done = mode === "receive" && compare(outstanding, "0") === 0;
            const error = done ? null : lineError(line);
            const short = serialLine(line) ? entry.missing.length > 0 : compare(entry.shortage || "0", "0") > 0;
            return (
              <TableRow key={line.id} data-testid="quantity-line">
                <TableCell className="align-top">
                  <span dir="ltr">{line.itemCode}</span> {localized(line.itemName)}
                  {line.lotNumber ? <span className="text-fg-muted"> · {line.lotNumber}</span> : null}
                  {error ? <p className="text-xs text-danger" role="alert">{error}</p> : null}
                </TableCell>
                <TableNumberCell className="align-top"><Qty value={outstanding} uom={line.uomCode} /></TableNumberCell>
                {done ? (
                  <TableCell colSpan={toWarehouseBins ? 3 : 2} className="align-top text-sm text-fg-muted">{t("inventory.transfers.lineSettled")}</TableCell>
                ) : serialLine(line) ? (
                  <TableCell colSpan={2} className="align-top">
                    <fieldset className="flex flex-col gap-1">
                      <legend className="text-xs text-fg-muted">{t("inventory.transfers.serialsArrived")}</legend>
                      {serialsOf(line).map((serial) => (
                        <label key={serial} className="flex items-center gap-2 text-sm">
                          <input type="checkbox" checked={!entry.missing.includes(serial)} onChange={(e) => { set(line.id, { missing: e.target.checked ? entry.missing.filter((s) => s !== serial) : [...entry.missing, serial] }); }} data-testid={`serial-arrived-${serial}`} />
                          <span dir="ltr" className="font-mono text-xs">{serial}</span>
                        </label>
                      ))}
                      {short ? <ShortageReason entry={entry} reasons={activeReasons} onChange={(patch) => { set(line.id, patch); }} /> : null}
                    </fieldset>
                  </TableCell>
                ) : (
                  <>
                    <TableCell className="align-top">
                      <TextField inputMode="decimal" value={entry.quantity} onChange={(e) => { set(line.id, { quantity: e.target.value }); }} aria-label={`${mode === "ship" ? t("inventory.transfers.shipNow") : t("inventory.transfers.receiveNow")} ${line.itemCode}`} className="w-28" dir="ltr" data-testid={`${mode}-qty-${line.itemCode}`} />
                    </TableCell>
                    {mode === "receive" ? (
                      <TableCell className="align-top">
                        <div className="flex flex-col gap-2">
                          <TextField inputMode="decimal" value={entry.shortage} onChange={(e) => { set(line.id, { shortage: e.target.value }); }} aria-label={`${t("inventory.transfers.shortage")} ${line.itemCode}`} className="w-28" dir="ltr" data-testid={`short-qty-${line.itemCode}`} />
                          {short ? <ShortageReason entry={entry} reasons={activeReasons} onChange={(patch) => { set(line.id, patch); }} /> : null}
                        </div>
                      </TableCell>
                    ) : null}
                  </>
                )}
                {mode === "receive" && toWarehouseBins && !done ? (
                  <TableCell className="align-top">
                    <SelectField value={entry.toBinId} onChange={(e) => { set(line.id, { toBinId: e.target.value }); }} aria-label={`${t("inventory.transfers.toBin")} ${line.itemCode}`}>
                      <option value="">—</option>
                      {(bins.data ?? []).map((b) => (
                        <option key={b.id} value={b.id}>
                          {b.code}
                        </option>
                      ))}
                    </SelectField>
                  </TableCell>
                ) : null}
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
      {mode === "receive" && activeReasons.length === 0 ? <p className="text-sm text-fg-muted">{t("inventory.transfers.noShortageReasons")}</p> : null}
      <FormError message={problem?.message ?? null} />
      <div className="flex flex-wrap gap-2">
        <Button onClick={() => { submit.mutate(); }} loading={submit.isPending} disabled={blocked} data-testid={`confirm-${mode}`}>
          {mode === "ship" ? t("inventory.transfers.confirmShip") : t("inventory.transfers.confirmReceive")}
        </Button>
        <Button variant="secondary" onClick={onCancel}>
          {t("common.cancel")}
        </Button>
      </div>
    </div>
  );
}

function ShortageReason({ entry, reasons, onChange }: { entry: LineEntry; reasons: components["schemas"]["ReasonCodeSummary"][]; onChange: (patch: Partial<LineEntry>) => void }) {
  const { t } = useTranslation();
  const reason = reasons.find((r) => r.id === entry.reasonCodeId);
  return (
    <div className="flex flex-col gap-1">
      <SelectField value={entry.reasonCodeId} onChange={(e) => { onChange({ reasonCodeId: e.target.value }); }} aria-label={t("inventory.transfers.shortageReason")} data-testid="shortage-reason">
        <option value="">{t("inventory.transfers.shortageReason")}</option>
        {reasons.map((r) => (
          <option key={r.id} value={r.id}>
            {r.code} · {localized(r.name)}
          </option>
        ))}
      </SelectField>
      {reason ? (
        <TextField value={entry.note} onChange={(e) => { onChange({ note: e.target.value }); }} placeholder={reason.requiresNote ? t("inventory.transfers.noteRequired") : t("inventory.transfers.noteOptional")} aria-label={t("inventory.transfers.shortageNote")} required={reason.requiresNote} data-testid="shortage-note" />
      ) : null}
    </div>
  );
}
