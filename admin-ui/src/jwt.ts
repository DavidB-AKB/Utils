// Client-side JWT payload decoding, for UX only (e.g. "signed in as X", proactively detecting an
// expired token before making a request). This never verifies the signature - it can't, without
// the signing secret - so it must never be used to make an actual authorization decision. The
// real authorization check always happens server-side: auth-guard.ts asks Permissions'
// /authorization endpoint, and api-client.ts surfaces 401/403 from every other call. Decoding here
// exists purely so a page can render something sensible before/without a round trip.
export interface JwtPayload {
  sub?: string;
  exp?: number;
  [claim: string]: unknown;
}

export function decodeJwtPayload(token: string): JwtPayload | null {
  const parts = token.split(".");
  if (parts.length !== 3) {
    return null;
  }

  try {
    const base64 = parts[1]!.replace(/-/g, "+").replace(/_/g, "/");
    const padded = base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), "=");
    return JSON.parse(atob(padded)) as JwtPayload;
  } catch {
    return null;
  }
}

export function isExpired(token: string): boolean {
  const payload = decodeJwtPayload(token);
  if (!payload?.exp) {
    return true;
  }

  return Date.now() >= payload.exp * 1000;
}

export function getSubject(token: string): string | null {
  return decodeJwtPayload(token)?.sub ?? null;
}
