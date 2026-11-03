// Appliance detail additions: comfort card, today's energy and the cheapest-hour button.
// Each panel owns one persistent element, so live re-renders of the detail screen do not lose its state.

import { commandErrorMessage } from "../../home/api.ts";
import { el, setChildren } from "../../components/dom.ts";
import { sendDeviceCommand } from "../../home/actions.ts";
import { defaultProgramTemp } from "../../home/commands.ts";
import { icon } from "../../home/icons.ts";
import { formatTemp, programLabel } from "../../home/labels.ts";
import type { HomeSession } from "../../home/session.ts";
import { actionLabel, conflictNote, feelingLabel, formatClock, formatKwh, formatTl, memberName, recommendationCommands } from "../../home/slice2/logic.ts";
import type { CheapestTime, Comfort } from "../../home/slice2/types.ts";
import type { Appliance, DeviceCommand } from "../../home/types.ts";
import { tr } from "../../i18n/tr.ts";
import { renderLoading } from "./common.ts";
import { showToast } from "./toast.ts";

export interface Panel {
  element: HTMLElement;
  refresh(delayMs?: number): void;
  dispose(): void;
}

// ---------- comfort ----------

const COMFORT_DEBOUNCE_MS = 700;

/** Comfort card: feeling, band, recommendation with reason and cost, apply button and compromise note. */
export function createComfortPanel(session: HomeSession, applianceId: string): Panel {
  const c = tr.home.comfort;
  const element = el("section", { class: "ev-panel ev-comfort", "aria-labelledby": "comfort-title", "aria-live": "polite" });
  let timer: ReturnType<typeof setTimeout> | undefined;
  let disposed = false;
  let applying = false;
  let loaded = false;
  let last: Comfort | undefined;

  const apply = async (comfort: Comfort): Promise<void> => {
    applying = true;
    render();
    let ok = true;
    const commands = ((): DeviceCommand[] => {
      const appliance = session.store.getAppliance(applianceId);
      return appliance ? recommendationCommands(comfort.recommendation, appliance) : [];
    })();
    for (const command of commands) {
      const appliance = session.store.getAppliance(applianceId);
      if (!appliance) break;
      const outcome = await sendDeviceCommand(session, appliance, command, "app");
      if (!outcome.ok) {
        ok = false;
        showToast(outcome.message, "error");
        break;
      }
    }
    applying = false;
    if (ok && commands.length > 0) showToast(c.applied, "success");
    if (!disposed) fetchNow();
  };

  function render(): void {
    const comfort = last;
    if (!comfort) return;
    const members = session.store.home?.members ?? [];
    const rec = comfort.recommendation;
    const appliance = session.store.getAppliance(applianceId);
    const canApply = appliance !== undefined && recommendationCommands(rec, appliance).length > 0;
    const note = conflictNote(comfort, members);
    const applyButton = canApply
      ? el("button", { type: "button", class: "button button--primary", "data-key": "comfort-apply", disabled: applying }, [icon("check", "icon icon--sm"), c.apply])
      : null;
    applyButton?.addEventListener("click", () => {
      void apply(comfort);
    });

    setChildren(element, [
      el("div", { class: "ev-comfort__head" }, [
        el("h2", { id: "comfort-title", class: "ev-panel__title" }, [icon("thermo", "icon icon--sm"), c.title]),
        el("span", { class: `ev-pill ev-pill--${comfort.inBand ? "finished" : "paused"}` }, [icon(comfort.inBand ? "check" : "alert", "icon icon--xs"), feelingLabel(comfort.feeling)]),
      ]),
      el("p", { class: "ev-comfort__band" }, [comfort.inBand ? c.inBand : c.outOfBand, ` · ${formatTemp(comfort.roomTempC)}`]),
      comfort.members.length > 0 && el("p", { class: "ev-muted" }, [c.forMembers(comfort.members.map((id) => memberName(members, id)).join(", "))]),
      el("div", { class: "ev-comfort__rec" }, [
        el("p", { class: "ev-comfort__label" }, [c.recommendation]),
        el("p", { class: "ev-comfort__action" }, [
          rec.action === "none" ? c.nothingToDo : actionLabel(rec.action),
          rec.action === "setpoint" && rec.setpoint !== null && ` → ${formatTemp(rec.setpoint)}`,
        ]),
        rec.reason !== "" && el("p", { class: "ev-comfort__reason" }, [rec.reason]),
        rec.costPerHourTl !== null && el("p", { class: "ev-muted" }, [c.costPerHour(formatTl(rec.costPerHourTl))]),
        applyButton,
      ]),
      note !== undefined && el("p", { class: "ev-comfort__conflict", role: "note" }, [icon("sparkle", "icon icon--xs"), note]),
      rec.alternatives.length > 0 &&
        el("div", {}, [
          el("p", { class: "ev-comfort__label" }, [c.alternatives]),
          el(
            "ul",
            { class: "ev-comfort__alts" },
            rec.alternatives.map((a) =>
              el("li", {}, [c.alternative(actionLabel(a.action), a.costPerHourTl === null ? "—" : formatTl(a.costPerHourTl))]),
            ),
          ),
        ]),
    ]);
  }

  function fetchNow(): void {
    void session.backend().then(
      async (b) => {
        try {
          const comfort = await b.slice2.getComfort(applianceId);
          if (disposed) return;
          last = comfort;
          loaded = true;
          render();
        } catch (error) {
          if (disposed || loaded) return;
          element.replaceChildren(el("p", { class: "ev-muted" }, [c.loadError, " ", commandErrorMessage(error)]));
        }
      },
      () => undefined,
    );
  }

  renderLoading(element, c.loading);
  fetchNow();

  return {
    element,
    refresh: (delayMs = COMFORT_DEBOUNCE_MS) => {
      if (timer !== undefined) clearTimeout(timer);
      timer = setTimeout(fetchNow, delayMs);
    },
    dispose: () => {
      disposed = true;
      if (timer !== undefined) clearTimeout(timer);
    },
  };
}

