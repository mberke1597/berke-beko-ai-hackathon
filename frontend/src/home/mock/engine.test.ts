import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../../api/errors.ts";
import { ChatController } from "../chat/controller.ts";
import { memoryStore } from "../chat/storage.ts";
import type { LiveHandlers } from "../live.ts";
import { recommendationCommands } from "../slice2/logic.ts";
import type { AppNotification, ComfortCheck, HouseholdTask } from "../slice2/types.ts";
import { createMockAgent } from "./agent.ts";
import { createMockEngine, tariffAt } from "./engine.ts";
import { createMockHome } from "./simulator.ts";

/** Local 14:00: day tariff, outside quiet hours. */
const NOW = new Date(2026, 10, 3, 14, 0, 0);

const disposers: (() => void)[] = [];
afterEach(() => {
  for (const d of disposers.splice(0)) d();
});

function setup(options: { comfortCheckDelayMs?: number; now?: () => Date } = {}) {
  const home = createMockHome({ tickMs: 0, latencyMs: 0, now: options.now ?? (() => NOW) });
  const agent = createMockAgent(home, { stepDelayMs: 0, now: options.now ?? (() => NOW) });
  const engine = createMockEngine(home, agent, { latencyMs: 0, comfortCheckDelayMs: options.comfortCheckDelayMs ?? 0, now: options.now ?? (() => NOW) });
  const tasks: HouseholdTask[] = [];
  const notifications: AppNotification[] = [];
  const checks: ComfortCheck[] = [];
  const handlers: LiveHandlers = {
    stateChanged: () => undefined,
    event: () => undefined,
    status: () => undefined,
    taskAssigned: (t) => tasks.push(t),
    notificationCreated: (n) => notifications.push(n),
    comfortCheckRequested: (c) => checks.push(c),
  };
  const stop = home.connectLive("home-1", handlers);
  disposers.push(
    stop,
    () => {
      engine.dispose();
    },
    () => {
      home.dispose();
    },
  );
  return { home, agent, engine, api: engine.api, tasks, notifications, checks };
}

const finishWasher = (home: ReturnType<typeof setup>["home"]): void => {
  for (let i = 0; i < 42; i++) home.tick();
};

describe("tariff", () => {
  it("uses night, peak and day bands by local hour", () => {
    expect(tariffAt(new Date(2026, 10, 3, 23, 0))).toBe(1.5);
    expect(tariffAt(new Date(2026, 10, 3, 3, 0))).toBe(1.5);
    expect(tariffAt(new Date(2026, 10, 3, 19, 0))).toBe(4.2);
    expect(tariffAt(new Date(2026, 10, 3, 12, 0))).toBe(2.6);
  });
});

describe("mock engine: settings and context", () => {
  it("defaults and saves settings per member", async () => {
    const { api } = setup();
    expect(await api.getSettings("ayse")).toMatchObject({ priorityMode: "saving", notificationLevel: "balanced", clothing: "normal", locationSharing: true });
    await api.putSettings("mehmet", { clothing: "heavy", notificationLevel: "detailed", quietHours: { from: "22:00", to: "07:00" } });
    expect(await api.getSettings("mehmet")).toMatchObject({ clothing: "heavy", notificationLevel: "detailed", quietHours: { from: "22:00" } });
    expect((await api.getSettings("ayse")).clothing).toBe("normal");
    await expect(api.getSettings("ghost")).rejects.toBeInstanceOf(ApiError);
  });

  it("builds the home context from the default member and presence", async () => {
    const { api } = setup();
    expect(await api.getContext()).toMatchObject({ outdoorTempC: 31, priorityMode: "saving", members: [{ memberId: "ayse", band: "home" }, { memberId: "mehmet" }, { memberId: "zeynep" }] });
    await api.putSettings("ayse", { priorityMode: "comfort" });
    expect((await api.getContext()).priorityMode).toBe("comfort");
  });
});

