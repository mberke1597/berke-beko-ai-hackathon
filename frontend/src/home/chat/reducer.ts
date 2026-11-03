// Pure reducer for the Çeliknaz chat: history, local sends and RunHub events. Unit tested.

import { tr } from "../../i18n/tr.ts";
import type { Channel, ChatAction, ChatMessage, RunEvent, RunStatus, Step, Totals } from "./types.ts";

export interface UserEntry {
  role: "user";
  id: string;
  text: string;
  channel: Channel | null;
}

export interface AssistantEntry {
  role: "assistant";
  id: string;
  /** Local id of the send that created this entry (until the run id is known). */
  localId: string | null;
  runId: string | null;
  text: string;
  status: RunStatus;
  actions: ChatAction[];
  steps: Step[];
  totals: Totals | null;
  /** Loaded from GET messages: steps come from GET /api/runs/{id} when needed. */
  fromHistory: boolean;
}

export type ChatEntry = UserEntry | AssistantEntry;

export interface ChatState {
  entries: ChatEntry[];
}

export type ChatEvent =
  | { kind: "history"; messages: ChatMessage[] }
  | { kind: "sent"; localId: string; text: string; channel: Channel }
  | { kind: "accepted"; localId: string; runId: string }
  | { kind: "failed"; localId?: string; runId?: string }
  | { kind: "runDetail"; runId: string; steps: Step[]; totals: Totals | null }
  | RunEvent;

export const emptyChat: ChatState = { entries: [] };

function upsertStep(steps: readonly Step[], step: Step): Step[] {
  const index = steps.findIndex((s) => s.stepId === step.stepId);
  if (index < 0) return [...steps, step];
  const next = [...steps];
  const current = next[index];
  // Keep fields a later event does not repeat (e.g. summary set at start).
  next[index] = current ? { ...current, ...step, summary: step.summary ?? current.summary } : step;
  return next;
}

function newAssistant(id: string, runId: string | null, localId: string | null): AssistantEntry {
  return { role: "assistant", id, localId, runId, text: "", status: "running", actions: [], steps: [], totals: null, fromHistory: false };
}

/** Finds the assistant entry for a run; early events attach to the oldest pending send without a run id. */
function findForRun(entries: readonly ChatEntry[], runId: string): number {
  const exact = entries.findIndex((e) => e.role === "assistant" && e.runId === runId);
  if (exact >= 0) return exact;
  return entries.findIndex((e) => e.role === "assistant" && e.runId === null && e.localId !== null && e.status === "running");
}

function updateAssistant(state: ChatState, runId: string, update: (entry: AssistantEntry) => AssistantEntry): ChatState {
  const entries = [...state.entries];
  let index = findForRun(entries, runId);
  if (index < 0) {
    // A run started elsewhere (another tab or device): show it at the end.
    entries.push(newAssistant(`run:${runId}`, runId, null));
    index = entries.length - 1;
  }
  const entry = entries[index] as AssistantEntry;
  entries[index] = update({ ...entry, runId });
  return { entries };
}

export function reduceChat(state: ChatState, event: ChatEvent): ChatState {
  switch (event.kind) {
    case "history": {
      const entries: ChatEntry[] = event.messages.map((m, i) =>
        m.role === "user"
          ? { role: "user", id: m.messageId || `h${String(i)}`, text: m.text, channel: null }
          : {
              ...newAssistant(m.messageId || `h${String(i)}`, m.runId, null),
              text: m.text,
              status: "completed",
              actions: m.actions,
              fromHistory: true,
            },
      );
      // Keep sends that happened while history was loading.
      const local = state.entries.filter((e) => e.id.startsWith("local:") || e.id.startsWith("pending:"));
      return { entries: [...entries, ...local] };
    }
    case "sent":
      return {
        entries: [
          ...state.entries,
          { role: "user", id: `local:${event.localId}`, text: event.text, channel: event.channel },
          newAssistant(`pending:${event.localId}`, null, event.localId),
        ],
      };
    case "accepted": {
      const entries = [...state.entries];
      const pending = entries.findIndex((e) => e.role === "assistant" && e.localId === event.localId);
      const existing = entries.findIndex((e) => e.role === "assistant" && e.runId === event.runId);
      if (pending >= 0 && existing >= 0 && pending !== existing) {
        entries[existing] = { ...(entries[existing] as AssistantEntry), localId: event.localId };
        entries.splice(pending, 1);
      } else if (pending >= 0) {
        entries[pending] = { ...(entries[pending] as AssistantEntry), runId: event.runId };
      }
      return { entries };
    }
    case "failed": {
      const entries = state.entries.map((e) => {
        if (e.role !== "assistant") return e;
        const match = (event.runId !== undefined && e.runId === event.runId) || (event.localId !== undefined && e.localId === event.localId);
        if (!match || e.status !== "running") return e;
        return { ...e, status: "failed" as const, text: e.text || tr.home.chat.fallback };
      });
      return { entries };
    }
    case "runDetail":
      return updateAssistant(state, event.runId, (e) => ({ ...e, steps: event.steps, totals: event.totals ?? e.totals }));
    case "RunStarted":
      return updateAssistant(state, event.runId, (e) => e);
    case "StepStarted":
    case "StepCompleted":
      return updateAssistant(state, event.runId, (e) => ({ ...e, steps: upsertStep(e.steps, event.step) }));
    case "StepFailed":
      return updateAssistant(state, event.runId, (e) => ({ ...e, steps: upsertStep(e.steps, event.step) }));
    case "TextDelta":
      return updateAssistant(state, event.runId, (e) => (e.status === "running" ? { ...e, text: e.text + event.delta } : e));
    case "RunCompleted":
      return updateAssistant(state, event.runId, (e) => {
        const failed = e.steps.some((s) => s.status === "failed");
        const text = event.text || e.text || (failed ? tr.home.chat.fallback : "");
        return { ...e, text, actions: event.actions, totals: event.totals ?? e.totals, status: failed ? "failed" : "completed" };
      });
  }
}

export function findAssistant(state: ChatState, runId: string): AssistantEntry | undefined {
  return state.entries.find((e): e is AssistantEntry => e.role === "assistant" && e.runId === runId);
}

export function findByLocalId(state: ChatState, localId: string): AssistantEntry | undefined {
  return state.entries.find((e): e is AssistantEntry => e.role === "assistant" && e.localId === localId);
}

/** Steps as a tree (depth first), each with its nesting depth, for the timeline. */
export function stepTree(steps: readonly Step[]): { step: Step; depth: number }[] {
  const out: { step: Step; depth: number }[] = [];
  const ids = new Set(steps.map((s) => s.stepId));
  const visit = (parent: string | null, depth: number): void => {
    for (const step of steps) {
      const p = step.parentStepId !== null && ids.has(step.parentStepId) ? step.parentStepId : null;
      if (p === parent) {
        out.push({ step, depth });
        visit(step.stepId, depth + 1);
      }
    }
  };
  visit(null, 0);
  return out;
}
