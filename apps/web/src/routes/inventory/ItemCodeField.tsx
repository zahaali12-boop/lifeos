import { useQuery } from "@tanstack/react-query";
import { useDeferredValue, useId, type ComponentProps } from "react";
import { api, unwrap } from "../../api";
import { localized } from "../../lib/format";
import { TextField } from "../common";

type ItemCodeFieldProps = Omit<ComponentProps<typeof TextField>, "value" | "onChange" | "list"> & {
  value: string;
  onChange: (code: string) => void;
};

/**
 * An item-code input that suggests items by code or name as the user types (a native datalist, so it stays keyboard-
 * and screen-reader-friendly and works right to left). The value is still the code; typing a known code works as before.
 */
export function ItemCodeField({ value, onChange, ...props }: ItemCodeFieldProps) {
  const listId = useId();
  const term = useDeferredValue(value.trim());
  const suggestions = useQuery({
    queryKey: ["item-suggestions", term],
    enabled: term.length >= 2,
    staleTime: 30_000,
    queryFn: async () => unwrap(await api.GET("/api/v1/items", { params: { query: { q: term, limit: 8 } } })),
  });
  return (
    <>
      <TextField {...props} value={value} onChange={(e) => { onChange(e.target.value.toUpperCase()); }} list={listId} autoComplete="off" dir="ltr" />
      <datalist id={listId}>
        {(suggestions.data?.items ?? []).map((item) => (
          <option key={item.id} value={item.code}>
            {localized(item.name)}
          </option>
        ))}
      </datalist>
    </>
  );
}
