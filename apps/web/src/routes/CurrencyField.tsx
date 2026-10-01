import { cn, Input, useFieldControl } from "@quicker/ui";
import { useId, useMemo, useState, type KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";
import { currencyOptions, useCurrencies, useFavouriteCurrencies, type Currency } from "../lib/currencies";
import { localized } from "../lib/format";

interface CurrencyFieldProps {
  value: string;
  onChange: (code: string) => void;
  /** Offers "no currency" first and lets the field be cleared (optional currencies such as an account's restriction). */
  allowEmpty?: boolean;
  required?: boolean;
  disabled?: boolean;
  name?: string;
  "data-testid"?: string;
  "aria-label"?: string;
}

/**
 * Choosing a currency: the workspace's favourites first (IQD, USD and AED unless an administrator sets others), then
 * every active currency, searchable by code or by its name in English or Arabic. An ARIA 1.2 combobox: arrows move,
 * Enter chooses, Escape closes, and typing a known code chooses it at once, so pasting or typing "USD" still works.
 */
export function CurrencyField({ value, onChange, allowEmpty = false, required, disabled, name, "data-testid": testId, "aria-label": ariaLabel }: CurrencyFieldProps) {
  const { t } = useTranslation();
  const control = useFieldControl();
  const listId = useId();
  const currencies = useCurrencies();
  const favourites = useFavouriteCurrencies();
  const [open, setOpen] = useState(false);
  const [typed, setTyped] = useState<string | null>(null);
  const [active, setActive] = useState(0);

  const all = useMemo(() => currencies.data ?? [], [currencies.data]);
  const byCode = useMemo(() => new Map(all.map((c) => [c.code, c])), [all]);
  const options = currencyOptions(all, favourites, typed ?? "");
  const choices: (Currency | null)[] = [...(allowEmpty && !typed ? [null] : []), ...options.favourites, ...options.others];
  const selected = byCode.get(value);
  const shown = typed ?? (value ? (selected ? `${value} · ${localized(selected.name)}` : value) : "");

  const choose = (choice: Currency | null): void => {
    onChange(choice?.code ?? "");
    setTyped(null);
    setOpen(false);
  };
  const optionId = (index: number): string => `${listId}-${String(index)}`;
  const move = (to: number): void => {
    const next = Math.max(0, Math.min(choices.length - 1, to));
    setActive(next);
    document.getElementById(optionId(next))?.scrollIntoView({ block: "nearest" });
  };
  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>): void => {
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      if (!open) {
        setOpen(true);
        setActive(0);
        return;
      }
      move(active + (event.key === "ArrowDown" ? 1 : -1));
    } else if (event.key === "Enter" && open) {
      event.preventDefault();
      const choice = choices[active];
      if (choice !== undefined) {
        choose(choice);
      }
    } else if (event.key === "Escape" && open) {
      event.preventDefault();
      event.stopPropagation();
      setTyped(null);
      setOpen(false);
    }
  };
  const onBlur = (): void => {
    // Leaving the field keeps what was chosen; text that is not a currency goes back to the last choice.
    if (typed !== null) {
      const code = typed.trim().toUpperCase();
      if (byCode.has(code)) {
        onChange(code);
      } else if (allowEmpty && code === "") {
        onChange("");
      }
    }
    setTyped(null);
    setOpen(false);
  };

  const renderOption = (choice: Currency | null, index: number) => (
    <li
      key={choice?.code ?? ""}
      id={optionId(index)}
      role="option"
      aria-selected={index === active}
      data-testid={choice ? `currency-option-${choice.code}` : "currency-option-none"}
      onMouseDown={(e) => { e.preventDefault(); }}
      onClick={() => { choose(choice); }}
      onMouseEnter={() => { setActive(index); }}
      className={cn("flex cursor-pointer items-baseline gap-2 px-3 py-1.5 text-sm", index === active && "bg-accent-soft", choice?.code === value && "font-semibold")}
    >
      {choice ? (
        <>
          <span dir="ltr" className="w-10 shrink-0 font-mono">{choice.code}</span>
          <span className="truncate" dir="auto">{localized(choice.name)}</span>
          <span className="ms-auto shrink-0 text-xs text-fg-muted" dir="ltr">{choice.symbol}</span>
        </>
      ) : (
        <span className="text-fg-muted">{t("currencyField.none")}</span>
      )}
    </li>
  );
  const offset = allowEmpty && !typed ? 1 : 0;

  return (
    <div className="relative">
      <Input
        {...control}
        type="text"
        dir="auto"
        role="combobox"
        aria-autocomplete="list"
        aria-expanded={open}
        aria-controls={listId}
        aria-activedescendant={open && choices.length > 0 ? optionId(active) : undefined}
        aria-label={ariaLabel}
        aria-required={required}
        required={required}
        disabled={disabled}
        name={name}
        autoComplete="off"
        spellCheck={false}
        placeholder={t("currencyField.placeholder")}
        value={shown}
        data-testid={testId}
        data-value={value}
        data-list-open={open}
        onFocus={(e) => { setOpen(true); setActive(0); e.target.select(); }}
        onClick={() => { setOpen(true); }}
        onChange={(e) => {
          const text = e.target.value;
          const code = text.trim().toUpperCase();
          const exact = byCode.get(code);
          if (exact?.isActive) {
            // A full code typed or pasted is a choice: take it and get out of the way.
            choose(exact);
            return;
          }
          setTyped(text);
          setOpen(true);
          setActive(0);
        }}
        onKeyDown={onKeyDown}
        onBlur={onBlur}
      />
      {open && !disabled ? (
        <ul id={listId} role="listbox" aria-label={t("currencyField.list")} className="absolute inset-x-0 top-full z-50 mt-1 max-h-72 overflow-y-auto rounded-md border border-border bg-surface py-1 shadow-lg" data-testid="currency-options">
          {allowEmpty && !typed ? renderOption(null, 0) : null}
          {options.favourites.length > 0 ? (
            <li role="presentation">
              <div className="px-3 pb-1 pt-2 text-xs font-semibold uppercase text-fg-muted" aria-hidden="true">{t("currencyField.favourites")}</div>
              <ul role="group" aria-label={t("currencyField.favourites")}>
                {options.favourites.map((c, i) => renderOption(c, offset + i))}
              </ul>
            </li>
          ) : null}
          {options.others.length > 0 ? (
            <li role="presentation">
              <div className="px-3 pb-1 pt-2 text-xs font-semibold uppercase text-fg-muted" aria-hidden="true">{t("currencyField.all")}</div>
              <ul role="group" aria-label={t("currencyField.all")}>
                {options.others.map((c, i) => renderOption(c, offset + options.favourites.length + i))}
              </ul>
            </li>
          ) : null}
          {choices.length === 0 ? <li role="presentation" className="px-3 py-2 text-sm text-fg-muted">{t("currencyField.noMatch", { query: typed ?? "" })}</li> : null}
        </ul>
      ) : null}
    </div>
  );
}
