import { describe, expect, it, vi } from "vitest";
import { createApiClient, type FetchFn } from "../../api/client.ts";
import { ApiError } from "../../api/errors.ts";
import { tr } from "../../i18n/tr.ts";
import { memoryStore } from "../chat/storage.ts";
import { createDemoHome } from "../mock/fixtures.ts";
import type { Appliance } from "../types.ts";
import { createHttpSlice2Api, type Slice2Api } from "./api.ts";
import { DEFAULT_MEMBER, MEMBER_STORAGE_KEY, Slice2Controller, type S2Change } from "./controller.ts";
import {
  badgeText,
  conflictNote,
  fairnessBars,
  feelingLabel,
  filterTasks,
  formatClock,
  formatDistance,
  formatKwh,
  formatRate,
  formatTl,
  formatUsdMoney,
  homesFromSlider,
  MAX_HOMES,
  MIN_HOMES,
  originLabel,
  pathFor,
  ratePoints,
  recommendationCommands,
  shouldAskComfort,
  sliderFromHomes,
  sortNotifications,
  strategyLabel,
  triggerLabel,
  unreadCount,
  upsertNotification,
  upsertTask,
} from "./logic.ts";
import {
  decodeComfort,
  decodeComfortCheck,
  decodeContext,
  decodeEnergy,
  decodeNotification,
  decodeNotificationCreated,
  decodeProfile,
  decodeProjection,
  decodeRule,
  decodeRunSummaries,
  decodeSettings,
  decodeSummary,
  decodeTask,
  decodeTaskAssigned,
  type AppNotification,
  type Comfort,
  type HouseholdTask,
  type Recommendation,
} from "./types.ts";

function appliance(id: string): Appliance {
  const found = createDemoHome().appliances.find((a) => a.id === id);
  if (!found) throw new Error(id);
  return found;
}

const members = createDemoHome().members;

function task(id: string, extra: Partial<HouseholdTask> = {}): HouseholdTask {
  return {
    id,
    title: "Çamaşırları as",
    origin: "device_event",
    applianceId: "washer-1",
    assignedMemberId: "mehmet",
    status: "open",
    effortPoints: 2,
    createdAtUtc: "2026-11-03T10:00:00Z",
    dueAtUtc: null,
    reason: "Adil dağıtım: son 7 günde en az iş yapan",
    ...extra,
  };
}

function notification(id: string, extra: Partial<AppNotification> = {}): AppNotification {
  return {
    id,
    memberId: "ayse",
    kind: "info",
    level: "important",
    title: "Başlık",
    text: "Metin",
    createdAtUtc: "2026-11-03T10:00:00Z",
    read: false,
    action: null,
    ...extra,
  };
}

// ---------- decoders (contract examples) ----------

