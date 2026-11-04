import { Loader2 } from "lucide-react";
import type { HTMLAttributes } from "react";
import { cn } from "../lib/cn";

export interface SpinnerProps extends HTMLAttributes<HTMLSpanElement> {
  /** Translated status text for assistive technology. */
  label: string;
}

export function Spinner({ label, className, ...props }: SpinnerProps) {
  return (
    <span role="status" className={cn("inline-flex items-center gap-2 text-fg-muted", className)} {...props}>
      <Loader2 className="size-4 animate-spin" aria-hidden="true" />
      <span className="sr-only">{label}</span>
    </span>
  );
}
