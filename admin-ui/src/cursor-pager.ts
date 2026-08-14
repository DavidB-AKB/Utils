// The backend's CursorPage<T> is forward-only by design (keyset pagination - see Utils/
// Pagination.cs) - there's no PrevCursor, only NextCursor. To support a "back" button anyway, this
// keeps a client-side stack of every cursor visited and walks it, rather than asking the server
// for something it deliberately doesn't offer.
export class CursorPager {
  private history: (string | null)[] = [null]; // history[0] = null = "first page"
  private index = 0;

  get currentCursor(): string | null {
    return this.history[this.index] ?? null;
  }

  get canGoBack(): boolean {
    return this.index > 0;
  }

  goBack(): void {
    if (this.canGoBack) {
      this.index--;
    }
  }

  // Call after a successful fetch, with that page's nextCursor. Returns whether there's a forward
  // page to advance to. Truncates any stale "forward" history past the current point first, so
  // going back then fetching a fresh page doesn't leave an inconsistent branch behind.
  hasNext(nextCursor: string | null): boolean {
    return nextCursor !== null;
  }

  advance(nextCursor: string | null): void {
    if (nextCursor === null) {
      return;
    }

    this.history = this.history.slice(0, this.index + 1);
    this.history.push(nextCursor);
    this.index++;
  }

  reset(): void {
    this.history = [null];
    this.index = 0;
  }
}

export interface PagerControls {
  element: HTMLElement;
  setState(canGoBack: boolean, canGoForward: boolean): void;
}

export function createPagerControls(onPrev: () => void, onNext: () => void): PagerControls {
  const prevButton = document.createElement("button");
  prevButton.textContent = "← Prev";
  prevButton.className = "admin-ui-pager-button";
  prevButton.addEventListener("click", onPrev);

  const nextButton = document.createElement("button");
  nextButton.textContent = "Next →";
  nextButton.className = "admin-ui-pager-button";
  nextButton.addEventListener("click", onNext);

  const element = document.createElement("div");
  element.className = "admin-ui-pager";
  element.append(prevButton, nextButton);

  return {
    element,
    setState(canGoBack, canGoForward) {
      prevButton.disabled = !canGoBack;
      nextButton.disabled = !canGoForward;
    },
  };
}