describe("slice 2 decoders", () => {
  it("decodes settings and context", () => {
    const settings = decodeSettings({
      memberId: "ayse",
      priorityMode: "saving",
      notificationLevel: "balanced",
      quietHours: { from: "23:00", to: "08:00" },
      clothing: "normal",
      locationSharing: true,
      sensitivityOffset: 0,
      estimatedMonthlyTl: { saving: 380, balanced: 460, comfort: 590 },
    });
    expect(settings).toMatchObject({ priorityMode: "saving", estimatedMonthlyTl: { balanced: 460 }, quietHours: { to: "08:00" } });
    expect(decodeSettings({ priorityMode: "saving" })).toBeUndefined();
    expect(decodeSettings({ memberId: "a", priorityMode: "weird" })?.priorityMode).toBe("saving");

    expect(
      decodeContext({
        outdoorTempC: 31,
        outdoorHumidityPct: 48,
        season: "summer",
        simulatedNowUtc: "2026-11-03T10:00:00Z",
        priorityMode: "balanced",
        members: [{ memberId: "ayse", band: "home" }, { memberId: "mehmet", band: "away" }],
      }),
    ).toMatchObject({ outdoorTempC: 31, priorityMode: "balanced", members: [{ band: "home" }, { band: "away" }] });
    expect(decodeContext({ priorityMode: "saving" })).toBeUndefined();
  });

  it("decodes the comfort example", () => {
    const comfort = decodeComfort({
      applianceId: "ac-salon",
      members: ["ayse"],
      roomTempC: 24.8,
      humidityPct: 55,
      pmv: -0.4,
      feeling: "slightly_cool",
      inBand: true,
      bandPmv: 1,
      recommendation: {
        action: "setpoint",
        setpoint: 25.5,
        mode: "cool",
        fan: "auto",
        reason: "Türkçe açıklama",
        costPerHourTl: 1.2,
        alternatives: [{ action: "fan", setpoint: 0, costPerHourTl: 0.2 }],
      },
      conflict: { present: false, compromiseSetpoint: null, perMember: [{ memberId: "ayse", pmvAtCompromise: 0.2 }] },
    });
    expect(comfort?.recommendation).toMatchObject({ action: "setpoint", setpoint: 25.5, costPerHourTl: 1.2 });
    expect(comfort?.recommendation.alternatives).toHaveLength(1);
    expect(comfort?.conflict.perMember[0]?.pmvAtCompromise).toBe(0.2);
    expect(decodeComfort({ roomTempC: 1 })).toBeUndefined();
    expect(decodeComfort({ applianceId: "x", roomTempC: 24 })?.recommendation.action).toBe("none");
  });

  it("decodes hub payloads", () => {
    expect(decodeComfortCheck({ applianceId: "ac-salon", memberId: "ayse", commandId: "c1" })).toEqual({ applianceId: "ac-salon", memberId: "ayse", commandId: "c1" });
    expect(decodeComfortCheck({ applianceId: "ac-salon" })).toBeUndefined();
    expect(decodeTaskAssigned({ task: task("t1") })?.id).toBe("t1");
    expect(decodeTaskAssigned(task("t1"))).toBeUndefined();
    expect(decodeNotificationCreated({ notification: notification("n1") })?.id).toBe("n1");
  });

  it("decodes tasks, rules, notifications and energy", () => {
    expect(decodeTask(task("t1"))).toEqual(task("t1"));
    expect(decodeTask({ id: "t", title: "x", origin: "alien", status: "weird" })).toMatchObject({ origin: "manual", status: "open" });
    expect(decodeRule({ id: "r", trigger: "washing_machine.finished", template: "Çamaşırları as", strategy: "fixed", fixedMemberId: "ayse", effortPoints: 2, enabled: false })).toMatchObject({
      strategy: "fixed",
      enabled: false,
    });
    expect(decodeNotification({ ...notification("n"), action: { type: "accept_routine", routineId: "r1" } })?.action).toEqual({ type: "accept_routine", routineId: "r1" });
    expect(decodeNotification({ ...notification("n"), action: { type: "other" } })?.action).toBeNull();
    expect(decodeEnergy({ todayKwh: 3.1, todayTl: 8, perDevice: [{ applianceId: "ac-salon", todayKwh: 2, todayTl: 5 }] })?.perDevice).toHaveLength(1);
  });

  it("decodes profile, dashboard and runs", () => {
    const profile = decodeProfile({
      sensitivityOffset: -0.3,
      preferences: [{ context: "weekday evening, hot", setpoint: 24.5, confidence: 0.8, samples: 13, text: "Özet" }],
      routines: [{ id: "r1", action: "power_on", applianceId: "ac-salon", dayType: "weekday", time: "18:30", windowMin: 20, confidence: 0.85, evidence: "Son 3 haftada 13 kez", status: "suggested" }],
      overrideRate: [{ weekStartUtc: "2026-10-26T00:00:00Z", rate: 0.3 }],
      goodFeedbackRate: [],
    });
    expect(profile?.routines[0]).toMatchObject({ time: "18:30", status: "suggested" });
    expect(profile?.overrideRate).toHaveLength(1);

    const summary = decodeSummary({
      homes: 1,
      activeMembers: 3,
      commandsToday: { voice: 1, app: 2, remote: 3, ai: 4, schedule: 0 },
      conversationsToday: 2,
      runsToday: 5,
      llm: { costUsdToday: 0.02, costUsdMonth: 1.5, tokensToday: 9000, avgCostUsdPerConversation: 0.01, avgLatencyMs: 2100 },
      energy: { savedKwhWeek: 4, savedTlWeek: 11 },
      learning: { overrideRateTrend: [], goodFeedbackTrend: [], suggestionAcceptanceRate: 0.5 },
      tools: [{ tool: "control_device", calls: 4, errors: 1 }],
      server: { monthlyCostTl: 450, costPerActiveUserTl: 150 },
    });
    expect(summary).toMatchObject({ commandsToday: { ai: 4 }, tools: [{ errors: 1 }], server: { costPerActiveUserTl: 150 } });
    expect(decodeProjection({ homes: 1000, llmCostUsdMonth: 1500, serverCostTlMonth: 6000, savedKwhMonth: 1, savedTlMonth: 2, assumptions: ["a"] })?.assumptions).toEqual(["a"]);
    expect(decodeProjection({ llmCostUsdMonth: 1 })).toBeUndefined();
    expect(decodeRunSummaries([{ runId: "r", status: "completed", userText: "Ev soğudu", costUsd: 0.01, durationMs: 1500 }])?.[0]?.userText).toBe("Ev soğudu");
    expect(decodeRunSummaries({})).toBeUndefined();
  });
});

