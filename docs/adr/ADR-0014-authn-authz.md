# ADR-0014: Authentication and authorization model

Status: accepted (founder approved the blueprint on 2026-09-22) · Date: 2026-09-22

## Context

SSO/OIDC, MFA, least-privilege down to company, branch, warehouse, document type and field, segregation of duties, API keys, mobile approvals, and a UI that hides what a user cannot do.

## Decision

### Authentication

* **Global identity** (`control.users`): email, display name, locale, time zone, password hash (Argon2id), MFA methods (TOTP, WebAuthn passkeys, recovery codes), status. A user may belong to several tenants (`control.tenant_memberships`), useful for accountants and consultants.
* **Federation**: per-tenant OIDC connections (Microsoft Entra ID, Google Workspace, generic OIDC; SAML in M8) with just-in-time membership provisioning by email domain and optional group-to-role mapping.
* **Sessions**: access token is a JWT (10 minutes) containing `sub`, `tenant_id`, `membership_id`, `auth_time`, `amr` (MFA level); refresh tokens are opaque, rotated on every use, revocable per session and listed in the user's security page. Tenant policy can require MFA, session lifetime limits, IP allow-lists and password policy.
* **API keys** per tenant with scopes, expiry, IP allow-list, hashed at rest, last-used tracking; service accounts for integrations.
* **Step-up**: sensitive actions (reopening periods, changing posting profiles, exporting the tenant, changing bank details) require a recent MFA (`auth_time` within 5 minutes) when the tenant enables it.

### Authorization

* **Permissions** are code-defined strings `module.entity.action` (`sales.invoice.post`, `gl.period.reopen`, `inventory.count.approve`) with a catalogue exposed to the UI.
* **Roles** per tenant bundle permissions; templates ship for common jobs (Accountant, AR Clerk, Warehouse Operator, Sales Rep, Approver, Auditor read-only, Admin).
* **Role assignments** carry **record scopes**: any combination of companies, branches and warehouses (empty = all). Scope is enforced by query filters injected in the read path and by command validation in the write path; reporting injects the same predicates (row-level security in reports).
* **Document-type rules**: a role may be limited to specific document types per action (create, approve, post, reverse, export).
* **Field-level rules**: per role and entity, fields can be `hidden`, `read_only` or `editable`; hidden fields are stripped from API responses and exports, and writes to them are rejected. The UI reads the same rules to hide/disable controls.
* **Amount limits** are workflow rules (ADR-0020), not permissions, so they can involve approvals.
* **Segregation of duties**: `sod_rules` list conflicting permission pairs (create supplier vs approve supplier payment, post journal vs reopen period, ...). Saving a role or assignment that creates a conflict warns (or blocks, per tenant setting) and records an acknowledged exception with a reason; a standing SoD report lists all users with conflicts.
* **Effective permissions** are computed and cached per membership with a version stamp invalidated on any role change; the UI receives the effective set at login.

### Audit

Login, MFA changes, failed attempts, role changes, scope changes, API key use and step-up events go to the audit log (ADR-0015). Break-glass: platform operators have no standing access to tenant data; support access is granted by the tenant admin for a time window and audited.

## Alternatives considered

* **External identity server (Keycloak, Auth0).** Solid, but an extra system to run on-premise and a dependency for every install; ASP.NET Core's identity primitives plus OpenIddict-style token issuing cover the needs, and federation gives customers their own IdP.
* **Pure RBAC without scopes.** Cannot express "AR clerk for branch Basra only".
* **Attribute-based policies as code.** Flexible but opaque to admins; scoped roles plus field and document-type rules cover ERP needs and stay configurable in the UI.

## Consequences

* One authorization path for UI, API and reports.
* Field- and scope-level rules make least privilege real; SoD makes auditors happy.
* Global identities plus tenant memberships model real accounting-firm usage from day one.
