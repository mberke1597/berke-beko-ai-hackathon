// Arçelik paneli (#/panel): usage, cost, energy and learning quality of the Çeliknaz demo home.
// Full-width page, outside the phone column. Charts are hand-made SVG.

import { ApiError } from "../api/errors.ts";
import { el, LOGO_HEIGHT, LOGO_WIDTH, logoSrc, setChildren } from "../components/dom.ts";
import { formatMs, formatUsd } from "../home/chat/format.ts";
import type { AssistantEntry } from "../home/chat/reducer.ts";
import { icon } from "../home/icons.ts";
import type { HomeSession } from "../home/session.ts";
import {
  formatClock,
  formatCompact,
  formatInteger,
  formatKwh,
  formatRate,
  formatTl,
  formatUsdMoney,
  homesFromSlider,
  SLIDER_STEPS,
  sliderFromHomes,
} from "../home/slice2/logic.ts";
import type { DashboardSummary, Projection, RunSummary } from "../home/slice2/types.ts";
import { tr } from "../i18n/tr.ts";
import { paths } from "../paths.ts";
import { hrefFor } from "../router.ts";
import { renderTimeline } from "./ev/chat-view.ts";
import { createRateChart } from "./ev/charts.ts";
import { setToastRegion } from "./ev/toast.ts";

const REFRESH_MS = 10_000;
const RUN_LIMIT = 50;
const PROJECTION_DEBOUNCE_MS = 250;
const DEFAULT_HOMES = 100_000;

function tile(label: string, value: string, hint?: string): HTMLElement {
  return el("div", { class: "panel-tile" }, [
    el("span", { class: "panel-tile__label" }, [label]),
    el("span", { class: "panel-tile__value" }, [value]),
    hint !== undefined && el("span", { class: "panel-tile__hint" }, [hint]),
  ]);
}

function card(title: string, id: string, children: (Node | null | false)[], extraClass = ""): HTMLElement {
  return el("section", { class: `panel-card ${extraClass}`, "aria-labelledby": id }, [el("h2", { id, class: "panel-card__title" }, [title]), ...children.filter((c): c is Node => c !== null && c !== false)]);
}

function errorText(error: unknown): string {
  return error instanceof ApiError ? error.message : tr.errors.unknown;
}

/** Commands by source as proportional bars (CSS), with the total. */
function commandsTile(summary: DashboardSummary): HTMLElement {
  const t = tr.home.panel;
  const entries = (["voice", "app", "remote", "ai", "schedule"] as const).map((key) => ({ key, label: t.sources[key], count: summary.commandsToday[key] }));
  const total = entries.reduce((n, e) => n + e.count, 0);
  const max = Math.max(1, ...entries.map((e) => e.count));
  return el("div", { class: "panel-tile panel-tile--wide" }, [
    el("span", { class: "panel-tile__label" }, [t.tiles.commandsToday]),
    el("span", { class: "panel-tile__value" }, [formatInteger(total)]),
    el(
      "ul",
      { class: "panel-bars" },
      entries.map((e) =>
        el("li", { class: "panel-bars__row" }, [
          el("span", { class: "panel-bars__label" }, [e.label]),
          el("span", { class: "panel-bars__track", role: "img", "aria-label": `${e.label}: ${formatInteger(e.count)}` }, [
            el("span", { class: `panel-bars__fill panel-bars__fill--${e.key}`, style: `width: ${String(Math.round((e.count / max) * 100))}%` }),
          ]),
          el("span", { class: "panel-bars__count" }, [formatInteger(e.count)]),
        ]),
      ),
    ),
  ]);
}

function runsSignature(runs: readonly RunSummary[]): string {
  return runs.map((r) => `${r.runId}:${r.status}:${String(r.costUsd)}`).join("|");
}

