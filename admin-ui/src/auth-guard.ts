import { apiRequest, ApiError } from "./api-client.js";
import { getSubject, isExpired } from "./jwt.js";
import { getToken } from "./token-storage.js";
import type { SubjectAuthorization } from "./types.js";

export type GuardResult =
  | { status: "authorized"; subjectId: string; authorization: SubjectAuthorization }
  | { status: "signed-out" }
  | { status: "forbidden"; subjectId: string };

// Every admin page template calls this before rendering anything real. It does not decide
// permissions itself - Users/Permissions/Comments all resolve authorization the same way (fresh,
// server-side, via Permissions - see Permissions/CLAUDE.md's "Design history") - so this asks the
// one source of truth directly, the same GET /authorization endpoint
// AuthorizationClaimsTransformation calls internally on every request, rather than duplicating
// that logic client-side (which would be untrustworthy anyway - the JWT itself carries no role/
// permission claims by design, only the `sub` claim, so there is nothing to decode locally).
//
// This is a cross-service call to Permissions from every page, including Users'/Comments' own -
// deliberate: these templates assume the shared-domain deployment a real product has (every
// service reachable from one origin), so it's same-origin in practice and needs no CORS.
export async function requirePermission(
  permissionsApiBaseUrl: string,
  requiredPermissionKey: string,
): Promise<GuardResult> {
  const token = getToken();
  if (!token || isExpired(token)) {
    return { status: "signed-out" };
  }

  const subjectId = getSubject(token);
  if (!subjectId) {
    return { status: "signed-out" };
  }

  const authorization = await apiRequest<SubjectAuthorization>(
    `${permissionsApiBaseUrl}/authorization?subjectType=user&subjectId=${encodeURIComponent(subjectId)}`,
  );

  return authorization.permissions.includes(requiredPermissionKey)
    ? { status: "authorized", subjectId, authorization }
    : { status: "forbidden", subjectId };
}

// Renders a full-page "not signed in" / "access denied" message into `container` and returns
// null, or returns the caller's subjectId (and leaves `container` untouched) so the page can
// render its real content. One helper every page's main.ts calls first, so the gate-and-render
// boilerplate isn't repeated across every template.
export async function guardPage(
  container: HTMLElement,
  permissionsApiBaseUrl: string,
  requiredPermissionKey: string,
): Promise<string | null> {
  let result: GuardResult;
  try {
    result = await requirePermission(permissionsApiBaseUrl, requiredPermissionKey);
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) {
      result = { status: "signed-out" };
    } else {
      renderGuardMessage(
        container,
        `Could not verify access: ${error instanceof Error ? error.message : String(error)}`,
      );
      return null;
    }
  }

  if (result.status === "signed-out") {
    renderGuardMessage(container, "You're not signed in. Sign in to the shared session for this domain, then reload this page.");
    return null;
  }

  if (result.status === "forbidden") {
    renderGuardMessage(
      container,
      `You're signed in, but you don't hold the "${requiredPermissionKey}" permission needed for this page.`,
    );
    return null;
  }

  return result.subjectId;
}

function renderGuardMessage(container: HTMLElement, message: string): void {
  container.innerHTML = "";
  const div = document.createElement("div");
  div.className = "admin-ui-guard";
  div.textContent = message;
  container.appendChild(div);
}
