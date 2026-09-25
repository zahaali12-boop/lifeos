# ADR-0010: Domain events, transactional outbox and background workers

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

Modules react to each other (commission accrual on invoice, search indexing, reporting projections, webhooks, notifications) without coupling and without losing events; heavy work (large cost re-applications, statement imports, exports, PDF, scheduled reports, revaluation runs) must leave the request path; on-premise installs must not require a broker.

## Decision

### Events

* **Domain events** are in-process and synchronous, raised by aggregates and handled within the same transaction by handlers in the same module (for example `InvoicePosted` → update customer statistics). They never cross module walls.
* **Integration events** are the module's public facts, declared in `<Module>.Contracts`, versioned (`SalesInvoicePosted.v1`), carrying ids and the minimal payload consumers need. They cross module walls only through the outbox.

### Transactional outbox

* `ops.outbox_messages (id uuid v7, tenant_id, occurred_at, event_type, event_version, aggregate_type, aggregate_id, payload jsonb, correlation_id, causation_id, actor, published_at, attempts, next_attempt_at, last_error)`, inserted in the business transaction.
* The worker's dispatcher claims batches with `FOR UPDATE SKIP LOCKED` ordered by `occurred_at`, invokes in-process handlers (projections, notifications, search, webhooks), records success in `ops.inbox (handler, event_id)` per handler for exactly-once effect, and marks `published_at`. Failures back off exponentially (1 s → 1 h) and move to a dead-letter state after 20 attempts with an operator alert; a UI page lists and retries dead letters.
* Ordering is guaranteed per aggregate (a partitioned claim by `aggregate_id` hash), not globally.
* `LISTEN/NOTIFY` wakes the dispatcher immediately after commit so projections land within milliseconds; polling every 2 s is the fallback.
* Published messages older than 30 days move to an archive partition.

### Jobs and schedules

* `ops.jobs (id, tenant_id, type, payload, priority, state, idempotency_key, run_after, attempts, locked_by, heartbeat_at, progress jsonb, result jsonb, error)`. Workers claim with `SKIP LOCKED` by priority and `run_after`; heartbeats let a crashed worker's jobs be reclaimed.
* `ops.schedules` stores cron expressions per tenant and job type (recurring invoices, depreciation, scheduled reports, revaluation reminders, backups); a scheduler tick (leader-elected through an advisory lock) enqueues jobs due.
* Jobs report progress; the UI subscribes (server-sent events) so users see long operations finish.
* Webhooks are jobs: `WebhookDelivery` with HMAC-SHA256 signature, timestamp, retries with backoff for 24 hours, delivery log per subscription, and a replay button.

### Worker topology

* `Quicker.Worker` runs the dispatcher, job executors, scheduler and PDF renderer; scale by adding instances. On a single-node on-premise install, the API host can embed the worker (`--embed-worker`).

## Alternatives considered

* **Message broker (RabbitMQ, Kafka, SQS).** More infrastructure to run on-premise and for a solo team; PostgreSQL delivers the needed throughput (thousands of events per second per node) with transactional guarantees and no dual-write problem. Revisit only with measurement.
* **Hangfire/Quartz.** Fine libraries, but each brings its own storage model and dashboard; a purpose-built table with `SKIP LOCKED` is under 500 lines and fully under our tenancy and audit rules.
* **Synchronous cross-module calls for reactions.** Couples modules and lengthens the posting transaction; reserved for financial consistency only (ADR-0001).

## Consequences

* No event is lost: it is committed with the data that produced it.
* All async behaviour is observable in two tables and one UI page.
* Projections are eventually consistent (milliseconds to seconds); the UI reads balances and documents from the transactional tables, and reports from projections, and says which is which.