// ---------- http api ----------

describe("slice 2 http api", () => {
  function recorder(): { calls: { method: string; url: string; body: unknown }[]; api: Slice2Api } {
    const calls: { method: string; url: string; body: unknown }[] = [];
    const fetchFn: FetchFn = (url, init) => {
      calls.push({ method: init?.method ?? "GET", url, body: typeof init?.body === "string" ? JSON.parse(init.body) : undefined });
      const json = (body: unknown, status = 200): Promise<Response> =>
        Promise.resolve(new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } }));
      if (init?.method === "PUT" && url.includes("/settings")) return Promise.resolve(new Response(null, { status: 204 }));
      if (url.includes("/comfort-feedback") || url.includes("/read") || (init?.method === "DELETE") || url.includes("/route")) {
        return Promise.resolve(new Response(null, { status: 204 }));
      }
      if (url.includes("/seed-history")) return json({ events: 170 });
      if (url.includes("/tasks") && init?.method === "POST") return json(task("t1"));
      if (url.includes("/task-rules")) return json(url.endsWith("task-rules") ? [] : { id: "rule-1", trigger: "washing_machine.finished", template: "x", strategy: "fixed", effortPoints: 1, enabled: true });
      if (url.includes("/tasks")) return json([task("t1")]);
      if (url.includes("/presence")) return json([{ memberId: "ayse", band: "near", distanceM: 900, etaMin: 4, updatedAtUtc: "2026-11-03T10:00:00Z" }]);
      if (url.includes("/projection")) return json({ homes: 100000, llmCostUsdMonth: 1, serverCostTlMonth: 2, savedKwhMonth: 3, savedTlMonth: 4, assumptions: [] });
      if (url.includes("/cheapest-time")) return json({ startAtUtc: "2026-11-03T20:00:00Z", costTl: 1.4, nowCostTl: 2.3, savingTl: 0.9 });
      return json({});
    };
    return { calls, api: createHttpSlice2Api(createApiClient(fetchFn)) };
  }

  it("builds the contract URLs, queries and bodies", async () => {
    const { calls, api } = recorder();
    await api.putSettings("mehmet", { priorityMode: "comfort", quietHours: { from: "22:00", to: "07:00" } });
    await api.postComfortFeedback("ayse", { applianceId: "ac-salon", sensation: -1, clothing: "heavy", commandId: null });
    await api.getCheapestTime("washer-1", "cotton40", "2026-11-04T00:00:00Z");
    await api.setRoute("mehmet", "commute_out");
    await api.listTasks("open");
    await api.createTask({ title: "Çöpü çıkar", assignedMemberId: null, effortPoints: 1 });
    await api.completeTask("t1");
    await api.skipTask("t1");
    await api.reassignTask("t1", null);
    await api.putRule("rule-1", { strategy: "fixed", fixedMemberId: "ayse", enabled: true });
    await api.resetLearning("ayse");
    await api.markNotificationRead("n 1");
    const seeded = await api.seedHistory("ayse", 5);
    await api.getProjection(100000);

    expect(seeded.events).toBe(170);
    expect(calls).toEqual([
      { method: "PUT", url: "/api/me/settings?memberId=mehmet", body: { priorityMode: "comfort", quietHours: { from: "22:00", to: "07:00" } } },
      { method: "POST", url: "/api/me/comfort-feedback?memberId=ayse", body: { applianceId: "ac-salon", sensation: -1, clothing: "heavy", commandId: null } },
      {
        method: "GET",
        url: "/api/homes/me/cheapest-time?applianceId=washer-1&programId=cotton40&deadlineUtc=2026-11-04T00%3A00%3A00Z",
        body: undefined,
      },
      { method: "PUT", url: "/api/mock/location/mehmet/route", body: { route: "commute_out" } },
      { method: "GET", url: "/api/homes/me/tasks?status=open", body: undefined },
      { method: "POST", url: "/api/homes/me/tasks", body: { title: "Çöpü çıkar", assignedMemberId: null, effortPoints: 1 } },
      { method: "POST", url: "/api/tasks/t1/done", body: {} },
      { method: "POST", url: "/api/tasks/t1/skip", body: {} },
      { method: "POST", url: "/api/tasks/t1/reassign", body: { memberId: null } },
      { method: "PUT", url: "/api/homes/me/task-rules/rule-1", body: { strategy: "fixed", fixedMemberId: "ayse", enabled: true } },
      { method: "DELETE", url: "/api/me/learning?memberId=ayse", body: undefined },
      { method: "POST", url: "/api/me/notifications/n%201/read", body: {} },
      { method: "POST", url: "/api/demo/seed-history", body: { memberId: "ayse", weeks: 5 } },
      { method: "GET", url: "/api/dashboard/projection?homes=100000", body: undefined },
    ]);
  });

  it("passes member ids for comfort and decodes presence", async () => {
    const { calls, api } = recorder();
    await api.getComfort("ac-salon", ["ayse", "mehmet"]).catch(() => undefined);
    await api.getComfort("ac-salon").catch(() => undefined);
    expect(calls.map((c) => c.url)).toEqual(["/api/devices/ac-salon/comfort?memberIds=ayse%2Cmehmet", "/api/devices/ac-salon/comfort"]);
    expect((await api.getPresence())[0]).toMatchObject({ band: "near", etaMin: 4, distanceM: 900 });
  });

  it("rejects payloads with the wrong shape", async () => {
    const fetchFn: FetchFn = () => Promise.resolve(new Response(JSON.stringify({ nope: true }), { status: 200, headers: { "content-type": "application/json" } }));
    const error = (await createHttpSlice2Api(createApiClient(fetchFn)).getContext().catch((e: unknown) => e)) as ApiError;
    expect(error.kind).toBe("invalid-response");
  });
});

