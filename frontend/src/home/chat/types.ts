// Types and decoders for "Phase 2 — agent runtime, chat, runs" (docs/architecture/contracts.md).

export const CELIKNAZ_AGENT_ID = "celiknaz";

export type Channel = "voice" | "text";
export type RunStatus = "running" | "completed" | "failed";
export type StepStatus = "pending" | "running" | "completed" | "failed";

/** One `control_device` call made by the agent. */
export interface ChatAction {
  applianceId: string;
  command: string;
  value: unknown;
  accepted: boolean;
}

export interface Totals {
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  costUsd: number;
  durationMs: number;
}

export interface Step {
  stepId: string;
  parentStepId: string | null;
  type: string;
  name: string;
  status: StepStatus;
  startedAtUtc: string | null;
  durationMs: number | null;
  tokens: { input: number; output: number } | null;
  costUsd: number | null;
  summary: string | null;
  spanId: string | null;
}

export interface ChatMessage {
  messageId: string;
  role: "user" | "assistant";
  text: string;
  runId: string | null;
  createdAtUtc: string;
  actions: ChatAction[];
}

export interface ConversationCreated {
  conversationId: string;
  agentId: string;
  createdAtUtc: string;
}

export interface MessageAccepted {
  runId: string;
  traceId: string;
}

export interface RunDetail {
  runId: string;
  conversationId: string;
  agentId: string;
  status: RunStatus;
  traceId: string;
  durationMs: number | null;
  totals: Totals | null;
  steps: Step[];
}

/** RunHub server-to-client events, tagged with the hub method name. */
export type RunEvent =
  | { kind: "RunStarted"; runId: string; traceId: string }
  | { kind: "StepStarted"; runId: string; step: Step }
  | { kind: "StepCompleted"; runId: string; step: Step }
  | { kind: "StepFailed"; runId: string; step: Step; error: string }
  | { kind: "TextDelta"; runId: string; delta: string }
  | { kind: "RunCompleted"; runId: string; text: string; actions: ChatAction[]; totals: Totals | null };

export type RunEventKind = RunEvent["kind"];

export const RUN_EVENT_KINDS: readonly RunEventKind[] = [
  "RunStarted",
  "StepStarted",
  "StepCompleted",
  "StepFailed",
  "TextDelta",
  "RunCompleted",
];

// ---------- decoders ----------

type Rec = Record<string, unknown>;

const isRecord = (v: unknown): v is Rec => typeof v === "object" && v !== null && !Array.isArray(v);
const isString = (v: unknown): v is string => typeof v === "string";
const num = (v: unknown, fallback = 0): number => (typeof v === "number" && Number.isFinite(v) ? v : fallback);
const numOrNull = (v: unknown): number | null => (typeof v === "number" && Number.isFinite(v) ? v : null);
const strOrNull = (v: unknown): string | null => (isString(v) ? v : null);

function list<T>(value: unknown, decode: (v: unknown) => T | undefined): T[] | undefined {
  if (value === undefined || value === null) return [];
  if (!Array.isArray(value)) return undefined;
  const out: T[] = [];
  for (const item of value) {
    const decoded = decode(item);
    if (decoded === undefined) return undefined;
    out.push(decoded);
  }
  return out;
}

export function decodeAction(value: unknown): ChatAction | undefined {
  if (!isRecord(value) || !isString(value.applianceId) || !isString(value.command)) return undefined;
  return { applianceId: value.applianceId, command: value.command, value: value.value ?? null, accepted: value.accepted !== false };
}

export function decodeTotals(value: unknown): Totals | null {
  if (!isRecord(value)) return null;
  return {
    inputTokens: num(value.inputTokens),
    outputTokens: num(value.outputTokens),
    cacheReadTokens: num(value.cacheReadTokens),
    cacheWriteTokens: num(value.cacheWriteTokens),
    costUsd: num(value.costUsd),
    durationMs: num(value.durationMs),
  };
}

const STEP_STATUSES: readonly StepStatus[] = ["pending", "running", "completed", "failed"];

