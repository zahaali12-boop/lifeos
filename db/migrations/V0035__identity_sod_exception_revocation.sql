-- A segregation-of-duties exception can be revoked before it expires; the row stays, with who revoked it, when and
-- why, so the history of what was allowed remains readable.
ALTER TABLE app.idn_sod_exceptions
  ADD COLUMN revoked_at timestamptz,
  ADD COLUMN revoked_by uuid,
  ADD COLUMN revoke_reason text,
  ADD CONSTRAINT idn_sod_exceptions_revocation_check CHECK ((revoked_at IS NULL) = (revoked_by IS NULL) AND (revoked_at IS NULL) = (revoke_reason IS NULL));
