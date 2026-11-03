import { Button, DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuSeparator, DropdownMenuTrigger, TooltipProvider, cn } from "@quicker/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, Outlet, useNavigate, useRouterState } from "@tanstack/react-router";
import { Bell, Inbox, Keyboard, Languages, LogOut, Menu, Moon, Search, Sun, User, UserCog } from "lucide-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { currentLanguage, setLanguage, type Language } from "../i18n";
import { formatNumber, setDigitStyle, useDigitStyle } from "../lib/format";
import { clearSession, useSession } from "../session/session";
import { CommandPalette } from "./CommandPalette";
import { DailyRatesPrompt } from "./DailyRatesPrompt";
import { navigationFor } from "./navigation";
import { NewMenu } from "./NewMenu";
import { ShortcutsOverlay } from "./ShortcutsOverlay";
import { SidebarNav } from "./SidebarNav";
import { StepUpDialog } from "./StepUpDialog";
import { FlatNav, TopNav } from "./TopNav";
import { installShortcutListener, registerShortcut } from "./useShortcuts";
import { covers } from "../lib/permissions";

type Theme = "light" | "dark";

function readTheme(): Theme {
  try {
    const stored = localStorage.getItem("quicker.theme");
    if (stored === "dark" || stored === "light") {
      return stored;
    }
  } catch {
    // ignore
  }
  return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

export function applyTheme(theme: Theme): void {
  document.documentElement.setAttribute("data-theme", theme);
  try {
    localStorage.setItem("quicker.theme", theme);
  } catch {
    // ignore
  }
}

/** The signed-in frame: skip link, the top bar (home, the module menus, search, approvals, language/theme/digits, notifications and the user menu) and the full-width page; on small screens the menus are a drawer. */
export function AppShell() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const session = useSession();
  const items = navigationFor(session?.tenant.edition);
  const location = useRouterState({ select: (s) => s.location.pathname });
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [shortcutsOpen, setShortcutsOpen] = useState(false);
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [theme, setTheme] = useState<Theme>(readTheme);
  const digits = useDigitStyle();
  const language = currentLanguage();

  const me = useQuery({
    queryKey: ["me"],
    queryFn: async () => unwrap(await api.GET("/api/v1/me")),
    staleTime: 60_000,
  });
  const permissions = useMemo(() => new Set<string>(me.data?.permissions ?? []), [me.data]);
  const unread = useQuery({
    queryKey: ["notifications", "unread-count"],
    queryFn: async () => unwrap(await api.GET("/api/v1/collaboration/notifications/unread-count")),
    refetchInterval: 30_000,
  });

  useEffect(() => {
    applyTheme(theme);
  }, [theme]);

  useEffect(() => installShortcutListener(), []);
  useEffect(() => {
    const unregister = [
      registerShortcut({ keys: "mod+k", description: "shortcuts.palette", group: "shortcuts.groups.global", handler: () => { setPaletteOpen(true); } }),
      registerShortcut({ keys: "?", description: "shortcuts.overlay", group: "shortcuts.groups.global", handler: () => { setShortcutsOpen(true); } }),
      ...items.map((item) => registerShortcut({ keys: item.shortcut, description: item.label, group: item.group ? `nav.groups.${item.group}` : "shortcuts.groups.navigation", handler: () => { void navigate({ to: item.to }); } })),
    ];
    return () => {
      unregister.forEach((fn) => {
        fn();
      });
    };
  }, [items, navigate]);

  const switchLanguage = useCallback(
    async (next: Language) => {
      await setLanguage(next);
      await queryClient.invalidateQueries();
    },
    [queryClient],
  );

  const logout = useCallback(async () => {
    try {
      await api.POST("/api/v1/me/logout");
    } finally {
      clearSession();
      queryClient.clear();
      void navigate({ to: "/login" });
    }
  }, [navigate, queryClient]);

  const visibleNavigation = items.filter((item) => !item.permission || covers(permissions, item.permission));
  // The trader edition has no modules: its links sit in the bar themselves.
  const flat = items.every((item) => !item.group);

  return (
    <TooltipProvider delayDuration={300}>
      <a href="#main" className="skip-link">
        {t("shell.skipToContent")}
      </a>
      <div className="flex h-dvh flex-col bg-canvas text-fg">
        {/* Small screens: the module menus as a drawer. */}
        <aside
          className={cn(
            "fixed inset-y-0 start-0 z-40 flex w-72 flex-col border-e border-border bg-surface transition-transform duration-base ease-standard lg:hidden",
            sidebarOpen ? "translate-x-0" : "-translate-x-full rtl:translate-x-full",
          )}
          aria-label={t("shell.drawerNavigation")}
          hidden={!sidebarOpen}
        >
          <div className="flex h-12 items-center gap-2 border-b border-border px-4">
            <span className="text-lg font-bold tracking-tight text-accent">Quicker</span>
            <span className="truncate text-xs text-fg-muted">{session?.tenant.name}</span>
          </div>
          <SidebarNav items={visibleNavigation} location={location} onNavigate={() => { setSidebarOpen(false); }} />
        </aside>
        {sidebarOpen ? <button type="button" className="fixed inset-0 z-30 bg-black/30 lg:hidden" aria-label={t("common.close")} onClick={() => { setSidebarOpen(false); }} /> : null}
        <header className="relative z-20 flex h-12 shrink-0 items-center gap-1 border-b border-border bg-surface/95 px-2 backdrop-blur sm:px-3">
          <Button variant="ghost" size="icon" className="lg:hidden" aria-label={t("shell.openMenu")} onClick={() => { setSidebarOpen(true); }}>
            <Menu aria-hidden="true" />
          </Button>
          <Link to="/" className="me-1 flex items-center gap-2 rounded-md px-1.5 py-1 focus:outline-none focus-visible:ring-2 focus-visible:ring-accent" aria-label={t("shell.home")} data-testid="home-link">
            <span className="text-base font-bold tracking-tight text-accent">Quicker</span>
          </Link>
          <div className="hidden min-w-0 lg:flex">
            {flat ? <FlatNav items={visibleNavigation} location={location} /> : <TopNav items={visibleNavigation} location={location} />}
          </div>
          <div className="ms-auto flex items-center gap-0.5">
            <NewMenu permissions={permissions} />
            <Button variant="secondary" className="hidden h-8 w-52 justify-start gap-2 text-fg-muted xl:inline-flex" onClick={() => { setPaletteOpen(true); }}>
              <Search aria-hidden="true" />
              <span className="flex-1 truncate text-start">{t("shell.search")}</span>
              <kbd className="font-mono text-xs" dir="ltr">
                ⌘K
              </kbd>
            </Button>
            <Button variant="ghost" size="icon" className="xl:hidden" aria-label={t("shell.search")} onClick={() => { setPaletteOpen(true); }}>
              <Search aria-hidden="true" />
            </Button>
            <Button variant="ghost" size="icon" asChild>
              <Link to="/approvals" aria-label={t("shell.approvals")} data-testid="approvals-link">
                <Inbox aria-hidden="true" />
              </Link>
            </Button>
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button variant="ghost" size="icon" aria-label={t("shell.language")} data-testid="language-menu">
                  <Languages aria-hidden="true" />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuLabel>{t("shell.language")}</DropdownMenuLabel>
                <DropdownMenuRadioGroup value={language} onValueChange={(value) => { void switchLanguage(value as Language); }}>
                  <DropdownMenuRadioItem value="en" data-testid="language-en">English</DropdownMenuRadioItem>
                  <DropdownMenuRadioItem value="ar" data-testid="language-ar">العربية</DropdownMenuRadioItem>
                </DropdownMenuRadioGroup>
                <DropdownMenuSeparator />
                <DropdownMenuLabel>{t("shell.digits")}</DropdownMenuLabel>
                <DropdownMenuRadioGroup value={digits} onValueChange={(value) => { setDigitStyle(value === "arab" ? "arab" : "latn"); }}>
                  <DropdownMenuRadioItem value="latn">{t("shell.digitsLatin")} (0123)</DropdownMenuRadioItem>
                  <DropdownMenuRadioItem value="arab">{t("shell.digitsArabic")} (٠١٢٣)</DropdownMenuRadioItem>
                </DropdownMenuRadioGroup>
              </DropdownMenuContent>
            </DropdownMenu>
            <Button variant="ghost" size="icon" aria-label={theme === "dark" ? t("shell.lightMode") : t("shell.darkMode")} onClick={() => { setTheme(theme === "dark" ? "light" : "dark"); }}>
              {theme === "dark" ? <Sun aria-hidden="true" /> : <Moon aria-hidden="true" />}
            </Button>
            <Button variant="ghost" size="icon" asChild>
              <Link to="/notifications" aria-label={t("nav.notifications")} className="relative">
                <Bell aria-hidden="true" />
                {unread.data && Number(unread.data.count) > 0 ? (
                  <span className="absolute -end-0.5 -top-0.5 min-w-4 rounded-full bg-danger px-1 text-center text-[10px] font-semibold leading-4 text-danger-fg" data-testid="unread-count">
                    {formatNumber(unread.data.count)}
                  </span>
                ) : null}
              </Link>
            </Button>
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button variant="ghost" size="icon" aria-label={t("shell.account")} data-testid="account-menu">
                  <User aria-hidden="true" />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuLabel>
                  <div className="text-sm font-medium text-fg">{session?.user.displayName}</div>
                  <div className="text-xs font-normal text-fg-muted">{session?.user.email}</div>
                  <div className="text-xs font-normal text-fg-subtle">{session?.tenant.name}</div>
                </DropdownMenuLabel>
                <DropdownMenuSeparator />
                <DropdownMenuItem onSelect={() => { void navigate({ to: "/account" }); }} data-testid="my-account">
                  <UserCog className="size-4" aria-hidden="true" />
                  {t("account.title")}
                </DropdownMenuItem>
                <DropdownMenuItem onSelect={() => { setShortcutsOpen(true); }} data-testid="keyboard-shortcuts">
                  <Keyboard className="size-4" aria-hidden="true" />
                  {t("shell.keyboardShortcuts")}
                </DropdownMenuItem>
                <DropdownMenuSeparator />
                <DropdownMenuItem onSelect={() => { void logout(); }} data-testid="logout">
                  <LogOut className="size-4" aria-hidden="true" />
                  {t("shell.signOut")}
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </div>
        </header>
        {/* Only the page scrolls, under a bar that stays put (nothing hides beneath it). */}
        <main id="main" className="min-h-0 min-w-0 flex-1 overflow-y-auto p-4 sm:p-6" tabIndex={-1}>
          <Outlet />
        </main>
      </div>
      <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} permissions={permissions} screens={items} />
      <ShortcutsOverlay open={shortcutsOpen} onOpenChange={setShortcutsOpen} />
      <StepUpDialog />
      {/* The trader's day starts with the rate: asked for at the first open from the hour chosen. */}
      {flat && me.data ? <DailyRatesPrompt permissions={permissions} /> : null}
    </TooltipProvider>
  );
}
