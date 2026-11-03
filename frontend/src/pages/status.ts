import { ApiError } from "../api/errors.ts";
import { getVersion, type VersionInfo } from "../api/version.ts";
import { el } from "../components/dom.ts";
import { tr } from "../i18n/tr.ts";

function formatDateTime(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  return new Intl.DateTimeFormat("tr-TR", { dateStyle: "medium", timeStyle: "medium" }).format(date);
}

function renderLoading(region: HTMLElement): void {
  region.setAttribute("aria-busy", "true");
  region.replaceChildren(
    el("div", { class: "state state--loading", role: "status" }, [
      el("span", { class: "spinner", "aria-hidden": "true" }),
      el("span", {}, [tr.status.loading]),
    ]),
  );
}

function renderError(region: HTMLElement, error: unknown, onRetry: () => void): void {
  region.removeAttribute("aria-busy");
  const message = error instanceof ApiError ? error.message : tr.errors.unknown;
  const traceId = error instanceof ApiError ? error.traceId : undefined;
  const retry = el("button", { type: "button", class: "button button--secondary" }, [tr.status.retry]);
  retry.addEventListener("click", onRetry);
  region.replaceChildren(
    el("div", { class: "alert alert--danger", role: "alert" }, [
      el("p", { class: "alert__message" }, [message]),
      traceId !== undefined && el("p", { class: "alert__meta" }, [`${tr.errors.traceId}: `, el("code", {}, [traceId])]),
      el("div", { class: "alert__actions" }, [retry]),
    ]),
  );
}

function renderSuccess(region: HTMLElement, info: VersionInfo): void {
  region.removeAttribute("aria-busy");
  const rows: [string, Node | string][] = [
    [tr.status.fields.name, info.name],
    [tr.status.fields.version, info.version],
    [tr.status.fields.commit, el("code", {}, [info.commit])],
    [tr.status.fields.environment, info.environment],
    [tr.status.fields.serverTime, formatDateTime(info.serverTimeUtc)],
  ];
  region.replaceChildren(
    el("div", { class: "card" }, [
      el("p", { class: "status-badge", role: "status" }, [
        el("span", { class: "status-badge__dot", "aria-hidden": "true" }),
        tr.status.healthy,
      ]),
      el(
        "dl",
        { class: "definition-list" },
        rows.flatMap(([term, value]) => [el("dt", {}, [term]), el("dd", {}, [value])]),
      ),
    ]),
  );
}

/** Home page: backend status from `GET /api/version`. Returns a cleanup that aborts the request. */
export function renderStatusPage(main: HTMLElement): () => void {
  const region = el("div", { class: "status-region", "aria-live": "polite" });
  main.replaceChildren(
    el("div", { class: "page" }, [
      el("div", { class: "page__header" }, [
        el("h1", { class: "page__title" }, [tr.status.title]),
        el("p", { class: "page__subtitle" }, [tr.status.subtitle]),
      ]),
      region,
    ]),
  );

  let controller: AbortController | undefined;

  const load = async (): Promise<void> => {
    controller?.abort();
    const current = new AbortController();
    controller = current;
    renderLoading(region);
    try {
      const info = await getVersion({ signal: current.signal });
      if (!current.signal.aborted) renderSuccess(region, info);
    } catch (error) {
      if (!current.signal.aborted) renderError(region, error, () => void load());
    }
  };

  void load();
  return () => {
    controller?.abort();
  };
}