// ---------- pure logic ----------

describe("slice 2 logic", () => {
  it("filters tasks for Bana, Herkes and Tamamlanan", () => {
    const tasks = [
      task("a", { assignedMemberId: "ayse", createdAtUtc: "2026-11-03T09:00:00Z" }),
      task("b", { assignedMemberId: "mehmet", createdAtUtc: "2026-11-03T11:00:00Z" }),
      task("c", { assignedMemberId: "ayse", status: "done" }),
      task("d", { assignedMemberId: "mehmet", status: "skipped" }),
    ];
    expect(filterTasks(tasks, "mine", "ayse").map((t) => t.id)).toEqual(["a"]);
    expect(filterTasks(tasks, "everyone", "ayse").map((t) => t.id)).toEqual(["b", "a"]);
    expect(filterTasks(tasks, "done", "ayse").map((t) => t.id).sort()).toEqual(["c", "d"]);
  });

  it("builds fairness bars relative to the highest member and keeps members without tasks", () => {
    const bars = fairnessBars([{ memberId: "ayse", pointsLast7Days: 6, doneLast7Days: 3 }, { memberId: "mehmet", pointsLast7Days: 3, doneLast7Days: 2 }], members);
    expect(bars.map((b) => [b.name, b.points, b.pct])).toEqual([["Ayşe", 6, 100], ["Mehmet", 3, 50], ["Zeynep", 0, 0]]);
    expect(fairnessBars([], members).every((b) => b.pct === 0)).toBe(true);
  });

  it("upserts tasks and notifications and orders notifications", () => {
    expect(upsertTask([task("a")], task("b")).map((t) => t.id)).toEqual(["b", "a"]);
    expect(upsertTask([task("a")], task("a", { status: "done" }))[0]?.status).toBe("done");
    const list = [notification("old", { read: true, createdAtUtc: "2026-11-03T12:00:00Z" }), notification("new1", { createdAtUtc: "2026-11-03T08:00:00Z" }), notification("new2", { createdAtUtc: "2026-11-03T09:00:00Z" })];
    expect(sortNotifications(list).map((n) => n.id)).toEqual(["new2", "new1", "old"]);
    expect(unreadCount(list)).toBe(2);
    expect(upsertNotification(list, notification("new1", { read: true })).find((n) => n.id === "new1")?.read).toBe(true);
    expect([0, 1, 9, 10].map(badgeText)).toEqual([undefined, "1", "9", "9+"]);
  });

  it("only asks the comfort question of the current member", () => {
    expect(shouldAskComfort({ applianceId: "ac-salon", memberId: "ayse", commandId: null }, "ayse")).toBe(true);
    expect(shouldAskComfort({ applianceId: "ac-salon", memberId: "ayse", commandId: null }, "mehmet")).toBe(false);
  });

  it("turns recommendations into commands the appliance supports", () => {
    const ac = appliance("ac-salon");
    const rec = (extra: Partial<Recommendation>): Recommendation => ({ action: "none", setpoint: null, mode: null, fan: null, reason: "", costPerHourTl: null, alternatives: [], ...extra });
    expect(recommendationCommands(rec({ action: "setpoint", setpoint: 24.5, mode: "cool" }), ac)).toEqual([{ type: "setpoint", value: 24.5 }]);
    expect(recommendationCommands(rec({ action: "setpoint", setpoint: 21, mode: "heat" }), ac)).toEqual([{ type: "mode", value: "heat" }, { type: "setpoint", value: 21 }]);
    expect(recommendationCommands(rec({ action: "fan", fan: "high" }), ac)).toEqual([{ type: "fan", value: "high" }]);
    expect(recommendationCommands(rec({ action: "dry" }), ac)).toEqual([{ type: "mode", value: "dry" }]);
    expect(recommendationCommands(rec({ action: "window" }), ac)).toEqual([]);
    expect(recommendationCommands(rec({ action: "none" }), ac)).toEqual([]);
    expect(recommendationCommands(rec({ action: "setpoint", setpoint: 24 }), appliance("washer-1"))).toEqual([]);
    const limited: Appliance = { ...ac, capabilities: { ...ac.capabilities, commands: ["power"] } };
    expect(recommendationCommands(rec({ action: "fan", fan: "high" }), limited)).toEqual([]);
  });

  it("describes the compromise for several members", () => {
    const comfort = (conflict: Comfort["conflict"]): Comfort => ({
      applianceId: "ac-salon",
      members: ["ayse", "mehmet"],
      roomTempC: 25,
      humidityPct: 50,
      pmv: 0,
      feeling: "neutral",
      inBand: true,
      bandPmv: 1,
      recommendation: { action: "none", setpoint: null, mode: null, fan: null, reason: "", costPerHourTl: null, alternatives: [] },
      conflict,
    });
    expect(conflictNote(comfort({ present: false, compromiseSetpoint: null, perMember: [] }), members)).toBeUndefined();
    const note = conflictNote(comfort({ present: true, compromiseSetpoint: 24.5, perMember: [{ memberId: "ayse", pmvAtCompromise: 0.24 }, { memberId: "mehmet", pmvAtCompromise: -0.31 }] }), members);
    expect(note).toBe("Evde birden fazla kişi var; uzlaşma ayarı 24,5°. Herkesin konfor farkı: Ayşe +0,2, Mehmet −0,3.");
  });

  it("formats Turkish values", () => {
    expect(formatTl(12.54)).toBe("12,5 TL");
    expect(formatKwh(3.14)).toBe("3,1 kWh");
    expect(formatRate(0.456)).toBe("%46");
    expect(formatDistance(640)).toBe("640 m");
    expect(formatDistance(2450)).toBe("2,5 km");
    expect(formatClock("not a date")).toBe("");
    expect(formatUsdMoney(1500000)).toBe("$1.500.000,00");
    expect(formatUsdMoney(0.0141)).toBe("$0,0141");
    expect(feelingLabel("slightly_cool")).toBe("Biraz serin");
    expect(feelingLabel("mystery")).toBe("mystery");
    expect(strategyLabel("fair_share")).toBe("Adil dağıt");
    expect(strategyLabel("round_robin")).toBe("Sırayla");
    expect(strategyLabel("fixed")).toBe("Hep aynı kişi");
    expect(strategyLabel("ask")).toBe("Sor");
    expect(triggerLabel("washing_machine.finished")).toBe("Çamaşır makinesi bitince");
    expect(triggerLabel("unknown.trigger")).toBe("unknown.trigger");
    expect(originLabel(task("a"), "Çamaşır Makinesi")).toBe("Çamaşır Makinesi bitti");
    expect(originLabel(task("a", { origin: "manual" }), undefined)).toBe(tr.home.tasks.origins.manual);
  });

  it("maps the fleet slider between 1k and 1M homes on a log scale", () => {
    expect(homesFromSlider(0)).toBe(MIN_HOMES);
    expect(homesFromSlider(1)).toBe(MAX_HOMES);
    expect(homesFromSlider(0.5)).toBe(30_000);
    expect(homesFromSlider(-1)).toBe(MIN_HOMES);
    expect(homesFromSlider(2)).toBe(MAX_HOMES);
    expect(sliderFromHomes(MIN_HOMES)).toBe(0);
    expect(sliderFromHomes(MAX_HOMES)).toBeCloseTo(1);
    expect(sliderFromHomes(100_000)).toBeCloseTo(2 / 3, 5);
    for (const p of [0.1, 0.33, 0.8]) {
      const homes = homesFromSlider(p);
      expect(homes).toBeGreaterThanOrEqual(MIN_HOMES);
      expect(homes).toBeLessThanOrEqual(MAX_HOMES);
    }
  });

  it("maps rates into chart coordinates", () => {
    const pad = { top: 10, right: 10, bottom: 20, left: 30 };
    const points = ratePoints([{ weekStartUtc: "a", rate: 0 }, { weekStartUtc: "b", rate: 0.5 }, { weekStartUtc: "c", rate: 1 }], 130, 130, pad);
    expect(points).toEqual([{ x: 30, y: 110 }, { x: 75, y: 60 }, { x: 120, y: 10 }]);
    expect(pathFor(points)).toBe("M30 110 L75 60 L120 10");
    expect(ratePoints([{ weekStartUtc: "a", rate: 2 }], 130, 130, pad)[0]).toEqual({ x: 75, y: 10 });
    expect(pathFor([])).toBe("");
  });
});

