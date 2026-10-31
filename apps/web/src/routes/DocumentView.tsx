import { Button } from "@quicker/ui";
import { ArrowLeft, X } from "lucide-react";
import { useEffect, useRef, type ReactNode } from "react";
import { useTranslation } from "react-i18next";

/**
 * A document opened from a list takes the whole page (as a form view does in Odoo) instead of a popup: a header with
 * the number, the partner and any badges, the status bar with the next action under it, then the document. Escape or
 * the back button return to the list; a real dialog opened over it (an edit form) takes Escape first.
 */
export function DocumentView({ onClose, closeLabel, title, subtitle, badges, testId, children }: { onClose: () => void; closeLabel: string; title: ReactNode; subtitle?: ReactNode; badges?: ReactNode; testId?: string | undefined; children: ReactNode }) {
  const { t } = useTranslation();
  const container = useRef<HTMLDivElement>(null);

  useEffect(() => {
    container.current?.focus({ preventScroll: true });
    const onKey = (event: KeyboardEvent): void => {
      if (event.key !== "Escape" || event.defaultPrevented) {
        return;
      }
      // A modal dialog over the document (an edit form, a confirmation) closes first; the document stays.
      if (document.querySelector('[role="dialog"][data-state="open"]')) {
        return;
      }
      onClose();
    };
    document.addEventListener("keydown", onKey);
    return () => { document.removeEventListener("keydown", onKey); };
  }, [onClose]);

  return (
    <div className="fixed inset-x-0 bottom-0 top-12 z-10 overflow-y-auto bg-canvas" data-testid="document-view">
      <div ref={container} tabIndex={-1} className="mx-auto flex max-w-6xl flex-col gap-4 p-4 outline-none sm:p-6" data-testid={testId}>
        <div className="flex flex-wrap items-center gap-2">
          <Button variant="ghost" size="icon" aria-label={t("document.back")} onClick={onClose} data-testid="document-back">
            <ArrowLeft aria-hidden="true" className="rtl:-scale-x-100" />
          </Button>
          <h2 className="flex flex-wrap items-center gap-2 text-lg font-semibold">
            <span dir="ltr">{title}</span>
            {badges}
          </h2>
          {subtitle ? <span className="text-sm text-fg-muted" dir="auto">{subtitle}</span> : null}
          <Button variant="ghost" size="icon" className="ms-auto" aria-label={closeLabel} onClick={onClose} data-testid="document-close">
            <X aria-hidden="true" />
          </Button>
        </div>
        {children}
      </div>
    </div>
  );
}
