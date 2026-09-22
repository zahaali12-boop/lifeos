# Progress log

The single place a new session reads first (after `CLAUDE.md`). Keep it current: what is done, what is in progress, what is next, what is open.

## Status

**Phase 0 (Blueprint): complete, awaiting founder approval.** No application code exists yet, by design.

Branch: `claude/quicker-erp-founding-arch-4cq18i` (all Phase 0 work). Default branch: `main`.

## Done

| Date | Item |
|------|------|
| 2026-09-22 | `docs/PRODUCT_BRIEF.md`: founder's brief saved verbatim. |
| 2026-09-22 | `docs/ASSUMPTIONS.md`: 8 questions for the founder (Q1–Q8) and assumptions A-001…A-063. |
| 2026-09-22 | `docs/ARCHITECTURE.md`: principles, stack, repository layout, runtime topology, tenancy, module map, posting and costing pipelines, security, reporting, front end, conventions, quality gates. |
| 2026-09-22 | `docs/adr/`: 30 ADRs (index in `docs/adr/README.md`), all `proposed`. |
| 2026-09-22 | `docs/DOMAIN_MODEL.md`: full entity model for every module with 22 Mermaid ER diagrams (all validated with the Mermaid parser) and the cross-module invariants. |
| 2026-09-22 | `docs/POSTING_RULES.md`: account roles and the journal matrix for sales, purchasing, inventory, banking, multi-currency, fixed assets, journals, intercompany, closing, reversals and rounding; scenario-to-rule index. |
| 2026-09-22 | `docs/ROADMAP.md`: M1–M10 as vertical slices with acceptance criteria and the hard scenarios each proves; launch gate; deferred list. |
| 2026-09-22 | `docs/GLOSSARY.md`, `README.md`, `CLAUDE.md`. |

## In progress

Nothing. Waiting for the founder's review of the blueprint.

## Next

1. Founder answers Q1–Q8 in `docs/ASSUMPTIONS.md` (or accepts the defaults) and approves the blueprint. ADR statuses move to `accepted`.
2. Start **M1 Foundations**, slice 1.1 (repository and toolchain), then 1.2 (database foundation) and 1.3 (kernel), in that order; see `docs/ROADMAP.md`.
3. First M1 commit moves the legacy Life OS files to `legacy/lifeos/` if Q2 is answered "move".

## Open issues and decisions pending

| ID | Issue | Owner | Blocking? |
|----|-------|-------|-----------|
| Q1 | Product name and brand | founder | no (default: Quicker) |
| Q2 | Fate of the pre-existing Life OS files in this repository | founder | no (default: move to `legacy/lifeos/`) |
| Q3 | Hosting region and data residency | founder | no (design is region-agnostic) |
| Q4 | Launch vertical / pilot customer | founder | no (default: trading and distribution) |
| Q5 | Commercial model | founder | no |
| Q6 | Identity providers | founder | no (default: built-in + Entra ID + Google) |
| Q7 | Demo companies' functional currencies and rate types | founder | no |
| Q8 | Arabic digit default | founder | no |

## How to resume in a new session

1. Read `CLAUDE.md`, then this file, then `docs/ROADMAP.md` for the current milestone and slice.
2. Re-read `docs/PRODUCT_BRIEF.md` once per session; skim `docs/ASSUMPTIONS.md` for anything marked `changed`.
3. Consult the ADR that governs the area you are about to touch before writing code.
4. Work in small commits; update this file and the relevant docs in the same commit as the code.
5. At the end of a milestone, write the report to the founder (built, how to see it, test results, decisions, gaps, next) and pause for review.
