# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

A shared library of small, generic, cross-cutting utilities for .NET 10 that have nothing to do with permissions/authz — created 2026-08-05 specifically to give those concerns a home separate from `Permissions.Core`. Before this repo existed, `Permissions.Core` was the one shared library every foundational service already referenced, so it was where cross-cutting code landed by default even when it had nothing to do with authorization (cursor pagination being the first example — see `Pagination.cs`). That's a naming/scope smell, not a load-bearing reason: this repo exists so "not permission-specific" and "not in `Permissions.Core`" mean the same thing going forward.

**Library-only, no host** — unlike `Users`/`Notifications`/`Comments`/`Permissions`, this repo has no `Program.cs`, no endpoints, no `DbContext` of its own, and never runs standalone. It's referenced the same way any of those repos' `.Core` libraries are (a `ProjectReference` from a sibling repo), just without the microservice `.Core`/host split, since there's no host to split off. Three projects:

- **`Utils/`** — the library itself (`Microsoft.NET.Sdk` class library, **zero package references** and no `FrameworkReference` to ASP.NET Core — nothing here should need either; if something needs ASP.NET Core types, that's a signal it belongs in `Permissions.Core` instead, which already has that dependency for its auth types).
- **`Utils.Data/`** — cross-cutting code that genuinely needs EF Core, kept out of `Utils` so a consumer wanting only cursor pagination doesn't drag EF Core in transitively. Same layering reasoning that split this repo out of `Permissions.Core` in the first place, applied one level down. It still defines no `DbContext` — it contributes an entity plus a `ModelBuilder` configuration extension to *someone else's* context. Depends on `Utils` and `Microsoft.EntityFrameworkCore`, nothing more; deliberately not on ASP.NET Core or `Permissions.Core`, since an audit primitive has no business knowing about HTTP or authorization.
- **`Utils.Tests/`** — xUnit.

## What belongs here vs. `Permissions.Core`

`Permissions.Core` is for permission/authz-specific cross-cutting code (JWT validation, `ClaimsPrincipal` extensions, strongly-typed-ID infrastructure that authz-adjacent services need) — see `Permissions/CLAUDE.md` and the top-level `CLAUDE.md`'s "Strongly-typed IDs"/"Cursor pagination" bullets for that history. **This repo is for everything else that's genuinely shared but has nothing to do with auth.** Same duplication bar applies as it always has in this workspace: don't add something here speculatively — extract only when a second (or third) service is about to duplicate real code, the same standard that justified `Permissions.Core` itself and, later, `Pagination.cs` moving out of it.

## Commands

From the repo root (where `Utils.slnx` lives):

```bash
dotnet restore
dotnet build
dotnet test                       # runs Utils.Tests (xUnit)
```

No `dotnet run` — there's no host. No EF Core migrations either: `Utils.Data` defines an entity and a `ModelBuilder` extension but owns no `DbContext`, so the `AuditEvents` table is created by whichever consuming service's migrations include it.

## Current contents

### `Utils`

- **`Pagination.cs`** — cursor (keyset) pagination primitive: `CursorPage<T>` (`{ Items, NextCursor }`), `PaginationDefaults.ClampLimit` (default 20, max 100), `PaginationCursor.Encode`/`Decode` (opaque base64 `"<sortKey><id>"` cursor). Moved here 2026-08-05 from `Permissions.Core/Pagination.cs`, where it originally landed only because `Permissions.Core` was the one library everyone already referenced — it has no relationship to permissions/authz. Used by every foundational service's list endpoints: `Users` (`GET /users`), `Permissions` (`GET /permissions`/`/roles`/`/assignments`), `Comments` (`GET /comments`), `Notifications` (`GET /rules`/`/templates`). See each service's own `CLAUDE.md` for how it orders its own list, and `Comments/CLAUDE.md`'s "Pagination + visibility filtering" section for a real interaction worth knowing about (pagination combined with a post-query authorization filter needs a top-up loop, not naive fetch-then-filter, or pages can look short when they aren't).

