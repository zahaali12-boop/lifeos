import type { InputHTMLAttributes, SelectHTMLAttributes, TextareaHTMLAttributes } from "react";
import { cn } from "../lib/cn";

const control =
  "w-full rounded-md border border-border bg-surface px-3 text-sm text-fg placeholder:text-fg-subtle transition-colors duration-fast ease-standard hover:border-border-strong disabled:cursor-not-allowed disabled:opacity-50 aria-invalid:border-danger";

export type InputProps = InputHTMLAttributes<HTMLInputElement>;

/** Text input; `dir="auto"` lets mixed Arabic/Latin content pick its own direction (ADR-0027). */
export function Input({ className, dir = "auto", ...props }: InputProps) {
  return <input className={cn(control, "h-9", className)} dir={dir} {...props} />;
}

export type TextareaProps = TextareaHTMLAttributes<HTMLTextAreaElement>;

export function Textarea({ className, dir = "auto", ...props }: TextareaProps) {
  return <textarea className={cn(control, "min-h-24 py-2", className)} dir={dir} {...props} />;
}

export type SelectProps = SelectHTMLAttributes<HTMLSelectElement>;

/** Native select: the most accessible and the most mobile-friendly choice for short lists. */
export function Select({ className, children, ...props }: SelectProps) {
  return (
    <select className={cn(control, "h-9 appearance-none bg-no-repeat pe-8", className)} {...props}>
      {children}
    </select>
  );
}
