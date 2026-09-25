import { Badge, Button, cn } from "@quicker/ui";
import { Link, Outlet, useRouterState } from "@tanstack/react-router";
import { ArrowRightLeft, ClipboardCheck, Home, Languages, Monitor, RefreshCw, Wifi, WifiOff } from "lucide-react";
import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { currentLanguage, setLanguage } from "../../i18n";
import { useSession } from "../../session/session";
import { useOnline } from "./context";
import { getQueue, syncQueue, useQueue } from "./queue";

const tabs = [
  { to: "/m", label: "mobile.tabs.home", icon: Home },
  { to: "/m/count", label: "mobile.tabs.count", icon: ClipboardCheck },
  { to: "/m/transfer", label: "mobile.tabs.transfer", icon: ArrowRightLeft },
  { to: "/m/queue", label: "mobile.tabs.queue", icon: RefreshCw },
] as const;

/** The scanner's frame (roadmap 3.8): a thin header with the network state, the screen, and a thumb-reach tab bar. */
export function MobileShell() {
  const { t } = useTranslation();
  const session = useSession();
  const online = useOnline();
  const queue = useQueue();
  const location = useRouterState({ select: (s) => s.location.pathname });
  const language = currentLanguage();
  const pending = queue.length;

  // Back online: replay what was captured meanwhile.
  useEffect(() => {
    if (online && getQueue().length > 0) {
      void syncQueue();
    }
  }, [online]);

  return (
    <div className="flex min-h-dvh flex-col bg-canvas text-fg">
      <a href="#main" className="skip-link">
        {t("shell.skipToContent")}
      </a>
      <header className="sticky top-0 z-10 flex h-14 items-center gap-2 border-b border-border bg-surface/95 px-3 backdrop-blur">
        <span className="text-lg font-bold tracking-tight text-accent">Quicker</span>
        <span className="truncate text-xs text-fg-muted">{session?.tenant.name}</span>
        <div className="ms-auto flex items-center gap-1">
          <Badge tone={online ? "success" : "warning"} className="gap-1" data-testid="online-status">
            {online ? <Wifi className="size-3" aria-hidden="true" /> : <WifiOff className="size-3" aria-hidden="true" />}
            {online ? t("mobile.online") : t("mobile.offline")}
          </Badge>
          <Button variant="ghost" size="icon" aria-label={t("mobile.switchLanguage")} data-testid="mobile-language" onClick={() => { void setLanguage(language === "ar" ? "en" : "ar"); }}>
            <Languages aria-hidden="true" />
          </Button>
          <Button variant="ghost" size="icon" asChild>
            <Link to="/" aria-label={t("mobile.backToDesktop")}>
              <Monitor aria-hidden="true" />
            </Link>
          </Button>
        </div>
      </header>
      <main id="main" tabIndex={-1} className="flex-1 p-4 pb-24">
        <Outlet />
      </main>
      <nav aria-label={t("mobile.tabs.label")} className="fixed inset-x-0 bottom-0 z-10 border-t border-border bg-surface pb-[env(safe-area-inset-bottom)]">
        <ul className="grid grid-cols-4">
          {tabs.map((tab) => {
            const active = tab.to === "/m" ? location === "/m" || location === "/m/" : location.startsWith(tab.to);
            return (
              <li key={tab.to}>
                <Link to={tab.to} aria-current={active ? "page" : undefined} className={cn("relative flex h-16 flex-col items-center justify-center gap-1 text-xs", active ? "font-medium text-accent" : "text-fg-muted")}>
                  <tab.icon className="size-5" aria-hidden="true" />
                  {t(tab.label)}
                  {tab.to === "/m/queue" && pending > 0 ? (
                    <span data-testid="queue-badge" className="absolute end-1/4 top-2 min-w-4 rounded-full bg-danger px-1 text-center text-[10px] font-semibold leading-4 text-danger-fg">
                      {pending}
                    </span>
                  ) : null}
                </Link>
              </li>
            );
          })}
        </ul>
      </nav>
    </div>
  );
}
