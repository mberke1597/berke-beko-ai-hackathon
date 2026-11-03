// Fake Çeliknaz agent for VITE_USE_MOCK=true: keyword intents, streamed text, step events and real
// mock appliance changes (commands go through the mock HomeWhiz with source "ai").

import { ApiError } from "../../api/errors.ts";
import { snapToRange } from "../commands.ts";
import { acModeLabel, formatPercent, formatTemp, programWithTemp, toneLabel } from "../labels.ts";
import type { ChatApi } from "../chat/api.ts";
import type { ConnectRunLive, RunLiveHandlers } from "../chat/run-live.ts";
import type { ChatAction, ChatMessage, RunDetail, RunEvent, Step, Totals } from "../chat/types.ts";
import type { DeviceCommand } from "../types.ts";
import type { MockHome } from "./simulator.ts";

export interface MockAgentOptions {
  /** Delay between streamed events; 0 still yields to the event loop. */
  stepDelayMs?: number;
  now?: () => Date;
}

const MODEL = "claude-sonnet-5-5";
const PRICE_IN_PER_MTOK = 3;
const PRICE_OUT_PER_MTOK = 15;

interface Plan {
  applianceId?: string;
  command?: DeviceCommand;
  /** Builds the answer from the command outcome. */
  answer: (accepted: boolean) => string;
}

function cost(input: number, output: number): number {
  return Number(((input * PRICE_IN_PER_MTOK + output * PRICE_OUT_PER_MTOK) / 1_000_000).toFixed(6));
}

export interface MockAgent {
  chat: ChatApi;
  connectRunLive: ConnectRunLive;
}

