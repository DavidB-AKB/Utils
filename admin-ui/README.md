# admin-ui

Shared TypeScript building blocks for the admin page templates in `Users/AdminUI/`,
`Permissions/AdminUI/`, and `Comments/AdminUI/`. Plain TypeScript, no framework, no bundler, no
npm dependencies - see the top-level `CLAUDE.md`'s "AdminUI templates" bullet for why.

## Deployment assumption

These templates assume a real product hosts every foundational service under one shared domain
(a reverse proxy or API gateway routing different paths/subdomains to each service). That means:

- **No login UI here.** A real product's own login flow is expected to populate the shared bearer
  token (see `token-storage.ts`) before a user ever reaches one of these admin pages. There's
  nothing to configure - just make sure whatever your product's login does writes to the same
  storage this reads from (swap `token-storage.ts` if you need a different mechanism, e.g. a
  cross-subdomain cookie instead of `localStorage`).
- **No CORS handling.** Every page calls its own service same-origin, plus one cross-service call
  to Permissions (`auth-guard.ts`, to resolve the caller's permissions) that's also same-origin
  under this deployment shape. If you're testing these templates against separately-hosted local
  `dotnet run` instances on different ports instead, that call *will* need CORS configured on
  Permissions, or you'll need to serve all three under one local reverse proxy.

## What's here

- `types.ts` - `CursorPage<T>`, `SubjectAuthorization` (the two shapes every service's list/
  authorization endpoints return).
- `token-storage.ts` - reads the shared bearer token.
- `jwt.ts` - client-side JWT payload decoding, UX only, never a trust boundary.
- `api-client.ts` - typed `fetch` wrapper; throws `ApiError` (carrying the real HTTP status) on
  any non-2xx response.
- `auth-guard.ts` - `guardPage(container, permissionsApiBaseUrl, requiredPermissionKey)`, called
  first by every page's `main.ts`. Asks Permissions' own `GET /authorization` endpoint whether the
  signed-in caller holds the permission the page needs, and renders a clear "not signed in" /
  "access denied" message in place of the real content if not.
- `dom.ts` - `formatDateTime`, `createActionButton` (a button that disables itself and shows a
  busy label for the duration of an async action - the lock/unlock/hide/revoke pattern every page
  repeats), `renderError`.
- `table.ts` - `renderTable(container, rows, columns)`, a generic column-defined table renderer.
  Not audit-specific despite most callers using it for an audit trail - it's also the base for
  every list view (users, permissions, roles, comments).
- `cursor-pager.ts` - `CursorPager` (a client-side back-stack over the backend's forward-only
  keyset cursor - see `Utils/Pagination.cs`, there's no `PrevCursor` to ask the server for) plus
  `createPagerControls` for the Prev/Next buttons.
- `styles.css` - one shared stylesheet every page links directly.

## Building

```bash
tsc -p admin-ui/tsconfig.json
```

Produces `dist/*.js` + `dist/*.d.ts`. Each service's `AdminUI/` imports directly from this
compiled output via a relative path (e.g. `import { guardPage } from
"../../../Utils/admin-ui/dist/auth-guard.js";`) - the same cross-repo, sibling-directory
convention the rest of this workspace uses for C# `ProjectReference`s, just source-compiled-once
instead of recompiled-per-consumer. **Build this package before building any service's AdminUI**;
nothing here depends on a service, but every service depends on this.
