import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { Field, PageHeader, SelectField } from "../common";
import { ActivityList } from "./Activities";
import { useRefreshSales } from "./Opportunities";

/** The CRM to-do list (roadmap 5.1): what is late, what is coming, and what was done, mine or everyone's. */
export function ActivitiesPage() {
  const { t } = useTranslation();
  const refreshSales = useRefreshSales();
  const [scope, setScope] = useState("mine");
  const [due, setDue] = useState("");
  const [status, setStatus] = useState("open");
  const activities = useQuery({
    queryKey: ["crm-activities", scope, due, status],
    queryFn: async () => unwrap(await api.GET("/api/v1/partners/crm-activities", { params: { query: { ...(scope === "mine" ? { mine: true } : {}), ...(due ? { due } : {}), ...(status ? { status } : {}) } } })),
  });
  const list = activities.data ?? [];
  const overdue = list.filter((a) => a.isOverdue);
  const rest = list.filter((a) => !a.isOverdue);
  return (
    <>
      <PageHeader title={t("nav.crmActivities")} description={t("sales.activitiesDescription")} />
      <div className="mb-4 grid gap-3 sm:grid-cols-3">
        <Field label={t("sales.whose")}>
          <SelectField value={scope} onChange={(e) => { setScope(e.target.value); }} data-testid="activity-scope">
            <option value="mine">{t("sales.assignedToMe")}</option>
            <option value="all">{t("sales.everyone")}</option>
          </SelectField>
        </Field>
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }} data-testid="activity-status">
            {["open", "done", "cancelled", ""].map((s) => (
              <option key={s} value={s}>
                {t(`sales.activityFilter.${s || "all"}`)}
              </option>
            ))}
          </SelectField>
        </Field>
        <Field label={t("sales.when")}>
          <SelectField value={due} onChange={(e) => { setDue(e.target.value); }} disabled={status !== "open"} data-testid="activity-due-filter">
            {["", "overdue", "upcoming"].map((d) => (
              <option key={d} value={d}>
                {t(`sales.dueFilter.${d || "all"}`)}
              </option>
            ))}
          </SelectField>
        </Field>
      </div>
      {activities.isPending ? (
        <p className="text-sm text-fg-muted">{t("common.loading")}</p>
      ) : (
        <div className="flex flex-col gap-6">
          {overdue.length > 0 ? (
            <section className="flex flex-col gap-2">
              <h2 className="text-sm font-semibold text-danger">{t("sales.overdueCount", { count: overdue.length })}</h2>
              <ActivityList activities={overdue} onChanged={refreshSales} showPartner testId="overdue-activities" />
            </section>
          ) : null}
          <section className="flex flex-col gap-2">
            {overdue.length > 0 ? <h2 className="text-sm font-semibold">{t("sales.theRest")}</h2> : null}
            <ActivityList activities={rest} onChanged={refreshSales} showPartner testId="activities" />
          </section>
        </div>
      )}
    </>
  );
}
