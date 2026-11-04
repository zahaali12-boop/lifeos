-- An operator can discard a dead-lettered outbox message after deciding its effect is not wanted (or was applied by
-- hand): the row stays with who discarded it, when and why, and later events of its aggregate stop waiting for it.
ALTER TABLE ops.outbox_messages
  ADD COLUMN discarded_at timestamptz,
  ADD COLUMN discarded_by text,
  ADD COLUMN discard_reason text,
  ADD CONSTRAINT outbox_messages_discard_check CHECK (
    (discarded_at IS NULL) = (discarded_by IS NULL) AND (discarded_at IS NULL) = (discard_reason IS NULL)
    AND (discarded_at IS NULL OR dead_at IS NOT NULL));

-- The per-aggregate ordering check looks for earlier messages still owed: neither published nor discarded.
DROP INDEX ops.outbox_messages_aggregate_idx;
CREATE INDEX outbox_messages_aggregate_idx ON ops.outbox_messages (aggregate_id, seq) WHERE published_at IS NULL AND discarded_at IS NULL;
CREATE INDEX outbox_messages_discarded_idx ON ops.outbox_messages (discarded_at) WHERE discarded_at IS NOT NULL;
