import { useSyncExternalStore } from "react";
import type { components } from "../api/schema";

export type TokenResponse = components["schemas"]["TokenResponse"];

/** What the app keeps between reloads: the tokens and who they belong to. Permissions come from GET /me at runtime. */
export interface Session {
  accessToken: string;
  refreshToken: string;
  tenant: TokenResponse["tenant"];
  user: TokenResponse["user"];
  membershipId: string;
}

const STORAGE_KEY = "quicker.session";
const listeners = new Set<() => void>();
let current: Session | null = load();

function load(): Session | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as Session) : null;
  } catch {
    return null;
  }
}

function emit(): void {
  listeners.forEach((listener) => {
    listener();
  });
}

export function getSession(): Session | null {
  return current;
}

/**
 * Takes what another window of the app stored (its renewal, its sign-in or its sign-out), so this window never trades
 * a refresh token that window already rotated; nothing happens when the stored session is this one already.
 */
export function syncSession(): Session | null {
  let raw: string | null;
  try {
    raw = localStorage.getItem(STORAGE_KEY);
  } catch {
    return current;
  }
  if (raw === (current ? JSON.stringify(current) : null)) {
    return current;
  }
  current = raw ? (JSON.parse(raw) as Session) : null;
  emit();
  return current;
}

if (typeof window !== "undefined") {
  // The browser tells every other window when one of them writes the session.
  window.addEventListener("storage", (event) => {
    if (event.key === null || event.key === STORAGE_KEY) {
      syncSession();
    }
  });
}

export function setSession(tokens: TokenResponse): Session {
  current = { accessToken: tokens.accessToken, refreshToken: tokens.refreshToken, tenant: tokens.tenant, user: tokens.user, membershipId: tokens.membershipId };
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(current));
  } catch {
    // ignore
  }
  emit();
  return current;
}

/** Keeps the stored user in step after the person changes their profile or two-step verification. */
export function updateSessionUser(user: Partial<Session["user"]>): void {
  if (!current) {
    return;
  }
  current = { ...current, user: { ...current.user, ...user } };
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(current));
  } catch {
    // ignore
  }
  emit();
}

/** Keeps the stored workspace in step after an admin switches its edition, so the shell changes without a new sign-in. */
export function updateSessionTenant(tenant: Partial<Session["tenant"]>): void {
  if (!current) {
    return;
  }
  current = { ...current, tenant: { ...current.tenant, ...tenant } };
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(current));
  } catch {
    // ignore
  }
  emit();
}

export function clearSession(): void {
  current = null;
  try {
    localStorage.removeItem(STORAGE_KEY);
  } catch {
    // ignore
  }
  emit();
}

export function useSession(): Session | null {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    getSession,
    getSession,
  );
}
