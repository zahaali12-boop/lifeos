import { useCallback, useState } from "react";
import { useSession } from "../session/session";

/**
 * A preference of the signed-in member in this workspace, kept in the browser (founder programme item 1d): the
 * warehouse they shipped from last, the bank account they received into last. Keyed by membership, so two people
 * sharing a computer, or one person in two workspaces, never see each other's defaults.
 */
export function usePref(name: string, fallback = ""): [string, (value: string) => void] {
  const session = useSession();
  const key = `quicker.pref:${session?.membershipId ?? "anonymous"}:${name}`;
  const [value, setValue] = useState<string>(() => {
    try {
      return localStorage.getItem(key) ?? fallback;
    } catch {
      return fallback;
    }
  });
  const remember = useCallback((next: string) => {
    setValue(next);
    try {
      if (next) {
        localStorage.setItem(key, next);
      } else {
        localStorage.removeItem(key);
      }
    } catch {
      // A private window or a full store: the default is simply not kept.
    }
  }, [key]);
  return [value, remember];
}