// ---------- energy ----------

/** Today's kWh and TL of one appliance. */
export function createEnergyPanel(session: HomeSession, applianceId: string): Panel {
  const element = el("div", { class: "ev-tile ev-tile--wide" });
  let disposed = false;
  let timer: ReturnType<typeof setTimeout> | undefined;

  function fetchNow(): void {
    void session.backend().then(
      async (b) => {
        try {
          const energy = await b.slice2.getEnergy();
          if (disposed) return;
          const mine = energy.perDevice.find((d) => d.applianceId === applianceId);
          element.replaceChildren(
            icon("chart", "icon ev-tile__icon"),
            el("span", { class: "ev-tile__label" }, [tr.home.energy.title]),
            el("span", { class: "ev-tile__value" }, [mine ? tr.home.energy.today(formatKwh(mine.todayKwh), formatTl(mine.todayTl)) : "—"]),
          );
        } catch {
          if (!disposed && element.childElementCount === 0) element.replaceChildren(el("span", { class: "ev-muted" }, [tr.home.energy.loadError]));
        }
      },
      () => undefined,
    );
  }
  fetchNow();

  return {
    element,
    refresh: (delayMs = 3000) => {
      if (timer !== undefined) clearTimeout(timer);
      timer = setTimeout(fetchNow, delayMs);
    },
    dispose: () => {
      disposed = true;
      if (timer !== undefined) clearTimeout(timer);
    },
  };
}

// ---------- cheapest hour ----------

type CheapestPhase = { kind: "idle" } | { kind: "loading" } | { kind: "error" } | { kind: "proposal"; programId: string; result: CheapestTime };

export interface CheapestPanel {
  element: HTMLElement;
  /** Re-renders for the current appliance state and selected program. */
  update(appliance: Appliance, programId: string | undefined): void;
}

