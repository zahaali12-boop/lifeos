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