describe("mock engine: comfort", () => {
  it("is in the band for the demo salon", async () => {
    const { api } = setup();
    const comfort = await api.getComfort("ac-salon");
    expect(comfort).toMatchObject({ applianceId: "ac-salon", inBand: true, feeling: "neutral", bandPmv: 1, members: ["ayse", "mehmet", "zeynep"] });
    expect(comfort.recommendation.action).toBe("none");
    await expect(api.getComfort("washer-1")).rejects.toBeInstanceOf(ApiError);
  });

  it("recommends the cheaper fan first and the setpoint when that is not enough", async () => {
    const { api, home } = setup();
    await api.putSettings("ayse", { priorityMode: "comfort", clothing: "heavy" });
    const fan = await api.getComfort("ac-salon", ["ayse"]);
    expect(fan.inBand).toBe(false);
    expect(fan.recommendation).toMatchObject({ action: "fan", fan: "high" });
    expect(fan.recommendation.reason).not.toBe("");
    const snapshot = home.snapshot().appliances.find((a) => a.id === "ac-salon");
    if (!snapshot) throw new Error("missing");
    expect(recommendationCommands(fan.recommendation, snapshot)).toEqual([{ type: "fan", value: "high" }]);

    for (let i = 0; i < 12; i++) await api.postComfortFeedback("ayse", { applianceId: "ac-salon", sensation: 1, clothing: null, commandId: null });
    const setpoint = await api.getComfort("ac-salon", ["ayse"]);
    expect(setpoint.recommendation.action).toBe("setpoint");
    expect(setpoint.recommendation.mode).toBe("cool");
    expect(setpoint.recommendation.setpoint).toBeLessThan(25);
    expect(setpoint.recommendation.costPerHourTl).toBeGreaterThan(0);
  });

  it("finds a compromise when two members feel differently", async () => {
    const { api } = setup();
    await api.putSettings("ayse", { clothing: "heavy" });
    await api.putSettings("mehmet", { clothing: "light" });
    const comfort = await api.getComfort("ac-salon", ["ayse", "mehmet"]);
    expect(comfort.conflict.present).toBe(true);
    expect(comfort.conflict.compromiseSetpoint).not.toBeNull();
    expect(comfort.conflict.perMember.map((m) => m.memberId)).toEqual(["ayse", "mehmet"]);
    expect((await api.getComfort("ac-salon", ["ayse"])).conflict.present).toBe(false);
  });

  it("learns from feedback and may update clothing", async () => {
    const { api } = setup();
    await api.postComfortFeedback("zeynep", { applianceId: "ac-salon", sensation: -1, clothing: "light", commandId: "c1" });
    expect(await api.getSettings("zeynep")).toMatchObject({ sensitivityOffset: -0.1, clothing: "light" });
  });
});

describe("mock engine: energy and cheapest time", () => {
  it("reports today's energy per device and grows with simulated time", async () => {
    const { api, home } = setup();
    const before = await api.getEnergy();
    expect(before.perDevice.map((d) => d.applianceId)).toContain("ac-salon");
    for (let i = 0; i < 10; i++) home.tick();
    const after = await api.getEnergy();
    expect(after.todayKwh).toBeGreaterThan(before.todayKwh);
    expect(after.savedWeekTl).toBeGreaterThan(0);
  });

  it("picks the night band for a washer program and reports the saving", async () => {
    const { api } = setup();
    const result = await api.getCheapestTime("washer-1", "cotton40", new Date(NOW.getTime() + 12 * 3600_000).toISOString());
    expect(new Date(result.startAtUtc).getHours()).toBe(22);
    expect(result.costTl).toBeLessThan(result.nowCostTl);
    expect(Math.abs(result.savingTl - (result.nowCostTl - result.costTl))).toBeLessThan(0.15);
    await expect(api.getCheapestTime("washer-1", "nope", NOW.toISOString())).rejects.toBeInstanceOf(ApiError);
    await expect(api.getCheapestTime("ghost", "cotton40", NOW.toISOString())).rejects.toBeInstanceOf(ApiError);
  });

  it("schedules a delayed program that starts when its time comes", async () => {
    let clock = NOW.getTime();
    const { home, api } = setup({ now: () => new Date(clock) });
    const cheapest = await api.getCheapestTime("washer-1", "quick30", new Date(NOW.getTime() + 12 * 3600_000).toISOString());
    await home.api.sendCommand("washer-1", { type: "stop", value: null, source: "app", memberId: "ayse" });
    const result = await home.api.sendCommand("washer-1", {
      type: "start_program",
      value: { program: "quick30", temperatureC: null, startAtUtc: cheapest.startAtUtc },
      source: "app",
      memberId: "ayse",
    });
    expect(result.appliance.state.status).toBe("idle");
    expect(result.appliance.state.program?.startAtUtc).toBe(cheapest.startAtUtc);
    expect(result.appliance.state.powerW).toBe(0);

    clock = Date.parse(cheapest.startAtUtc) + 60_000;
    home.tick();
    const started = home.snapshot().appliances.find((a) => a.id === "washer-1");
    expect(started?.state.status).toBe("running");
    expect(started?.state.program?.startAtUtc).toBeNull();
  });
});