// ---------- controller ----------

describe("Slice2Controller", () => {
  function fakeApi(overrides: Partial<Slice2Api> = {}): Slice2Api {
    const settings = {
      memberId: "ayse",
      priorityMode: "saving" as const,
      notificationLevel: "balanced" as const,
      quietHours: { from: "23:00", to: "08:00" },
      clothing: "normal" as const,
      locationSharing: true,
      sensitivityOffset: 0,
      estimatedMonthlyTl: { saving: 380, balanced: 460, comfort: 590 },
    };
    const base = {
      getSettings: vi.fn((memberId: string) => Promise.resolve({ ...settings, memberId })),
      putSettings: vi.fn(() => Promise.resolve(null)),
      getContext: vi.fn(() =>
        Promise.resolve({ outdoorTempC: 31, outdoorHumidityPct: 48, season: "summer", simulatedNowUtc: "2026-11-03T10:00:00Z", priorityMode: "saving" as const, members: [] }),
      ),
      listNotifications: vi.fn(() => Promise.resolve([] as AppNotification[])),
      markNotificationRead: vi.fn(() => Promise.resolve(null)),
      listTasks: vi.fn((status: string) => Promise.resolve(status === "open" ? [task("t-open")] : status === "done" ? [task("t-done", { status: "done" as const })] : [])),
    };
    return { ...base, ...overrides } as unknown as Slice2Api;
  }

  function create(api: Slice2Api, initial: Record<string, string> = {}) {
    const storage = memoryStore(initial);
    const onMemberChange = vi.fn();
    const controller = new Slice2Controller({ api: () => Promise.resolve(api), storage, onMemberChange });
    const changes: S2Change[] = [];
    controller.subscribe((c) => changes.push(c));
    return { controller, storage, onMemberChange, changes };
  }

  it("starts with the stored member or the default one", () => {
    expect(create(fakeApi()).controller.memberId).toBe(DEFAULT_MEMBER);
    expect(create(fakeApi(), { [MEMBER_STORAGE_KEY]: "zeynep" }).controller.memberId).toBe("zeynep");
  });

  it("switches the demo member, remembers it and tells the chat to restart", async () => {
    const api = fakeApi();
    const { controller, storage, onMemberChange, changes } = create(api);
    controller.setMember("mehmet");
    expect(controller.memberId).toBe("mehmet");
    expect(storage.get(MEMBER_STORAGE_KEY)).toBe("mehmet");
    expect(onMemberChange).toHaveBeenCalledWith("mehmet");
    expect(changes[0]).toEqual({ kind: "member" });
    await vi.waitFor(() => {
      expect(controller.settings?.memberId).toBe("mehmet");
    });
    controller.setMember("mehmet");
    expect(onMemberChange).toHaveBeenCalledTimes(1);
  });

  it("falls back when the stored member does not exist in the home", () => {
    const { controller } = create(fakeApi(), { [MEMBER_STORAGE_KEY]: "ghost" });
    controller.validateMember(["ayse", "mehmet", "zeynep"]);
    expect(controller.memberId).toBe("ayse");
    controller.validateMember([]);
    expect(controller.memberId).toBe("ayse");
  });

  it("applies settings changes immediately and rolls back from the server when saving fails", async () => {
    const putSettings = vi.fn(() => Promise.reject(new ApiError("http", 500)));
    const { controller } = create(fakeApi({ putSettings }));
    await controller.loadSettings();
    const pending = controller.updateSettings({ priorityMode: "comfort" });
    expect(controller.settings?.priorityMode).toBe("comfort");
    await expect(pending).rejects.toBeInstanceOf(ApiError);
    expect(controller.settings?.priorityMode).toBe("saving");
  });

  it("refreshes the context after the priority mode changed", async () => {
    const getContext = vi.fn(() =>
      Promise.resolve({ outdoorTempC: 31, outdoorHumidityPct: 48, season: "summer", simulatedNowUtc: "", priorityMode: "comfort" as const, members: [] }),
    );
    const { controller } = create(fakeApi({ getContext }));
    await controller.loadSettings();
    await controller.updateSettings({ priorityMode: "comfort" });
    expect(getContext).toHaveBeenCalledTimes(1);
    expect(controller.context?.priorityMode).toBe("comfort");
    await controller.updateSettings({ clothing: "heavy" });
    expect(getContext).toHaveBeenCalledTimes(1);
  });

  it("shows only the current member's notifications and counts unread ones", async () => {
    const markNotificationRead = vi.fn(() => Promise.reject(new ApiError("http", 500)));
    const { controller, changes } = create(fakeApi({ markNotificationRead }));
    controller.ingestNotification(notification("mine", { memberId: "ayse" }));
    controller.ingestNotification(notification("theirs", { memberId: "mehmet" }));
    expect(controller.notifications.map((n) => n.id)).toEqual(["mine"]);
    expect(controller.unread).toBe(1);
    expect(changes.at(-1)).toMatchObject({ kind: "notifications", created: { id: "mine" } });

    await expect(controller.markRead("mine")).rejects.toBeInstanceOf(ApiError);
    expect(controller.unread).toBe(1);
  });

  it("marks notifications read once", async () => {
    const markNotificationRead = vi.fn(() => Promise.resolve(null));
    const { controller } = create(fakeApi({ markNotificationRead }));
    controller.ingestNotification(notification("n1"));
    await controller.markRead("n1");
    await controller.markRead("n1");
    expect(controller.unread).toBe(0);
    expect(markNotificationRead).toHaveBeenCalledTimes(1);
  });

  it("opens the comfort question only for the current member", () => {
    const { controller, changes } = create(fakeApi());
    controller.ingestComfortCheck({ applianceId: "ac-salon", memberId: "mehmet", commandId: null });
    expect(changes).toHaveLength(0);
    controller.ingestComfortCheck({ applianceId: "ac-salon", memberId: "ayse", commandId: "c1" });
    expect(changes).toEqual([{ kind: "comfortCheck", check: { applianceId: "ac-salon", memberId: "ayse", commandId: "c1" } }]);
  });

  it("loads tasks of every status and merges live and action results", async () => {
    const { controller, changes } = create(fakeApi());
    expect(controller.tasksLoaded).toBe(false);
    await controller.loadTasks();
    expect(controller.tasksLoaded).toBe(true);
    expect(controller.tasks.map((t) => t.id).sort()).toEqual(["t-done", "t-open"]);

    controller.ingestTaskAssigned(task("t-live"));
    expect(changes.at(-1)).toMatchObject({ kind: "tasks", assigned: { id: "t-live" } });
    controller.applyTask(task("t-live", { status: "done" }));
    expect(controller.tasks.find((t) => t.id === "t-live")?.status).toBe("done");
    expect(changes.at(-1)).toEqual({ kind: "tasks" });
  });
});
