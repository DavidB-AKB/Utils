# Handover runbook

For whoever operates a handed-over instance. The customer takes over Postgres, a container runtime,
secrets, backups, TLS and upgrades — so this is part of the deliverable, not documentation about it.

**Every command here has been run.** Where something is not yet possible, it says so rather than
describing a plausible-looking command that does not exist.

---

## What is not here yet

There are **no container images, no migration runner and no composition** — see `Utils` issue #4.
Until that lands, "deploy" means running the hosts yourself (`dotnet run`, or `dotnet publish` and a
systemd unit per host). Everything else below is real today.

---

## 1. Provision from scratch

### 1.1 Postgres

One Postgres 17 instance, one database. Every service lives in its own schema inside it, with its
own `__EFMigrationsHistory` table — that is why migrations from eight services can share one
database without fighting.

```bash
createdb -U postgres ServicesDb
```

`RelishYourChef`, if deployed, needs its own `RelishYourChefDb`. No other service does.

### 1.2 Roles

```bash
psql -U postgres -d ServicesDb \
  -v runtime_password="$(openssl rand -base64 32)" \
  -v migrator_password="$(openssl rand -base64 32)" \
  -f deploy/sql/01-roles.sql
```

Two logins, and the split is load-bearing rather than tidy: `app_migrator` **owns** the tables, which
is what stops `app_runtime` disabling the append-only triggers. Collapse them into one login and
both halves of the audit protection evaporate at once.

### 1.3 Migrations

Run as `app_migrator`, never as the runtime login. Each service, from its own repo:

```bash
dotnet ef database update \
  --project <Service>.Core --startup-project <Service> --context <Service>DbContext
```

`--context` is required in every repo that references `Permissions.Core`, because two `DbContext`s
are then visible to the design-time tools. `migrate-local-db.sh` at the workspace root does all of
them and is re-runnable; it is written for local development but the command shape is identical.

### 1.4 Grants and append-only protection

**Both, in this order, after migrations** — they inspect the live schema, so there has to be
something to find:

```bash
psql -U postgres -d ServicesDb \
  -v runtime_role=app_runtime -v migrator_role=app_migrator \
  -f deploy/sql/02-runtime-grants.sql
psql -U postgres -d ServicesDb -f deploy/sql/03-append-only-audit.sql
```

Expected output from the second: `Append-only triggers installed on 8 AuditEvents table(s).` It
**fails loudly** if it finds none, because a provisioning step that prints nothing looks exactly like
one that worked.

**Re-run both after every upgrade.** New tables need grants, and a new service's audit table needs
its triggers.

Verify:

```bash
# Should print 16 — two triggers (row + truncate) per audit table.
psql -U postgres -d ServicesDb -tAc \
  "SELECT count(*) FROM pg_trigger WHERE tgname LIKE 'utils_audit_append_only%' AND NOT tgisinternal;"

# Should print exactly: INSERT SELECT
psql -U postgres -d ServicesDb -tAc \
  "SELECT privilege_type FROM information_schema.role_table_grants
   WHERE grantee='app_runtime' AND table_schema='users' AND table_name='AuditEvents' ORDER BY 1;"
```

### 1.5 Secrets