const SEARCH_WINDOW_MS = 12 * 60 * 60 * 1000;

/** "En ucuz saatte başlat": asks for the cheapest start time, shows start time and saving, then schedules the program. */
export function createCheapestPanel(session: HomeSession, send: (appliance: Appliance, command: DeviceCommand) => Promise<boolean>): CheapestPanel {
  const t = tr.home.cheapest;
  const element = el("div", { class: "ev-cheapest", "aria-live": "polite" });
  let phase: CheapestPhase = { kind: "idle" };
  let currentAppliance: Appliance | undefined;
  let currentProgram: string | undefined;

  function render(): void {
    const appliance = currentAppliance;
    const programId = currentProgram;
    if (!appliance) return;
    const busy = appliance.state.status === "running" || appliance.state.status === "paused";

    if (phase.kind === "proposal") {
      const { result, programId: proposed } = phase;
      const confirm = el("button", { type: "button", class: "button button--primary", "data-key": "cheapest-confirm" }, [icon("clock", "icon icon--sm"), t.confirm]);
      const cancel = el("button", { type: "button", class: "button button--ghost" }, [t.cancel]);
      cancel.addEventListener("click", () => {
        phase = { kind: "idle" };
        render();
      });
      confirm.addEventListener("click", () => {
        confirm.disabled = true;
        const target = currentAppliance;
        if (!target) return;
        void send(target, { type: "start_program", value: { program: proposed, temperatureC: defaultProgramTemp(proposed), startAtUtc: result.startAtUtc } }).then((ok) => {
          if (ok) showToast(t.scheduled(formatClock(result.startAtUtc)), "success");
          phase = ok ? { kind: "idle" } : phase;
          render();
        });
      });
      element.replaceChildren(
        el("div", { class: "ev-cheapest__proposal" }, [
          el("p", { class: "ev-cheapest__text" }, [t.proposal(formatClock(result.startAtUtc), formatTl(result.costTl), formatTl(result.nowCostTl))]),
          el("p", { class: `ev-cheapest__saving ${result.savingTl > 0 ? "" : "ev-muted"}` }, [result.savingTl > 0 ? t.saving(formatTl(result.savingTl)) : t.noSaving]),
          el("p", { class: "ev-muted" }, [programLabel(proposed)]),
          el("div", { class: "ev-actions" }, [confirm, cancel]),
        ]),
      );
      return;
    }

    const button = el("button", { type: "button", class: "button button--secondary", "data-key": "cheapest", disabled: busy || programId === undefined || phase.kind === "loading" }, [
      icon("leaf", "icon icon--sm"),
      phase.kind === "loading" ? t.searching : t.button,
    ]);
    button.addEventListener("click", () => {
      if (programId === undefined) return;
      phase = { kind: "loading" };
      render();
      void session.backend().then(
        async (b) => {
          try {
            const now = Date.parse(session.s2.context?.simulatedNowUtc ?? "");
            const deadline = new Date((Number.isNaN(now) ? Date.now() : now) + SEARCH_WINDOW_MS).toISOString();
            const result = await b.slice2.getCheapestTime(appliance.id, programId, deadline);
            phase = { kind: "proposal", programId, result };
          } catch {
            phase = { kind: "error" };
          }
          render();
        },
        () => {
          phase = { kind: "error" };
          render();
        },
      );
    });
    element.replaceChildren(button, el("p", { class: phase.kind === "error" ? "field__error" : "ev-muted", role: phase.kind === "error" ? "alert" : undefined }, [phase.kind === "error" ? t.error : t.deadlineHint]));
  }

  return {
    element,
    update: (appliance, programId) => {
      const focus = (document.activeElement as HTMLElement | null)?.dataset.key;
      currentAppliance = appliance;
      currentProgram = programId;
      render();
      if (focus !== undefined && !element.contains(document.activeElement)) element.querySelector<HTMLElement>(`[data-key="${CSS.escape(focus)}"]`)?.focus();
    },
  };
}
