import { ApiError } from "../../api/errors.ts";
import { el } from "../../components/dom.ts";
import type { HomeSession } from "../../home/session.ts";
import { tr } from "../../i18n/tr.ts";

export function renderLoading(region: HTMLElement, text: string = tr.home.loading): void {
  region.replaceChildren(
    el("div", { class: "state state--loading", role: "status" }, [el("span", { class: "spinner", "aria-hidden": "true" }), el("span", {}, [text])]),
  );
}

export function renderLoadError(region: HTMLElement, error: unknown, onRetry: () => void): void {
  const message = error instanceof ApiError ? error.message : tr.errors.unknown;
  const traceId = error instanceof ApiError ? error.traceId : undefined;
  const retry = el("button", { type: "button", class: "button button--secondary" }, [tr.home.retry]);
  retry.addEventListener("click", onRetry);
  region.replaceChildren(
    el("div", { class: "alert alert--danger ev-alert", role: "alert" }, [
      el("p", { class: "alert__message" }, [message]),
      traceId !== undefined && el("p", { class: "alert__meta" }, [`${tr.errors.traceId}: `, el("code", {}, [traceId])]),
      el("div", { class: "alert__actions" }, [retry]),
    ]),
  );
}

/** Loads the home into the session store, showing loading and error states in `region`. */
export function loadHome(session: HomeSession, region: HTMLElement, onReady: () => void): void {
  if (session.store.home) {
    onReady();
    return;
  }
  renderLoading(region);
  session.ensureLoaded().then(
    () => {
      if (region.isConnected) onReady();
    },
    (error: unknown) => {
      if (region.isConnected) {
        renderLoadError(region, error, () => {
          loadHome(session, region, onReady);
        });
      }
    },
  );
}
