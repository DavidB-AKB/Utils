// Reads the bearer token a real product's own login flow is expected to have already populated
// somewhere shared across every admin page hosted on the same domain - these templates don't
// implement login themselves (see this package's README). Defaults to localStorage, which is
// same-origin-scoped - correct for admin pages served as different paths under one origin (the
// deployment shape these templates assume: "every real product has access to every service and
// hosts them under a shared domain"), but NOT shared across different *subdomains*. A deployment
// that spans subdomains instead of paths should swap this for a cookie scoped to the shared parent
// domain - this file is the one place that would need to change.
const STORAGE_KEY = "adminUi.authToken";

export function getToken(): string | null {
  return localStorage.getItem(STORAGE_KEY);
}

export function setToken(token: string): void {
  localStorage.setItem(STORAGE_KEY, token);
}

export function clearToken(): void {
  localStorage.removeItem(STORAGE_KEY);
}
