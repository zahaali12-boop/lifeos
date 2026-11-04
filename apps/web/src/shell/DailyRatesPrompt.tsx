import { Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, Field } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Coins } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";
import { formatDate, formatNumber } from "../lib/format";
import { covers } from "../lib/permissions";
import { toFormProblem } from "../lib/problem";
import { DecimalField, FormError, SelectField } from "../routes/common";
import { useCompanyContext } from "../routes/inventory/shared";

type DailyRates = components["schemas"]["TraderDailyRates"];

const RATE_MANAGE = "organization.rate.manage";
const HOUR_SETTING = "trader.rates.prompt_hour";
const HOURS = [-1, ...Array.from({ length: 24 }, (_, hour) => hour)];

function laterKey(companyId: string, date: string): string {
  return `quicker.rates.later:${companyId}:${date}`;
}

function putOff(companyId: string, date: string): boolean {
  try {
    return sessionStorage.getItem(laterKey(companyId, date)) === "1";
  } catch {
    return false;
  }
}

/**
 * The day's exchange rate, asked for at the first open of the day from the hour the trader chose (trader edition,
 * ADR-0035 §14): one box per currency the shop buys in that has no rate for today, the last known rate as a hint,
 * saved into the organization's rates in one go. "Later" keeps quiet until the app is opened again; the hour, and
 * "never", are set from the dialog itself.
 */
export function DailyRatesPrompt({ permissions }: { permissions: Set<string> }) {
  const { companyId } = useCompanyContext();
  const enabled = Boolean(companyId) && covers(permissions, RATE_MANAGE);
  const status = useQuery({
    queryKey: ["daily-rates", companyId],
    enabled,
    refetchInterval: 5 * 60_000,
    queryFn: async () => unwrap(await api.GET("/api/v1/trader/rates/today", { params: { query: { companyId } } })),
  });
  if (!enabled || !status.data?.due) {
    return null;
  }
  // Keyed by the day so a new day starts the dialog afresh.
  return <DailyRatesDialog key={`${companyId}:${status.data.date}`} companyId={companyId} status={status.data} />;
}

function DailyRatesDialog({ companyId, status }: { companyId: string; status: DailyRates }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const missing = status.rates.filter((r) => !r.enteredToday);
  const [open, setOpen] = useState(() => !putOff(companyId, status.date));
  const [values, setValues] = useState<Record<string, string>>({});
  const [hour, setHour] = useState(String(status.promptHour));
  const [error, setError] = useState<string | null>(null);
  const entered = missing.filter((r) => Number(values[r.currency] ?? "") > 0);

  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["daily-rates"] });
    await queryClient.invalidateQueries({ queryKey: ["trader-pricing"] });
  };
  const later = (): void => {
    try {
      sessionStorage.setItem(laterKey(companyId, status.date), "1");
    } catch {
      // The prompt simply returns at the next open.
    }
    setOpen(false);
  };
  const save = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/trader/rates/today", { body: { companyId, rates: entered.map((r) => ({ currency: r.currency, rate: Number(values[r.currency]) })) } })),
    onSuccess: async () => {
      setOpen(false);
      await refresh();
    },
    onError: (failure) => {
      setError(toFormProblem(failure, t("trader.rates.failed")).message);
    },
  });
  const changeHour = useMutation({
    mutationFn: async (value: number) => unwrap(await api.PUT("/api/v1/organization/settings/{key}", { params: { path: { key: HOUR_SETTING } }, body: { value, valueType: "number" } })),
    onSuccess: async (_, value) => {
      if (value < 0) {
        setOpen(false);
      }
      await refresh();
    },
    onError: (failure) => {
      setError(toFormProblem(failure, t("trader.rates.failed")).message);
    },
  });
  const submit = (event: FormEvent): void => {
    event.preventDefault();
    setError(null);
    if (entered.length > 0) {
      save.mutate();
    }
  };

  return (
    <Dialog open={open} onOpenChange={(isOpen) => { if (!isOpen) { later(); } }}>
      <DialogContent closeLabel={t("common.close")} data-testid="daily-rates">
        <form onSubmit={submit} className="flex flex-col gap-4">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2 text-lg font-semibold">
              <Coins className="size-5 text-accent" aria-hidden="true" />
              {t("trader.rates.title")}
            </DialogTitle>
            <DialogDescription>{t("trader.rates.description", { date: formatDate(status.date) })}</DialogDescription>
          </DialogHeader>
          {missing.map((r, index) => (
            <Field
              key={r.currency}
              label={t("trader.rates.rateLabel", { currency: r.currency, functional: status.currency })}
              description={r.rate === null ? t("trader.rates.noneYet") : t("trader.rates.lastKnown", { rate: formatNumber(r.rate, { maximumFractionDigits: 6 }), date: formatDate(r.asOf) })}
            >
              <DecimalField
                value={values[r.currency] ?? ""}
                onChange={(e) => { setValues({ ...values, [r.currency]: e.target.value }); }}
                placeholder={r.rate === null ? "" : String(r.rate)}
                autoFocus={index === 0}
                data-testid={`rate-${r.currency}`}
              />
            </Field>
          ))}
          <FormError message={error} />
          <div className="flex flex-wrap items-center gap-2 text-sm text-fg-muted">
            <label htmlFor="daily-rates-hour">{t("trader.rates.askFrom")}</label>
            <SelectField id="daily-rates-hour" value={hour} onChange={(e) => { setHour(e.target.value); changeHour.mutate(Number(e.target.value)); }} className="w-auto" data-testid="daily-rates-hour">
              {HOURS.map((h) => (
                <option key={h} value={h}>
                  {h < 0 ? t("trader.rates.never") : `${String(h).padStart(2, "0")}:00`}
                </option>
              ))}
            </SelectField>
          </div>
          <DialogFooter>
            <Button type="button" variant="ghost" onClick={later} data-testid="daily-rates-later">
              {t("trader.rates.later")}
            </Button>
            <Button type="submit" disabled={save.isPending || entered.length === 0} data-testid="save-daily-rates">
              {t("trader.rates.save")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
