import { Button } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { Rocket } from "lucide-react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { useCan } from "../../lib/permissions";
import { usePref } from "../../lib/prefs";
import { doneSteps, setupSteps } from "./SetupPage";

/**
 * The home page's nudge while the workspace is being set up (A-173): how many of the wizard's steps are done and a
 * way in; gone once everything is done, or when the member hides it.
 */
export function SetupCard({ companyId }: { companyId: string }) {
  const { t } = useTranslation();
  const can = useCan();
  const [hidden, hide] = usePref("setup.cardHidden");
  const [taxSkipped] = usePref("setup.taxSkipped");
  const allowed = can("organization.company.read");
  const status = useQuery({
    queryKey: ["setup-status", companyId],
    enabled: allowed && hidden !== "1",
    queryFn: async () => unwrap(await api.GET("/api/v1/onboarding/status", { params: { query: companyId ? { companyId } : {} } })),
  });
  const bankAccounts = useQuery({
    queryKey: ["bank-accounts", companyId],
    enabled: allowed && hidden !== "1" && Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/bank-accounts", { params: { query: { companyId } } })),
  });
  if (!allowed || hidden === "1" || !status.data) {
    return null;
  }
  const done = doneSteps(status.data, bankAccounts.data?.length ?? 0, taxSkipped === "1");
  if (done.has("ready")) {
    return null;
  }
  const next = setupSteps.find((step) => !done.has(step)) ?? "ready";
  return (
    <section className="mb-6 flex flex-wrap items-center gap-4 rounded-lg border border-accent/40 bg-accent-soft/40 p-4" aria-labelledby="setup-card-title" data-testid="setup-card">
      <Rocket className="size-8 shrink-0 text-accent" aria-hidden="true" />
      <div className="min-w-0 flex-1">
        <h2 id="setup-card-title" className="text-base font-semibold">{t("dashboard.setup.title")}</h2>
        <p className="text-sm text-fg-muted">
          {t("dashboard.setup.progress", { done: setupSteps.filter((step) => done.has(step)).length, total: setupSteps.length })}
          {" · "}
          {t("dashboard.setup.next", { step: t(`setup.steps.${next}.title`) })}
        </p>
      </div>
      <Button asChild><Link to="/setup" data-testid="setup-card-continue">{t("dashboard.setup.continue")}</Link></Button>
      <Button variant="ghost" size="sm" onClick={() => { hide("1"); }} data-testid="setup-card-hide">{t("dashboard.setup.hide")}</Button>
    </section>
  );
}
