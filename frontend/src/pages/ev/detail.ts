import { el } from "../../components/dom.ts";
import { sendDeviceCommand } from "../../home/actions.ts";
import { commandErrorMessage } from "../../home/api.ts";
import {
  availableProgramActions,
  canStep,
  defaultProgramTemp,
  ovenRange,
  snapToRange,
  stepValue,
  supportsCommand,
  type ProgramAction,
} from "../../home/commands.ts";
import { applianceIconName, icon, toneIconName, type IconName } from "../../home/icons.ts";
import {
  acModeLabel,
  fanLabel,
  formatDuration,
  formatNumber,
  formatPercent,
  formatTemp,
  ovenModeLabel,
  programLabel,
  stateTone,
  toneLabel,
} from "../../home/labels.ts";
import type { HomeSession } from "../../home/session.ts";
import type { Appliance, DeviceCommand, SetpointRange } from "../../home/types.ts";
import { tr } from "../../i18n/tr.ts";
import { paths } from "../../paths.ts";
import { hrefFor } from "../../router.ts";
import { createTelemetryChart } from "./chart.ts";
import { loadHome, renderLoading } from "./common.ts";
import { showToast } from "./toast.ts";

const SETPOINT_DEBOUNCE_MS = 500;

/** Local, unsaved UI state that must survive live re-renders. */
interface DetailState {
  remote: boolean;
  pendingSetpoint: number | undefined;
  program: string | undefined;
  oven: { mode: string; targetTempC: number; timerMin: number } | undefined;
  chart: HTMLElement;
}

function tile(iconName: IconName, label: string, value: string): HTMLElement {
  return el("div", { class: "ev-tile" }, [
    icon(iconName, "icon ev-tile__icon"),
    el("span", { class: "ev-tile__label" }, [label]),
    el("span", { class: "ev-tile__value" }, [value]),
  ]);
}

function section(title: string, children: (Node | false | null)[], key?: string): HTMLElement {
  const id = `detail-${key ?? title.replace(/\s+/g, "-").toLowerCase()}`;
  return el("section", { class: "ev-panel", "aria-labelledby": id }, [el("h2", { id, class: "ev-panel__title" }, [title]), ...children]);
}

function segmented(
  label: string,
  key: string,
  options: readonly string[],
  selected: string,
  toLabel: (value: string) => string,
  disabled: boolean,
  onSelect: (value: string) => void,
): HTMLElement {
  return el(
    "div",
    { class: "ev-segmented", role: "group", "aria-label": label },
    options.map((option) => {
      const button = el(
        "button",
        {
          type: "button",
          class: "ev-segmented__option",
          "aria-pressed": String(option === selected),
          "data-key": `${key}-${option}`,
          disabled,
        },
        [toLabel(option)],
      );
      button.addEventListener("click", () => {
        if (option !== selected) onSelect(option);
      });
      return button;
    }),
  );
}

function stepper(
  key: string,
  value: number,
  range: SetpointRange,
  disabled: boolean,
  labels: { decrease: string; increase: string; value: string },
  onChange: (next: number) => void,
): HTMLElement {
  const minus = el(
    "button",
    { type: "button", class: "ev-stepper__btn", "aria-label": labels.decrease, "data-key": `${key}-minus`, disabled: disabled || !canStep(value, -1, range) },
    [icon("minus")],
  );
  const plus = el(
    "button",
    { type: "button", class: "ev-stepper__btn", "aria-label": labels.increase, "data-key": `${key}-plus`, disabled: disabled || !canStep(value, 1, range) },
    [icon("plus")],
  );
  minus.addEventListener("click", () => {
    onChange(stepValue(value, -1, range));
  });
  plus.addEventListener("click", () => {
    onChange(stepValue(value, 1, range));
  });
  return el("div", { class: "ev-stepper" }, [
    minus,
    el("output", { class: "ev-stepper__value", "aria-live": "polite", "aria-label": labels.value }, [formatTemp(value)]),
    plus,
  ]);
}

