import { Button, cn } from "@quicker/ui";
import { Link, Outlet, useRouterState } from "@tanstack/react-router";
import { Banknote, Coins, Home, Languages, Monitor, ShoppingBag, Users } from "lucide-react";
import { useTranslation } from "react-i18next";
import { currentLanguage, setLanguage } from "../../i18n";
import { useSession } from "../../session/session";
import { localized } from "../../lib/format";
import { SelectField } from "../common";
import { useCompanyContext } from "../inventory/shared";
import { chooseFullProgram } from "./shared";

const tabs = [
  { to: "/p", label: "phone.tabs.home", icon: Home, testId: "phone-tab-home" },
  { to: "/p/sell", label: "phone.tabs.sell", icon: ShoppingBag, testId: "phone-tab-sell" },
  { to: "/p/collect", label: "phone.tabs.collect", icon: Coins, testId: "phone-tab-collect" },
  { to: "/p/buy", label: "phone.tabs.buy", icon: Banknote, testId: "phone-tab-buy" },
  { to: "/p/customers", label: "phone.tabs.customers", icon: Users, testId: "phone-tab-customers" },
] as const;

/**
 * The phone site's frame (ADR-0035 §21): a thin header with the workspace, the company when there is more than one,
 * the language and the way to the full program; the screen; a thumb-reach tab bar with the five things the trader
 * does away from the desk.
 */
export function PhoneShell() {
  const { t } = useTranslation();
  const session = useSession();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const location = useRouterState({ select: (s) => s.location.pathname });
  const language = currentLanguage();

  return (
    <div className="flex min-h-dvh flex-col bg-canvas text-fg">
      <a href="#main" className="skip-link">
        {t("shell.skipToContent")}
      </a>
      <header className="sticky top-0 z-10 flex h-14 items-center gap-2 border-b border-border bg-surface/95 px-3 backdrop-blur">
        <span className="truncate text-base font-bold tracking-tight text-accent">{session?.tenant.name}</span>
        {companies.length > 1 ? (
          <SelectField aria-label={t("phone.company")} className="h-9 max-w-40 text-sm" value={companyId} onChange={(event) => { setCompanyId(event.target.value); }} data-testid="phone-company">
            {companies.map((company) => (
              <option key={company.id} value={company.id}>
                {company.code} · {localized(company.legalName)}
              </option>
            ))}
          </SelectField>
        ) : null}
        <div className="ms-auto flex items-center gap-1">
          <Button variant="ghost" size="icon" aria-label={t("mobile.switchLanguage")} data-testid="phone-language" onClick={() => { void setLanguage(language === "ar" ? "en" : "ar"); }}>
            <Languages aria-hidden="true" />
          </Button>
          <Button variant="ghost" size="icon" asChild>
            <Link to="/" aria-label={t("phone.fullProgram")} title={t("phone.fullProgram")} data-testid="phone-full-program" onClick={chooseFullProgram}>
              <Monitor aria-hidden="true" />
            </Link>
          </Button>
        </div>
      </header>
      <main id="main" tabIndex={-1} className="flex-1 p-4 pb-24">
        <Outlet />
      </main>
      <nav aria-label={t("phone.tabs.label")} className="fixed inset-x-0 bottom-0 z-10 border-t border-border bg-surface pb-[env(safe-area-inset-bottom)]">
        <ul className="grid grid-cols-5">
          {tabs.map((tab) => {
            const active = tab.to === "/p" ? location === "/p" || location === "/p/" : location.startsWith(tab.to);
            return (
              <li key={tab.to}>
                <Link to={tab.to} aria-current={active ? "page" : undefined} data-testid={tab.testId} className={cn("flex h-16 flex-col items-center justify-center gap-1 text-xs", active ? "font-medium text-accent" : "text-fg-muted")}>
                  <tab.icon className="size-5" aria-hidden="true" />
                  {t(tab.label)}
                </Link>
              </li>
            );
          })}
        </ul>
      </nav>
    </div>
  );
}
