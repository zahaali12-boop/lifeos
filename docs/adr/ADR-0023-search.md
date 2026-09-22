# ADR-0023: Global search

Status: proposed · Date: 2026-09-22

## Context

Global search across every record from the command palette, in English and Arabic, respecting permissions, fast enough for typeahead, on-premise friendly.

## Decision

* A per-tenant **search index table** `srch_documents` (`tenant_id`, `entity_type`, `entity_id`, `company_id`, `branch_id`, `title`, `subtitle`, `keywords`, `tsv tsvector`, `trigrams`, `updated_at`), maintained by outbox projections for every searchable entity (partners, items, documents by number, accounts, users, reports, settings pages).
* PostgreSQL full-text search with a custom configuration: `simple` dictionary plus unaccent, Arabic normalisation (strip tashkeel, normalise alef/ya/ta-marbuta variants, Arabic-Indic digits to Western), and `pg_trgm` GIN indexes for fuzzy and prefix matching on codes and numbers.
* Query pipeline: exact code/number match first (fast path), then full-text ranked, then trigram similarity; results grouped by entity type with permission filtering applied in the query (scope predicates) and by a final authorization check.
* Command palette also indexes actions and navigation targets (static, per role).
* Latency budget: p95 under 100 ms for typeahead on a tenant with 1M indexed rows (verified in M10).

## Alternatives considered

* **Meilisearch / Typesense / OpenSearch.** Excellent relevance and typo tolerance, but another service for on-premise and another place where tenant isolation must be proven; revisit if relevance or latency measurements demand it. The projection design allows swapping the store.

## Consequences

* No extra infrastructure; isolation and permissions use the same mechanisms as everything else.
