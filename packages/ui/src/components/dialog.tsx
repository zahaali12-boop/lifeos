import * as DialogPrimitive from "@radix-ui/react-dialog";
import { X } from "lucide-react";
import type { ComponentProps, HTMLAttributes } from "react";
import { cn } from "../lib/cn";

export const Dialog = DialogPrimitive.Root;
export const DialogTrigger = DialogPrimitive.Trigger;
export const DialogClose = DialogPrimitive.Close;
export const DialogTitle = DialogPrimitive.Title;
export const DialogDescription = DialogPrimitive.Description;

export interface DialogContentProps extends ComponentProps<typeof DialogPrimitive.Content> {
  /** Accessible name of the close button (translated by the caller). */
  closeLabel: string;
}

/** Modal dialog on Radix: focus trap, escape, labelled by DialogTitle; centred, full-width on small screens. */
export function DialogContent({ className, children, closeLabel, ...props }: DialogContentProps) {
  return (
    <DialogPrimitive.Portal>
      <DialogPrimitive.Overlay className="fixed inset-0 z-40 bg-black/40 backdrop-blur-[1px]" />
      <DialogPrimitive.Content
        className={cn(
          "fixed inset-x-4 top-1/2 z-50 mx-auto max-h-[85dvh] w-auto max-w-lg -translate-y-1/2 overflow-y-auto rounded-lg border border-border bg-surface p-6 shadow-lg focus:outline-none sm:inset-x-auto sm:start-1/2 sm:w-full sm:-translate-x-1/2 rtl:sm:translate-x-1/2",
          className,
        )}
        {...props}
      >
        {children}
        <DialogPrimitive.Close className="absolute end-3 top-3 rounded-sm p-1 text-fg-muted hover:bg-surface-sunken hover:text-fg" aria-label={closeLabel}>
          <X className="size-4" aria-hidden="true" />
        </DialogPrimitive.Close>
      </DialogPrimitive.Content>
    </DialogPrimitive.Portal>
  );
}

export function DialogHeader({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn("mb-4 flex flex-col gap-1 pe-8", className)} {...props} />;
}

export function DialogFooter({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn("mt-6 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end", className)} {...props} />;
}
