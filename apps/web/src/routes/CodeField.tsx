import { Button } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { Wand2 } from "lucide-react";
import { useEffect, useRef, useState, type ComponentProps } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { TextField } from "./common";

type CodeFieldProps = Omit<ComponentProps<typeof TextField>, "value" | "onChange"> & {
  /** The kind of record the code belongs to, as the server's code catalogue names it (customer, item, warehouse, account…). */
  kind: string;
  value: string;
  onChange: (code: string) => void;
  /** What the code is unique within: the company, chart, warehouse or dimension. Undefined for codes unique in the workspace. */
  scope?: string | null | undefined;
  /** For an account: the parent's code, whose children the new code continues. */
  parent?: string | null | undefined;
  /** The record being edited (its own code is not a duplicate); none for a new record. */
  recordId?: string | null | undefined;
  "data-testid"?: string | undefined;
};

/**
 * A code input that keeps codes unique and saves typing (A-165): on a new record whose codes run in sequence
 * (customers, items, warehouses, accounts) the next free code is filled in, continuing the codes already there, until
 * something else is typed; a code that is already used is flagged while it is typed, with the next free one a click
 * away; and the wand fills in the next free code at any time.
 */
export function CodeField({ kind, value, onChange, scope, parent, recordId, "data-testid": testId, className, ...props }: CodeFieldProps) {
  const { t } = useTranslation();
  const [term, setTerm] = useState(value.trim());
  const typed = useRef(false);
  const filled = useRef<string | null>(null);
  const input = useRef<HTMLInputElement>(null);
  const selectFilled = useRef(false);
  const ready = scope === undefined || Boolean(scope);
  const locked = Boolean(props.disabled) || Boolean(props.readOnly);

  // Asks once typing pauses.
  useEffect(() => {
    const timer = window.setTimeout(() => { setTerm(value.trim()); }, 250);
    return () => { window.clearTimeout(timer); };
  }, [value]);

  const suggestion = useQuery({
    queryKey: ["code-suggestion", kind, scope ?? null, parent ?? null, recordId ?? null, term],
    enabled: ready && !locked,
    staleTime: 5_000,
    queryFn: async () =>
      unwrap(await api.GET("/api/v1/numbering/codes/{kind}", {
        params: { path: { kind }, query: { ...(term ? { code: term } : {}), ...(scope ? { scope } : {}), ...(parent ? { parent } : {}), ...(recordId ? { except: recordId } : {}) } },
      })),
  });
  const data = suggestion.data;

  // A new record starts with the next code, and follows it (a new parent for an account) until the user types their own.
  useEffect(() => {
    if (!data?.autoFill || recordId || locked || typed.current) {
      return;
    }
    if ((value === "" || value === filled.current) && value !== data.next) {
      filled.current = data.next;
      selectFilled.current = true;
      onChange(data.next);
    }
  }, [data, recordId, locked, value, onChange]);
  // A code filled in while the person is already in the field is selected, so their first keystroke replaces it.
  useEffect(() => {
    if (selectFilled.current && value === filled.current) {
      selectFilled.current = false;
      if (input.current && document.activeElement === input.current) {
        input.current.select();
      }
    }
  }, [value]);

  const current = value.trim();
  // The answer must be about what is in the field now, not what was in it a moment ago.
  const taken = Boolean(data?.taken) && data?.code?.toLocaleLowerCase() === current.toLocaleLowerCase() && current !== "";
  const useNext = (): void => {
    if (data) {
      typed.current = false;
      filled.current = data.next;
      onChange(data.next);
    }
  };

  return (
    <div className={className}>
      <div className="flex items-center gap-1">
        <TextField
          {...props}
          ref={input}
          value={value}
          onChange={(e) => { typed.current = true; onChange(e.target.value); }}
          aria-invalid={taken ? true : props["aria-invalid"]}
          data-testid={testId}
          dir="ltr"
        />
        {locked ? null : (
          <Button type="button" variant="ghost" size="icon" className="shrink-0" onClick={useNext} disabled={!data} aria-label={t("codes.suggest")} title={t("codes.suggest")} data-testid={testId ? `${testId}-suggest` : undefined}>
            <Wand2 aria-hidden="true" />
          </Button>
        )}
      </div>
      <p className="mt-1 min-h-4 text-xs" aria-live="polite" data-testid={testId ? `${testId}-hint` : undefined}>
        {locked || !data ? null : taken ? (
          <span className="text-danger">
            {t("codes.taken", { code: current })}{" "}
            <button type="button" className="font-medium underline underline-offset-2" onClick={useNext} data-testid={testId ? `${testId}-use-next` : undefined}>
              {t("codes.useNext", { code: data.next })}
            </button>
          </span>
        ) : current === "" ? (
          <span className="text-fg-muted">{t("codes.next", { code: data.next })}</span>
        ) : current === filled.current && !typed.current ? (
          <span className="text-fg-muted">{t("codes.suggested")}</span>
        ) : null}
      </p>
    </div>
  );
}
