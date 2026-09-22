# ADR-0012: API design: REST, OpenAPI 3.1, versioning, pagination, webhooks

Status: proposed · Date: 2026-09-22

## Context

Everything the UI can do, the documented API can do; no data lock-in; idempotent mutations; webhooks and bulk import/export.

## Decision

### Style

* **REST over HTTPS with JSON**, resource-oriented, under `/api/v1`. Commands that are not CRUD are sub-resources with verbs in the path: `POST /sales-invoices/{id}/post`, `POST /sales-invoices/{id}/reverse`, `POST /purchase-orders/{id}/approve`.
* **OpenAPI 3.1** generated from code at build, published at `/api/v1/openapi.json` and rendered docs at `/docs/api`. Contract tests fail the build if a breaking change (removed field, changed type, removed endpoint) is introduced without a version bump (`oasdiff` in CI).
* The web UI uses only the generated client (`packages/api-client`), so every UI capability is by construction a public API capability.

### Conventions

* Ids are UUIDs; lookups also accept `code`/`number` with `?by=code`.
* **Pagination**: cursor-based (`?cursor=&limit=`), stable ordering by `(sort key, id)`; totals on request (`?include=total`) because counting is expensive on large tables.
* **Filtering**: a small, documented filter language in `?filter=` (`status eq 'posted' and postingDate ge 2026-01-01 and customer.code in ('C001','C002')`) compiled server-side to parameterised SQL with an allow-list of fields per resource; full-text `?q=`.
* **Field selection and expansion**: `?fields=` and `?expand=lines,customer` with documented depth limits.
* **Errors**: RFC 9457 problem details, stable `code` (`stock.unavailable`, `period.closed`, `credit.limit_exceeded`), field-level `errors[]`, and a `why` object for business blocks (rule, threshold, actual values, approval route).
* **Idempotency-Key** required on `POST`/`PATCH` mutations (ADR-0009); `ETag`/`If-Match` on updates.
* **Bulk**: `POST /bulk/{resource}` with NDJSON body up to 10 MB, processed as a job with a per-row result file; `GET /export/{resource}` streams CSV/NDJSON with the same filter language; full tenant export produces a bundle (all tables as NDJSON plus attachments manifest).
* **Long operations** return `202` with a job resource (`/jobs/{id}`) and support server-sent events for progress.
* **Rate limits** per tenant and API key (token bucket; headers `RateLimit-*`), with higher limits for bulk endpoints.
* **Versioning**: URL major version; additive changes without version bump; deprecations announced in headers (`Deprecation`, `Sunset`) with at least 12 months overlap.
* **Localization**: `Accept-Language` selects labels in responses that carry display text; monetary values are returned as strings with currency codes (never floats); dates ISO 8601.
* **Scopes**: API keys and OAuth tokens carry scopes mirroring permissions; the same authorization code path serves UI and API.

### Webhooks

Subscriptions per tenant (`event types, URL, secret, active, filters`), HMAC-SHA256 signature header, timestamp to prevent replay, retries for 24 hours with backoff, delivery log and replay, and a "test delivery" button. Event catalogue published with the OpenAPI document (AsyncAPI-style section).

### Semantic query endpoint

`POST /reports/query` accepts a report definition (model, dimensions, measures, filters, pivot, sort, limit) and returns rows plus drill keys; the same endpoint powers the report builder, dashboards and the AI layer (ADR-0021, ADR-0028).

## Alternatives considered

* **GraphQL.** Attractive for the UI, but authorization at field level over a huge graph, N+1 pitfalls and the lack of an "API-first" contract for non-technical integrators outweigh it. The semantic query endpoint covers ad-hoc shapes.
* **gRPC.** Great internally; not what integrators expect for an ERP.
* **Offset pagination.** Simpler but degrades on large tables and skips/duplicates rows under concurrent inserts.

## Consequences

* One contract for UI, integrators and AI; contract tests keep it honest.
* Generated client and server validation from the same source means the UI never drifts from the API.
