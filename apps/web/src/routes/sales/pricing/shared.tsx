import { Badge } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../../api";
import type { components } from "../../../api/schema";
import { formatDate, formatNumber, localized } from "../../../lib/format";
import { money } from "../shared";

export type PriceList = components["schemas"]["PriceListSummary"];
export type PriceListItem = components["schemas"]["PriceListItemSummary"];
export type PriceAgreement = components["schemas"]["PriceAgreementSummary"];
export type DiscountRule = components["schemas"]["DiscountRuleSummary"];
export type Promotion = components["schemas"]["PromotionSummary"];
export type PriceFloor = components["schemas"]["PriceFloorSummary"];
export type PricingResult = components["schemas"]["PricingResult"];
export type PricedLine = components["schemas"]["PricedLine"];
export type PriceStep = components["schemas"]["PriceStep"];
export type PriceCandidate = components["schemas"]["PriceCandidate"];
export type RecordRef = components["schemas"]["RecordRef"];

export function usePriceLists(companyId: string) {
  return useQuery({ queryKey: ["price-lists", companyId], enabled: Boolean(companyId), queryFn: async () => unwrap(await api.GET("/api/v1/pricing/price-lists", { params: { query: { companyId } } })) });
}

export function useCategories() {
  return useQuery({ queryKey: ["item-categories"], queryFn: async () => unwrap(await api.GET("/api/v1/items/categories")) });
}

export function useBrands() {
  return useQuery({ queryKey: ["item-brands"], queryFn: async () => unwrap(await api.GET("/api/v1/items/brands")) });
}

/** The company's customers, for choosing whom a list, an agreement or a rule is for. */
export function useCompanyCustomers(companyId: string) {
  return useQuery({ queryKey: ["customers", companyId, "all"], enabled: Boolean(companyId), queryFn: async () => unwrap(await api.GET("/api/v1/partners/customers", { params: { query: { companyId } } })) });
}

export function refLabel(ref: RecordRef | null | undefined): string {
  return ref ? `${ref.code} · ${localized(ref.name)}` : "";
}

/** A price, keeping the decimals prices keep (two more than the currency's own), with its currency. */
export function price(value: number | string | null | undefined, currency: string): string {
  if (value === null || value === undefined || value === "") {
    return "";
  }
  return `${formatNumber(value, { minimumFractionDigits: 0, maximumFractionDigits: 10 })} ${currency}`;
}

export function Validity({ from, to }: { from?: string | null; to?: string | null }) {
  const { t } = useTranslation();
  if (!from && !to) {
    return <span className="text-fg-muted">{t("pricing.always")}</span>;
  }
  return (
    <span className="tabular">
      {from ? formatDate(from) : "…"} – {to ? formatDate(to) : "…"}
    </span>
  );
}

// ------------------------------------------------------------------ why this price

const enumeratedFacts = new Set(["rateMethod", "combination", "valueType", "roundingMode", "onBreach", "scope", "rateType"]);
const dateFacts = new Set(["rateDate", "validFrom", "validTo"]);
const textFacts = new Set(["currency", "from", "to", "uom", "fromUom", "toUom", "parentList", "derivedFrom"]);

function FactValue({ name, value }: { name: string; value: string }) {
  const { t } = useTranslation();
  if (name === "taxBasis") {
    return <>{t(value === "inclusive" ? "pricing.inclusive" : "pricing.exclusive")}</>;
  }
  if (enumeratedFacts.has(name)) {
    return <>{t(`pricing.value.${value}`, { defaultValue: value })}</>;
  }
  if (dateFacts.has(name)) {
    return <span className="tabular">{formatDate(value)}</span>;
  }
  if (textFacts.has(name) || value === "true") {
    return <span dir="ltr">{value === "true" ? t("common.yes") : value}</span>;
  }
  return (
    <span className="tabular" dir="ltr">
      {formatNumber(value, { maximumFractionDigits: 12 })}
    </span>
  );
}

/** A candidate's reason in words: "min_quantity:100" reads "needs at least 100". */
export function useDetail(): (detail: string | null | undefined) => string {
  const { t } = useTranslation();
  return (detail) => {
    if (!detail) {
      return "";
    }
    const [key = "", value = ""] = detail.split(/:(.*)/su);
    const [first = "", second = ""] = value.split("/");
    return t(`pricing.detail.${key}`, { value, first: first.replace(">", " → "), second, defaultValue: detail });
  };
}

function outcomeTone(outcome: string): "success" | "accent" | "danger" | "neutral" {
  switch (outcome) {
    case "won":
    case "applied":
    case "passed":
      return "success";
    case "breached":
      return "danger";
    case "lost":
    case "no_saving":
    case "line_taken":
      return "accent";
    default:
      return "neutral";
  }
}

