import { describe, expect, it, vi } from "vitest";
import { createApiClient, type FetchFn } from "../../api/client.ts";
import { ApiError } from "../../api/errors.ts";
import { tr } from "../../i18n/tr.ts";
import { createMockAgent } from "../mock/agent.ts";
import { createDemoHome } from "../mock/fixtures.ts";
import { createMockHome } from "../mock/simulator.ts";
import { HomeStore } from "../store.ts";
import type { Appliance } from "../types.ts";
import { bootstrapConversation, createHttpChatApi, type ChatApi } from "./api.ts";
import { ChatController } from "./controller.ts";
import { actionChip, actionValueText, formatMs, formatUsd, totalsLine } from "./format.ts";
import { emptyChat, findAssistant, reduceChat, stepTree, type AssistantEntry, type ChatEvent, type ChatState } from "./reducer.ts";
import { isMuted, setMuted } from "./speech.ts";
import { memoryStore, safeStore, STORAGE_KEYS } from "./storage.ts";
import { decodeRunEvent, type Step } from "./types.ts";

function appliance(id: string): Appliance {
  const found = createDemoHome().appliances.find((a) => a.id === id);
  if (!found) throw new Error(id);
  return found;
}

function step(stepId: string, parentStepId: string | null, status: Step["status"] = "running", extra: Partial<Step> = {}): Step {
  return {
    stepId,
    parentStepId,
    type: "x",
    name: stepId,
    status,
    startedAtUtc: null,
    durationMs: null,
    tokens: null,
    costUsd: null,
    summary: null,
    spanId: null,
    ...extra,
  };
}

const reduceAll = (events: ChatEvent[], start: ChatState = emptyChat): ChatState => events.reduce(reduceChat, start);

