import { commandErrorMessage } from "../../home/api.ts";
import { el, setChildren } from "../../components/dom.ts";
import { icon } from "../../home/icons.ts";
import type { HomeSession } from "../../home/session.ts";
import { formatRate } from "../../home/slice2/logic.ts";
import type { Profile, Routine } from "../../home/slice2/types.ts";
import { formatNumber } from "../../home/labels.ts";
import { tr } from "../../i18n/tr.ts";
import { paths } from "../../paths.ts";
import { createRateChart } from "./charts.ts";
import { loadHome, renderLoadError, renderLoading } from "./common.ts";
import { renderSubPage } from "./subpage.ts";
import { showToast } from "./toast.ts";

function lookup(table: Readonly<Record<string, string>>, key: string): string {
  return Object.hasOwn(table, key) ? (table[key] ?? key) : key;
}

/** "Çeliknaz neler öğrendi?": preferences, routines with evidence, weekly trend chart and the demo seed button. */
export function renderLearning(main: HTMLElement, session: HomeSession): () => void {
  const t = tr.home.learn;
  const body = renderSubPage(main, t.title, paths.evSettings);
  const content = el("div", { class: "ev-stack" });
  let disposed = false;
  let profile: Profile | undefined;

  const reload = async (): Promise<void> => {
    profile = await (await session.backend()).slice2.getProfile(session.s2.memberId);
    if (!disposed) draw();
  };

  function routineCard(routine: Routine): HTMLElement {
    const appliance = session.store.getAppliance(routine.applianceId)?.displayName ?? routine.applianceId;
    const decide = (accept: boolean): void => {
      void session.backend().then(async (b) => {
        try {
          if (accept) await b.slice2.acceptRoutine(routine.id);
          else await b.slice2.rejectRoutine(routine.id);
          showToast(accept ? t.accepted : t.rejected, "success");
          await reload();
        } catch (error) {
          showToast(commandErrorMessage(error), "error");
        }
      });
    };
    const open = routine.status === "detected" || routine.status === "suggested";
    const buttons = open
      ? el("div", { class: "ev-actions" }, [
          (() => {
            const b = el("button", { type: "button", class: "button button--primary button--sm", "data-key": `accept-${routine.id}` }, [icon("check", "icon icon--sm"), t.accept]);
            b.addEventListener("click", () => {
              b.disabled = true;
              decide(true);
            });
            return b;
          })(),
          (() => {
            const b = el("button", { type: "button", class: "button button--secondary button--sm", "data-key": `reject-${routine.id}` }, [t.reject]);
            b.addEventListener("click", () => {
              b.disabled = true;
              decide(false);
            });
            return b;
          })(),
        ])
      : null;
    return el("article", { class: "ev-routine" }, [
      el("div", { class: "ev-routine__head" }, [
        el("h3", { class: "ev-routine__title" }, [`${appliance} · ${t.routineAt(t.dayTypes[routine.dayType], routine.time)}`]),
        el("span", { class: `badge badge--neutral ev-routine__status ev-routine__status--${routine.status}` }, [lookup(t.statuses, routine.status)]),
      ]),
      el("p", { class: "ev-routine__evidence" }, [routine.evidence]),
      el("p", { class: "ev-muted" }, [t.confidence(formatRate(routine.confidence))]),
      buttons,
    ]);
  }

  function draw(): void {
    const p = profile;
    if (!p) return;
    const focus = (document.activeElement as HTMLElement | null)?.dataset.key;
    const empty = p.preferences.length === 0 && p.routines.length === 0 && p.overrideRate.length === 0;

    const lastOverride = p.overrideRate[p.overrideRate.length - 1]?.rate;
    const lastGood = p.goodFeedbackRate[p.goodFeedbackRate.length - 1]?.rate;
    const chartLabel = t.chartLabel(lastOverride === undefined ? "—" : formatRate(lastOverride), lastGood === undefined ? "—" : formatRate(lastGood));

    const seed = el("button", { type: "button", class: "button button--secondary", "data-key": "seed" }, [icon("sparkle", "icon icon--sm"), t.demoButton]);
    seed.addEventListener("click", () => {
      seed.disabled = true;
      void session.backend().then(async (b) => {
        try {
          const result = await b.slice2.seedHistory(session.s2.memberId, 5);
          showToast(t.demoDone(String(result.events)), "success");
          await reload();
          void session.s2.loadNotifications().catch(() => undefined);
        } catch (error) {
          seed.disabled = false;
          showToast(error instanceof Error ? `${t.demoError} ${commandErrorMessage(error)}` : t.demoError, "error");
        }
      });
    });

    setChildren(content, [
      empty &&
        el("div", { class: "ev-empty" }, [
          el("span", { class: "ev-empty__icon ev-empty__icon--brand" }, [icon("sparkle", "icon icon--xl")]),
          el("h2", { class: "ev-empty__title" }, [t.emptyTitle]),
          el("p", { class: "ev-empty__body" }, [t.emptyBody]),
        ]),
      el("section", { class: "ev-panel", "aria-labelledby": "learn-pref" }, [
        el("h2", { id: "learn-pref", class: "ev-panel__title" }, [t.preferences]),
        ...(p.preferences.length === 0
          ? [el("p", { class: "ev-muted" }, [t.noPreferences])]
          : p.preferences.map((pref) =>
              el("div", { class: "ev-pref" }, [
                el("p", { class: "ev-pref__text" }, [pref.text !== "" ? pref.text : `${pref.context}: ${formatNumber(pref.setpoint)}°`]),
                el("p", { class: "ev-muted" }, [`${t.confidence(formatRate(pref.confidence))} · ${t.samples(String(pref.samples))}`]),
              ]),
            )),
        p.sensitivityOffset !== 0 && el("p", { class: "ev-muted" }, [t.sensitivity(formatNumber(p.sensitivityOffset))]),
      ]),
      el("section", { class: "ev-panel", "aria-labelledby": "learn-routines" }, [
        el("h2", { id: "learn-routines", class: "ev-panel__title" }, [t.routines]),
        ...(p.routines.length === 0 ? [el("p", { class: "ev-muted" }, [t.noRoutines])] : p.routines.map(routineCard)),
      ]),
      el("section", { class: "ev-panel", "aria-labelledby": "learn-chart" }, [
        el("h2", { id: "learn-chart", class: "ev-panel__title" }, [icon("chart", "icon icon--sm"), t.chartTitle]),
        createRateChart(
          [
            { tone: "override", label: t.chartOverride, points: p.overrideRate },
            { tone: "good", label: t.chartGood, points: p.goodFeedbackRate },
          ],
          chartLabel,
          t.chartEmpty,
        ),
        el("p", { class: "ev-muted" }, [t.chartHint]),
      ]),
      el("section", { class: "ev-panel ev-panel--demo", "aria-labelledby": "learn-demo" }, [
        el("h2", { id: "learn-demo", class: "ev-panel__title" }, [t.demoTitle, " ", el("span", { class: "badge badge--neutral" }, ["demo"])]),
        el("p", { class: "ev-muted" }, [t.demoHint]),
        seed,
      ]),
    ]);
    if (focus !== undefined) content.querySelector<HTMLElement>(`[data-key="${CSS.escape(focus)}"]`)?.focus();
  }

  const start = (): void => {
    renderLoading(body, t.loading);
    reload().then(
      () => {
        if (!disposed) body.replaceChildren(content);
      },
      (error: unknown) => {
        if (!disposed) renderLoadError(body, error, start);
      },
    );
  };
  loadHome(session, body, start);
  return () => {
    disposed = true;
  };
}