export function createMockAgent(home: MockHome, options: MockAgentOptions = {}): MockAgent {
  const { stepDelayMs = 220, now = () => new Date() } = options;
  const conversations = new Map<string, ChatMessage[]>();
  const runs = new Map<string, RunDetail>();
  const listeners = new Map<string, Set<RunLiveHandlers>>();
  let counter = 0;
  const nextId = (prefix: string): string => `${prefix}-${String(Date.now())}-${String(++counter)}`;
  const sleep = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, stepDelayMs));

  const emit = (conversationId: string, event: RunEvent): void => {
    for (const l of listeners.get(conversationId) ?? []) l.event(structuredClone(event));
  };

  function plan(text: string): Plan {
    const t = text.toLocaleLowerCase("tr-TR");
    const view = home.snapshot();
    const ac = view.appliances.find((a) => a.id === "ac-salon");
    const washer = view.appliances.find((a) => a.id === "washer-1");
    const range = ac?.capabilities.setpoint ?? { min: 16, max: 30, step: 0.5 };
    const setpoint = ac?.state.ac?.setpoint ?? 25;

    if (t.includes("klima") && t.includes("kapat")) {
      return {
        applianceId: "ac-salon",
        command: { type: "power", value: false },
        answer: (ok) => (ok ? "Salon klimasını kapattım. Bu saatte yaklaşık 2 TL tasarruf eder." : "Klimayı kapatamadım."),
      };
    }
    if (/sıcak|terle|bunal|serinlet|soğut/.test(t)) {
      const value = snapToRange(setpoint - 1, range);
      return {
        applianceId: "ac-salon",
        command: { type: "setpoint", value },
        answer: (ok) => (ok ? `Salon klimasını ${formatTemp(value)}'ye indirdim. Birkaç dakikada serinler.` : "Bu ayarı uygulayamadım."),
      };
    }
    if (/soğu|üşü|ısıt|serin/.test(t)) {
      const value = snapToRange(setpoint + 1, range);
      return {
        applianceId: "ac-salon",
        command: { type: "setpoint", value },
        answer: (ok) =>
          ok ? `Salon klimasını ${formatTemp(value)}'ye aldım. Tasarruf modunda bu, günde yaklaşık 1 TL fark eder.` : "Bu ayarı uygulayamadım.",
      };
    }
    if (t.includes("çamaşır") && /başlat|çalıştır|yıka/.test(t)) {
      if (washer && (washer.state.status === "running" || washer.state.status === "paused")) {
        return { answer: () => "Çamaşır makinesi zaten çalışıyor; bitince haber veririm." };
      }
      return {
        applianceId: "washer-1",
        command: { type: "start_program", value: { program: "cotton40", temperatureC: 40, startAtUtc: null } },
        answer: (ok) => (ok ? `Çamaşır makinesini ${programWithTemp("cotton40", 40)} programında başlattım.` : "Çamaşır makinesini başlatamadım."),
      };
    }
    const ovenMatch = t.includes("fırın") ? /(\d{2,3})/.exec(t) : null;
    if (ovenMatch?.[1] !== undefined) {
      const target = snapToRange(Number(ovenMatch[1]), { min: 50, max: 250, step: 5 });
      return {
        applianceId: "oven-1",
        command: { type: "set_oven", value: { mode: "fan", targetTempC: target, timerMin: null } },
        answer: (ok) => (ok ? `Fırını fanlı modda ${formatTemp(target)}'ye ayarladım. Isınınca haber veririm.` : "Fırını ayarlayamadım."),
      };
    }
    return {
      answer: () => {
        const parts: string[] = [];
        if (ac?.state.ac) {
          parts.push(
            `Salon ${formatTemp(ac.state.ac.roomTempC)}, nem ${formatPercent(ac.state.ac.humidityPct)}; klima ${toneLabel(ac).toLocaleLowerCase("tr-TR")} (${acModeLabel(ac.state.ac.mode)}).`,
          );
        }
        if (washer) parts.push(`Çamaşır makinesi: ${toneLabel(washer).toLocaleLowerCase("tr-TR")}.`);
        parts.push("Bir şey ayarlamamı ister misiniz?");
        return parts.join(" ");
      },
    };
  }

  async function run(conversationId: string, runId: string, text: string): Promise<void> {
    const started = Date.now();
    const steps: Step[] = [];
    const detail: RunDetail = {
      runId,
      conversationId,
      agentId: "celiknaz",
      status: "running",
      traceId: runId.replace(/\D/g, "").padEnd(32, "0").slice(0, 32),
      durationMs: null,
      totals: null,
      steps,
    };
    runs.set(runId, detail);
    const put = (step: Step): Step => {
      const i = steps.findIndex((s) => s.stepId === step.stepId);
      if (i < 0) steps.push(step);
      else steps[i] = step;
      return step;
    };
    const step = (stepId: string, parentStepId: string | null, type: string, name: string, summary: string | null): Step => ({
      stepId,
      parentStepId,
      type,
      name,
      status: "running",
      startedAtUtc: now().toISOString(),
      durationMs: null,
      tokens: null,
      costUsd: null,
      summary,
      spanId: stepId.padEnd(16, "0").slice(0, 16),
    });
    const startStep = async (s: Step): Promise<number> => {
      put(s);
      emit(conversationId, { kind: "StepStarted", runId, step: s });
      await sleep();
      return Date.now();
    };
    const completeStep = (s: Step, t0: number, extra: Partial<Step> = {}): void => {
      const done = put({ ...s, ...extra, status: "completed", durationMs: Math.max(1, Date.now() - t0 + 40) });
      emit(conversationId, { kind: "StepCompleted", runId, step: done });
    };

    emit(conversationId, { kind: "RunStarted", runId, traceId: detail.traceId });

    const trigger = step(`${runId}-s1`, null, "trigger.user_message", "Kullanıcı mesajı", text.slice(0, 60));
    completeStep(trigger, await startStep(trigger));

    const agent = step(`${runId}-s2`, null, "agent.llm", "Çeliknaz", MODEL);
    const tAgent = await startStep(agent);

    const intent = plan(text);
    const llm1 = step(`${runId}-s3`, agent.stepId, "llm.call", `chat ${MODEL}`, null);
    const t1 = await startStep(llm1);
    const in1 = 1850 + text.length * 3;
    const out1 = intent.command ? 64 : 38;
    completeStep(llm1, t1, { tokens: { input: in1, output: out1 }, costUsd: cost(in1, out1), summary: `${MODEL} · tool_use` });

    const status = step(`${runId}-s4`, agent.stepId, "tool.call", "get_home_status", "get_home_status · home-1");
    completeStep(status, await startStep(status));

    let action: ChatAction | undefined;
    if (intent.command && intent.applianceId) {
      const argText = JSON.stringify(intent.command.value);
      const tool = step(`${runId}-s5`, agent.stepId, "tool.call", "control_device", `control_device · ${intent.applianceId} ${intent.command.type}=${argText}`);
      const tTool = await startStep(tool);
      let accepted = true;
      try {
        await home.api.sendCommand(intent.applianceId, { type: intent.command.type, value: intent.command.value, source: "ai", memberId: "ayse" });
      } catch {
        accepted = false;
      }
      action = { applianceId: intent.applianceId, command: intent.command.type, value: intent.command.value, accepted };
      completeStep(tool, tTool, { summary: `${tool.summary ?? ""} · ${accepted ? "202" : "422"}` });
    }

    const answer = intent.answer(action?.accepted ?? true);
    const llm2 = step(`${runId}-s6`, agent.stepId, "llm.call", `chat ${MODEL}`, null);
    const t2 = await startStep(llm2);
    // Stream the answer in small chunks.
    for (const chunk of answer.match(/\S+\s*/g) ?? [answer]) {
      emit(conversationId, { kind: "TextDelta", runId, delta: chunk });
      await new Promise((resolve) => setTimeout(resolve, stepDelayMs / 4));
    }
    const in2 = in1 + 420;
    const out2 = Math.round(answer.length / 3);
    completeStep(llm2, t2, { tokens: { input: in2, output: out2 }, costUsd: cost(in2, out2), summary: `${MODEL} · end_turn` });
    completeStep(agent, tAgent);

    const respond = step(`${runId}-s7`, null, "respond", "Yanıt", null);
    completeStep(respond, await startStep(respond));

    const llmSteps = steps.filter((s) => s.type === "llm.call");
    const totals: Totals = {
      inputTokens: llmSteps.reduce((n, s) => n + (s.tokens?.input ?? 0), 0),
      outputTokens: llmSteps.reduce((n, s) => n + (s.tokens?.output ?? 0), 0),
      cacheReadTokens: 0,
      cacheWriteTokens: 0,
      costUsd: Number(llmSteps.reduce((n, s) => n + (s.costUsd ?? 0), 0).toFixed(6)),
      durationMs: Date.now() - started,
    };
    const actions = action ? [action] : [];
    detail.status = "completed";
    detail.durationMs = totals.durationMs;
    detail.totals = totals;
    conversations.get(conversationId)?.push({
      messageId: nextId("m"),
      role: "assistant",
      text: answer,
      runId,
      createdAtUtc: now().toISOString(),
      actions,
    });
    emit(conversationId, { kind: "RunCompleted", runId, text: answer, actions, totals });
  }

  const notFound = (): ApiError => new ApiError("http", 404, { status: 404, title: "Not found" });

  const chat: ChatApi = {
    createConversation: (agentId) => {
      const conversationId = nextId("conv");
      conversations.set(conversationId, []);
      return Promise.resolve({ conversationId, agentId, createdAtUtc: now().toISOString() });
    },
    getMessages: (conversationId) => {
      const list = conversations.get(conversationId);
      return list ? Promise.resolve(structuredClone(list)) : Promise.reject(notFound());
    },
    sendMessage: (conversationId, text: string) => {
      const list = conversations.get(conversationId);
      if (!list) return Promise.reject(notFound());
      const runId = nextId("run");
      list.push({ messageId: nextId("m"), role: "user", text, runId, createdAtUtc: now().toISOString(), actions: [] });
      // The run continues in the background, like the backend (202 first).
      setTimeout(() => void run(conversationId, runId, text), 0);
      return Promise.resolve({ runId, traceId: "" });
    },
    getRun: (runId) => {
      const detail = runs.get(runId);
      return detail ? Promise.resolve(structuredClone(detail)) : Promise.reject(notFound());
    },
  };

  const connectRunLive: ConnectRunLive = (conversationId, handlers) => {
    let set = listeners.get(conversationId);
    if (!set) {
      set = new Set();
      listeners.set(conversationId, set);
    }
    set.add(handlers);
    handlers.status("connected");
    return {
      ready: Promise.resolve(),
      stop: () => {
        listeners.get(conversationId)?.delete(handlers);
      },
    };
  };

  return { chat, connectRunLive };
}