describe("mock engine: tasks", () => {
  it("creates a fair-share task when the washer finishes and announces it", async () => {
    const { home, api, tasks, notifications } = setup();
    expect((await api.getTaskStats()).map((s) => [s.memberId, s.pointsLast7Days])).toEqual([["ayse", 6], ["mehmet", 4], ["zeynep", 2]]);
    finishWasher(home);
    const open = await api.listTasks("open");
    expect(open).toHaveLength(1);
    expect(open[0]).toMatchObject({ title: "Çamaşırları as", origin: "device_event", applianceId: "washer-1", assignedMemberId: "zeynep", effortPoints: 2, status: "open" });
    expect(open[0]?.reason).toContain("Adil dağıtım");
    expect(tasks.map((t) => t.id)).toEqual([open[0]?.id]);
    expect(notifications[0]).toMatchObject({ kind: "task", memberId: "zeynep" });
  });

  it("completing adds points to the assignee and skipping does not", async () => {
    const { home, api } = setup();
    finishWasher(home);
    const [created] = await api.listTasks("open");
    if (!created) throw new Error("no task");
    expect((await api.completeTask(created.id)).status).toBe("done");
    expect((await api.getTaskStats()).find((s) => s.memberId === "zeynep")?.pointsLast7Days).toBe(4);

    const manual = await api.createTask({ title: "Çöpü çıkar", assignedMemberId: "ayse", effortPoints: 3 });
    expect(manual).toMatchObject({ origin: "manual", assignedMemberId: "ayse", reason: "Elle atandı" });
    await api.skipTask(manual.id);
    expect((await api.getTaskStats()).find((s) => s.memberId === "ayse")?.pointsLast7Days).toBe(6);
    expect((await api.listTasks("skipped")).map((t) => t.id)).toContain(manual.id);
    await expect(api.completeTask("ghost")).rejects.toBeInstanceOf(ApiError);
    await expect(api.createTask({ title: "  ", assignedMemberId: null, effortPoints: 1 })).rejects.toBeInstanceOf(ApiError);
  });

  it("reassigns to a member or redistributes", async () => {
    const { api } = setup();
    const created = await api.createTask({ title: "Çiçekleri sula", assignedMemberId: "ayse", effortPoints: 1 });
    expect((await api.reassignTask(created.id, "mehmet")).assignedMemberId).toBe("mehmet");
    const redistributed = await api.reassignTask(created.id, null);
    expect(redistributed.assignedMemberId).toBe("zeynep");
    expect(redistributed.reason).toContain("Adil dağıtım");
  });

  it("follows the rule strategy and does nothing for disabled rules", async () => {
    const { home, api } = setup();
    await api.putRule("rule-washer", { strategy: "fixed", fixedMemberId: "mehmet" });
    finishWasher(home);
    expect((await api.listTasks("open"))[0]).toMatchObject({ assignedMemberId: "mehmet" });

    const rules = await api.listRules();
    expect(rules.find((r) => r.id === "rule-oven")?.enabled).toBe(false);
    await api.putRule("rule-washer", { enabled: false });
    expect((await api.listRules()).find((r) => r.id === "rule-washer")?.enabled).toBe(false);
    await expect(api.putRule("ghost", { enabled: true })).rejects.toBeInstanceOf(ApiError);
  });

  it("does not notify members whose level is quiet but still announces the task", async () => {
    const { home, api, tasks, notifications } = setup();
    await api.putSettings("zeynep", { notificationLevel: "quiet" });
    finishWasher(home);
    expect(tasks).toHaveLength(1);
    expect(notifications.filter((n) => n.memberId === "zeynep")).toHaveLength(0);
  });
});

