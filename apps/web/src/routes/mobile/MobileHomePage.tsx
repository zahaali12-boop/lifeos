import { Button, Field } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { ArrowRightLeft, ClipboardCheck, RefreshCw } from "lucide-react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { SelectField } from "../common";
import { setScanContext, useScanContext } from "./context";
import { useQueue } from "./queue";
import { localized } from "./text";

/** Where the operator works and what to do next. */
export function MobileHomePage() {
  const { t } = useTranslation();
  const context = useScanContext();
  const queue = useQueue();
  const companyId = context.companyId;
  const companies = useQuery({ queryKey: ["mobile", "companies"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/companies")) });
  const warehouses = useQuery({
    queryKey: ["mobile", "warehouses", companyId],
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses", { params: { query: { companyId: companyId ?? "" } } })),
    enabled: companyId !== null,
  });
  const ready = context.companyId !== null && context.warehouseId !== null;

  return (
    <div className="flex flex-col gap-4">
      <div>
        <h1 className="text-xl font-semibold tracking-tight">{t("mobile.home.title")}</h1>
        <p className="mt-1 text-sm text-fg-muted">{t("mobile.home.description")}</p>
      </div>
      <Field label={t("mobile.home.company")}>
        <SelectField className="h-12 text-base" data-testid="scan-company" value={context.companyId ?? ""} onChange={(event) => { setScanContext({ companyId: event.target.value || null, warehouseId: null }); }}>
          <option value="">{t("mobile.home.chooseCompany")}</option>
          {(companies.data ?? []).map((company) => (
            <option key={company.id} value={company.id}>
              {company.code} · {localized(company.legalName)}
            </option>
          ))}
        </SelectField>
      </Field>
      <Field label={t("mobile.home.warehouse")}>
        <SelectField className="h-12 text-base" data-testid="scan-warehouse" value={context.warehouseId ?? ""} disabled={companyId === null} onChange={(event) => { setScanContext({ warehouseId: event.target.value || null }); }}>
          <option value="">{t("mobile.home.chooseWarehouse")}</option>
          {(warehouses.data ?? []).map((warehouse) => (
            <option key={warehouse.id} value={warehouse.id}>
              {warehouse.code} · {localized(warehouse.name)}
            </option>
          ))}
        </SelectField>
      </Field>
      <div className="grid gap-2">
        <Button asChild size="lg" className="h-14 justify-start gap-3 text-base" disabled={!ready}>
          <Link to="/m/count" aria-disabled={!ready}>
            <ClipboardCheck aria-hidden="true" />
            {t("mobile.home.startCount")}
          </Link>
        </Button>
        <Button asChild variant="secondary" size="lg" className="h-14 justify-start gap-3 text-base">
          <Link to="/m/transfer" aria-disabled={!ready}>
            <ArrowRightLeft aria-hidden="true" />
            {t("mobile.home.startTransfer")}
          </Link>
        </Button>
        <Button asChild variant="secondary" size="lg" className="h-14 justify-start gap-3 text-base">
          <Link to="/m/queue">
            <RefreshCw aria-hidden="true" />
            {t("mobile.home.openQueue")}
          </Link>
        </Button>
      </div>
      <p className="text-sm text-fg-muted" data-testid="pending-text">
        {t("mobile.pending", { count: queue.length })}
      </p>
      <p className="text-xs text-fg-subtle">{t("mobile.home.installHint")}</p>
    </div>
  );
}
