import { useSyncExternalStore } from "react";

/**
 * Step-up (re-confirming identity) for the actions the API guards with a recent-authentication window: API keys,
 * single sign-on, the tenant policy, reopening a hard-closed period and the like. The API client asks here when a
 * request comes back `auth.step_up_required`; the shell's dialog answers, and the client replays the request once the
 * session is fresh. Concurrent requests share one prompt.
 */
let pending: Promise<boolean> | null = null;
let settle: ((confirmed: boolean) => void) | null = null;
const listeners = new Set<() => void>();

function emit(): void {
  listeners.forEach((listener) => {
    listener();
  });
}

/** Opens the prompt (or joins the open one); resolves true once identity is confirmed, false when cancelled. */
export function requestStepUp(): Promise<boolean> {
  pending ??= new Promise<boolean>((resolve) => {
    settle = resolve;
  });
  emit();
  return pending;
}

/** Called by the dialog: true after a successful step-up, false when the person cancels. */
export function settleStepUp(confirmed: boolean): void {
  const resolve = settle;
  pending = null;
  settle = null;
  emit();
  resolve?.(confirmed);
}

export function useStepUpRequested(): boolean {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    () => pending !== null,
    () => false,
  );
}