describe("RunHub event reducer", () => {
  it("streams text, collects steps and finishes on RunCompleted", () => {
    const state = reduceAll([
      { kind: "sent", localId: "1", text: "Ev soğudu", channel: "voice" },
      { kind: "accepted", localId: "1", runId: "r1" },
      { kind: "RunStarted", runId: "r1", traceId: "t" },
      { kind: "StepStarted", runId: "r1", step: step("s1", null, "running", { summary: "trigger" }) },
      { kind: "StepCompleted", runId: "r1", step: step("s1", null, "completed", { durationMs: 12 }) },
      { kind: "TextDelta", runId: "r1", delta: "Salonu " },
      { kind: "TextDelta", runId: "r1", delta: "25°'ye aldım." },
    ]);
    const running = findAssistant(state, "r1");
    expect(running?.status).toBe("running");
    expect(running?.text).toBe("Salonu 25°'ye aldım.");
    expect(running?.steps).toEqual([step("s1", null, "completed", { durationMs: 12, summary: "trigger" })]);

    const action = { applianceId: "ac-salon", command: "setpoint", value: 25, accepted: true };
    const done = reduceChat(state, { kind: "RunCompleted", runId: "r1", text: "Salonu 25°'ye aldım.", actions: [action], totals: null });
    const entry = findAssistant(done, "r1");
    expect(entry?.status).toBe("completed");
    expect(entry?.actions).toEqual([action]);
    expect(done.entries.map((e) => e.role)).toEqual(["user", "assistant"]);
    // Late deltas after completion are ignored.
    expect(findAssistant(reduceChat(done, { kind: "TextDelta", runId: "r1", delta: "x" }), "r1")?.text).toBe("Salonu 25°'ye aldım.");
  });

  it("attaches events that arrive before the 202 to the pending send", () => {
    const state = reduceAll([
      { kind: "sent", localId: "1", text: "merhaba", channel: "text" },
      { kind: "RunStarted", runId: "r9", traceId: "" },
      { kind: "TextDelta", runId: "r9", delta: "Merhaba" },
      { kind: "accepted", localId: "1", runId: "r9" },
    ]);
    expect(state.entries).toHaveLength(2);
    const entry = state.entries[1] as AssistantEntry;
    expect(entry.runId).toBe("r9");
    expect(entry.localId).toBe("1");
    expect(entry.text).toBe("Merhaba");
  });

  it("shows the Turkish fallback when a run fails", () => {
    const failedStep = reduceAll([
      { kind: "sent", localId: "1", text: "x", channel: "text" },
      { kind: "accepted", localId: "1", runId: "r1" },
      { kind: "StepFailed", runId: "r1", step: step("llm", null, "failed"), error: "boom" },
      { kind: "RunCompleted", runId: "r1", text: "", actions: [], totals: null },
    ]);
    expect(findAssistant(failedStep, "r1")).toMatchObject({ status: "failed", text: tr.home.chat.fallback });

    const network = reduceAll([
      { kind: "sent", localId: "2", text: "x", channel: "text" },
      { kind: "failed", localId: "2" },
    ]);
    expect(network.entries[1]).toMatchObject({ status: "failed", text: tr.home.chat.fallback });
  });

  it("loads history and keeps sends made while it loaded", () => {
    const state = reduceAll([
      { kind: "sent", localId: "1", text: "yeni", channel: "text" },
      {
        kind: "history",
        messages: [
          { messageId: "m1", role: "user", text: "eski", runId: "r0", createdAtUtc: "", actions: [] },
          { messageId: "m2", role: "assistant", text: "cevap", runId: "r0", createdAtUtc: "", actions: [] },
        ],
      },
    ]);
    expect(state.entries.map((e) => e.id)).toEqual(["m1", "m2", "local:1", "pending:1"]);
    expect(state.entries[1]).toMatchObject({ fromHistory: true, status: "completed" });
    const withSteps = reduceChat(state, { kind: "runDetail", runId: "r0", steps: [step("a", null, "completed")], totals: null });
    expect(findAssistant(withSteps, "r0")?.steps).toHaveLength(1);
  });

  it("keeps unchanged entries by reference (keyed rendering)", () => {
    const state = reduceAll([
      { kind: "sent", localId: "1", text: "a", channel: "text" },
      { kind: "accepted", localId: "1", runId: "r1" },
    ]);
    const next = reduceChat(state, { kind: "TextDelta", runId: "r1", delta: "b" });
    expect(next.entries[0]).toBe(state.entries[0]);
    expect(next.entries[1]).not.toBe(state.entries[1]);
  });

  it("orders steps as a tree", () => {
    const tree = stepTree([step("agent", null), step("llm1", "agent"), step("respond", null), step("tool", "agent")]);
    expect(tree.map((t) => `${t.step.stepId}:${String(t.depth)}`)).toEqual(["agent:0", "llm1:1", "tool:1", "respond:0"]);
  });

  it("decodes hub payloads", () => {
    expect(decodeRunEvent("TextDelta", { runId: "r", delta: "a" })).toEqual({ kind: "TextDelta", runId: "r", delta: "a" });
    expect(decodeRunEvent("TextDelta", { runId: "r" })).toBeUndefined();
    expect(decodeRunEvent("StepFailed", { runId: "r", step: { stepId: "s", status: "running" }, error: "e" })).toMatchObject({
      step: { status: "failed" },
      error: "e",
    });
    expect(
      decodeRunEvent("RunCompleted", {
        runId: "r",
        text: "t",
        actions: [{ applianceId: "ac-salon", command: "setpoint", value: 25, accepted: true }],
        totals: { inputTokens: 10, outputTokens: 2, cacheReadTokens: 0, cacheWriteTokens: 0, costUsd: 0.001, durationMs: 900 },
      }),
    ).toMatchObject({ kind: "RunCompleted", actions: [{ applianceId: "ac-salon" }], totals: { costUsd: 0.001 } });
  });
});

