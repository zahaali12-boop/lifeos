import { useQuery } from "@tanstack/react-query";
import { useDeferredValue, useMemo, type ComponentProps } from "react";
import { api, unwrap } from "../../api";
import { localized } from "../../lib/format";
import { ComboField } from "../ComboField";

type ItemCodeFieldProps = Omit<ComponentProps<typeof ComboField>, "value" | "onChange" | "options" | "transform" | "onQueryChange"> & {
  value: string;
  onChange: (code: string) => void;
};

/**
 * An item-code input whose list opens on click (the first items, then the ones matching what is typed, by code or
 * name). The value is still the code; typing a known code works as before.
 */
export function ItemCodeField({ value, onChange, ...props }: ItemCodeFieldProps) {
  const term = useDeferredValue(value.trim());
  const suggestions = useQuery({
    queryKey: ["item-suggestions", term],
    staleTime: 30_000,
    queryFn: async () => unwrap(await api.GET("/api/v1/items", { params: { query: term ? { q: term, limit: 12 } : { limit: 12 } } })),
  });
  const options = useMemo(() => (suggestions.data?.items ?? []).map((item) => ({ value: item.code, label: localized(item.name) })), [suggestions.data]);
  return <ComboField {...props} value={value} onChange={onChange} options={options} transform={(raw) => raw.toUpperCase()} dir="ltr" />;
}
