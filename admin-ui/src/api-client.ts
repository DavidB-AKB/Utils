import { getToken } from "./token-storage.js";

// Carries the real HTTP status so callers can branch on 401 ("not signed in") vs 403 ("signed in,
// but missing the permission") vs anything else, rather than parsing response.ok themselves at
// every call site. `body` is whatever the server's error response deserialized to (usually
// `{ error: string }` - see each service's own endpoint mapping), left undefined if the response
// had no JSON body at all.
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly body: unknown,
    message?: string,
  ) {
    super(message ?? `Request failed with status ${status}`);
    this.name = "ApiError";
  }
}

export interface RequestOptions {
  method?: "GET" | "POST" | "PATCH" | "DELETE";
  body?: unknown;
}

// Thin fetch wrapper: attaches the bearer token from shared storage (see token-storage.ts) and
// serializes/deserializes JSON. Every service's host uses ASP.NET Core's default Web JSON options
// (camelCase property names) plus Permissions.Core's GuidIdJsonConverterFactory (strongly-typed
// IDs serialize as plain strings, not the nested-object shape System.Text.Json would otherwise
// produce - see Permissions/CLAUDE.md's "Strongly-typed IDs") - nothing on the client needs to
// account for either, a plain string field is always just a plain string here.
export async function apiRequest<T>(url: string, options: RequestOptions = {}): Promise<T> {
  const token = getToken();
  const headers: Record<string, string> = { Accept: "application/json" };
  if (token) {
    headers.Authorization = `Bearer ${token}`;
  }

  if (options.body !== undefined) {
    headers["Content-Type"] = "application/json";
  }

  const response = await fetch(url, {
    method: options.method ?? "GET",
    headers,
    body: options.body !== undefined ? JSON.stringify(options.body) : undefined,
  });

  if (!response.ok) {
    let body: unknown;
    try {
      body = await response.json();
    } catch {
      // Non-JSON error body (e.g. a bare 401/403 with no content) - fine, body stays undefined.
    }

    throw new ApiError(response.status, body);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}
