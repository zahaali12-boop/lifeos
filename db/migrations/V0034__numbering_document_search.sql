-- Document search (GET /api/v1/numbering/documents) matches any part of an issued number ("00027", "2026-000"), so the
-- allocation texts get a trigram index (pg_trgm is installed by V0001); other modules read a document's number by its
-- id (a journal entry's source document), so the allocations are indexed by document too.
CREATE INDEX num_allocations_text_trgm_idx ON app.num_allocations USING gin (text gin_trgm_ops);
CREATE INDEX num_allocations_document_idx ON app.num_allocations (tenant_id, document_id);
