import { cva, type VariantProps } from "class-variance-authority";
import type { HTMLAttributes } from "react";
import { cn } from "../lib/cn";

const badgeVariants = cva("inline-flex items-center rounded-sm border px-2 py-0.5 text-xs font-medium", {
  variants: {
    tone: {
      neutral: "border-border bg-surface-sunken text-fg-muted",
      accent: "border-transparent bg-accent-soft text-accent",
      success: "border-transparent bg-success-soft text-success",
      warning: "border-transparent bg-warning-soft text-warning",
      danger: "border-transparent bg-danger-soft text-danger",
      info: "border-transparent bg-info-soft text-fg",
    },
  },
  defaultVariants: { tone: "neutral" },
});

export interface BadgeProps extends HTMLAttributes<HTMLSpanElement>, VariantProps<typeof badgeVariants> {}

/** Status and category labels; colour is never the only signal, so the text says what the tone means. */
export function Badge({ className, tone, ...props }: BadgeProps) {
  return <span className={cn(badgeVariants({ tone }), className)} {...props} />;
}
