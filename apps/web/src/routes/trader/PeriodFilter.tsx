import { useState } from "react";
import { useTranslation } from "react-i18next";
import { todayIn } from "../../lib/dates";
import { Field, SelectField, TextField } from "../common";
import { periodPresets, periodRange, type DateRange, type Period, type PeriodPreset } from "./period";

/** The period a screen shows, with the days it covers on the day it is (in the signed-in person's time zone). */
export function usePeriod(initial: PeriodPreset = "month"): [Period, (period: Period) => void, DateRange] {
  const [period, setPeriod] = useState<Period>({ preset: initial, from: "", to: "" });
  return [period, setPeriod, periodRange(period, todayIn())];
}

/** The period chooser of the trader's lists (ADR-0035 §18): a preset, or a range typed by hand. */
export function PeriodFilter({ value, onChange }: { value: Period; onChange: (period: Period) => void }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-wrap items-end gap-3" data-testid="period-filter">
      <Field label={t("trader.period.label")}>
        <SelectField value={value.preset} onChange={(e) => { onChange({ ...value, preset: e.target.value as PeriodPreset }); }} data-testid="period-preset">
          {periodPresets.map((preset) => (
            <option key={preset} value={preset}>
              {t(`trader.period.presets.${preset}`)}
            </option>
          ))}
        </SelectField>
      </Field>
      {value.preset === "custom" ? (
        <>
          <Field label={t("trader.period.from")}>
            <TextField type="date" value={value.from} onChange={(e) => { onChange({ ...value, from: e.target.value }); }} dir="ltr" data-testid="period-from" />
          </Field>
          <Field label={t("trader.period.to")}>
            <TextField type="date" value={value.to} onChange={(e) => { onChange({ ...value, to: e.target.value }); }} dir="ltr" data-testid="period-to" />
          </Field>
        </>
      ) : null}
    </div>
  );
}
