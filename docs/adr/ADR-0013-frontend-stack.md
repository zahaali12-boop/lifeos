# ADR-0013: Front-end stack, design system, grids, i18n and RTL

Status: proposed · Date: 2026-09-22

## Context

The product must feel like the best modern SaaS: keyboard-first, command palette, global search, saved views, bulk actions, virtualized grids, WCAG 2.2 AA, English and Arabic with full RTL from the first screen, and a mobile web scanning app.

## Decision

* **React 19 + TypeScript** (strict), **Vite** build, single application in `apps/web` with route groups for desktop, mobile and the scanning app (PWA, camera via `BarcodeDetector` with ZXing fallback, offline scan queue in IndexedDB).
* **TanStack Router** (typed routes), **TanStack Query** (server state, optimistic updates, cache keyed by tenant), **TanStack Form**, **TanStack Table + Virtual** for grids and the pivot component.
* **Design system** in `packages/ui`: design tokens (colour with AA-contrast pairs, spacing, typography with Arabic-capable font stack: Inter + IBM Plex Sans Arabic, motion), components on **Radix UI primitives** (accessible by construction), **Tailwind CSS v4** with tokens as CSS variables, **Stylelint** rule forbidding physical properties (`margin-left`) in favour of logical ones (`margin-inline-start`) so RTL is automatic. Storybook documents every component with RTL and dark-mode variants; axe runs on every story in CI.
* **i18n**: `i18next` with ICU message format (plurals, gender, Arabic plural categories), `Intl` for numbers/dates/currencies with a per-user digit preference (Western or Eastern Arabic digits), locale-aware collation for sorting. All strings are keys; a CI check fails on untranslated Arabic keys (ASSUMPTIONS A-063).
* **Keyboard-first**: a global shortcut registry, command palette (Ctrl/Cmd+K) exposing navigation, actions, recent records and global search; a "?" overlay lists shortcuts per screen; list pages support j/k navigation, Enter to open, and Ctrl+Enter to save on forms.
* **Data grid**: virtualized rows and columns, sticky headers and pinned columns, column chooser, resize/reorder, inline editing with validation, grouping and aggregates, multi-select with bulk actions, saved views (filters, columns, sort, grouping) private or shared, keyboard cell navigation, export of the current view. Cells that display money show the currency and drill to the source.
* **Charts**: Apache ECharts (canvas performance for large series, RTL text support); colours from the design tokens.
* **Forms**: schema-driven from OpenAPI plus server validation problem details mapped to fields; document forms support draft autosave and conflict handling (ETag).
* **Accessibility**: WCAG 2.2 AA target; focus management, ARIA from Radix, reduced-motion support, minimum 44px touch targets on mobile, colour never the only signal.
* **Performance budgets**: initial route under 250 KB gzip, route-level code splitting, list pages render 100k rows through virtualization without pagination in the client, p95 interaction under 100 ms.
* **Testing**: Vitest + Testing Library for components, Playwright for flows (including RTL snapshots), Storybook interaction tests.

## Alternatives considered

* **Commercial grid (AG Grid Enterprise, DevExtreme).** Fast start, but licensing per developer, RTL and accessibility gaps in pivot/tree modes, and a hard dependency for the product's strongest selling point (reporting). Building on TanStack keeps control.
* **Angular / Vue.** Both viable; React has the largest hiring pool and ecosystem for the components we need.
* **Server-rendered UI (Blazor, Razor).** Would collapse the API-first guarantee (UI logic that is not an API) and lacks the ecosystem for grids and charts at this level.

## Consequences

* A real design system is upfront work in M1 but pays off in every later screen.
* RTL correctness is enforced by lint and tests, not by memory.
* The generated API client makes the UI a thin, typed consumer of the public API.