describe("action chip formatting", () => {
  const ac = appliance("ac-salon");

  it("formats the example chip", () => {
    expect(actionChip({ applianceId: "ac-salon", command: "setpoint", value: 25, accepted: true }, ac)).toEqual({
      icon: "ac",
      text: "Salon Kliması → 25°",
      accepted: true,
      applianceId: "ac-salon",
    });
  });

  it.each([
    [{ command: "setpoint", value: 24.5 }, "ac-salon", "24,5°"],
    [{ command: "power", value: false }, "ac-salon", tr.home.chat.action.off],
    [{ command: "power", value: true }, "ac-salon", tr.home.chat.action.on],
    [{ command: "mode", value: "heat" }, "ac-salon", "Isıtma"],
    [{ command: "mode", value: "grill" }, "oven-1", "Izgara"],
    [{ command: "fan", value: "high" }, "ac-salon", "Fan hızı: Yüksek"],
    [{ command: "start_program", value: { program: "cotton40", temperatureC: 40 } }, "washer-1", "Pamuklu 40 başladı"],
    [{ command: "stop", value: null }, "washer-1", tr.home.chat.action.stopped],
    [{ command: "set_oven", value: { mode: "fan", targetTempC: 180, timerMin: 30 } }, "oven-1", "Fanlı · 180° · 30 dk"],
    [{ command: "unknown_cmd", value: 1 }, "ac-salon", tr.home.chat.action.changed],
  ])("%o on %s -> %s", (partial, id, text) => {
    expect(actionValueText({ applianceId: id, accepted: true, ...partial }, appliance(id))).toBe(text);
  });

  it("marks rejected actions and unknown appliances", () => {
    const chip = actionChip({ applianceId: "ghost", command: "power", value: true, accepted: false }, undefined);
    expect(chip.text).toBe(`ghost → ${tr.home.chat.action.on} (${tr.home.chat.action.rejected})`);
    expect(chip.icon).toBe("sparkle");
  });

  it("formats durations, cost and totals", () => {
    expect(formatMs(850)).toBe("850 ms");
    expect(formatMs(1530)).toBe("1,5 sn");
    expect(formatMs(null)).toBe("—");
    expect(formatUsd(0.00123)).toBe("$0.0012");
    expect(formatUsd(0.25)).toBe("$0.25");
    expect(totalsLine({ inputTokens: 100, outputTokens: 20, cacheReadTokens: 0, cacheWriteTokens: 0, costUsd: 0.0006, durationMs: 2100 })).toBe(
      "Toplam 2,1 sn · 120 token · $0.0006",
    );
  });
});

