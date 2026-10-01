import { Field, Input, Select, Textarea, useFieldControl } from "@quicker/ui";
import type { ComponentProps, ReactNode } from "react";

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

export function FormError({ message }: { message: string | null }) {
  return message ? (
    <p role="alert" className="rounded-md border border-danger/40 bg-danger-soft px-3 py-2 text-sm text-danger">
      {message}
    </p>
  ) : null;
}

export { Field };
