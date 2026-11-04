# ADR-0019: Custom fields, attachments, comments and saved views

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

Admins add fields to any entity without code; every document has comments, attachments and an activity timeline; users save views; all of it must appear in the API, imports/exports, print templates and reports, under permissions.

## Decision

### Custom fields

* `cf_definitions`: tenant, entity type, key, labels (EN/AR), data type (text, long text, number, money, date, datetime, boolean, select, multi-select, lookup to another entity, user, attachment), validation (required, regex, min/max, options), default, `is_indexed`, `is_reportable`, `show_on_list`, `show_on_print`, group/section, order, `applies_when` (optional condition, for example only for item category X), field-level permission defaults.
* Storage: `custom_fields jsonb` column on every extensible table, validated on write against the definitions (typed coercion, options), GIN-indexed; indexed fields also get a generated expression index per definition (created by a job when `is_indexed` is set).
* Exposure: appear in OpenAPI as `customFields: {key: value}` with the schema generated per tenant on `/api/v1/meta/custom-fields`; the filter language accepts `customFields.key`; imports/exports include them; the semantic layer adds them as dimensions or measures when `is_reportable`; print templates access them.
* Line-level and header-level fields are separate entity types (`sales_order`, `sales_order_line`).
* Deleting a definition archives it (data stays in JSONB, hidden); renaming keeps the key.

### Attachments, comments, activity

* `col_attachments`: polymorphic (`entity_type`, `entity_id`), file metadata, object storage key, content hash, virus scan status, uploaded by, visibility (internal | shared with partner portal later), and version chain for replaced files.
* `col_comments`: threaded, mentions (`@user`) producing notifications, edit history, internal flag.
* `col_activities`: the unified timeline per record: system events (status changes, postings, approvals, prints, emails sent) from the audit log plus human notes, calls, meetings and tasks (with due dates and assignees, powering CRM activities on partners and opportunities).
* `col_notifications` with per-user channel preferences (in-app, email, mobile push later) and digest options; the workflow engine and mentions are the main producers.

### Saved views

`ux_saved_views`: entity type, name, owner, shared (tenant | role | private), definition (filters, columns, sort, grouping, pivot), default flag per user. Exposed in the API so integrators can reuse view filters.

### Numbering, print and other admin configuration

Follow the same pattern: configuration is versioned data with audit trail, editable in the admin UI, exported/imported as part of a tenant configuration bundle (also used for industry templates).

## Alternatives considered

* **Entity-attribute-value tables.** Flexible but slow to query and awkward to index; JSONB with metadata-driven expression indexes is the modern PostgreSQL answer.
* **Schema-per-tenant ALTER TABLE for custom columns.** Real columns, but thousands of tenant-specific DDL changes are an operational hazard in a shared schema.

## Consequences

* Custom fields are first-class across UI, API, import, print and reports from the moment they are defined.
* Collaboration features are one polymorphic module reused by every document.
