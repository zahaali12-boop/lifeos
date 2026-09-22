# Progress log

The single place a new session reads first (after `CLAUDE.md`). Keep it current: what is done, what is in progress, what is next, what is open.

## Status

**Phase 0 (Blueprint): approved by the founder on 2026-09-22 (defaults accepted for Q1–Q8).**

**M1 Foundations: in progress** (see `docs/ROADMAP.md`). Development environment note: this session runs on Ubuntu 24.04 with .NET 10.0.112 SDK (apt), Node 22 + pnpm, a local PostgreSQL 16 cluster and Docker (image pulls from Docker Hub are blocked by the egress policy, so tests use the `QUICKER_TEST_CONNECTION` override instead of Testcontainers here; CI uses a postgres:17 service container).

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

- M1 slice 1.1 Repository and toolchain: legacy Life OS app moved to `legacy/lifeos/` (Q2 default), ADRs marked accepted.

## Next

1. M1 slice 1.1: .NET solution, kernel, migrator, API host skeleton, web app skeleton, Compose, Makefile, CI.
2. M1 slices 1.2–1.12 in roadmap order.

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
