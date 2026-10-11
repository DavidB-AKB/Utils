# Deployment SQL

Provisioning for a `ServicesDb` deployment. Three re-runnable scripts, applied in order by a
privileged login (the Postgres superuser, or whatever role owns the database).

```bash
psql -v runtime_password=… -v migrator_password=… -f sql/01-roles.sql
# …then run every service's EF migrations as app_migrator…
psql -v runtime_role=app_runtime -v migrator_role=app_migrator -f sql/02-runtime-grants.sql
psql -f sql/03-append-only-audit.sql
```

**02 and 03 must be re-run after every migration**, and the ordering above is not arbitrary: both
discover what they act on by inspecting the live schema, so there has to be something there to find.
`03` fails loudly rather than silently doing nothing if it finds no audit tables — because a
provisioning step that prints nothing looks exactly like one that worked.

## Why there are two logins

The application runs as `app_runtime`; migrations run as `app_migrator`, which **owns** the schemas
and tables. That split is what the whole append-only mechanism rests on:

- The runtime login holds `INSERT`/`SELECT` on audit tables and not `UPDATE`/`DELETE`/`TRUNCATE`.
- Because it does not *own* the tables, it also cannot disable the triggers that enforce that.

Collapse the two into one login and both halves evaporate at once.

## The `DENY` correction

The original design (see `Utils/CLAUDE.md`) called for `DENY UPDATE, DELETE`, reasoning that `DENY`
"survives the login later picking up a broader role". That is true in SQL Server. **Postgres has no
`DENY` at all** — its privilege system is purely additive: `GRANT` adds, `REVOKE` removes a prior
grant, and nothing expresses "never allow this even if granted later".

Since every service here is Postgres-only, the SQL as documented could not have run (it also used
SQL Server bracket identifiers). More importantly, the *property* it promised was unavailable from
the privilege system, so the revokes in `02` are necessary but not sufficient — correct for the roles
as configured, and silently undone by a later `GRANT`.

`03` closes that with a `BEFORE UPDATE OR DELETE` row trigger and a separate `BEFORE TRUNCATE`
statement trigger on every `AuditEvents` table. A trigger applies to **every** role, including the
table owner and a superuser. Run both scripts; neither alone is the control.

Verified end to end by `Utils.Tests/AppendOnlyGrantTests.cs`, which provisions a real database and a
real unprivileged login and then tries to rewrite history as that login — including the case where
somebody grants the runtime role `UPDATE` afterwards and the trigger is the only thing left refusing.

## Three things that are easy to get wrong

- **`TRUNCATE` is not covered by withholding `DELETE`.** It is a privilege of its own *and* it fires
  no row-level triggers, so it needed both an explicit revoke and its own statement-level trigger.
  Omitting it leaves a one-statement way to erase the entire trail.
- **`ALTER DEFAULT PRIVILEGES` is not optional.** Without it the first request after an upgrade fails
  with `permission denied for table` on whatever the migration just added — a failure that looks like
  an application bug and is not.
- **Schemas and audit tables are discovered, not listed.** A schema belongs to a service exactly when
  it holds that service's own `__EFMigrationsHistory` table, which is the workspace's shared-database
  convention rather than a heuristic. The hand-maintained list this replaced was already two schemas
  out of date (it predated `contacts` and `tasks`) while looking complete.

## What this is not

**Tamper-evidence.** A superuser can drop a trigger and rewrite history, and nothing here records
that. Evidence needs a hash chain over the rows or an append-only WAL archive, and is worth reaching
for only if it becomes a stated requirement.

**A deployment.** There are still no container images, no migration runner and no composition — see
`Utils` issue #4. This is the part of that work which could be written, version-controlled and
*tested* before any of it exists, because it is the only layer of append-only enforcement that is an
actual security control.

**RelishYourChef's** audit table lives in its own `RelishYourChefDb`, so it needs `03` run against
that database too. `02` does not apply there: it has no separate runtime login today.
