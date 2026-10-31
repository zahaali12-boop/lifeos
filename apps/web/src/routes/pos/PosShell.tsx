import { Badge, Button, cn } from "@quicker/ui";
import { Link, Outlet, useRouterState } from "@tanstack/react-router";
import { Languages, ListOrdered, Monitor, ShoppingBag, Store, Wifi, WifiOff } from "lucide-react";
import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { currentLanguage, setLanguage } from "../../i18n";
import { localized } from "../../lib/format";
import { useOnline } from "../mobile/context";
import { pendingOrders, useTill } from "./store";
import { syncOrders } from "./sync";

const tabs = [
  { to: "/pos", label: "pos.till.tabs.register", icon: Store, testId: "till-tab-register" },
  { to: "/pos/sell", label: "pos.till.tabs.sell", icon: ShoppingBag, testId: "till-tab-sell" },
  { to: "/pos/orders", label: "pos.till.tabs.orders", icon: ListOrdered, testId: "till-tab-orders" },
] as const;

/** The till's frame (A-180): the register and session it works on, the network state, what waits to sync, and the three screens; the same session as the desktop. */
export function PosShell() {
  const { t } = useTranslation();
  const till = useTill();
  const online = useOnline();
  const location = useRouterState({ select: (s) => s.location.pathname });
  const language = currentLanguage();
  const pending = till.orders.filter((o) => o.status !== "synced").length;

  // Back online: send what was sold meanwhile.
  useEffect(() => {
    if (online && pendingOrders().length > 0) {
      void syncOrders();
    }
  }, [online]);

  return (
    <div className="flex min-h-dvh flex-col bg-canvas text-fg print:hidden">
      <a href="#main" className="skip-link">
        {t("shell.skipToContent")}
      </a>
      <header className="sticky top-0 z-10 flex h-14 items-center gap-3 border-b border-border bg-surface/95 px-4 backdrop-blur">
        <span className="text-lg font-bold tracking-tight text-accent">Quicker</span>
        <span className="text-sm font-medium">{t("pos.till.title")}</span>
        {till.register ? (
          <span className="truncate text-sm text-fg-muted" data-testid="till-register-name">
            {localized(till.register.name)}
            {till.session ? <span dir="ltr"> · {till.session.number}</span> : null}
          </span>
        ) : null}
        <nav aria-label={t("pos.till.tabs.label")} className="ms-4 hidden items-center gap-1 md:flex">
          {tabs.map((tab) => {
            const active = tab.to === "/pos" ? location === "/pos" || location === "/pos/" : location.startsWith(tab.to);
            return (
              <Link key={tab.to} to={tab.to} aria-current={active ? "page" : undefined} data-testid={tab.testId} className={cn("flex h-9 items-center gap-2 rounded-md px-3 text-sm", active ? "bg-accent-soft font-medium text-accent" : "text-fg-muted hover:bg-surface-raised")}>
                <tab.icon className="size-4" aria-hidden="true" />
                {t(tab.label)}
                {tab.to === "/pos/orders" && pending > 0 ? <Badge tone="warning" data-testid="till-pending">{pending}</Badge> : null}
              </Link>
            );
          })}
        </nav>
        <div className="ms-auto flex items-center gap-1">
          <Badge tone={online ? "success" : "warning"} className="gap-1" data-testid="online-status">
            {online ? <Wifi className="size-3" aria-hidden="true" /> : <WifiOff className="size-3" aria-hidden="true" />}
            {online ? t("mobile.online") : t("mobile.offline")}
          </Badge>
          <Button variant="ghost" size="icon" aria-label={t("mobile.switchLanguage")} data-testid="till-language" onClick={() => { void setLanguage(language === "ar" ? "en" : "ar"); }}>
            <Languages aria-hidden="true" />
          </Button>
          <Button variant="ghost" size="icon" asChild>
            <Link to="/" aria-label={t("mobile.backToDesktop")}>
              <Monitor aria-hidden="true" />
            </Link>
          </Button>
        </div>
      </header>
      <nav aria-label={t("pos.till.tabs.label")} className="flex border-b border-border bg-surface md:hidden">
        {tabs.map((tab) => {
          const active = tab.to === "/pos" ? location === "/pos" || location === "/pos/" : location.startsWith(tab.to);
          return (
            <Link key={tab.to} to={tab.to} aria-current={active ? "page" : undefined} className={cn("flex flex-1 items-center justify-center gap-2 py-2 text-sm", active ? "border-b-2 border-accent font-medium text-accent" : "text-fg-muted")}>
              <tab.icon className="size-4" aria-hidden="true" />
              {t(tab.label)}
            </Link>
          );
        })}
      </nav>
      <main id="main" tabIndex={-1} className="flex-1 p-4">
        <Outlet />
      </main>
    </div>
  );
}
