import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@quicker/ui";
import { useTranslation } from "react-i18next";
import { describeKeys, useShortcutList } from "./useShortcuts";

export interface ShortcutsOverlayProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

/** The "?" overlay: every registered shortcut, grouped, in the current language. */
export function ShortcutsOverlay({ open, onOpenChange }: ShortcutsOverlayProps) {
  const { t } = useTranslation();
  const shortcuts = useShortcutList();
  const groups = new Map<string, typeof shortcuts>();
  for (const shortcut of shortcuts) {
    const list = groups.get(shortcut.group) ?? [];
    list.push(shortcut);
    groups.set(shortcut.group, list);
  }
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
        <DialogHeader>
          <DialogTitle className="text-lg font-semibold">{t("shortcuts.title")}</DialogTitle>
          <DialogDescription className="text-sm text-fg-muted">{t("shortcuts.description")}</DialogDescription>
        </DialogHeader>
        <div className="grid gap-6 sm:grid-cols-2">
          {[...groups.entries()].map(([group, items]) => (
            <section key={group} aria-labelledby={`shortcuts-${group}`}>
              <h3 id={`shortcuts-${group}`} className="mb-2 text-xs font-semibold uppercase tracking-wide text-fg-muted">
                {t(group)}
              </h3>
              <dl className="flex flex-col gap-1.5">
                {items.map((item) => (
                  <div key={item.keys} className="flex items-center justify-between gap-4 text-sm">
                    <dt className="text-fg">{t(item.description)}</dt>
                    <dd>
                      <kbd className="rounded-sm border border-border bg-surface-sunken px-1.5 py-0.5 font-mono text-xs text-fg-muted" dir="ltr">
                        {describeKeys(item.keys, t("shortcuts.then"))}
                      </kbd>
                    </dd>
                  </div>
                ))}
              </dl>
            </section>
          ))}
        </div>
      </DialogContent>
    </Dialog>
  );
}
