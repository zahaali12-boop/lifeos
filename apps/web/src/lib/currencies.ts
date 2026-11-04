import { useQuery } from "@tanstack/react-query";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";

export type Currency = components["schemas"]["CurrencySummary"];

/** The workspace setting holding the currencies pinned at the top of every currency picker (a JSON array of codes). */
export const favouriteCurrenciesSetting = "organization.currencies.favourites";

/** Pinned until an administrator chooses otherwise (A-155). */
export const defaultFavouriteCurrencies = ["IQD", "USD", "AED"];

/** Every ISO 4217 currency the platform knows (cached: the list changes only with a release). */
export function useCurrencies() {
  return useQuery({ queryKey: ["currencies"], staleTime: 60 * 60_000, queryFn: async () => unwrap(await api.GET("/api/v1/organization/currencies")) });
}

/** The workspace's favourite currencies, or the defaults when the setting is unset, malformed or not readable. */
export function useFavouriteCurrencies(): string[] {
  const settings = useQuery({
    queryKey: ["settings", null],
    staleTime: 5 * 60_000,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/organization/settings")),
  });
  return parseFavourites(settings.data?.find((s) => s.key === favouriteCurrenciesSetting)?.value);
}

export function parseFavourites(value: unknown): string[] {
  if (!Array.isArray(value)) {
    return defaultFavouriteCurrencies;
  }
  const codes = value.filter((v): v is string => typeof v === "string" && /^[A-Z]{3}$/u.test(v));
  return codes.length > 0 ? [...new Set(codes)] : defaultFavouriteCurrencies;
}

/**
 * What a currency picker lists for what was typed: with nothing typed, the favourites (in their order) then every
 * other active currency by code; otherwise the active currencies whose code starts with, or whose name in any language
 * contains, the text, favourites first.
 */
export function currencyOptions(all: Currency[], favourites: string[], typed: string): { favourites: Currency[]; others: Currency[] } {
  const active = all.filter((c) => c.isActive);
  const byCode = new Map(active.map((c) => [c.code, c]));
  const query = typed.trim().toLocaleLowerCase();
  const matches = (c: Currency): boolean =>
    !query || c.code.toLowerCase().startsWith(query) || Object.values(c.name).some((name) => name.toLocaleLowerCase().includes(query));
  const pinned = favourites.map((code) => byCode.get(code)).filter((c): c is Currency => c !== undefined && matches(c));
  const pinnedCodes = new Set(pinned.map((c) => c.code));
  const others = active.filter((c) => !pinnedCodes.has(c.code) && matches(c)).sort((a, b) => a.code.localeCompare(b.code));
  return { favourites: pinned, others };
}