describe("conversation bootstrap", () => {
  function fakeChat(overrides: Partial<ChatApi> = {}): ChatApi {
    return {
      createConversation: vi.fn(() => Promise.resolve({ conversationId: "c-new", agentId: "celiknaz", createdAtUtc: "" })),
      getMessages: vi.fn(() => Promise.resolve([])),
      sendMessage: vi.fn(() => Promise.resolve({ runId: "r", traceId: "" })),
      getRun: vi.fn(() => Promise.reject(new Error("unused"))),
      ...overrides,
    };
  }

  it("creates a conversation for celiknaz/ayse and stores its id", async () => {
    const createConversation = vi.fn(() => Promise.resolve({ conversationId: "c-new", agentId: "celiknaz", createdAtUtc: "" }));
    const chat = fakeChat({ createConversation });
    const store = memoryStore();
    await expect(bootstrapConversation(chat, store, "ayse")).resolves.toEqual({ conversationId: "c-new", messages: [] });
    expect(createConversation).toHaveBeenCalledWith("celiknaz", "ayse");
    expect(store.get(STORAGE_KEYS.conversationId)).toBe("c-new");
  });

  it("reuses the stored conversation and loads its history", async () => {
    const messages = [{ messageId: "m", role: "user" as const, text: "a", runId: null, createdAtUtc: "", actions: [] }];
    const createConversation = vi.fn(() => Promise.reject(new Error("unused")));
    const chat = fakeChat({ getMessages: vi.fn(() => Promise.resolve(messages)), createConversation });
    const result = await bootstrapConversation(chat, memoryStore({ [STORAGE_KEYS.conversationId]: "c-old" }), "ayse");
    expect(result).toEqual({ conversationId: "c-old", messages });
    expect(createConversation).not.toHaveBeenCalled();
  });

  it("replaces a stored id the server no longer knows", async () => {
    const chat = fakeChat({ getMessages: vi.fn(() => Promise.reject(new ApiError("http", 404))) });
    const store = memoryStore({ [STORAGE_KEYS.conversationId]: "c-gone" });
    expect((await bootstrapConversation(chat, store, "ayse")).conversationId).toBe("c-new");
    expect(store.get(STORAGE_KEYS.conversationId)).toBe("c-new");
  });

  it("rethrows other errors", async () => {
    const chat = fakeChat({ getMessages: vi.fn(() => Promise.reject(new ApiError("http", 500))) });
    await expect(bootstrapConversation(chat, memoryStore({ [STORAGE_KEYS.conversationId]: "c" }), "ayse")).rejects.toBeInstanceOf(ApiError);
  });

  it("survives storage that throws", () => {
    const broken = safeStore(() => {
      throw new Error("SecurityError");
    });
    expect(broken.get("k")).toBeUndefined();
    expect(() => {
      broken.set("k", "v");
      broken.remove("k");
    }).not.toThrow();
    const throwing = safeStore(() => ({ getItem: () => { throw new Error("x"); }, setItem: () => { throw new Error("x"); } }) as unknown as Storage);
    expect(throwing.get("k")).toBeUndefined();
    expect(() => {
      throwing.set("k", "v");
    }).not.toThrow();
  });

  it("remembers the voice mute setting", () => {
    const store = memoryStore();
    expect(isMuted(store)).toBe(false);
    setMuted(store, true);
    expect(isMuted(store)).toBe(true);
    setMuted(store, false);
    expect(isMuted(store)).toBe(false);
  });

  it("calls the Phase 2 endpoints", async () => {
    const calls: { url: string; body: unknown }[] = [];
    const fetchFn: FetchFn = (url, init) => {
      calls.push({ url, body: init?.body === undefined ? undefined : JSON.parse(init.body as string) });
      const body = url.endsWith("/conversations")
        ? { conversationId: "c1", agentId: "celiknaz", createdAtUtc: "" }
        : url.endsWith("/messages") && init?.method === "POST"
          ? { runId: "r1", traceId: "t1" }
          : url.endsWith("/messages")
            ? []
            : { runId: "r1", status: "completed", steps: [] };
      return Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } }));
    };
    const chat = createHttpChatApi(createApiClient(fetchFn));
    await chat.createConversation("celiknaz", "ayse");
    await chat.getMessages("c1");
    await chat.sendMessage("c1", "Ev soğudu", "voice");
    await chat.getRun("r1");
    expect(calls).toEqual([
      { url: "/api/conversations", body: { agentId: "celiknaz", memberId: "ayse" } },
      { url: "/api/conversations/c1/messages", body: undefined },
      { url: "/api/conversations/c1/messages", body: { text: "Ev soğudu", channel: "voice" } },
      { url: "/api/runs/r1", body: undefined },
    ]);
  });
});