/** Arçelik paneli page. Returns a cleanup that stops the refresh timer. */
export function renderPanel(root: HTMLElement, session: HomeSession): () => void {
  const t = tr.home.panel;
  let disposed = false;
  let projectionTimer: ReturnType<typeof setTimeout> | undefined;
  let lastRunsSignature = "";
  let homes = DEFAULT_HOMES;

  const tiles = el("div", { class: "panel-tiles" });
  const learningBox = el("div", {});
  const toolsBox = el("div", {});
  const runsBox = el("div", { class: "panel-runs" });
  const projectionBox = el("div", { class: "panel-projection__out", "aria-live": "polite" });
  const status = el("div", { class: "ev-muted", role: "status" });

  const refreshButton = el("button", { type: "button", class: "button button--secondary button--sm" }, [t.refresh]);
  const toasts = el("div", { class: "ev-toasts", "aria-live": "polite" });
  setToastRegion(toasts);

  // ---------- projection slider ----------
  const sliderId = "panel-homes";
  const slider = el("input", {
    id: sliderId,
    class: "panel-slider",
    type: "range",
    min: "0",
    max: String(SLIDER_STEPS),
    step: "1",
    value: String(Math.round(sliderFromHomes(homes) * SLIDER_STEPS)),
  });
  const homesValue = el("output", { class: "panel-projection__homes", for: sliderId }, [formatInteger(homes)]);
  slider.addEventListener("input", () => {
    homes = homesFromSlider(Number(slider.value) / SLIDER_STEPS);
    homesValue.textContent = formatInteger(homes);
    slider.setAttribute("aria-valuetext", formatInteger(homes));
    if (projectionTimer !== undefined) clearTimeout(projectionTimer);
    projectionTimer = setTimeout(() => {
      void loadProjection();
    }, PROJECTION_DEBOUNCE_MS);
  });

  function drawProjection(p: Projection): void {
    setChildren(projectionBox, [
      el("div", { class: "panel-tiles panel-tiles--tight" }, [
        tile(t.projection.llmMonth, formatUsdMoney(p.llmCostUsdMonth)),
        tile(t.projection.serverMonth, formatTl(p.serverCostTlMonth)),
        tile(t.projection.savedTl, formatTl(p.savedTlMonth)),
        tile(t.projection.savedKwh, formatKwh(p.savedKwhMonth)),
      ]),
      p.assumptions.length > 0 &&
        el("div", {}, [el("h3", { class: "panel-subtitle" }, [t.projection.assumptions]), el("ul", { class: "panel-assumptions" }, p.assumptions.map((a) => el("li", {}, [a])))]),
    ]);
  }

  async function loadProjection(): Promise<void> {
    const requested = homes;
    try {
      const p = await (await session.backend()).slice2.getProjection(requested);
      if (!disposed && requested === homes) drawProjection(p);
    } catch (error) {
      if (!disposed) projectionBox.replaceChildren(el("p", { class: "field__error", role: "alert" }, [`${t.projection.error} ${errorText(error)}`]));
    }
  }

  // ---------- summary ----------
  function drawSummary(s: DashboardSummary): void {
    setChildren(tiles, [
      tile(t.tiles.activeMembers, formatInteger(s.activeMembers)),
      commandsTile(s),
      tile(t.tiles.runsToday, formatInteger(s.runsToday)),
      tile(t.tiles.conversationsToday, formatInteger(s.conversationsToday)),
      tile(t.tiles.llmToday, formatUsdMoney(s.llm.costUsdToday)),
      tile(t.tiles.llmMonth, formatUsdMoney(s.llm.costUsdMonth)),
      tile(t.tiles.avgConversation, formatUsdMoney(s.llm.avgCostUsdPerConversation)),
      tile(t.tiles.tokensToday, formatCompact(s.llm.tokensToday), s.llm.tokensToday >= 1000 ? formatInteger(s.llm.tokensToday) : undefined),
      tile(t.tiles.latency, formatMs(s.llm.avgLatencyMs)),
      tile(t.tiles.savedKwh, formatKwh(s.energy.savedKwhWeek)),
      tile(t.tiles.savedTl, formatTl(s.energy.savedTlWeek)),
      tile(t.tiles.serverMonth, formatTl(s.server.monthlyCostTl)),
      tile(t.tiles.serverPerUser, formatTl(s.server.costPerActiveUserTl)),
      tile(t.tiles.acceptance, formatRate(s.learning.suggestionAcceptanceRate)),
    ]);

    const lastOverride = s.learning.overrideRateTrend[s.learning.overrideRateTrend.length - 1]?.rate;
    const lastGood = s.learning.goodFeedbackTrend[s.learning.goodFeedbackTrend.length - 1]?.rate;
    setChildren(learningBox, [
      createRateChart(
        [
          { tone: "override", label: t.chartOverride, points: s.learning.overrideRateTrend },
          { tone: "good", label: t.chartGood, points: s.learning.goodFeedbackTrend },
        ],
        tr.home.learn.chartLabel(lastOverride === undefined ? "—" : formatRate(lastOverride), lastGood === undefined ? "—" : formatRate(lastGood)),
        t.trendEmpty,
      ),
    ]);

    setChildren(toolsBox, [
      s.tools.length === 0
        ? el("p", { class: "ev-muted" }, [t.toolsEmpty])
        : el("table", { class: "panel-table" }, [
            el("thead", {}, [el("tr", {}, [el("th", { scope: "col" }, [t.toolsHead.tool]), el("th", { scope: "col", class: "num" }, [t.toolsHead.calls]), el("th", { scope: "col", class: "num" }, [t.toolsHead.errors])])]),
            el(
              "tbody",
              {},
              s.tools.map((tool) =>
                el("tr", {}, [el("th", { scope: "row" }, [el("code", {}, [tool.tool])]), el("td", { class: "num" }, [formatInteger(tool.calls)]), el("td", { class: `num ${tool.errors > 0 ? "panel-table__bad" : ""}` }, [formatInteger(tool.errors)])]),
              ),
            ),
          ]),
    ]);
  }

  // ---------- runs ----------
  function runRow(run: RunSummary): HTMLElement[] {
    const detail = el("tr", { class: "panel-table__detail", hidden: true }, [el("td", { colspan: "6" }, [])]);
    const cell = detail.firstElementChild as HTMLElement;
    const buttonLabel = el("span", {}, [t.showSteps]);
    const button = el("button", { type: "button", class: "button button--secondary button--sm", "aria-expanded": "false" }, [icon("chart", "icon icon--sm"), buttonLabel]);
    let loaded = false;
    button.addEventListener("click", () => {
      const open = detail.hidden;
      detail.hidden = !open;
      button.setAttribute("aria-expanded", String(open));
      buttonLabel.textContent = open ? t.hideSteps : t.showSteps;
      if (!open || loaded) return;
      loaded = true;
      cell.replaceChildren(el("p", { class: "ev-muted" }, [tr.home.chat.timeline.loading]));
      void session.backend().then(
        async (b) => {
          try {
            const full = await b.chat.getRun(run.runId);
            const entry: AssistantEntry = {
              role: "assistant",
              id: `panel:${run.runId}`,
              localId: null,
              runId: run.runId,
              text: "",
              status: run.status === "failed" ? "failed" : "completed",
              actions: [],
              steps: full.steps,
              totals: full.totals,
              fromHistory: false,
            };
            const timeline = renderTimeline(entry, { store: session.store }, true);
            setChildren(cell, [
              timeline ?? el("p", { class: "ev-muted" }, [tr.home.chat.timeline.empty]),
              run.traceId !== "" && el("p", { class: "ev-muted" }, [`${t.linkToTrace}: `, el("code", {}, [run.traceId])]),
            ]);
          } catch {
            loaded = false;
            cell.replaceChildren(el("p", { class: "field__error", role: "alert" }, [t.stepsError]));
          }
        },
        () => {
          loaded = false;
        },
      );
    });
    const statusText = Object.hasOwn(t.runStatus, run.status) ? (t.runStatus[run.status] ?? run.status) : run.status;
    const row = el("tr", {}, [
      el("td", {}, [formatClock(run.startedAtUtc)]),
      el("th", { scope: "row", class: "panel-table__text" }, [run.userText]),
      el("td", {}, [el("span", { class: `badge badge--neutral panel-status panel-status--${run.status}` }, [statusText])]),
      el("td", { class: "num" }, [formatMs(run.durationMs)]),
      el("td", { class: "num" }, [formatUsd(run.costUsd)]),
      el("td", {}, [button]),
    ]);
    return [row, detail];
  }

  function drawRuns(runs: readonly RunSummary[]): void {
    const signature = runsSignature(runs);
    if (signature === lastRunsSignature && runsBox.childElementCount > 0) return;
    lastRunsSignature = signature;
    if (runs.length === 0) {
      runsBox.replaceChildren(el("p", { class: "ev-muted" }, [t.runsEmpty]));
      return;
    }
    runsBox.replaceChildren(
      el("div", { class: "panel-table__wrap" }, [
        el("table", { class: "panel-table" }, [
          el("thead", {}, [
            el("tr", {}, [
              el("th", { scope: "col" }, [t.runsHead.time]),
              el("th", { scope: "col" }, [t.runsHead.request]),
              el("th", { scope: "col" }, [t.runsHead.status]),
              el("th", { scope: "col", class: "num" }, [t.runsHead.duration]),
              el("th", { scope: "col", class: "num" }, [t.runsHead.cost]),
              el("th", { scope: "col" }, [t.runsHead.steps]),
            ]),
          ]),
          el("tbody", {}, runs.flatMap(runRow)),
        ]),
      ]),
    );
  }

  async function load(): Promise<void> {
    try {
      const slice2 = (await session.backend()).slice2;
      const [summary, runs] = await Promise.all([slice2.getSummary(), slice2.listRuns(RUN_LIMIT)]);
      if (disposed) return;
      status.textContent = "";
      drawSummary(summary);
      drawRuns(runs);
    } catch (error) {
      if (!disposed) status.replaceChildren(el("span", { class: "field__error", role: "alert" }, [`${t.loadError} ${errorText(error)}`]));
    }
  }

  refreshButton.addEventListener("click", () => {
    lastRunsSignature = "";
    void load();
    void loadProjection();
  });

  const back = el("a", { class: "button button--secondary button--sm", href: hrefFor(paths.ev) }, [icon("back", "icon icon--sm"), t.back]);

  root.replaceChildren(
    el("div", { class: "panel-page" }, [
      el("header", { class: "panel-header" }, [
        el("a", { class: "panel-header__brand", href: hrefFor(paths.panel) }, [
          el("img", { class: "app-header__logo", src: logoSrc, alt: tr.app.logoAlt, width: String(LOGO_WIDTH), height: String(LOGO_HEIGHT) }),
          el("div", {}, [el("h1", { class: "panel-header__title" }, [t.pageTitle]), el("p", { class: "ev-muted" }, [t.subtitle])]),
        ]),
        el("div", { class: "panel-header__actions" }, [refreshButton, back]),
      ]),
      el("main", { class: "panel-main", id: "main-content" }, [
        status,
        card(t.sections.usage, "panel-usage", [tiles], "panel-card--flat"),
        el("div", { class: "panel-columns" }, [
          card(t.sections.learning, "panel-learning", [learningBox]),
          card(t.sections.tools, "panel-tools", [toolsBox]),
        ]),
        card(t.sections.runs, "panel-runs-title", [runsBox]),
        card(t.sections.projection, "panel-projection", [
          el("div", { class: "panel-projection" }, [
            el("label", { class: "field__label", for: sliderId }, [t.projection.sliderLabel]),
            el("div", { class: "panel-projection__control" }, [slider, homesValue]),
            projectionBox,
          ]),
        ]),
      ]),
      toasts,
    ]),
  );

  void load();
  void loadProjection();
  const timer = setInterval(() => {
    void load();
  }, REFRESH_MS);

  return () => {
    disposed = true;
    clearInterval(timer);
    if (projectionTimer !== undefined) clearTimeout(projectionTimer);
  };
}
