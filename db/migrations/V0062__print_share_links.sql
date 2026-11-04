-- Print share links (roadmap 5.9, ADR-0022 amendment): a rendered PDF opened without signing in, for WhatsApp and the
-- like. The token is handed out once; only its hash is kept. Links expire, can be revoked and count their openings.
CREATE TABLE app.prt_share_links (
  tenant_id      uuid NOT NULL,
  id             uuid NOT NULL,
  company_id     uuid NOT NULL,
  render_id      uuid NOT NULL,
  document_type  text NOT NULL,
  document_id    uuid,
  token_hash     text NOT NULL,
  channel        text NOT NULL DEFAULT 'link' CHECK (channel IN ('link', 'whatsapp')),
  recipient      text,
  expires_at     timestamptz NOT NULL,
  revoked_at     timestamptz,
  open_count     integer NOT NULL DEFAULT 0 CHECK (open_count >= 0),
  last_opened_at timestamptz,
  created_by     uuid,
  created_at     timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, render_id) REFERENCES app.prt_renders (tenant_id, id)
);
CREATE UNIQUE INDEX prt_share_links_token_idx ON app.prt_share_links (tenant_id, token_hash);
CREATE INDEX prt_share_links_document_idx ON app.prt_share_links (tenant_id, document_type, document_id, created_at DESC);
CALL app.enable_tenant_rls('app.prt_share_links');