/** Appliance detail screen (full screen with back arrow). Returns a cleanup function. */
export function renderDetail(main: HTMLElement, session: HomeSession, id: string): () => void {
  const screen = el("div", { class: "ev-detail" });
  main.replaceChildren(screen);
  let unsubscribe: (() => void) | undefined;
  let setpointTimer: number | undefined;
  let chartController: AbortController | undefined;

  const state: DetailState = {
    remote: false,
    pendingSetpoint: undefined,
    program: undefined,
    oven: undefined,
    chart: el("div", { class: "ev-chart-region" }),
  };

  const send = async (appliance: Appliance, command: DeviceCommand): Promise<boolean> => {
    const outcome = await sendDeviceCommand(session, appliance, command, state.remote ? "remote" : "app");
    if (!outcome.ok) showToast(outcome.message, "error");
    return outcome.ok;
  };

  const topBar = (appliance: Appliance | undefined): HTMLElement => {
    const back = el("a", { class: "ev-icon-button ev-detail__back", href: hrefFor(paths.ev), "aria-label": tr.home.detail.back }, [icon("back")]);
    const title = el("h1", { class: "ev-detail__title" }, [appliance?.displayName ?? ""]);
    const tone = appliance ? stateTone(appliance) : undefined;
    return el("div", { class: "ev-detail__bar" }, [
      back,
      title,
      appliance &&
        tone &&
        el("span", { class: `ev-pill ev-pill--${tone}` }, [icon(toneIconName(tone), "icon icon--xs"), toneLabel(appliance)]),
    ]);
  };

  const powerRow = (appliance: Appliance): HTMLElement | null => {
    if (!supportsCommand(appliance, "power")) return null;
    const on = appliance.state.power;
    const button = el(
      "button",
      {
        type: "button",
        class: `ev-power ${on ? "ev-power--on" : ""}`,
        "aria-pressed": String(on),
        "data-key": "power",
      },
      [icon("power"), on ? tr.home.detail.turnOff : tr.home.detail.turnOn],
    );
    button.addEventListener("click", () => {
      button.disabled = true;
      void send(appliance, { type: "power", value: !on }).finally(() => {
        button.disabled = false;
      });
    });
    return el("div", { class: "ev-detail__power" }, [
      el("span", { class: "ev-detail__power-label" }, [tr.home.detail.power]),
      button,
    ]);
  };

  // ---------- air conditioner ----------
  const acBody = (appliance: Appliance): HTMLElement[] => {
    const ac = appliance.state.ac;
    const caps = appliance.capabilities;
    if (!ac) return [];
    const off = !appliance.state.power;
    const range = caps.setpoint ?? { min: 16, max: 30, step: 0.5 };
    const shown = state.pendingSetpoint ?? ac.setpoint;

    const onSetpoint = (next: number): void => {
      state.pendingSetpoint = snapToRange(next, range);
      draw();
      window.clearTimeout(setpointTimer);
      setpointTimer = window.setTimeout(() => {
        const value = state.pendingSetpoint;
        const current = session.store.getAppliance(id);
        if (value === undefined || !current) return;
        void send(current, { type: "setpoint", value }).then(() => {
          state.pendingSetpoint = undefined;
          draw();
        });
      }, SETPOINT_DEBOUNCE_MS);
    };

    const remoteSwitch = el(
      "button",
      { type: "button", role: "switch", class: "ev-switch", "aria-checked": String(state.remote), "data-key": "remote", "aria-describedby": "remote-hint" },
      [el("span", { class: "ev-switch__track", "aria-hidden": "true" }, [el("span", { class: "ev-switch__thumb" })]), tr.home.detail.remote],
    );
    remoteSwitch.addEventListener("click", () => {
      state.remote = !state.remote;
      draw();
    });

    return [
      el("div", { class: `ev-dial ${state.remote ? "ev-dial--remote" : ""}` }, [
        el("span", { class: "ev-dial__label" }, [tr.home.detail.setpoint]),
        stepper("setpoint", shown, range, off || !supportsCommand(appliance, "setpoint"), {
          decrease: tr.home.detail.decrease,
          increase: tr.home.detail.increase,
          value: tr.home.detail.setpoint,
        }, onSetpoint),
        el("span", { class: "ev-dial__mode" }, [off ? tr.home.status.off : acModeLabel(ac.mode)]),
      ]),
      el("div", { class: "ev-tiles" }, [
        tile("thermo", tr.home.detail.roomTemp, formatTemp(ac.roomTempC)),
        tile("drop", tr.home.detail.humidity, formatPercent(ac.humidityPct)),
        tile("power", tr.home.detail.powerDraw, tr.home.detail.watt(formatNumber(appliance.state.powerW))),
      ]),
      section(tr.home.detail.mode, [
        segmented(tr.home.detail.mode, "mode", caps.modes, ac.mode, acModeLabel, off || !supportsCommand(appliance, "mode"), (mode) => {
          void send(appliance, { type: "mode", value: mode });
        }),
      ], "mode"),
      caps.fanLevels.length > 0
        ? section(tr.home.detail.fan, [
            segmented(tr.home.detail.fan, "fan", caps.fanLevels, ac.fan, fanLabel, off || !supportsCommand(appliance, "fan"), (fan) => {
              void send(appliance, { type: "fan", value: fan });
            }),
          ], "fan")
        : el("span"),
      section(tr.home.detail.chartTitle, [state.chart], "chart"),
      el("div", { class: "ev-panel ev-remote" }, [
        el("div", { class: "ev-remote__head" }, [icon("remote", "icon"), remoteSwitch]),
        el("p", { id: "remote-hint", class: "ev-remote__hint" }, [tr.home.detail.remoteHint]),
      ]),
    ];
  };

  // ---------- washer / dishwasher ----------
  const programBody = (appliance: Appliance): HTMLElement[] => {
    const s = appliance.state;
    const p = s.program;
    const caps = appliance.capabilities;
    const active = s.status === "running" || s.status === "paused";
    const actions = availableProgramActions(appliance);
    state.program ??= p?.program ?? caps.programs[0];

    const info: HTMLElement[] = [];
    if (p && (active || s.status === "finished")) {
      info.push(
        el("div", { class: "ev-tiles" }, [
          tile("clock", tr.home.detail.remaining, s.status === "finished" ? tr.home.card.finished : formatDuration(p.remainingMin)),
          tile("washer", tr.home.detail.program, programLabel(p.program)),
          tile("thermo", tr.home.detail.temperature, p.temperatureC === null ? "—" : formatTemp(p.temperatureC)),
        ]),
        el(
          "div",
          {
            class: "ev-progress",
            role: "progressbar",
            "aria-valuemin": "0",
            "aria-valuemax": "100",
            "aria-valuenow": String(p.progressPct),
            "aria-label": tr.home.detail.progress,
          },
          [el("span", { class: "ev-progress__bar", style: `width: ${String(p.progressPct)}%` })],
        ),
      );
    }

    let programPicker: HTMLElement;
    if (caps.programs.length === 0) {
      programPicker = el("p", { class: "ev-muted" }, [tr.home.detail.noPrograms]);
    } else {
      const select = el(
        "select",
        { id: "program-select", class: "input ev-select", "data-key": "program", disabled: active },
        caps.programs.map((program) => el("option", { value: program, selected: program === state.program }, [programLabel(program)])),
      );
      select.addEventListener("change", () => {
        state.program = select.value;
      });
      programPicker = el("div", { class: "field" }, [el("label", { class: "field__label", for: "program-select" }, [tr.home.detail.program]), select]);
    }

    const actionIcon: Record<ProgramAction, IconName> = { start: "play", pause: "pause", resume: "play", stop: "stop" };
    const actionLabel: Record<ProgramAction, string> = {
      start: tr.home.detail.start,
      pause: tr.home.detail.pause,
      resume: tr.home.detail.resume,
      stop: tr.home.detail.stop,
    };
    const buttons = actions.map((action) => {
      const button = el(
        "button",
        { type: "button", class: `button ${action === "stop" ? "button--secondary" : "button--primary"}`, "data-key": `action-${action}` },
        [icon(actionIcon[action], "icon icon--sm"), actionLabel[action]],
      );
      button.addEventListener("click", () => {
        let command: DeviceCommand;
        if (action === "start") {
          const program = state.program;
          if (program === undefined) return;
          command = { type: "start_program", value: { program, temperatureC: defaultProgramTemp(program), startAtUtc: null } };
        } else {
          command = { type: action, value: null };
        }
        button.disabled = true;
        void send(appliance, command).finally(() => {
          button.disabled = false;
        });
      });
      return button;
    });

    const cheapest = el("button", { type: "button", class: "button button--ghost", disabled: true, "aria-describedby": "cheapest-hint" }, [
      icon("leaf", "icon icon--sm"),
      tr.home.detail.cheapest,
    ]);

    return [
      ...info,
      section(tr.home.detail.program, [
        programPicker,
        el("div", { class: "ev-actions" }, buttons),
        el("div", { class: "ev-cheapest" }, [cheapest, el("p", { id: "cheapest-hint", class: "ev-muted" }, [tr.home.detail.cheapestSoon])]),
      ], "program"),
    ];
  };

  // ---------- oven ----------
  const ovenBody = (appliance: Appliance): HTMLElement[] => {
    const o = appliance.state.oven;
    if (!o) return [];
    const caps = appliance.capabilities;
    const range = ovenRange(appliance);
    state.oven ??= { mode: o.mode, targetTempC: snapToRange(o.targetTempC, range), timerMin: o.timerMin ?? 0 };
    const form = state.oven;
    const running = appliance.state.status === "running" || appliance.state.status === "paused";

    const timer = el("input", {
      id: "oven-timer",
      class: "input ev-number",
      type: "number",
      inputmode: "numeric",
      min: "0",
      max: "600",
      step: "5",
      value: String(form.timerMin),
      "data-key": "timer",
    });
    timer.addEventListener("input", () => {
      const value = Math.round(Number(timer.value));
      form.timerMin = Number.isFinite(value) && value > 0 ? Math.min(600, value) : 0;
    });

    const apply = el("button", { type: "button", class: "button button--primary", "data-key": "apply-oven" }, [tr.home.detail.apply]);
    apply.addEventListener("click", () => {
      apply.disabled = true;
      void send(appliance, {
        type: "set_oven",
        value: { mode: form.mode, targetTempC: form.targetTempC, timerMin: form.timerMin > 0 ? form.timerMin : null },
      }).finally(() => {
        apply.disabled = false;
      });
    });
    const stop = running && supportsCommand(appliance, "stop")
      ? el("button", { type: "button", class: "button button--secondary", "data-key": "action-stop" }, [icon("stop", "icon icon--sm"), tr.home.detail.stop])
      : null;
    stop?.addEventListener("click", () => {
      void send(appliance, { type: "stop", value: null });
    });

    return [
      el("div", { class: "ev-tiles" }, [
        tile("thermo", tr.home.detail.currentTemp, formatTemp(o.currentTempC)),
        tile("oven", tr.home.detail.ovenMode, ovenModeLabel(o.mode)),
        tile("clock", tr.home.detail.timerTile, o.timerMin === null || o.timerMin === 0 ? "—" : formatDuration(o.timerMin)),
      ]),
      el("p", { class: "ev-muted", role: "status", hidden: !running }, [
        o.currentTempC < o.targetTempC ? tr.home.detail.preheating : tr.home.detail.preheated,
      ]),
      section(tr.home.detail.ovenMode, [
        segmented(tr.home.detail.ovenMode, "oven-mode", caps.modes, form.mode, ovenModeLabel, false, (mode) => {
          form.mode = mode;
          draw();
        }),
        el("div", { class: "ev-dial ev-dial--compact" }, [
          el("span", { class: "ev-dial__label" }, [tr.home.detail.targetTemp]),
          stepper("oven-temp", form.targetTempC, range, false, {
            decrease: tr.home.detail.decrease,
            increase: tr.home.detail.increase,
            value: tr.home.detail.targetTemp,
          }, (next) => {
            form.targetTempC = next;
            draw();
          }),
        ]),
        el("div", { class: "field" }, [el("label", { class: "field__label", for: "oven-timer" }, [tr.home.detail.timer]), timer]),
        el("div", { class: "ev-actions" }, [apply, stop]),
      ], "oven"),
    ];
  };

  // ---------- refrigerator ----------
  const fridgeBody = (appliance: Appliance): HTMLElement[] => {
    const f = appliance.state.fridge;
    if (!f) return [];
    return [
      el("div", { class: "ev-tiles" }, [
        tile("fridge", tr.home.detail.fridgeTemp, formatTemp(f.fridgeTempC)),
        tile("thermo", tr.home.detail.freezerTemp, formatTemp(f.freezerTempC)),
        tile(f.doorOpen ? "alert" : "check", tr.home.detail.door, f.doorOpen ? tr.home.detail.doorOpen : tr.home.detail.doorClosed),
      ]),
      el("p", { class: "ev-muted" }, [tr.home.detail.readOnly]),
    ];
  };

  const loadChart = (appliance: Appliance): void => {
    if (appliance.type !== "air_conditioner") return;
    chartController?.abort();
    const controller = new AbortController();
    chartController = controller;
    renderLoading(state.chart, tr.home.detail.chartLoading);
    void session
      .api()
      .then((api) => api.getTelemetry(appliance.id, { signal: controller.signal }))
      .then(
        (points) => {
          if (!controller.signal.aborted) state.chart.replaceChildren(createTelemetryChart(points));
        },
        (error: unknown) => {
          if (controller.signal.aborted) return;
          state.chart.replaceChildren(el("p", { class: "ev-muted" }, [tr.home.detail.chartError, " ", commandErrorMessage(error)]));
        },
      );
  };

  function draw(): void {
    const appliance = session.store.getAppliance(id);
    if (!appliance) {
      screen.replaceChildren(
        topBar(undefined),
        el("div", { class: "ev-detail__body" }, [
          el("p", { class: "ev-muted", role: "alert" }, [tr.home.detail.notFound]),
          el("a", { class: "button button--primary", href: hrefFor(paths.ev) }, [tr.home.detail.backHome]),
        ]),
      );
      return;
    }

    // Remember focus so live updates do not steal it.
    const focusKey = (document.activeElement as HTMLElement | null)?.dataset.key;

    let body: HTMLElement[];
    switch (appliance.type) {
      case "air_conditioner":
        body = acBody(appliance);
        break;
      case "washing_machine":
      case "dishwasher":
        body = programBody(appliance);
        break;
      case "oven":
        body = ovenBody(appliance);
        break;
      case "refrigerator":
        body = fridgeBody(appliance);
        break;
    }

    screen.replaceChildren(
      topBar(appliance),
      el("div", { class: `ev-detail__body ev-detail__body--${appliance.type}` }, [
        el("div", { class: "ev-detail__hero" }, [
          el("span", { class: `ev-detail__icon ev-detail__icon--${stateTone(appliance)}` }, [icon(applianceIconName(appliance.type), "icon icon--xl")]),
          el("span", { class: "ev-detail__model" }, [`${tr.home.detail.model}: ${appliance.model}`]),
        ]),
        powerRow(appliance),
        ...body,
      ]),
    );

    if (focusKey !== undefined) {
      const target = screen.querySelector<HTMLElement>(`[data-key="${CSS.escape(focusKey)}"]`);
      if (target && !(target as HTMLButtonElement).disabled) target.focus();
    }
  }

  loadHome(session, screen, () => {
    draw();
    const appliance = session.store.getAppliance(id);
    if (appliance) loadChart(appliance);
    unsubscribe = session.store.subscribe((change) => {
      if ((change.kind === "appliance" && change.appliance.id === id) || change.kind === "home") draw();
    });
  });

  return () => {
    unsubscribe?.();
    window.clearTimeout(setpointTimer);
    // Do not lose a setpoint change that is still waiting for the debounce.
    const pending = state.pendingSetpoint;
    const current = session.store.getAppliance(id);
    if (pending !== undefined && current) void send(current, { type: "setpoint", value: pending });
    chartController?.abort();
  };
}