### `Utils.Data`

- **`Auditing/`** — the shared audit-trail primitive. `Permissions` and `Comments` each independently grew a near-identical `AuditEvent`/`IAuditLogger`/`AuditLogger` before this was extracted, and `RelishYourChef` would have made a third — real duplication, which is the bar. Four pieces:
  - **`AuditEvent`** — `{ Id, EventType, TargetType, TargetId, ActorSubjectType, ActorSubjectId, DetailsJson, OccurredAtUtc }` plus `MaxXLength` constants. `TargetType`/`TargetId` are opaque strings, since one log spans several entity kinds. Actor fields are **non-nullable**: every path that writes one already required a specific permission, so there's always a real actor and no system-triggered case to model.
  - **`IAuditLogger`/`AuditLogger<TContext>`** — generic over the consuming service's own `DbContext`, resolved through `Set<AuditEvent>()` so that context needs no interface or `DbSet` property. `Log(...)` only adds to the change tracker and never calls `SaveChangesAsync`, so the audit row commits atomically with whatever business write triggered it. Register as `services.AddScoped<IAuditLogger, AuditLogger<TheServicesDbContext>>()`.
  - **`AuditEventModelBuilderExtensions.ConfigureAuditEvents()`** — call from `OnModelCreating`. Applies key, max lengths, and the two indexes every consumer needs: `(TargetType, TargetId, OccurredAtUtc)` for "everything that happened to this entity" and `(ActorSubjectType, ActorSubjectId)` for "everything this actor did." No foreign key to anything, deliberately — an audit row has to outlive whatever it describes, and `TargetId` isn't always an entity id.
  - **`AuditEventQuery.ToPageAsync()`** — the shared cursor/ordering/paging half of an audit-log endpoint. Takes an already-filtered `IQueryable<AuditEvent>` so each service keeps its own filters. Newest-first with `Id` descending as the keyset tiebreak; `Permissions` and `Comments` previously used opposite tiebreak directions (each internally consistent, but different), unified here.

  **This is a shared shape and a shared writer, not a centralized audit database.** Each service maps `AuditEvent` into its own schema and owns its own rows. There is deliberately no cross-service audit query — "who was granted admin" is a `Permissions` question and "who read my direct messages" is a `Comments` question, and nothing has needed to ask both at once. **Event-type constants stay per-service** (`Permissions.Models.AuditEventTypes`, `CommentsService.Models.AuditEventTypes`): the events a service records are exactly as service-specific as its domain, so there's no shared vocabulary here.

## Messaging contract

`Utils/Messaging/FactPublishing.cs` — `FactEnvelope` (`MessageId`, `EventType`, `OccurredAtUtc`, `Recipient`, `Data`) and `IFactPublisher`. The contract a producing service's outbox relay publishes through and a consuming service implements to receive.

**Why it lives here rather than in either service.** Before it existed, `Users`' relay spoke its own `ITopicPublisher`/`OutboxMessage` and `Notifications`' inbound side spoke `IEventIngestionService`/`EventEnvelope`, so whatever hosted both had to hand-write a mapping between two near-identical shapes — and that mapping encoded the consumer's wire format in a third place nothing tested. With one shared contract, `Notifications` ships its own `IFactPublisher` (`AddNotificationsAsFactSubscriber`) and the host wires the two together with a single registration.

This does not weaken the "no references between foundational services" rule — it's what upholds it. Both services depend on an abstraction neither owns, which is dependency inversion; a project reference from one to the other is what the convention actually forbids. Nor does it weaken the outbox pattern: the guarantees are that the fact commits atomically with the business write and is delivered at-least-once afterwards, and neither depends on the transport being networked.

