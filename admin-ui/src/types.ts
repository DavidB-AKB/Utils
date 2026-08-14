// Types genuinely shared across every service's admin page - see this package's README for the
// scope line (generic pagination/response shapes belong here; a service's own domain types, like
// Users' User or Comments' Comment, are defined locally in that service's own AdminUI).

// Mirrors Utils/Pagination.cs's CursorPage<T> - every list endpoint across Users/Permissions/
// Comments returns this shape.
export interface CursorPage<T> {
  items: T[];
  nextCursor: string | null;
}

// Mirrors Permissions.Core's IAuthorizationProvider.SubjectAuthorization - the resolved
// permission set for a subject, as returned by GET /authorization on Permissions.
export interface SubjectAuthorization {
  roles: string[];
  permissions: string[];
}
