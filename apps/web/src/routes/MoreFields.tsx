import { ChevronDown } from "lucide-react";
import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "@quicker/ui";

/**
 * The fields a form rarely needs, folded away under "More" so the everyday ones stand alone (founder programme item
 * 1d). They unfold by themselves when one of them already holds a value or has an error, so nothing is ever hidden
 * that matters.
 */
export function MoreFields({ children, label, filled = false, className, testId = "more-fields" }: { children: ReactNode; label?: string | undefined; filled?: boolean | undefined; className?: string | undefined; testId?: string | undefined }) {
  const { t } = useTranslation();
  const [opened, setOpened] = useState(false);
  const open = opened || filled;
  return (
    <div className={cn("col-span-full flex flex-col gap-3", className)}>
      <button
        type="button"
        aria-expanded={open}
        onClick={() => { setOpened(!open); }}
        className="inline-flex w-fit items-center gap-1 rounded-md text-sm text-fg-muted hover:text-fg focus:outline-none focus-visible:ring-2 focus-visible:ring-accent"
        data-testid={testId}
      >
        <ChevronDown className={cn("size-4 transition-transform", open ? "rotate-180" : "")} aria-hidden="true" />
        {open ? t("common.fewerFields") : (label ?? t("common.moreFields"))}
      </button>
      {open ? <div className="grid gap-4 sm:grid-cols-3" data-testid={`${testId}-panel`}>{children}</div> : null}
    </div>
  );
}
