// Small DOM helpers shared across every admin page - kept deliberately minimal (no framework, no
// virtual DOM) so what's on screen is always a direct, traceable result of the code that ran. See
// this package's README for why plain TypeScript was chosen over a framework.

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) {
    return "—";
  }

  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
}

// A button that disables itself and shows `busyLabel` for the duration of `onClick`'s promise,
// then re-enables and calls `onDone` (typically "reload this table") - the one repeated pattern
// every moderation/admin action button needs (lock/unlock, hide/unhide, revoke, ...), and easy to
// get subtly wrong by hand at every call site (double-submit on a slow request, no feedback while
// it's in flight).
export function createActionButton(
  label: string,
  onClick: () => Promise<void>,
  options?: { busyLabel?: string; className?: string; onDone?: () => void; onError?: (error: unknown) => void },
): HTMLButtonElement {
  const button = document.createElement("button");
  button.textContent = label;
  if (options?.className) {
    button.className = options.className;
  }

  button.addEventListener("click", () => {
    button.disabled = true;
    const originalLabel = button.textContent;
    if (options?.busyLabel) {
      button.textContent = options.busyLabel;
    }

    onClick()
      .then(() => options?.onDone?.())
      .catch((error: unknown) => {
        if (options?.onError) {
          options.onError(error);
        } else {
          window.alert(error instanceof Error ? error.message : String(error));
        }
      })
      .finally(() => {
        button.disabled = false;
        button.textContent = originalLabel;
      });
  });

  return button;
}

export function renderError(container: HTMLElement, error: unknown): void {
  container.innerHTML = "";
  const div = document.createElement("div");
  div.className = "admin-ui-error";
  div.textContent = error instanceof Error ? error.message : String(error);
  container.appendChild(div);
}
