import { useQuery } from "@tanstack/react-query";
import { useDeferredValue, useMemo, useState, type ComponentProps } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { ComboField } from "../ComboField";
import { QuickCreateDialog } from "../QuickCreate";

type ItemCodeFieldProps = Omit<ComponentProps<typeof ComboField>, "value" | "onChange" | "options" | "transform" | "onQueryChange" | "onCreate" | "createLabel"> & {
  value: string;
  onChange: (code: string) => void;
  /** The currency a list price typed in a quick create is in (the company's). */
  currency?: string | undefined;
};

/**
 * An item-code input whose list opens on click (the first items, then the ones matching what is typed, by code or
 * name). The value is still the code; typing a known code works as before. A code that matches nothing can be made
 * into a new item on the spot by anyone allowed to manage items.
 */
export function ItemCodeField({ value, onChange, currency, ...props }: ItemCodeFieldProps) {
  const { t } = useTranslation();
  const can = useCan();
  const [quick, setQuick] = useState<string | null>(null);
  const term = useDeferredValue(value.trim());
  const suggestions = useQuery({
    queryKey: ["item-suggestions", term],
    staleTime: 30_000,
    queryFn: async () => unwrap(await api.GET("/api/v1/items", { params: { query: term ? { q: term, limit: 12 } : { limit: 12 } } })),
  });
  const options = useMemo(() => (suggestions.data?.items ?? []).map((item) => ({ value: item.code, label: localized(item.name) })), [suggestions.data]);
  const canCreate = can("inventory.item.manage");
  return (
    <>
      <ComboField
        {...props}
        value={value}
        onChange={onChange}
        options={options}
        transform={(raw) => raw.toUpperCase()}
        onCreate={canCreate ? (typed) => { setQuick(typed); } : undefined}
        createLabel={(typed) => t("quick.createItem", { code: typed })}
        dir="ltr"
      />
      {quick !== null ? (
        <QuickCreateDialog kind="item" companyId="" currency={currency ?? ""} initial={quick} onClose={() => { setQuick(null); }} onCreated={(created) => { onChange(created.code); setQuick(null); }} />
      ) : null}
    </>
  );
}