Two values are required; the service refuses to start without them and tells you so, listing
everything missing at once (see `Utils/CLAUDE.md`'s "Secrets"):

| Key | Supply as env var | or as a file in the secrets directory |
|---|---|---|
| `ConnectionStrings:ServicesDb` | `ConnectionStrings__ServicesDb` | `ConnectionStrings__ServicesDb` |
| `Jwt:Secret` (≥32 chars) | `Jwt__Secret` | `Jwt__Secret` |

Optional, each with a working fallback: `Smtp:Host`/`Username`/`Password` (omit and notifications are
logged, not sent) and `Leads:Categorizer:Claude:ApiKey`.

**Prefer files.** An environment variable is readable from `/proc/<pid>/environ`, is inherited by
every child process, and shows up in crash dumps. The default directory is `/run/secrets`, override
with `Secrets:Directory`.

```bash
install -m 0400 -o app /dev/stdin /run/secrets/Jwt__Secret <<< "$(openssl rand -hex 32)"
```

**The connection string must use `app_runtime`, not `app_migrator`.** Using the migrator at runtime
is the single configuration mistake that silently removes the audit guarantee.

---

## 2. Rotate `Jwt:Secret`

Every service validates tokens with the same secret, so rotation is **not** per-service and there is
no overlap window in the current design: changing it invalidates every access token in flight.

1. Generate: `openssl rand -hex 32`.
2. Write it to every host's secrets directory.
3. Restart every host, including the one that issues tokens (`Users`).
4. Users are signed out and sign in again. Refresh tokens are database rows, not JWTs, so they
   survive — a client holding one recovers without the user re-entering a password.

Do all hosts together. A host still holding the old secret rejects tokens the others accept, which
presents as intermittent 401s rather than as a configuration problem.

---

## 3. Back up and restore

### 3.1 Take a backup

```bash
pg_dump -U postgres -d ServicesDb -Fc -f servicesdb-$(date +%F).dump
pg_dumpall -U postgres --roles-only -f roles-$(date +%F).sql
```

**Both files, and the second is the one everybody forgets.** A per-database dump does **not**
contain roles — verified: `pg_restore -l` on a `ServicesDb` dump lists zero `ROLE` entries. Restore
into a fresh cluster without the roles file and every `GRANT` in the dump fails on a role that does
not exist.

### 3.2 Restore

```bash
psql -U postgres -f roles-2026-10-16.sql          # first, into a fresh cluster
createdb -U postgres ServicesDb
pg_restore -U postgres -d ServicesDb servicesdb-2026-10-16.dump
```

**The append-only protection survives a restore — verified, not assumed.** After a dump/restore
cycle: all 16 triggers present, `app_runtime` holding exactly `INSERT, SELECT` on audit tables and
full access to business tables, an `INSERT` into an audit table succeeding and a `DELETE` refused
with `permission denied for table AuditEvents`.

That verification matters because the failure would be invisible: a restore that quietly dropped the
triggers would leave a system that looks identical and has no audit protection at all.

### 3.3 After any restore

Re-run `02` and `03` anyway. They are idempotent, they cost seconds, and they turn "the protection
probably came back" into "the protection is there".

---

## 4. Apply an upgrade

1. **Take a backup first** (§3.1). There is **no tested rollback** — see below.
2. Stop the hosts. The background services in §5 are the reason: a relay mid-publish during a schema
   change is the one thing here that can produce a duplicate side effect.
3. Run migrations as `app_migrator` (§1.3).
4. Re-run grants and triggers (§1.4). **Not optional** — a new table has no grants, and the first
   request after an upgrade would fail with `permission denied for table`, which looks like an
   application bug and is not.
5. Start the hosts. Each validates its secrets on startup and refuses to run misconfigured.

**Forward-fix only.** `Down()` migrations exist because EF generates them, but nothing in this
codebase has ever executed one — no test applies a migration and reverts it. Treat them as
documentation of intent. A release that goes wrong is fixed by another migration going forward, or
by restoring the backup from step 1. This is why step 1 is step 1.

A migration that **backfills an audit table** needs the trigger disabled explicitly, run, and
re-enabled. That friction is deliberate: rewriting audit history should be a visible act in a
reviewed migration, not an `UPDATE` that works because nothing stopped it.

---

## 5. Background services, and what a stalled one looks like

Each host runs these continuously. They fail quietly by design — none of them take a request — so
this is how to tell a stalled one from an idle one.

| Service | In | Does | Stalled looks like |
|---|---|---|---|
| `OutboxRelayService` | Users, Leads, Invoices, Contacts, Tasks, Comments | Leases rows from that service's `OutboxMessages` and publishes them | `OutboxMessages` rows with `CompletedAtUtc` null and `EnqueuedAtUtc` getting old |
| `WorkItemSweeper` | Tasks | Emits `workitem.due_soon`/`overdue`/`stale` | Overdue work items with `DueNotifiedAtUtc` null |
| `LeadSweeper` | Leads | Emits `lead.stale` | Undecided leads with `StaleNotifiedAtUtc` null and an old `CreatedAtUtc` |
| `NotificationRetryService` | Notifications | Retries failed deliveries | `NotificationDeliveries` with a rising `AttemptCount` |
| `InAppRetentionService` | Notifications | Prunes read in-app notifications | Only runs if `Notifications:InApp:RetentionDays` is set; logs that it is off otherwise |

One query answers "is anything stuck", across every service:

```sql
SELECT 'users' AS schema, count(*) FROM users."OutboxMessages"
  WHERE "CompletedAtUtc" IS NULL AND "DeadLetteredAtUtc" IS NULL AND "EnqueuedAtUtc" < now() - interval '15 minutes'
UNION ALL SELECT 'leads',    count(*) FROM leads."OutboxMessages"    WHERE "CompletedAtUtc" IS NULL AND "DeadLetteredAtUtc" IS NULL AND "EnqueuedAtUtc" < now() - interval '15 minutes'
UNION ALL SELECT 'invoices', count(*) FROM invoices."OutboxMessages" WHERE "CompletedAtUtc" IS NULL AND "DeadLetteredAtUtc" IS NULL AND "EnqueuedAtUtc" < now() - interval '15 minutes'
UNION ALL SELECT 'contacts', count(*) FROM contacts."OutboxMessages" WHERE "CompletedAtUtc" IS NULL AND "DeadLetteredAtUtc" IS NULL AND "EnqueuedAtUtc" < now() - interval '15 minutes'
UNION ALL SELECT 'tasks',    count(*) FROM tasks."OutboxMessages"    WHERE "CompletedAtUtc" IS NULL AND "DeadLetteredAtUtc" IS NULL AND "EnqueuedAtUtc" < now() - interval '15 minutes'
UNION ALL SELECT 'comments', count(*) FROM comments."OutboxMessages" WHERE "CompletedAtUtc" IS NULL AND "DeadLetteredAtUtc" IS NULL AND "EnqueuedAtUtc" < now() - interval '15 minutes';
```

**Dead-lettered rows need a human.** They are messages that failed their attempt ceiling and will
never be retried — silent by design, because the alternative is one poison message starving the
queue:

```sql
SELECT "EventType", "DeadLetterReason", "EnqueuedAtUtc" FROM tasks."OutboxMessages"
WHERE "DeadLetteredAtUtc" IS NOT NULL ORDER BY "EnqueuedAtUtc" DESC;
```

---

## 6. The drift cost this accepts

Each handed-over instance diverges: different version, different configuration, different data. That
is the ongoing price of the no-tenancy decision, paid in exchange for a much simpler data layer with
no tenant id in any table or index.

It **grows linearly with customers and stays invisible until the third one**, at which point
"reproduce the bug" means asking which version they are on and discovering nobody recorded it. Two
cheap things that keep it manageable, worth doing before the second handover rather than after the
third:

- **Record the deployed version per instance**, somewhere outside that instance.
- **Keep each instance's configuration in version control**, separately from this repository, so
  "what is different about theirs" is a diff rather than an investigation.
