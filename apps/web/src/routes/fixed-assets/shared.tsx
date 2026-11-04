import { Badge } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import type { Flow } from "../StatusBar";

export type Asset = components["schemas"]["AssetSummary"];
export type AssetBookValue = components["schemas"]["AssetBookValueSummary"];
export type AssetCategory = components["schemas"]["AssetCategorySummary"];
export type AssetBook = components["schemas"]["AssetBookSummary"];
export type DepreciationRun = components["schemas"]["DepreciationRunSummary"];

export const depreciationMethods = ["straight_line", "declining_balance", "double_declining", "sum_of_years", "units_of_production", "none"] as const;
export const depreciationConventions = ["full_month", "mid_month", "daily", "next_month"] as const;
export const acquisitionSources = ["supplier", "bank", "clearing", "opening"] as const;
export const assetStatuses = ["draft", "active", "fully_depreciated", "disposed"] as const;

export function useAssetCategories(activeOnly = false) {
  return useQuery({
    queryKey: ["asset-categories", activeOnly],
    queryFn: async () => unwrap(await api.GET("/api/v1/fixed-assets/categories", { params: { query: activeOnly ? { isActive: true } : {} } })),
  });
}

export function useAssetBooks(companyId: string) {
  return useQuery({
    queryKey: ["asset-books", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/fixed-assets/books", { params: { query: { companyId } } })),
  });
}

const tones: Record<string, "neutral" | "info" | "success" | "warning" | "danger"> = {
  draft: "neutral", active: "success", fully_depreciated: "info", disposed: "neutral", posted: "success", reversed: "danger", planned: "neutral", skipped: "neutral",
};

export function AssetStatus({ status }: { status: string }) {
  const { t } = useTranslation();
  return (
    <Badge tone={tones[status] ?? "neutral"} data-testid="doc-status">
      {t(`fixedAssets.statuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

/** The path of an asset: draft, active, fully depreciated; disposed sits off the path. */
export function assetFlow(status: string, t: TFunction): Flow {
  const label = (id: string): string => t(`fixedAssets.statuses.${id}`, { defaultValue: id });
  const steps = ["draft", "active", "fully_depreciated"].map((id) => ({ id, label: label(id) }));
  return steps.some((s) => s.id === status)
    ? { steps, state: { step: status, label: label(status) } }
    : { steps, state: { step: null, label: label(status), tone: "neutral" } };
}

/** The GL book's view of an asset, which the register shows. */
export function glBook(asset: Asset): AssetBookValue | undefined {
  return (asset.books ?? []).find((b) => b.postsToGl) ?? (asset.books ?? [])[0];
}