export function decodeStep(value: unknown): Step | undefined {
  if (!isRecord(value) || !isString(value.stepId)) return undefined;
  const status = STEP_STATUSES.includes(value.status as StepStatus) ? (value.status as StepStatus) : "running";
  const tokens = isRecord(value.tokens) ? { input: num(value.tokens.input), output: num(value.tokens.output) } : null;
  return {
    stepId: value.stepId,
    parentStepId: strOrNull(value.parentStepId),
    type: isString(value.type) ? value.type : "",
    name: isString(value.name) ? value.name : isString(value.type) ? value.type : value.stepId,
    status,
    startedAtUtc: strOrNull(value.startedAtUtc),
    durationMs: numOrNull(value.durationMs),
    tokens,
    costUsd: numOrNull(value.costUsd),
    summary: strOrNull(value.summary),
    spanId: strOrNull(value.spanId),
  };
}

export function decodeMessage(value: unknown): ChatMessage | undefined {
  if (!isRecord(value) || (value.role !== "user" && value.role !== "assistant") || !isString(value.text)) return undefined;
  const actions = list(value.actions, decodeAction);
  if (!actions) return undefined;
  return {
    messageId: isString(value.messageId) ? value.messageId : "",
    role: value.role,
    text: value.text,
    runId: strOrNull(value.runId),
    createdAtUtc: isString(value.createdAtUtc) ? value.createdAtUtc : "",
    actions,
  };
}

export function decodeMessages(value: unknown): ChatMessage[] | undefined {
  return Array.isArray(value) ? list(value, decodeMessage) : undefined;
}

export function decodeConversationCreated(value: unknown): ConversationCreated | undefined {
  if (!isRecord(value) || !isString(value.conversationId)) return undefined;
  return {
    conversationId: value.conversationId,
    agentId: isString(value.agentId) ? value.agentId : CELIKNAZ_AGENT_ID,
    createdAtUtc: isString(value.createdAtUtc) ? value.createdAtUtc : "",
  };
}

export function decodeMessageAccepted(value: unknown): MessageAccepted | undefined {
  if (!isRecord(value) || !isString(value.runId)) return undefined;
  return { runId: value.runId, traceId: isString(value.traceId) ? value.traceId : "" };
}

const RUN_STATUSES: readonly RunStatus[] = ["running", "completed", "failed"];

export function decodeRunDetail(value: unknown): RunDetail | undefined {
  if (!isRecord(value) || !isString(value.runId)) return undefined;
  const steps = list(value.steps, decodeStep);
  if (!steps) return undefined;
  return {
    runId: value.runId,
    conversationId: isString(value.conversationId) ? value.conversationId : "",
    agentId: isString(value.agentId) ? value.agentId : "",
    status: RUN_STATUSES.includes(value.status as RunStatus) ? (value.status as RunStatus) : "running",
    traceId: isString(value.traceId) ? value.traceId : "",
    durationMs: numOrNull(value.durationMs),
    totals: decodeTotals(value.totals),
    steps,
  };
}

/** Decodes a RunHub payload for the given hub method. */
export function decodeRunEvent(kind: RunEventKind, payload: unknown): RunEvent | undefined {
  if (!isRecord(payload) || !isString(payload.runId)) return undefined;
  const runId = payload.runId;
  switch (kind) {
    case "RunStarted":
      return { kind, runId, traceId: isString(payload.traceId) ? payload.traceId : "" };
    case "StepStarted":
    case "StepCompleted": {
      const step = decodeStep(payload.step);
      return step ? { kind, runId, step } : undefined;
    }
    case "StepFailed": {
      const step = decodeStep(payload.step);
      return step ? { kind, runId, step: { ...step, status: "failed" }, error: isString(payload.error) ? payload.error : "" } : undefined;
    }
    case "TextDelta":
      return isString(payload.delta) ? { kind, runId, delta: payload.delta } : undefined;
    case "RunCompleted": {
      const actions = list(payload.actions, decodeAction);
      if (!actions) return undefined;
      return { kind, runId, text: isString(payload.text) ? payload.text : "", actions, totals: decodeTotals(payload.totals) };
    }
  }
}
