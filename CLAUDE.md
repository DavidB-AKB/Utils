# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

A shared library of small, generic, cross-cutting utilities for .NET 10 that have nothing to do with permissions/authz — created 2026-08-05 specifically to give those concerns a home separate from `Permissions.Core`. Before this repo existed, `Permissions.Core` was the one shared library every foundational service already referenced, so it was where cross-cutting code landed by default even when it had nothing to do with authorization (cursor pagination being the first example — see `Pagination.cs`). That's a naming/scope smell, not a load-bearing reason: this repo exists so "not permission-specific" and "not in `Permissions.Core`" mean the same thing going forward.

**Library-only, no host** — unlike `Users`/`Notifications`/`Comments`/`Permissions`, this repo has no `Program.cs`, no endpoints, no `DbContext`, and never runs standalone. It's referenced the same way any of those repos' `.Core` libraries are (a `ProjectReference` from a sibling repo), just without the microservice `.Core`/host split, since there's no host to split off. Two projects:

- **`Utils/`** — the library itself (`Microsoft.NET.Sdk` class library, no `FrameworkReference` to ASP.NET Core — nothing here should need it; if something does, that's a signal it belongs in `Permissions.Core` instead, which already has that dependency for its auth types).
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

No `dotnet run` — there's no host. No EF Core migrations — there's no `DbContext`.

## Current contents

- **`Pagination.cs`** — cursor (keyset) pagination primitive: `CursorPage<T>` (`{ Items, NextCursor }`), `PaginationDefaults.ClampLimit` (default 20, max 100), `PaginationCursor.Encode`/`Decode` (opaque base64 `"<sortKey><id>"` cursor). Moved here 2026-08-05 from `Permissions.Core/Pagination.cs`, where it originally landed only because `Permissions.Core` was the one library everyone already referenced — it has no relationship to permissions/authz. Used by every foundational service's list endpoints: `Users` (`GET /users`), `Permissions` (`GET /permissions`/`/roles`/`/assignments`), `Comments` (`GET /comments`), `Notifications` (`GET /rules`/`/templates`). See each service's own `CLAUDE.md` for how it orders its own list, and `Comments/CLAUDE.md`'s "Pagination + visibility filtering" section for a real interaction worth knowing about (pagination combined with a post-query authorization filter needs a top-up loop, not naive fetch-then-filter, or pages can look short when they aren't).

## How other services consume this

Same cross-repo `ProjectReference` pattern every sibling-repo reference in this workspace uses (relative path, since these are sibling directories under `GitHub/`, not published NuGet packages yet):

```xml
<ProjectReference Include="..\..\Utils\Utils\Utils.csproj" />
```

Referenced directly by each service's own `.Core` project (`Permissions.Core`, `UsersMicroservice.Core`, `CommentsService.Core`, `NotificationsService.Core`) rather than relying on it flowing transitively through `Permissions.Core` — the two libraries are unrelated concerns, and a service should be able to depend on this one without needing `Permissions.Core` at all. `Permissions.Core` itself also references this directly, for the same reason (its own list endpoints use `Pagination.cs`, and it has no special claim to distributing this library to everyone else). Each `.Tests` project gets this transitively through its own `.Core` `ProjectReference` — none of them need a direct reference of their own, confirmed working during the migration.

## Working directory note

`dotnet build`/`dotnet test` at the repo root pick up `Utils.slnx` and cover both projects.
