import { createContext, useContext, useId, type HTMLAttributes, type LabelHTMLAttributes, type ReactNode } from "react";
import { cn } from "../lib/cn";

interface FieldContextValue {
  id: string;
  descriptionId: string;
  errorId: string;
  error?: string | undefined;
  description?: ReactNode;
}

const FieldContext = createContext<FieldContextValue | null>(null);

export interface FieldProps extends HTMLAttributes<HTMLDivElement> {
  label: ReactNode;
  /** A server or client validation message; sets aria-invalid and aria-describedby on the control. */
  error?: string | undefined;
  description?: ReactNode;
  required?: boolean;
  children: ReactNode;
}

/**
 * Label, control, description and error wired together with ARIA. The control inside uses `useFieldControl()`
 * (or is a plain input that spreads `fieldControlProps`) so error mapping from problem details is one prop.
 */
export function Field({ label, error, description, required, className, children, ...props }: FieldProps) {
  const id = useId();
  const value: FieldContextValue = { id, descriptionId: `${id}-description`, errorId: `${id}-error`, error, description };
  return (
    <FieldContext.Provider value={value}>
      <div className={cn("flex flex-col gap-1.5", className)} {...props}>
        <Label htmlFor={id}>
          {label}
          {required ? (
            <span className="text-danger" aria-hidden="true">
              {" "}
              *
            </span>
          ) : null}
        </Label>
        {children}
        {description ? (
          <p id={value.descriptionId} className="text-xs text-fg-muted">
            {description}
          </p>
        ) : null}
        {error ? (
          <p id={value.errorId} className="text-xs text-danger" role="alert">
            {error}
          </p>
        ) : null}
      </div>
    </FieldContext.Provider>
  );
}

export type LabelProps = LabelHTMLAttributes<HTMLLabelElement>;

export function Label({ className, ...props }: LabelProps) {
  return <label className={cn("text-sm font-medium text-fg", className)} {...props} />;
}

/** The ARIA wiring a control inside a Field spreads onto itself. */
export function useFieldControl(): { id: string; "aria-invalid": boolean | undefined; "aria-describedby": string | undefined; "aria-required"?: boolean } {
  const field = useContext(FieldContext);
  if (!field) {
    return { id: "", "aria-invalid": undefined, "aria-describedby": undefined };
  }
  const describedBy = [field.description ? field.descriptionId : null, field.error ? field.errorId : null].filter(Boolean).join(" ");
  return { id: field.id, "aria-invalid": field.error ? true : undefined, "aria-describedby": describedBy || undefined };
}