describe("chat controller with the mock agent", () => {
  it("'ev soğudu' raises the salon setpoint, streams the answer and tags the appliance", async () => {
    const home = createMockHome({ tickMs: 0, latencyMs: 0 });
    const agent = createMockAgent(home, { stepDelayMs: 0 });
    const store = new HomeStore();
    store.setHome(await home.api.getHome());
    const stateChanged = vi.fn();
    home.connectLive("home-1", { stateChanged, event: vi.fn(), status: vi.fn() });
    const deltas: string[] = [];
    const controller = new ChatController({
      chat: agent.chat,
      connectRunLive: (id, handlers) =>
        agent.connectRunLive(id, {
          ...handlers,
          event: (e) => {
            if (e.kind === "TextDelta") deltas.push(e.delta);
            handlers.event(e);
          },
        }),
      store: memoryStore(),
      memberId: () => "ayse",
      onActions: (actions) => {
        store.markAiChanged(actions.map((a) => a.applianceId));
      },
    });

    const entry = await controller.send("Ev soğudu, biraz ısıt", "voice");
    expect(entry.status).toBe("completed");
    expect(entry.actions).toEqual([{ applianceId: "ac-salon", command: "setpoint", value: 26, accepted: true }]);
    expect(deltas.length).toBeGreaterThan(1);
    expect(deltas.join("")).toBe(entry.text);
    expect(entry.steps.map((s) => s.name)).toEqual(
      expect.arrayContaining(["Kullanıcı mesajı", "Çeliknaz", "get_home_status", "control_device", "Yanıt"]),
    );
    expect(entry.steps.every((s) => s.status === "completed")).toBe(true);
    expect(entry.totals?.costUsd).toBeGreaterThan(0);
    expect(home.snapshot().appliances.find((a) => a.id === "ac-salon")?.state.ac?.setpoint).toBe(26);
    expect(stateChanged).toHaveBeenCalled();
    expect(store.isAiChanged("ac-salon")).toBe(true);
    expect(store.isAiChanged("washer-1")).toBe(false);

    // The run is stored for the timeline of history entries.
    const detail = await agent.chat.getRun(entry.runId ?? "");
    expect(detail.status).toBe("completed");
    controller.dispose();
    home.dispose();
  });

  it("shows the fallback when sending fails", async () => {
    const home = createMockHome({ tickMs: 0, latencyMs: 0 });
    const agent = createMockAgent(home, { stepDelayMs: 0 });
    const controller = new ChatController({
      chat: { ...agent.chat, sendMessage: () => Promise.reject(new ApiError("http", 500)) },
      connectRunLive: agent.connectRunLive,
      store: memoryStore(),
      memberId: () => "ayse",
    });
    const entry = await controller.send("merhaba", "text");
    expect(entry).toMatchObject({ status: "failed", text: tr.home.chat.fallback });
    home.dispose();
  });

  it("recovers the answer from history when no hub events arrive", async () => {
    vi.useFakeTimers();
    try {
      const chat: ChatApi = {
        createConversation: () => Promise.resolve({ conversationId: "c", agentId: "celiknaz", createdAtUtc: "" }),
        getMessages: () =>
          Promise.resolve([{ messageId: "a", role: "assistant", text: "Tamam", runId: "r1", createdAtUtc: "", actions: [] }]),
        sendMessage: () => Promise.resolve({ runId: "r1", traceId: "" }),
        getRun: () => Promise.reject(new Error("x")),
      };
      const controller = new ChatController({
        chat,
        connectRunLive: () => ({ ready: Promise.resolve(), stop: () => undefined }),
        store: memoryStore(),
        memberId: () => "ayse",
        idleTimeoutMs: 1000,
      });
      const done = controller.send("selam", "text");
      await vi.advanceTimersByTimeAsync(1500);
      await expect(done).resolves.toMatchObject({ status: "completed", text: "Tamam" });
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("AI tags", () => {
  it("expire after a short time", () => {
    const store = new HomeStore();
    const listener = vi.fn();
    store.subscribe(listener);
    store.markAiChanged(["ac-salon"], 1000);
    expect(store.isAiChanged("ac-salon", 1001)).toBe(true);
    expect(store.isAiChanged("ac-salon", 1000 + 20_001)).toBe(false);
    expect(listener).toHaveBeenCalledWith({ kind: "aiTag", applianceId: "ac-salon" });
  });
});
