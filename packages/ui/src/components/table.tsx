import { useEffect, useRef, useState, type HTMLAttributes, type TdHTMLAttributes, type ThHTMLAttributes } from "react";
import { cn } from "../lib/cn";

/**
 * Semantic table primitives for small, non-virtualized lists; the data grid in the app builds on the same classes.
 * A table wider than its column scrolls sideways, and the scrolling box then takes keyboard focus so it can be
 * scrolled without a mouse (WCAG 2.1.1) even when nothing inside it is focusable.
 */
export function Table({ className, ...props }: HTMLAttributes<HTMLTableElement>) {
  const box = useRef<HTMLDivElement>(null);
  const [scrollable, setScrollable] = useState(false);
  useEffect(() => {
    const element = box.current;
    if (!element || typeof ResizeObserver === "undefined") {
      return undefined;
    }
    const measure = (): void => { setScrollable(element.scrollWidth > element.clientWidth); };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    if (element.firstElementChild) {
      observer.observe(element.firstElementChild);
    }
    return () => { observer.disconnect(); };
  }, []);
  return (
    <div ref={box} tabIndex={scrollable ? 0 : undefined} className="w-full overflow-x-auto rounded-md border border-border focus-visible:outline-2 focus-visible:outline-accent">
      <table className={cn("w-full caption-bottom text-sm", className)} {...props} />
    </div>
  );
}

export function TableHeader({ className, ...props }: HTMLAttributes<HTMLTableSectionElement>) {
  return <thead className={cn("bg-surface-sunken text-start", className)} {...props} />;
}

export function TableBody({ className, ...props }: HTMLAttributes<HTMLTableSectionElement>) {
  return <tbody className={cn("[&_tr:last-child]:border-0", className)} {...props} />;
}

export function TableRow({ className, ...props }: HTMLAttributes<HTMLTableRowElement>) {
  return <tr className={cn("border-b border-border transition-colors hover:bg-surface-sunken/60 data-[state=selected]:bg-selection", className)} {...props} />;
}

export function TableHead({ className, ...props }: ThHTMLAttributes<HTMLTableCellElement>) {
  return <th scope="col" className={cn("h-10 px-3 text-start align-middle text-xs font-semibold uppercase tracking-wide text-fg-muted", className)} {...props} />;
}

export function TableCell({ className, ...props }: TdHTMLAttributes<HTMLTableCellElement>) {
  return <td className={cn("px-3 py-2 align-middle", className)} {...props} />;
}

/** Numbers align to the end and use tabular figures so columns of amounts line up in both directions. */
export function TableNumberCell({ className, ...props }: TdHTMLAttributes<HTMLTableCellElement>) {
  return <td className={cn("px-3 py-2 text-end align-middle tabular", className)} {...props} />;
}