function Candidates({ candidates }: { candidates: PriceCandidate[] }) {
  const { t } = useTranslation();
  const detail = useDetail();
  if (candidates.length === 0) {
    return null;
  }
  return (
    <ul className="mt-2 flex flex-col gap-1 border-s-2 border-border ps-3 text-xs" aria-label={t("pricing.considered")}>
      {candidates.map((c, index) => (
        <li key={`${c.source}-${c.refCode ?? ""}-${String(index)}`} className="flex flex-wrap items-center gap-2" data-testid="price-candidate">
          <Badge tone={outcomeTone(c.outcome)}>{t(`pricing.outcome.${c.outcome}`, { defaultValue: c.outcome })}</Badge>
          <span>{t(`pricing.source.${c.source}`, { defaultValue: c.source })}</span>
          {c.refCode ? (
            <span className="font-medium" dir="ltr">
              {c.refCode}
            </span>
          ) : null}
          {c.amount !== null && c.amount !== undefined ? <span className="tabular text-fg-muted" dir="ltr">{formatNumber(c.amount, { maximumFractionDigits: 10 })}</span> : null}
          {c.detail ? <span className="text-fg-muted">{detail(c.detail)}</span> : null}
        </li>
      ))}
    </ul>
  );
}

function stepTitle(step: PriceStep, t: TFunction): string {
  const kind = t(`pricing.step.${step.kind}`, { defaultValue: step.kind });
  if (step.kind === "base_price" && step.source) {
    return `${kind}: ${t(`pricing.source.${step.source}`, { defaultValue: step.source })}`;
  }
  if (step.source && step.kind !== "unit" && step.kind !== "currency" && step.kind !== "derivation" && step.kind !== "floor") {
    return `${kind}: ${t(`pricing.source.${step.source}`, { defaultValue: step.source })}`;
  }
  return kind;
}

/** One step of the explanation: what it started from and ended at, what it took off, the facts behind it, and what else was considered. */
export function PriceStepView({ step, currency, index }: { step: PriceStep; currency: string; index: number }) {
  const { t } = useTranslation();
  const isAmount = step.kind === "line_discount" || step.kind === "promotion" || step.kind === "document_discount";
  const show = (value: number | string | null | undefined): string => (isAmount ? money(value, currency) : formatNumber(value ?? 0, { maximumFractionDigits: 10 }));
  return (
    <li className="rounded-md border border-border bg-surface p-3" data-testid={`price-step-${step.kind}`}>
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <span className="text-sm font-medium">
          <span className="tabular text-fg-muted">{index + 1}. </span>
          {stepTitle(step, t)}
          {step.refCode ? (
            <>
              {" "}
              <bdi className="font-semibold">{step.refCode}</bdi>
            </>
          ) : null}
        </span>
        {step.after !== null ? (
          <span className="tabular text-sm" dir="ltr">
            {step.before !== null ? `${show(step.before)} → ` : ""}
            <strong>{show(step.after)}</strong>
            {step.amount ? <span className="ms-2 text-success">−{money(step.amount, currency)}</span> : null}
          </span>
        ) : null}
      </div>
      {step.facts.length > 0 ? (
        <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 text-xs">
          {step.facts.map((fact) => (
            <div key={fact.key} className="contents">
              <dt className="text-fg-muted">{t(`pricing.fact.${fact.key}`, { defaultValue: fact.key })}</dt>
              <dd>
                <FactValue name={fact.key} value={fact.value} />
              </dd>
            </div>
          ))}
        </dl>
      ) : null}
      <Candidates candidates={step.candidates} />
    </li>
  );
}

/** "Why this price": every step of a line's price, in order, with the rules each step passed over (ADR-0030). */
export function PriceExplanation({ line, currency }: { line: PricedLine; currency: string }) {
  const { t } = useTranslation();
  return (
    <section aria-label={t("pricing.whyThisPrice")} className="flex flex-col gap-2" data-testid="price-explanation">
      <h3 className="text-sm font-semibold">{t("pricing.whyThisPrice")}</h3>
      {line.problem ? (
        <p role="alert" className="rounded-md border border-danger/40 bg-danger-soft px-3 py-2 text-sm text-danger" data-testid="price-problem">
          {t(`pricing.problem.${line.problem.code}`, { defaultValue: line.problem.message })}
        </p>
      ) : null}
      <ol className="flex flex-col gap-2">
        {line.steps.map((step, index) => (
          <PriceStepView key={index} step={step} currency={currency} index={index} />
        ))}
      </ol>
      {line.floor ? (
        <p className={`text-xs ${line.floor.breached ? "text-danger" : "text-fg-muted"}`} data-testid="price-floor">
          {line.floor.breached ? t(`pricing.floorBreached.${line.floor.onBreach}`) : t("pricing.floorPassed")}
        </p>
      ) : null}
    </section>
  );
}
