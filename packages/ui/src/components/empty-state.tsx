import type { HTMLAttributes, ReactNode } from "react";
import { cn } from "../lib/cn";

export interface EmptyStateProps extends Omit<HTMLAttributes<HTMLDivElement>, "title"> {
  icon?: ReactNode;
  title: ReactNode;
  description?: ReactNode;
  action?: ReactNode;
}

/** What a list shows before it has rows: the situation in words and the one action that changes it. */
export function EmptyState({ icon, title, description, action, className, ...props }: EmptyStateProps) {
  return (
    <div className={cn("flex flex-col items-center justify-center gap-3 rounded-lg border border-dashed border-border px-6 py-12 text-center", className)} {...props}>
      {icon ? (
        <div className="text-fg-subtle [&_svg]:size-8" aria-hidden="true">
          {icon}
        </div>
      ) : null}
      <h3 className="text-base font-semibold text-fg">{title}</h3>
      {description ? <p className="max-w-md text-sm text-fg-muted">{description}</p> : null}
      {action ? <div className="mt-2">{action}</div> : null}
    </div>
  );
}
