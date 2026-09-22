import { Button, DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuSeparator, DropdownMenuTrigger, TooltipProvider, cn } from "@quicker/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, Outlet, useNavigate, useRouterState } from "@tanstack/react-router";
import { Bell, Keyboard, Languages, LogOut, Menu, Moon, Search, Sun, User } from "lucide-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { currentLanguage, setLanguage, type Language } from "../i18n";
import { formatNumber, setDigitStyle, useDigitStyle } from "../lib/format";
import { clearSession, useSession } from "../session/session";
import { CommandPalette } from "./CommandPalette";
import { navigation } from "./navigation";
import { ShortcutsOverlay } from "./ShortcutsOverlay";
import { installShortcutListener, registerShortcut } from "./useShortcuts";

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

/** The signed-in frame: skip link, sidebar navigation, header with search, language/theme/digits, notifications and the user menu. */
export function AppShell() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const session = useSession();
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
      ...navigation.map((item) => registerShortcut({ keys: item.shortcut, description: item.label, group: "shortcuts.groups.navigation", handler: () => { void navigate({ to: item.to }); } })),
    ];
    return () => {
      unregister.forEach((fn) => {
        fn();
      });
    };
  }, [navigate]);

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

  const visibleNavigation = navigation.filter((item) => !item.permission || permissions.has("*") || permissions.has(item.permission));

  return (
    <TooltipProvider delayDuration={300}>
      <a href="#main" className="skip-link">
        {t("shell.skipToContent")}
      </a>
      <div className="flex min-h-dvh bg-canvas text-fg">
        <aside
          className={cn(
            "fixed inset-y-0 start-0 z-30 flex w-64 flex-col border-e border-border bg-surface transition-transform duration-base ease-standard lg:static lg:translate-x-0",
            sidebarOpen ? "translate-x-0" : "-translate-x-full rtl:translate-x-full lg:rtl:translate-x-0",
          )}
          aria-label={t("shell.primaryNavigation")}
        >
          <div className="flex h-14 items-center gap-2 border-b border-border px-4">
            <span className="text-lg font-bold tracking-tight text-accent">Quicker</span>
            <span className="truncate text-xs text-fg-muted">{session?.tenant.name}</span>
          </div>
          <nav className="flex-1 overflow-y-auto p-2">
            <ul className="flex flex-col gap-0.5">
              {visibleNavigation.map((item) => {
                const active = item.to === "/" ? location === "/" : location.startsWith(item.to);
                return (
                  <li key={item.to}>
                    <Link
                      to={item.to}
                      onClick={() => { setSidebarOpen(false); }}
                      aria-current={active ? "page" : undefined}
                      className={cn("flex h-9 items-center gap-3 rounded-md px-3 text-sm transition-colors", active ? "bg-accent-soft font-medium text-accent" : "text-fg-muted hover:bg-surface-sunken hover:text-fg")}
                    >
                      <item.icon className="size-4 shrink-0" aria-hidden="true" />
                      <span className="truncate">{t(item.label)}</span>
                    </Link>
                  </li>
                );
              })}
            </ul>
          </nav>
          <div className="border-t border-border p-3 text-xs text-fg-subtle">
            <Button variant="ghost" size="sm" className="w-full justify-start gap-2" onClick={() => { setShortcutsOpen(true); }}>
              <Keyboard aria-hidden="true" />
              {t("shell.keyboardShortcuts")}
            </Button>
          </div>
        </aside>
        {sidebarOpen ? <button type="button" className="fixed inset-0 z-20 bg-black/30 lg:hidden" aria-label={t("common.close")} onClick={() => { setSidebarOpen(false); }} /> : null}
        <div className="flex min-w-0 flex-1 flex-col">
          <header className="sticky top-0 z-10 flex h-14 items-center gap-2 border-b border-border bg-surface/95 px-3 backdrop-blur sm:px-4">
            <Button variant="ghost" size="icon" className="lg:hidden" aria-label={t("shell.openMenu")} onClick={() => { setSidebarOpen(true); }}>
              <Menu aria-hidden="true" />
            </Button>
            <Button variant="secondary" className="hidden h-9 min-w-56 justify-start gap-2 text-fg-muted sm:inline-flex" onClick={() => { setPaletteOpen(true); }}>
              <Search aria-hidden="true" />
              <span className="flex-1 text-start">{t("shell.search")}</span>
              <kbd className="font-mono text-xs" dir="ltr">
                ⌘K
              </kbd>
            </Button>
            <Button variant="ghost" size="icon" className="sm:hidden" aria-label={t("shell.search")} onClick={() => { setPaletteOpen(true); }}>
              <Search aria-hidden="true" />
            </Button>
            <div className="ms-auto flex items-center gap-1">
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
                  </DropdownMenuLabel>
                  <DropdownMenuSeparator />
                  <DropdownMenuItem onSelect={() => { void logout(); }} data-testid="logout">
                    <LogOut className="size-4" aria-hidden="true" />
                    {t("shell.signOut")}
                  </DropdownMenuItem>
                </DropdownMenuContent>
              </DropdownMenu>
            </div>
          </header>
          <main id="main" className="flex-1 p-4 sm:p-6" tabIndex={-1}>
            <Outlet />
          </main>
        </div>
      </div>
      <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} permissions={permissions} />
      <ShortcutsOverlay open={shortcutsOpen} onOpenChange={setShortcutsOpen} />
    </TooltipProvider>
  );
}