**`PublishAsync` returns nothing, deliberately.** The contract is "returns normally = handled, throws = retry." A consumer recognising a `MessageId` it has already processed must return normally, because a duplicate *is* the expected outcome of at-least-once delivery. Modelling that as a `false` return invites a publisher to treat it as failure and hand the message back to the queue, eventually dead-lettering something that was in fact delivered — so the shape makes that mistake unrepresentable rather than merely documenting against it.

## Leased queue

`Utils/Queueing/` (the contract) and `Utils.Data/Queueing/` (the EF implementation). Take / complete / return over work stored as rows, with lease expiry and dead-lettering. Built for `Users`' `OutboxRelayService`, which previously selected unpublished rows with no lease at all — two relay hosts would both publish the same rows, making it unsafe to scale horizontally.

- **`ILeasable`** — the contract a message entity implements. Lease columns live on the message row itself, so claiming is one conditional update with no side table to keep in sync.
- **`ILeasedQueue<TMessage>`** — `TakeAsync` / `CompleteAsync` / `ReturnAsync`. Complete and Return return `false` on a stale lease (expired and re-taken by another consumer) rather than silently stomping the current holder's work.
- **`DbLeasedQueue<TContext, TMessage>`** — generic over the consuming service's own context, resolved via `Set<TMessage>()`.
- **`ConfigureLeasable<TMessage>()`** — call in `OnModelCreating`; adds the take-query index, the dead-letter index, and the concurrency token the claim depends on.

**The two parts that are easy to omit and are what actually make it safe:**
- **Lease expiry.** A consumer that dies holding a lease never calls Return, so the work has to become visible again on its own. Without it a crash strands work permanently — the same failure class as the Notifications bug in `idempotency-review.md`.
- **Attempt ceiling + dead-lettering.** Without one, a permanently-failing message retries forever and starves everything behind it. `AttemptCount` increments on every *take*, not on every failure, so a message that reliably kills its consumer still walks toward the ceiling instead of retrying indefinitely.

**Why database-backed rather than SQS, and where SQS would go.** The relay leases rows in the *same database* as the business write that produced them; that transactional coupling is the entire point of the outbox pattern, and SQS cannot provide it — you can't atomically write a business row and enqueue to a remote queue, which is the dual-write problem the outbox exists to solve. **SQS belongs downstream of a relay built on this, never in place of it**:

```
business write + outbox row      one transaction, database
        ↓  relay: take → publish → complete       ← this queue
   SNS/SQS
        ↓  consumer: receive → process → delete   ← SQS's own visibility timeout
```

No SQS adapter exists yet, deliberately — nothing needs cross-process fan-out. The seam is `ILeasedQueue`; adding one later doesn't disturb the outbox half.

**Known limit**: `TakeAsync` reads candidates and then stamps leases, relying on a concurrency token to reject a contended claim. A single provider-specific statement would claim and return in one round trip without the read-then-write window - `UPDATE ... RETURNING` on Postgres, `UPDATE ... WITH (READPAST, UPDLOCK) ... OUTPUT inserted.*` on SQL Server - but that's raw SQL and this project stays provider-agnostic (it references only `Microsoft.EntityFrameworkCore`). The current form is correct — the loser's claim is rejected — just chattier under contention.

## Append-only enforcement

Audit rows must never be updated or deleted. That's enforced in **three layers, of increasing strength** — and it matters not to confuse them, because only the second is an actual security control.

**1. `IAppendOnly` + `AppendOnlySaveChangesInterceptor` (in code).** Any entity marked `IAppendOnly` causes `SaveChanges`/`SaveChangesAsync` to throw if it's tracked as `Modified` or `Deleted`. Each consuming `DbContext` registers it in `OnConfiguring` — deliberately there rather than at `AddDbContext`, so it applies regardless of provider or how options were supplied, including when a test host swaps in the InMemory provider. Covered by `Utils.Tests/AppendOnlyTests.cs`, including that a rejected save doesn't partially apply.

