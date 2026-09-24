# Backup, restore and verification

Operator runbook (roadmap 10.3, ADR-0024 "Backups and recovery"). The commands live in the migrator (`src/Host/Quicker.Migrator`), which reads its connections from `QUICKER__DB__OWNERCONNECTION`, `QUICKER__DB__APPPASSWORD` and `QUICKER__DB__APPCONNECTION`.

## What a backup is

`backup --out FILE` writes a `pg_dump` of the whole database (every tenant) in PostgreSQL's custom format, plus `FILE.manifest.json`:

* the row count of every table, counted **in the same snapshot the dump was taken in** (the command exports a snapshot, `pg_dump` imports it), so the counts are exactly what the file holds even while people keep working;
* the file's SHA-256, its size, the time of the snapshot, the source database, the owning role, the server's major version and the last migration applied.

Keep the two files together. A backup never overwrites an existing file.

What the database backup does **not** contain, by design:

| Not in the dump | Where it lives | How it is protected |
|---|---|---|
| Attachment files and item images | object storage (`Quicker:Storage`) | bucket versioning or the provider's own backup |
| Audit anchors | the anchor store (`Quicker:Audit:Anchoring`, S3 object lock in production) | write-once retention; they must survive the database, that is their purpose |
| Roles (`quicker_owner`, `quicker_app`) | the PostgreSQL cluster | recreated by `restore` (and `migrate`) |

Continuous protection (point-in-time recovery from WAL, RPO 5 minutes) is the managed database's (ADR-0024); the logical backup is the portable, verifiable copy on top of it.

## Prerequisites

* **A role that sees every tenant.** Row-level security is forced on every tenant table, so the backup role must be a superuser or have `BYPASSRLS`; the command refuses any other role before writing anything (a dump through row security would silently miss rows).
* **Client tools at least as new as the server.** `pg_dump` refuses a newer server. The command looks for the tools of the server's major version: `--pg-bin DIR` or `QUICKER__BACKUP__PGBIN` first, then `/usr/lib/postgresql/<major>/bin` (Debian/Ubuntu), then the `PATH`, and says which version it needs when none fits.

## Commands

```
# Back up (the default name carries the UTC time)
make backup                                   # or: dotnet run --project src/Host/Quicker.Migrator -- backup --out backups/quicker.dump

# Restore into a NEW database and prove it
make restore FROM=backups/quicker-20260924T115035Z.dump DB=quicker_restore_0924

# Run the invariant harness on a live database (every active tenant, or one)
make verify                                   # or: make verify TENANT=demo
```

`restore --from FILE --database NAME`:

1. refuses a file whose SHA-256 no longer matches its manifest (changed, truncated, or copied without its manifest);
2. refuses a target database that already exists: a restore never writes over data;
3. refuses to run as a role other than the one that owned the objects, and on a server older than the source;
4. creates the roles if the cluster lacks them, creates the database, and restores it in **one transaction** (`pg_restore --single-transaction --exit-on-error`);
5. counts every table again and compares it with the manifest, table by table;
6. runs the invariant harness over every active tenant of the restored database: balanced entries, trial balance zero, balances equal to lines, audit chain intact against its anchors, tenant isolation, gapless numbering, stock equal to its ledger, inventory equal to the GL, GRNI, payables and bank equal to their subledgers.

The command exits 0 only when every table matches and every check passes. Nothing is dropped on failure: the database stays for inspection. The audit-chain check reads the anchor store, so run `restore` and `verify` with the same `Quicker:Audit:Anchoring` settings as the API; with other settings an anchored chain reports `anchor_mismatch` ("cannot be read back").

To switch the system over to a restored database, point `QUICKER__DB__OWNERCONNECTION` and `QUICKER__DB__APPCONNECTION` of the API and worker at it and restart them.

## Drill record

| Date | Source | Dump | Backup | Restore + counts + harness | Result |
|---|---|---|---|---|---|
| 2026-09-24 | local development database (the demo tenant and 379 tenants left by the browser journeys; 177 tables, 301,676 rows) | 20.6 MB | 7.6 s | 34 s | every table matched its manifest; the demo tenant passed all 11 checks; see open issue I4 for six journey tenants whose payables check fails in the source database as well |

The automated drill (`tests/Demo/Quicker.Demo.Tests/BackupRestoreTests.cs`) repeats it in CI on every change: the demo tenant backed up, restored into a new database with every row, all checks passing, and the harness failing once a posted line is removed from the restored books.
