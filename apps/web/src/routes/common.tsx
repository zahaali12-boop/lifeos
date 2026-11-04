import { Field, Input, Select, Textarea, useFieldControl } from "@quicker/ui";
import { useLayoutEffect, useRef, type ComponentProps, type ReactNode } from "react";

export function PageHeader({ title, description, actions }: { title: ReactNode; description?: ReactNode; actions?: ReactNode }) {
  return (
    <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
      <div>
        <h1 className="text-xl font-semibold tracking-tight">{title}</h1>
        {description ? <p className="mt-1 text-sm text-fg-muted">{description}</p> : null}
      </div>
      {actions ? <div className="flex flex-wrap items-center gap-2">{actions}</div> : null}
    </div>
  );
}

/** Inputs wired to the enclosing Field's ARIA attributes. */
export function TextField(props: ComponentProps<typeof Input>) {
  const control = useFieldControl();
  return <Input {...control} {...props} />;
}

export function SelectField(props: ComponentProps<typeof Select>) {
  const control = useFieldControl();
  return <Select {...control} {...props} />;
}

export function TextareaField(props: ComponentProps<typeof Textarea>) {
  const control = useFieldControl();
  return <Textarea {...control} {...props} />;
}

/** A one-line textarea that grows with what is typed, so a long description stays readable in a table cell. */
export function GrowingTextareaField({ className, onChange, value, ...props }: ComponentProps<typeof Textarea>) {
  const control = useFieldControl();
  const ref = useRef<HTMLTextAreaElement>(null);
  useLayoutEffect(() => {
    const area = ref.current;
    if (area) {
      area.style.height = "auto";
      area.style.height = `${String(area.scrollHeight)}px`;
    }
  }, [value]);
  return <Textarea ref={ref} rows={1} value={value} onChange={onChange} className={`min-h-9 resize-none overflow-hidden py-1.5 ${className ?? ""}`} {...control} {...props} />;
}

export function FormError({ message }: { message: string | null }) {
  return message ? (
    <p role="alert" className="rounded-md border border-danger/40 bg-danger-soft px-3 py-2 text-sm text-danger">
      {message}
    </p>
  ) : null;
}

export { Field };

/** Arabic-Indic digits and separators typed into a number field become ASCII; grouping separators and spaces are dropped; only digits, one point and a leading minus remain. */
export function normalizeDecimalInput(text: string): string {
  let out = "";
  let point = false;
  for (const ch of text.replace(/[\u066B]/g, ".")) {
    const code = ch.charCodeAt(0);
    if (code >= 0x0660 && code <= 0x0669) {
      out += String(code - 0x0660);
    } else if (code >= 0x06f0 && code <= 0x06f9) {
      out += String(code - 0x06f0);
    } else if (ch >= "0" && ch <= "9") {
      out += ch;
    } else if (ch === "." && !point) {
      point = true;
      out += ch;
    } else if (ch === "-" && out.length === 0) {
      out += ch;
    }
  }
  return out;
}

/** A plain decimal string with thousands separators in its integer part ("1234567.5" → "1,234,567.5"); anything else is returned as typed. */
export function groupDecimal(raw: string): string {
  if (!/^-?\d+(\.\d*)?$/.test(raw)) {
    return raw;
  }
  const sign = raw.startsWith("-") ? "-" : "";
  const [integer = "", fraction] = raw.slice(sign.length).split(".");
  const grouped = integer.replace(/\B(?=(\d{3})+(?!\d))/g, ",");
  return sign + grouped + (fraction === undefined ? "" : `.${fraction}`);
}

/**
 * A number typed as text (amounts, quantities, rates): accepts digits in either script, a decimal point and grouping
 * separators, hands the plain decimal string on through onChange, and always shows the value with thousands
 * separators, while typing too, so a figure reads the same in a field as it does in a table.
 */
export function DecimalField({ value, onChange, ...props }: Omit<ComponentProps<typeof Input>, "value" | "inputMode" | "type"> & { value: string | number | null | undefined }) {
  const control = useFieldControl();
  const raw = value === null || value === undefined ? "" : String(value);
  return (
    <Input
      {...control}
      {...props}
      type="text"
      inputMode="decimal"
      dir="ltr"
      value={groupDecimal(raw)}
      onChange={(e) => { e.target.value = normalizeDecimalInput(e.target.value); onChange?.(e); }}
    />
  );
}