**This is a guardrail against an in-process mistake, not a security control.** It stops a developer accidentally mutating an audit row. It does nothing against raw SQL, a different context, a DBA, or an attacker who already has database credentials — the exact scenarios an audit trail exists to survive. Don't let its presence justify skipping layer 2.

**2. Database grant (the real enforcement) — a deployment requirement, not code.** The application's runtime SQL login must hold `INSERT` and `SELECT` on the audit tables and **not** `UPDATE` or `DELETE`, with migrations run under a separate, more privileged login. Roughly:

```sql
-- Runtime login: can write and read audit history, cannot rewrite it.
GRANT SELECT, INSERT ON [users].[AuditEvents]       TO [app_runtime];
GRANT SELECT, INSERT ON [permissions].[AuditEvents] TO [app_runtime];
GRANT SELECT, INSERT ON [comments].[AuditEvents]    TO [app_runtime];
DENY  UPDATE, DELETE  ON [users].[AuditEvents]       TO [app_runtime];
DENY  UPDATE, DELETE  ON [permissions].[AuditEvents] TO [app_runtime];
DENY  UPDATE, DELETE  ON [comments].[AuditEvents]    TO [app_runtime];
```

(RelishYourChef's audit table lives in its own `RelishYourChefDb`, so it needs the same treatment there.) `DENY` rather than merely withholding `GRANT`, since `DENY` survives the login later picking up a broader role. **This isn't wired up anywhere yet** — no deployment exists — but it's the layer that actually matters, and it's easy to forget precisely because it isn't code.

**3. Ledger tables (compliance-grade, not currently used).** SQL Server 2022 and Azure SQL support `WITH (LEDGER = ON)` append-only tables: cryptographically verifiable, `UPDATE`/`DELETE` blocked by the engine itself, and tampering detectable after the fact. Worth reaching for only if tamper-*evidence* becomes a stated requirement rather than tamper-*resistance* — EF Core won't manage it (raw SQL in a migration), and it constrains later schema changes and restore semantics.

## Retention vs. append-only

These are in genuine tension and the resolution should be deliberate: "never delete" and "delete after N years" can't both be satisfied by the same mechanism. When a retention policy becomes real, it needs an explicit archival path (export, then a privileged purge under the migration login, itself audited) rather than someone quietly running a `DELETE` — which the grant above would block anyway. Nothing implements this yet; the tables grow unbounded today.

## How other services consume this

Same cross-repo `ProjectReference` pattern every sibling-repo reference in this workspace uses (relative path, since these are sibling directories under `GitHub/`, not published NuGet packages yet):

```xml
<ProjectReference Include="..\..\Utils\Utils\Utils.csproj" />
<!-- only if the service keeps an audit trail -->
<ProjectReference Include="..\..\Utils\Utils.Data\Utils.Data.csproj" />
```

`Utils.Data` is referenced separately and only by services that actually audit (`Permissions`, `Comments` today) — it isn't bundled into the `Utils` reference precisely so services that don't audit stay free of the EF Core dependency. `Utils.Data` references `Utils`, so a consumer of the former gets the latter transitively, but reference both explicitly anyway to match how every other cross-repo dependency in this workspace is declared.

Referenced directly by each service's own `.Core` project (`Permissions.Core`, `UsersMicroservice.Core`, `CommentsService.Core`, `NotificationsService.Core`) rather than relying on it flowing transitively through `Permissions.Core` — the two libraries are unrelated concerns, and a service should be able to depend on this one without needing `Permissions.Core` at all. `Permissions.Core` itself also references this directly, for the same reason (its own list endpoints use `Pagination.cs`, and it has no special claim to distributing this library to everyone else). Each `.Tests` project gets this transitively through its own `.Core` `ProjectReference` — none of them need a direct reference of their own, confirmed working during the migration.

## Working directory note

`dotnet build`/`dotnet test` at the repo root pick up `Utils.slnx` and cover all three projects.
