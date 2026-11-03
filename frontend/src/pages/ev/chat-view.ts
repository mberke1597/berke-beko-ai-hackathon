// Chat bubbles, action chips and the "Nasıl karar verdi?" timeline (Asistan tab and voice overlay).

import { el } from "../../components/dom.ts";
import { actionChip, formatMs, formatTokens, formatUsd, stepStatusLabel, totalsLine } from "../../home/chat/format.ts";
import { stepTree, type AssistantEntry, type ChatEntry } from "../../home/chat/reducer.ts";
import type { Step } from "../../home/chat/types.ts";
import { icon, type IconName } from "../../home/icons.ts";
import type { HomeStore } from "../../home/store.ts";
import { tr } from "../../i18n/tr.ts";
import { devicePath } from "../../paths.ts";
import { hrefFor } from "../../router.ts";

export interface ChatViewDeps {
  store: HomeStore;
  /** Loads steps of a past run when its timeline is opened. */
  loadRun?: (runId: string) => Promise<void>;
}

export function renderActionChips(entry: AssistantEntry, store: HomeStore): HTMLElement | null {
  if (entry.actions.length === 0) return null;
  return el(
    "div",
    { class: "chat-chips" },
    entry.actions.map((action) => {
      const chip = actionChip(action, store.getAppliance(action.applianceId));
      return el(
        "a",
        {
          class: `chat-chip ${chip.accepted ? "" : "chat-chip--rejected"}`,
          href: hrefFor(devicePath(chip.applianceId)),
          "aria-label": tr.home.chat.openDevice(chip.text),
        },
        [icon(chip.icon, "icon icon--sm"), el("span", {}, [chip.text])],
      );
    }),
  );
}

function thinking(): HTMLElement {
  return el("span", { class: "chat-thinking", role: "status" }, [
    el("span", { class: "chat-thinking__dots", "aria-hidden": "true" }, [el("span"), el("span"), el("span")]),
    tr.home.chat.thinking,
  ]);
}

const stepIcon: Record<Step["status"], IconName> = { pending: "idle", running: "running", completed: "check", failed: "alert" };

function renderStep(step: Step, depth: number): HTMLElement {
  const meta = [formatMs(step.durationMs), formatTokens(step.tokens), step.costUsd === null ? undefined : formatUsd(step.costUsd)].filter(
    (x): x is string => x !== undefined,
  );
  return el("li", { class: `chat-step chat-step--${step.status}`, style: `--depth: ${String(depth)}` }, [
    el("span", { class: "chat-step__head" }, [
      icon(stepIcon[step.status], "icon icon--xs"),
      el("span", { class: "chat-step__name" }, [step.name]),
      el("span", { class: "chat-step__status" }, [stepStatusLabel(step)]),
    ]),
    step.summary !== null && step.summary !== "" && el("span", { class: "chat-step__summary" }, [step.summary]),
    el("span", { class: "chat-step__meta" }, [meta.join(" · ")]),
  ]);
}

export function renderTimeline(entry: AssistantEntry, deps: ChatViewDeps, open: boolean): HTMLDetailsElement | null {
  if (entry.runId === null) return null;
  const runId = entry.runId;
  const body = el("div", { class: "chat-timeline__body" });
  const fill = (): void => {
    if (entry.steps.length === 0) {
      body.replaceChildren(el("p", { class: "ev-muted" }, [entry.fromHistory ? tr.home.chat.timeline.loading : tr.home.chat.timeline.empty]));
      return;
    }
    const totals = totalsLine(entry.totals);
    const list = el(
      "ol",
      { class: "chat-steps" },
      stepTree(entry.steps).map(({ step, depth }) => renderStep(step, depth)),
    );
    body.replaceChildren(list);
    if (totals !== undefined) body.append(el("p", { class: "chat-timeline__totals" }, [totals]));
  };
  fill();
  const details = el("details", { class: "chat-timeline", "data-entry": entry.id, open }, [
    el("summary", { class: "chat-timeline__summary" }, [icon("chart", "icon icon--xs"), tr.home.chat.timeline.title]),
    body,
  ]);
  details.addEventListener("toggle", () => {
    if (details.open && entry.fromHistory && entry.steps.length === 0 && deps.loadRun) {
      deps.loadRun(runId).catch(() => {
        body.replaceChildren(el("p", { class: "ev-muted" }, [tr.home.chat.timeline.error]));
      });
    }
  });
  return details;
}

/** One chat entry. `openTimelines` keeps expanded timelines open across re-renders. */
export function renderEntry(entry: ChatEntry, deps: ChatViewDeps, openTimelines: ReadonlySet<string>): HTMLElement {
  if (entry.role === "user") {
    return el("div", { class: "chat-row chat-row--user", "data-entry": entry.id }, [
      el("p", { class: "chat-bubble chat-bubble--user" }, [
        entry.channel === "voice" && icon("mic", "icon icon--xs chat-bubble__channel"),
        entry.text,
      ]),
    ]);
  }
  const running = entry.status === "running";
  const text = entry.text === "" && running ? thinking() : el("span", {}, [entry.text]);
  return el("div", { class: `chat-row chat-row--assistant ${entry.status === "failed" ? "chat-row--failed" : ""}`, "data-entry": entry.id }, [
    el("div", { class: "chat-bubble chat-bubble--assistant", "aria-busy": running ? "true" : "false" }, [
      text,
      running && entry.text !== "" && el("span", { class: "chat-caret", "aria-hidden": "true" }),
    ]),
    renderActionChips(entry, deps.store),
    renderTimeline(entry, deps, openTimelines.has(entry.id)),
  ]);
}

/**
 * Keyed list renderer: rebuilds only entries whose object changed (the reducer keeps unchanged
 * entries by reference) and keeps open timelines open.
 */
export function createEntryList(container: HTMLElement, deps: ChatViewDeps): (entries: readonly ChatEntry[]) => void {
  const rendered = new Map<string, { entry: ChatEntry; node: HTMLElement }>();
  return (entries) => {
    const open = new Set<string>();
    for (const d of container.querySelectorAll<HTMLDetailsElement>("details.chat-timeline[open]")) {
      if (d.dataset.entry !== undefined) open.add(d.dataset.entry);
    }
    const nodes: HTMLElement[] = [];
    const seen = new Set<string>();
    for (const entry of entries) {
      seen.add(entry.id);
      const cached = rendered.get(entry.id);
      if (cached?.entry === entry) {
        nodes.push(cached.node);
        continue;
      }
      const node = renderEntry(entry, deps, open);
      rendered.set(entry.id, { entry, node });
      nodes.push(node);
    }
    for (const id of [...rendered.keys()]) if (!seen.has(id)) rendered.delete(id);
    // Only touch the DOM where order or nodes changed.
    nodes.forEach((node, index) => {
      if (container.children[index] !== node) container.insertBefore(node, container.children[index] ?? null);
    });
    while (container.children.length > nodes.length) container.lastElementChild?.remove();
  };
}