describe("mock engine: presence", () => {
  it("starts at home and reports distance and band while leaving (no ETA)", async () => {
    const { api, home } = setup();
    expect((await api.getPresence()).every((p) => p.band === "home" && p.distanceM === 0 && p.etaMin === null)).toBe(true);
    await api.setRoute("mehmet", "commute_out");
    home.tick();
    home.tick();
    expect((await api.getPresence()).find((p) => p.memberId === "mehmet")).toMatchObject({ band: "near", distanceM: 1300, etaMin: null });
    expect((await api.getPresence()).find((p) => p.memberId === "ayse")?.band).toBe("home");
  });

  it("turns the AC off when everybody left (saving) and back on when someone arrives", async () => {
    const { home, api, notifications } = setup();
    for (const id of ["ayse", "mehmet", "zeynep"]) await api.setRoute(id, "commute_out");
    for (let i = 0; i < 8; i++) home.tick();
    const away = await api.getPresence();
    expect(away.every((p) => p.band === "away")).toBe(true);
    expect(home.snapshot().appliances.find((a) => a.id === "ac-salon")?.state.power).toBe(false);
    expect(notifications.some((n) => n.memberId === "ayse" && n.title === "Çeliknaz evi ayarladı")).toBe(true);
    expect((await api.getContext()).members.every((m) => m.band === "away")).toBe(true);

    await api.setRoute("ayse", "commute_back");
    expect((await api.getPresence()).find((p) => p.memberId === "ayse")?.etaMin).toBe(8);
    for (let i = 0; i < 9; i++) home.tick();
    const back = await api.getPresence();
    expect(back.find((p) => p.memberId === "ayse")).toMatchObject({ band: "home", etaMin: null });
    expect(home.snapshot().appliances.find((a) => a.id === "ac-salon")?.state.power).toBe(true);
  });

  it("pre-conditions the salon for a balanced household that is approaching", async () => {
    const { home, api } = setup();
    await api.putSettings("ayse", { priorityMode: "balanced" });
    for (const id of ["ayse", "mehmet", "zeynep"]) await api.setRoute(id, "commute_out");
    for (let i = 0; i < 8; i++) home.tick();
    const salon = home.snapshot().appliances.find((a) => a.id === "ac-salon");
    expect(salon?.state.power).toBe(true);
    expect(salon?.state.ac?.setpoint).toBe(28);

    await api.setRoute("zeynep", "commute_back");
    for (let i = 0; i < 5; i++) home.tick();
    expect((await api.getPresence()).find((p) => p.memberId === "zeynep")?.band).toBe("near");
    await expect(api.setRoute("ghost", "at_home")).rejects.toBeInstanceOf(ApiError);
  });
});

describe("mock engine: learning, notifications and comfort questions", () => {
  it("seeds demo history, offers a routine suggestion and lets the user accept it", async () => {
    const { api, notifications } = setup();
    expect((await api.getProfile("ayse")).routines).toEqual([]);
    const seeded = await api.seedHistory("ayse", 5);
    expect(seeded.events).toBe(170);
    const profile = await api.getProfile("ayse");
    expect(profile.preferences).toHaveLength(2);
    expect(profile.routines[0]).toMatchObject({ id: "routine-1", time: "18:30", status: "suggested" });
    expect(profile.overrideRate).toHaveLength(5);
    expect(profile.overrideRate[0]?.rate).toBeGreaterThan(profile.overrideRate[4]?.rate ?? 1);
    expect(profile.goodFeedbackRate[4]?.rate).toBeGreaterThan(profile.goodFeedbackRate[0]?.rate ?? 0);
    expect(profile.sensitivityOffset).toBe(-0.3);

    const suggestion = notifications.find((n) => n.kind === "suggestion");
    expect(suggestion?.action).toEqual({ type: "accept_routine", routineId: "routine-1" });
    expect((await api.listNotifications("ayse")).map((n) => n.id)).toContain(suggestion?.id);

    expect((await api.acceptRoutine("routine-1")).status).toBe("accepted");
    expect((await api.rejectRoutine("routine-2")).status).toBe("rejected");
    await expect(api.acceptRoutine("ghost")).rejects.toBeInstanceOf(ApiError);
  });

  it("resets what was learned", async () => {
    const { api } = setup();
    await api.seedHistory("ayse", 5);
    await api.resetLearning("ayse");
    expect(await api.getProfile("ayse")).toMatchObject({ preferences: [], routines: [], overrideRate: [], goodFeedbackRate: [], sensitivityOffset: 0 });
    expect((await api.getSettings("ayse")).sensitivityOffset).toBe(0);
  });

  it("marks notifications read and caps suggestions at three per day", async () => {
    const { api, notifications } = setup();
    for (let i = 0; i < 5; i++) await api.seedHistory("ayse", 5);
    expect(notifications.filter((n) => n.kind === "suggestion")).toHaveLength(3);
    const [first] = await api.listNotifications("ayse");
    if (!first) throw new Error("none");
    expect(first.read).toBe(false);
    await api.markNotificationRead(first.id);
    expect((await api.listNotifications("ayse")).find((n) => n.id === first.id)?.read).toBe(true);
    await expect(api.markNotificationRead("ghost")).rejects.toBeInstanceOf(ApiError);
  });

  it("holds back notifications during quiet hours except critical ones", async () => {
    const night = new Date(2026, 10, 3, 23, 30);
    const { api, notifications } = setup({ now: () => night });
    await api.seedHistory("ayse", 5);
    expect(notifications).toHaveLength(0);
  });

  it("asks the comfort question after an AI command and records it in the dashboard", async () => {
    const { api, agent, checks, home } = setup();
    const controller = new ChatController({ chat: agent.chat, connectRunLive: agent.connectRunLive, store: memoryStore(), memberId: () => "mehmet" });
    const entry = await controller.send("Ev soğudu, biraz ısıt", "voice");
    expect(entry.status).toBe("completed");
    await vi.waitFor(() => {
      expect(checks).toHaveLength(1);
    });
    expect(checks[0]).toMatchObject({ applianceId: "ac-salon", memberId: "mehmet" });

    await home.api.sendCommand("ac-salon", { type: "fan", value: "low", source: "remote", memberId: "ayse" });
    await new Promise((resolve) => setTimeout(resolve, 5));
    expect(checks).toHaveLength(1);

    const summary = await api.getSummary();
    expect(summary).toMatchObject({ runsToday: 1, conversationsToday: 1, commandsToday: { ai: 1, remote: 1 } });
    expect(summary.tools.find((t) => t.tool === "control_device")?.calls).toBe(1);
    expect(summary.llm.costUsdToday).toBeGreaterThan(0);
    expect(summary.llm.avgLatencyMs).toBeGreaterThanOrEqual(0);

    const runs = await api.listRuns(10);
    expect(runs).toHaveLength(1);
    expect(runs[0]).toMatchObject({ userText: "Ev soğudu, biraz ısıt", status: "completed" });
    expect((await agent.chat.getRun(runs[0]?.runId ?? "")).steps.length).toBeGreaterThan(3);
    controller.dispose();
  });

  it("stays quiet when the member chose quiet notifications", async () => {
    const { api, agent, checks } = setup();
    await api.putSettings("mehmet", { notificationLevel: "quiet" });
    const controller = new ChatController({ chat: agent.chat, connectRunLive: agent.connectRunLive, store: memoryStore(), memberId: () => "mehmet" });
    await controller.send("Ev soğudu", "text");
    await new Promise((resolve) => setTimeout(resolve, 5));
    expect(checks).toHaveLength(0);
    controller.dispose();
  });
});

describe("mock engine: dashboard projection", () => {
  it("scales per-home averages linearly with the number of homes", async () => {
    const { api } = setup();
    const small = await api.getProjection(1000);
    const large = await api.getProjection(100_000);
    expect(large.homes).toBe(100_000);
    expect(large.llmCostUsdMonth).toBeCloseTo(small.llmCostUsdMonth * 100, 0);
    expect(large.serverCostTlMonth).toBeCloseTo(small.serverCostTlMonth * 100, 0);
    expect(large.savedTlMonth).toBeGreaterThan(small.savedTlMonth);
    expect(large.assumptions.length).toBeGreaterThan(0);
    const summary = await api.getSummary();
    expect(summary.server).toEqual({ monthlyCostTl: 450, costPerActiveUserTl: 150 });
    expect(summary.homes).toBe(1);
  });
});
