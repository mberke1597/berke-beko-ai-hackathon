// In-browser stand-in for the slice 2 backend (VITE_USE_MOCK=true): settings, comfort, energy, presence,
// tasks, learning, notifications and dashboard. Plausible and deterministic, not physically accurate.

import { ApiError } from "../../api/errors.ts";
import { snapToRange } from "../commands.ts";
import type { Slice2Api } from "../slice2/api.ts";
import type {
  AppNotification,
  Band,
  Comfort,
  ComfortFeedback,
  Conflict,
  DashboardSummary,
  HouseholdTask,
  NewTask,
  NotificationAction,
  NotificationImportance,
  NotificationKind,
  PriorityMode,
  Profile,
  Recommendation,
  Route,
  Routine,
  RulePatch,
  Settings,
  SettingsPatch,
  TaskRule,
  TaskStat,
  TaskStatus,
} from "../slice2/types.ts";
import type { Appliance } from "../types.ts";
import type { MockAgentStats } from "./agent.ts";
import type { MockHome } from "./simulator.ts";

export interface MockEngineOptions {
  now?: () => Date;
  latencyMs?: number;
  /** Real milliseconds between an AI command and the comfort question. */
  comfortCheckDelayMs?: number;
}

export interface MockEngine {
  api: Slice2Api;
  dispose(): void;
}

const HOME_ID = "home-1";
const TARIFF = { day: 2.6, peak: 4.2, night: 1.5 };
const PRIORITY_BAND: Record<PriorityMode, number> = { saving: 1, balanced: 0.7, comfort: 0.5 };
const NEUTRAL_TEMP = { light: 25.8, normal: 24.5, heavy: 23.2 };
const FAN_COOLING: Record<string, number> = { auto: 0.05, low: 0.1, medium: 0.2, high: 0.35 };
const WEEK_MS = 7 * 24 * 60 * 60 * 1000;
/** Energy in the mock grows faster than real time so a short demo shows movement. */
const ENERGY_SCALE = 25;
const AWAY_DISTANCE_M = 5000;
const COMMUTE_STEP_M = 650;

const round1 = (v: number): number => Math.round(v * 10) / 10;
const round2 = (v: number): number => Math.round(v * 100) / 100;

/** Time-of-use tariff by local hour: night 22-06, peak 17-22, day otherwise. */
export function tariffAt(date: Date): number {
  const hour = date.getHours();
  if (hour >= 22 || hour < 6) return TARIFF.night;
  if (hour >= 17) return TARIFF.peak;
  return TARIFF.day;
}

function problem(status: number, title: string): ApiError {
  return new ApiError("http", status, { status, title });
}

interface TaskRecord extends HouseholdTask {
  doneAtMs: number | null;
}

interface PresenceState {
  route: Route;
  distanceM: number;
  band: Band;
}

interface MemberProfile {
  profile: Profile;
}

export function createMockEngine(home: MockHome, agent: { stats(): MockAgentStats }, options: MockEngineOptions = {}): MockEngine {
  const { now = () => new Date(), latencyMs = 60, comfortCheckDelayMs = 9000 } = options;
  const timers = new Set<ReturnType<typeof setTimeout>>();
  const initial = home.snapshot();
  const members = initial.members;
  const defaultMember = members.find((m) => m.isDefault)?.id ?? "ayse";
  let counter = 0;
  const nextId = (prefix: string): string => `${prefix}-${String(++counter)}`;

  const later = <T>(produce: () => T): Promise<T> =>
    new Promise<T>((resolve, reject) => {
      const run = (): void => {
        try {
          resolve(produce());
        } catch (error) {
          reject(error instanceof Error ? error : new Error(String(error)));
        }
      };
      if (latencyMs <= 0) run();
      else setTimeout(run, latencyMs);
    });

  // ---------- settings ----------

  const settings = new Map<string, Settings>();
  const defaultSettings = (memberId: string): Settings => ({
    memberId,
    priorityMode: "saving",
    notificationLevel: "balanced",
    quietHours: { from: "23:00", to: "08:00" },
    clothing: "normal",
    locationSharing: true,
    sensitivityOffset: 0,
    estimatedMonthlyTl: { saving: 380, balanced: 460, comfort: 590 },
  });
  const settingsOf = (memberId: string): Settings => {
    let s = settings.get(memberId);
    if (!s) {
      s = defaultSettings(memberId);
      settings.set(memberId, s);
    }
    return s;
  };
  const requireMember = (memberId: string): void => {
    if (!members.some((m) => m.id === memberId)) throw problem(404, "Üye bulunamadı");
  };

  // ---------- notifications ----------

  const notifications: AppNotification[] = [];
  const suggestionDays = new Map<string, number>();

  function insideQuietHours(memberId: string): boolean {
    const { from, to } = settingsOf(memberId).quietHours;
    const hhmm = `${String(now().getHours()).padStart(2, "0")}:${String(now().getMinutes()).padStart(2, "0")}`;
    return from <= to ? hhmm >= from && hhmm < to : hhmm >= from || hhmm < to;
  }

  /** Honours the member's notification level, quiet hours and the 3 suggestions per day cap. */
  function deliver(
    memberId: string,
    kind: NotificationKind,
    level: NotificationImportance,
    title: string,
    text: string,
    action: NotificationAction | null = null,
  ): AppNotification | undefined {
    const pref = settingsOf(memberId).notificationLevel;
    const allowed = pref === "detailed" || (pref === "balanced" && level !== "info") || level === "critical";
    if (!allowed || (level !== "critical" && insideQuietHours(memberId))) return undefined;
    if (kind === "suggestion") {
      const dayKey = `${memberId}:${now().toDateString()}`;
      const sent = suggestionDays.get(dayKey) ?? 0;
      if (sent >= 3) return undefined;
      suggestionDays.set(dayKey, sent + 1);
    }
    const notification: AppNotification = {
      id: nextId("n"),
      memberId,
      kind,
      level,
      title,
      text,
      createdAtUtc: now().toISOString(),
      read: false,
      action,
    };
    notifications.unshift(notification);
    home.emitExtra({ kind: "notificationCreated", notification: structuredClone(notification) });
    return notification;
  }

  // ---------- presence ----------

  const presence = new Map<string, PresenceState>(members.map((m) => [m.id, { route: "at_home", distanceM: 0, band: "home" }]));
  const bandFor = (distance: number, previous: Band): Band => {
    // Hysteresis: leave "home" above 300 m, return below 250 m.
    if (distance < (previous === "home" ? 300 : 250)) return "home";
    return distance < 2000 ? "near" : "away";
  };
  const nobodyHome = (): boolean => [...presence.values()].every((p) => p.band !== "home");

  function setAcs(command: (ac: Appliance) => { type: "power"; value: boolean } | { type: "setpoint"; value: number } | undefined, text: string): void {
    let changed = false;
    for (const appliance of home.snapshot().appliances) {
      if (appliance.type !== "air_conditioner") continue;
      const cmd = command(appliance);
      if (!cmd) continue;
      changed = true;
      void home.api.sendCommand(appliance.id, { ...cmd, source: "ai", memberId: defaultMember }).catch(() => undefined);
    }
    if (changed) deliver(defaultMember, "info", "important", "Çeliknaz evi ayarladı", text);
  }

  function onPresenceChange(member: string, before: Band, after: Band): void {
    const mode = settingsOf(defaultMember).priorityMode;
    if (after === "away" && nobodyHome()) {
      if (mode === "saving") setAcs((a) => (a.state.power ? { type: "power", value: false } : undefined), "Herkes evden çıktı; klimaları kapattım.");
      else {
        const target = mode === "balanced" ? 28 : 26.5;
        setAcs((a) => (a.state.power ? { type: "setpoint", value: snapToRange(target, a.capabilities.setpoint ?? { min: 16, max: 30, step: 0.5 }) } : undefined), `Herkes evden çıktı; klimaları ${String(target).replace(".", ",")}° bekleme ayarına aldım.`);
      }
    } else if (after === "near" && before === "away" && nobodyHome() && mode !== "saving") {
      setAcs((a) => (a.id === "ac-salon" ? { type: "power", value: true } : undefined), `${members.find((m) => m.id === member)?.displayName ?? member} eve yaklaşıyor; salonu önceden serinletiyorum.`);
    } else if (after === "home" && before !== "home" && mode === "saving") {
      setAcs((a) => (a.id === "ac-salon" && !a.state.power ? { type: "power", value: true } : undefined), "Eve hoş geldiniz; salon klimasını açtım.");
    }
  }

  function advancePresence(): void {
    for (const [id, state] of presence) {
      const before = state.band;
      if (state.route === "commute_out") state.distanceM = Math.min(AWAY_DISTANCE_M, state.distanceM + COMMUTE_STEP_M);
      else if (state.route === "commute_back") {
        state.distanceM = Math.max(0, state.distanceM - COMMUTE_STEP_M);
        if (state.distanceM === 0) state.route = "at_home";
      }
      state.band = bandFor(state.distanceM, state.band);
      if (state.band !== before) onPresenceChange(id, before, state.band);
    }
  }

  // ---------- energy ----------

  const todayKwh = new Map<string, number>(
    initial.appliances.map((a) => [a.id, { "ac-salon": 3.1, "ac-yatak": 0.6, "ac-cocuk": 0.4, "washer-1": 0.9, "dishwasher-1": 0.5, "oven-1": 0.7, "fridge-1": 0.8 }[a.id] ?? 0.2]),
  );
  const WEEK_BASE_KWH = 31.2;
  const SAVED_BASE_KWH = 4.2;

  function accumulateEnergy(): void {
    for (const a of home.snapshot().appliances) {
      const kwh = (a.state.powerW / 1000) * (1 / 60) * ENERGY_SCALE;
      todayKwh.set(a.id, (todayKwh.get(a.id) ?? 0) + kwh);
    }
  }

  // ---------- tasks ----------

  const rules: TaskRule[] = [
    { id: "rule-washer", trigger: "washing_machine.finished", template: "Çamaşırları as", strategy: "fair_share", fixedMemberId: null, effortPoints: 2, enabled: true },
    { id: "rule-dishwasher", trigger: "dishwasher.finished", template: "Bulaşık makinesini boşalt", strategy: "fair_share", fixedMemberId: null, effortPoints: 2, enabled: true },
    { id: "rule-oven", trigger: "oven.timer_done", template: "Fırını kontrol et", strategy: "fair_share", fixedMemberId: null, effortPoints: 1, enabled: false },
  ];
  const tasks: TaskRecord[] = [];
  const hoursAgo = (h: number): Date => new Date(now().getTime() - h * 3600_000);
  const seedTask = (title: string, memberId: string, effort: number, status: TaskStatus, ago: number): void => {
    tasks.push({
      id: nextId("task"),
      title,
      origin: "device_event",
      applianceId: null,
      assignedMemberId: memberId,
      status,
      effortPoints: effort,
      createdAtUtc: hoursAgo(ago).toISOString(),
      dueAtUtc: null,
      reason: "Adil dağıtım: son 7 günde en az iş yapan",
      doneAtMs: status === "done" ? hoursAgo(ago - 0.5).getTime() : null,
    });
  };
  seedTask("Çamaşırları as", "ayse", 2, "done", 70);
  seedTask("Bulaşık makinesini boşalt", "ayse", 2, "done", 50);
  seedTask("Çamaşırları as", "ayse", 2, "done", 30);
  seedTask("Bulaşık makinesini boşalt", "mehmet", 2, "done", 40);
  seedTask("Çamaşırları as", "mehmet", 2, "done", 20);
  seedTask("Bulaşık makinesini boşalt", "zeynep", 2, "done", 10);
  let rrIndex = 0;

  const pointsOf = (memberId: string): number =>
    tasks.filter((t) => t.assignedMemberId === memberId && t.status === "done" && t.doneAtMs !== null && now().getTime() - t.doneAtMs <= WEEK_MS).reduce((n, t) => n + t.effortPoints, 0);
  const lastTaskAt = (memberId: string): number =>
    Math.max(0, ...tasks.filter((t) => t.assignedMemberId === memberId).map((t) => Date.parse(t.createdAtUtc)));

  function pick(rule: TaskRule | undefined): { memberId: string; reason: string } {
    const strategy = rule?.strategy ?? "fair_share";
    if (strategy === "fixed" && rule?.fixedMemberId) return { memberId: rule.fixedMemberId, reason: "Sabit kişi: kural gereği hep aynı kişi" };
    if (strategy === "round_robin") {
      const member = members[rrIndex % members.length];
      rrIndex += 1;
      return { memberId: member?.id ?? defaultMember, reason: "Sırayla: sıra bu kişide" };
    }
    if (strategy === "ask") return { memberId: defaultMember, reason: "Kime verileceği soruluyor; şimdilik ev sahibine atandı" };
    const atHome = members.filter((m) => presence.get(m.id)?.band === "home");
    const pool = atHome.length > 0 ? atHome : members;
    const sorted = [...pool].sort((a, b) => pointsOf(a.id) - pointsOf(b.id) || lastTaskAt(a.id) - lastTaskAt(b.id));
    return { memberId: sorted[0]?.id ?? defaultMember, reason: atHome.length > 0 ? "Adil dağıtım: evde olanlar arasında son 7 günde en az iş yapan" : "Adil dağıtım: son 7 günde en az iş yapan" };
  }

  function announceTask(task: TaskRecord): void {
    home.emitExtra({ kind: "taskAssigned", task: publicTask(task) });
    const name = members.find((m) => m.id === task.assignedMemberId)?.displayName ?? task.assignedMemberId;
    deliver(task.assignedMemberId, "task", "important", "Yeni görev", `${task.title} (${name} için, ${String(task.effortPoints)} puan)`);
  }

  function createTaskFromEvent(applianceId: string, event: string): void {
    const appliance = home.snapshot().appliances.find((a) => a.id === applianceId);
    if (!appliance) return;
    const prefix: Record<Appliance["type"], string> = {
      air_conditioner: "air_conditioner",
      washing_machine: "washing_machine",
      dishwasher: "dishwasher",
      oven: "oven",
      refrigerator: "refrigerator",
    };
    const rule = rules.find((r) => r.enabled && r.trigger === `${prefix[appliance.type]}.${event}`);
    if (!rule) return;
    const { memberId, reason } = pick(rule);
    const task: TaskRecord = {
      id: nextId("task"),
      title: rule.template,
      origin: "device_event",
      applianceId,
      assignedMemberId: memberId,
      status: "open",
      effortPoints: rule.effortPoints,
      createdAtUtc: now().toISOString(),
      dueAtUtc: null,
      reason,
      doneAtMs: null,
    };
    tasks.unshift(task);
    announceTask(task);
  }

  const publicTask = (t: TaskRecord): HouseholdTask => {
    const rest: Partial<TaskRecord> = structuredClone(t);
    delete rest.doneAtMs;
    return rest as HouseholdTask;
  };
  const findTask = (id: string): TaskRecord => {
    const t = tasks.find((x) => x.id === id);
    if (!t) throw problem(404, "Görev bulunamadı");
    return t;
  };

  // ---------- learning ----------

  const profiles = new Map<string, MemberProfile>();
  const feedbackCounts = new Map<string, { good: number; total: number }>();
  const emptyProfile = (): Profile => ({ sensitivityOffset: 0, preferences: [], routines: [], overrideRate: [], goodFeedbackRate: [] });
  const profileOf = (memberId: string): MemberProfile => {
    let p = profiles.get(memberId);
    if (!p) {
      p = { profile: emptyProfile() };
      profiles.set(memberId, p);
    }
    return p;
  };
  const findRoutine = (id: string): { routine: Routine; owner: string } => {
    for (const [owner, p] of profiles) {
      const routine = p.profile.routines.find((r) => r.id === id);
      if (routine) return { routine, owner };
    }
    throw problem(404, "Rutin bulunamadı");
  };

  function weekSeries(values: number[]): { weekStartUtc: string; rate: number }[] {
    return values.map((rate, i) => ({ weekStartUtc: new Date(now().getTime() - (values.length - 1 - i) * WEEK_MS).toISOString(), rate }));
  }

  function seedHistory(memberId: string, weeks: number): number {
    const p = profileOf(memberId);
    const offset = -0.3;
    settingsOf(memberId).sensitivityOffset = offset;
    const w = Math.max(2, Math.min(12, weeks));
    const ramp = (from: number, to: number): number[] => Array.from({ length: w }, (_, i) => round2(from + ((to - from) * i) / (w - 1)));
    p.profile = {
      sensitivityOffset: offset,
      preferences: [
        { context: "hafta içi akşam, sıcak", setpoint: 24.5, confidence: 0.8, samples: 13, text: "Hafta içi akşamları sıcak havada salonu 24,5° tercih ediyorsunuz." },
        { context: "gece", setpoint: 26, confidence: 0.7, samples: 9, text: "Geceleri yatak odasını 26° civarında tutuyorsunuz." },
      ],
      routines: [
        { id: "routine-1", action: "power_on", applianceId: "ac-salon", dayType: "weekday", time: "18:30", windowMin: 20, confidence: 0.85, evidence: "Son 3 haftada 13 kez 18:30 civarı açtınız", status: "suggested" },
        { id: "routine-2", action: "power_on", applianceId: "ac-yatak", dayType: "weekend", time: "22:15", windowMin: 20, confidence: 0.62, evidence: "Son 4 hafta sonunda 6 kez 22:15 civarı açtınız", status: "detected" },
      ],
      overrideRate: weekSeries(ramp(0.42, 0.12)),
      goodFeedbackRate: weekSeries(ramp(0.55, 0.88)),
    };
    deliver(memberId, "suggestion", "important", "Yeni rutin önerisi", "Hafta içi 18:30'da salonu açıyorsunuz. Otomatik yapayım mı?", { type: "accept_routine", routineId: "routine-1" });
    return w * 34;
  }

  // ---------- comfort ----------

  function pmvOf(roomTempC: number, humidityPct: number, fan: string, clothing: Settings["clothing"], offset: number): number {
    const base = (roomTempC - NEUTRAL_TEMP[clothing]) * 0.4 + (humidityPct - 50) * 0.012 - (FAN_COOLING[fan] ?? 0.05);
    return round2(base + offset);
  }

  function feelingOf(pmv: number): string {
    if (pmv < -2.5) return "cold";
    if (pmv < -1.5) return "cool";
    if (pmv < -0.5) return "slightly_cool";
    if (pmv <= 0.5) return "neutral";
    if (pmv <= 1.5) return "slightly_warm";
    if (pmv <= 2.5) return "warm";
    return "hot";
  }

  function acCostPerHour(ac: { roomTempC: number }, setpoint: number, mode: string): number {
    const kw = mode === "fan" ? 0.06 : mode === "dry" ? 0.35 : Math.min(1.2, 0.3 + 0.12 * Math.abs(ac.roomTempC - setpoint));
    return round2(kw * tariffAt(now()));
  }

  function comfortFor(applianceId: string, memberIds: readonly string[] | undefined): Comfort {
    const appliance = home.snapshot().appliances.find((a) => a.id === applianceId);
    if (!appliance?.state.ac) throw problem(404, "Klima bulunamadı");
    const ac = appliance.state.ac;
    const range = appliance.capabilities.setpoint ?? { min: 16, max: 30, step: 0.5 };
    const atHome = members.filter((m) => presence.get(m.id)?.band === "home").map((m) => m.id);
    const ids = memberIds && memberIds.length > 0 ? [...memberIds] : atHome.length > 0 ? atHome : [defaultMember];
    const inputs = ids.map((id) => settingsOf(id));
    const priority = settingsOf(defaultMember).priorityMode;
    const band = PRIORITY_BAND[priority];
    const pmvs = inputs.map((s) => pmvOf(ac.roomTempC, ac.humidityPct, ac.fan, s.clothing, s.sensitivityOffset));
    const worst = pmvs.reduce((w, v) => (Math.abs(v) > Math.abs(w) ? v : w), 0);
    const inBand = Math.abs(worst) <= band;
    const outdoor = 31;

    const pmvAt = (sp: number): number[] => inputs.map((s) => pmvOf(sp, ac.humidityPct, ac.fan, s.clothing, s.sensitivityOffset));
    const worstAt = (sp: number): number => Math.max(...pmvAt(sp).map(Math.abs));
    let compromise = ac.setpoint;
    let best = Number.POSITIVE_INFINITY;
    for (let sp = range.max; sp >= range.min; sp -= range.step) {
      const w = worstAt(sp);
      if (w < best - 1e-9) {
        best = w;
        compromise = sp;
      }
    }
    const opposing = pmvs.length > 1 && Math.min(...pmvs) < 0 && Math.max(...pmvs) > 0;
    const conflict: Conflict =
      pmvs.length > 1
        ? { present: opposing, compromiseSetpoint: opposing ? compromise : null, perMember: ids.map((memberId, i) => ({ memberId, pmvAtCompromise: pmvAt(compromise)[i] ?? 0 })) }
        : { present: false, compromiseSetpoint: null, perMember: [] };

    const costNow = acCostPerHour(ac, ac.setpoint, ac.mode);
    let recommendation: Recommendation;
    const warm = worst > 0;
    if (inBand) {
      recommendation = { action: "none", setpoint: null, mode: null, fan: null, reason: "Oda konfor aralığında; bir şey yapmanıza gerek yok.", costPerHourTl: appliance.state.power ? costNow : 0, alternatives: [] };
    } else if (warm && outdoor <= ac.roomTempC - 2) {
      recommendation = { action: "window", setpoint: null, mode: null, fan: null, reason: "Dışarısı odadan daha serin; pencereyi açmak ücretsiz.", costPerHourTl: 0, alternatives: [] };
    } else if (warm && ac.humidityPct > 60) {
      recommendation = { action: "dry", setpoint: null, mode: "dry", fan: null, reason: "Nem yüksek; nem alma modu daha ucuza daha rahat hissettirir.", costPerHourTl: acCostPerHour(ac, ac.setpoint, "dry"), alternatives: [] };
    } else if (warm && ac.fan !== "high" && worst <= band + 0.6) {
      recommendation = { action: "fan", setpoint: null, mode: null, fan: "high", reason: "Hafif sıcak; fan hızını artırmak kompresörden daha az enerji harcar.", costPerHourTl: acCostPerHour(ac, ac.setpoint, "fan"), alternatives: [] };
    } else {
      const target = opposing ? compromise : (() => {
        const dir = warm ? -1 : 1;
        let sp = snapToRange(ac.setpoint, range);
        for (let i = 0; i < 40; i++) {
          const w = Math.max(...pmvAt(sp).map(Math.abs));
          if (w <= band * 0.9) break;
          const next = sp + dir * range.step;
          if (next < range.min || next > range.max) break;
          sp = next;
        }
        return sp;
      })();
      const mode = warm ? "cool" : "heat";
      recommendation = {
        action: "setpoint",
        setpoint: snapToRange(target, range),
        mode,
        fan: null,
        reason: opposing ? "Birden fazla kişi var; herkesin en az rahatsız olacağı uzlaşma ayarı." : warm ? "Oda konfor aralığının üstünde; hedefi biraz düşürmek yeterli." : "Oda konfor aralığının altında; hedefi biraz yükseltmek yeterli.",
        costPerHourTl: acCostPerHour(ac, target, mode),
        alternatives: [],
      };
      if (warm && ac.fan !== "high") recommendation.alternatives.push({ action: "fan", setpoint: null, costPerHourTl: acCostPerHour(ac, ac.setpoint, "fan") });
    }

    return {
      applianceId,
      members: ids,
      roomTempC: ac.roomTempC,
      humidityPct: ac.humidityPct,
      pmv: worst,
      feeling: feelingOf(worst),
      inBand,
      bandPmv: band,
      recommendation,
      conflict,
    };
  }

  // ---------- subscriptions on the mock home ----------

  const commandsBySource = { voice: 0, app: 0, remote: 0, ai: 0, schedule: 0 };
  const stopCommands = home.onCommand((appliance, request) => {
    commandsBySource[request.source] += 1;
    if (request.source === "ai" && appliance.type === "air_conditioner" && request.type !== "power") {
      const timer = setTimeout(() => {
        timers.delete(timer);
        if (settingsOf(request.memberId).notificationLevel === "quiet") return;
        home.emitExtra({ kind: "comfortCheckRequested", check: { applianceId: appliance.id, memberId: request.memberId, commandId: null } });
      }, comfortCheckDelayMs);
      timers.add(timer);
    }
  });
  const stopTicks = home.onTick(() => {
    accumulateEnergy();
    advancePresence();
  });
  const stopLive = home.connectLive(HOME_ID, {
    stateChanged: () => undefined,
    status: () => undefined,
    event: (event) => {
      if (event.event === "finished" || event.event === "timer_done") createTaskFromEvent(event.applianceId, event.event);
    },
  });

  // ---------- dashboard ----------

  function energySummary(): { todayKwh: number; todayTl: number; weekKwh: number; weekTl: number; savedWeekKwh: number; savedWeekTl: number } {
    const today = [...todayKwh.values()].reduce((n, v) => n + v, 0);
    const tariff = tariffAt(now());
    const weekKwh = WEEK_BASE_KWH + today;
    const savedKwh = SAVED_BASE_KWH + today * 0.12;
    return {
      todayKwh: round1(today),
      todayTl: round1(today * tariff),
      weekKwh: round1(weekKwh),
      weekTl: round1(weekKwh * TARIFF.day),
      savedWeekKwh: round1(savedKwh),
      savedWeekTl: round1(savedKwh * TARIFF.day),
    };
  }

  function summary(): DashboardSummary {
    const stats = agent.stats();
    const costToday = stats.runs.reduce((n, r) => n + r.costUsd, 0);
    // Trends of the first member that has learned something (the demo seeds one member at a time).
    const learning = [...profiles.values()].map((p) => p.profile).find((p) => p.overrideRate.length > 0) ?? emptyProfile();
    const e = energySummary();
    const decided = notifications.filter((n) => n.kind === "suggestion");
    const accepted = [...profiles.values()].flatMap((p) => p.profile.routines).filter((r) => r.status === "accepted").length;
    return {
      homes: 1,
      activeMembers: members.length,
      commandsToday: { ...commandsBySource },
      conversationsToday: stats.conversations,
      runsToday: stats.runs.length,
      llm: {
        costUsdToday: round2(costToday * 10000) / 10000,
        costUsdMonth: round2(1.42 + costToday),
        tokensToday: stats.tokens,
        avgCostUsdPerConversation: stats.conversations > 0 ? Number((costToday / stats.conversations).toFixed(4)) : 0,
        avgLatencyMs: stats.runs.length > 0 ? Math.round(stats.runs.reduce((n, r) => n + r.durationMs, 0) / stats.runs.length) : 0,
      },
      energy: { savedKwhWeek: e.savedWeekKwh, savedTlWeek: e.savedWeekTl },
      learning: {
        overrideRateTrend: structuredClone(learning.overrideRate),
        goodFeedbackTrend: structuredClone(learning.goodFeedbackRate),
        suggestionAcceptanceRate: decided.length > 0 ? round2(accepted / decided.length) : 0,
      },
      tools: stats.tools,
      server: { monthlyCostTl: 450, costPerActiveUserTl: round2(450 / members.length) },
    };
  }

  // ---------- API ----------

  const api: Slice2Api = {
    getSettings: (memberId) =>
      later(() => {
        requireMember(memberId);
        return structuredClone(settingsOf(memberId));
      }),
    putSettings: (memberId, patch: SettingsPatch) =>
      later(() => {
        requireMember(memberId);
        const s = settingsOf(memberId);
        if (patch.priorityMode !== undefined) s.priorityMode = patch.priorityMode;
        if (patch.notificationLevel !== undefined) s.notificationLevel = patch.notificationLevel;
        if (patch.quietHours !== undefined) s.quietHours = { ...patch.quietHours };
        if (patch.clothing !== undefined) s.clothing = patch.clothing;
        if (patch.locationSharing !== undefined) s.locationSharing = patch.locationSharing;
        return null;
      }),
    getContext: () =>
      later(() => ({
        outdoorTempC: 31,
        outdoorHumidityPct: 48,
        season: "summer",
        simulatedNowUtc: now().toISOString(),
        priorityMode: settingsOf(defaultMember).priorityMode,
        members: members.map((m) => ({ memberId: m.id, band: presence.get(m.id)?.band ?? "home" })),
      })),

    getComfort: (applianceId, memberIds) => later(() => comfortFor(applianceId, memberIds)),
    postComfortFeedback: (memberId, feedback: ComfortFeedback) =>
      later(() => {
        requireMember(memberId);
        const s = settingsOf(memberId);
        s.sensitivityOffset = round2(Math.max(-1, Math.min(1, s.sensitivityOffset + feedback.sensation * 0.1)));
        if (feedback.clothing !== null) s.clothing = feedback.clothing;
        const c = feedbackCounts.get(memberId) ?? { good: 0, total: 0 };
        c.total += 1;
        if (feedback.sensation === 0) c.good += 1;
        feedbackCounts.set(memberId, c);
        return null;
      }),
    getEnergy: () =>
      later(() => {
        const e = energySummary();
        const tariff = tariffAt(now());
        return {
          ...e,
          perDevice: [...todayKwh.entries()].map(([applianceId, kwh]) => ({ applianceId, todayKwh: round1(kwh), todayTl: round1(kwh * tariff) })),
        };
      }),
    getCheapestTime: (applianceId, programId, deadlineUtc) =>
      later(() => {
        const appliance = home.snapshot().appliances.find((a) => a.id === applianceId);
        if (!appliance) throw problem(404, "Cihaz bulunamadı");
        if (!appliance.capabilities.programs.includes(programId)) throw problem(422, "Program desteklenmiyor");
        const durations: Record<string, number> = { cotton40: 120, cotton60: 150, eco40: 180, synthetic40: 90, quick30: 30, eco50: 180, auto: 120, intensive70: 140, quick35: 30 };
        const watts: Record<string, number> = { cotton40: 450, cotton60: 650, eco40: 300, synthetic40: 400, quick30: 500, eco50: 350, auto: 600, intensive70: 900, quick35: 800 };
        const kwh = ((durations[programId] ?? 90) / 60) * ((watts[programId] ?? 450) / 1000);
        const start = now();
        const deadline = Date.parse(deadlineUtc);
        const latest = Number.isNaN(deadline) ? start.getTime() + 12 * 3600_000 : deadline;
        const costAt = (t: number): number => kwh * tariffAt(new Date(t));
        let bestStart = start.getTime();
        for (let t = start.getTime(); t <= latest; t += 30 * 60_000) {
          if (costAt(t) < costAt(bestStart) - 1e-9) bestStart = t;
        }
        const nowCost = costAt(start.getTime());
        const cost = costAt(bestStart);
        return { startAtUtc: new Date(bestStart).toISOString(), costTl: round1(cost), nowCostTl: round1(nowCost), savingTl: round1(Math.max(0, nowCost - cost)) };
      }),

    getPresence: () =>
      later(() =>
        members.map((m) => {
          const p = presence.get(m.id) ?? { route: "at_home" as Route, distanceM: 0, band: "home" as Band };
          return {
            memberId: m.id,
            band: p.band,
            distanceM: p.distanceM,
            etaMin: p.route === "commute_back" && p.distanceM > 0 ? Math.ceil(p.distanceM / COMMUTE_STEP_M) : null,
            updatedAtUtc: now().toISOString(),
          };
        }),
      ),
    setRoute: (memberId, route) =>
      later(() => {
        requireMember(memberId);
        const p = presence.get(memberId);
        if (!p) return null;
        p.route = route;
        if (route === "at_home") {
          p.distanceM = 0;
          const before = p.band;
          p.band = "home";
          if (before !== "home") onPresenceChange(memberId, before, "home");
        } else if (route === "commute_back" && p.distanceM < 300) {
          p.distanceM = AWAY_DISTANCE_M;
        }
        return null;
      }),

    listTasks: (status) => later(() => tasks.filter((t) => t.status === status).map(publicTask)),
    createTask: (input: NewTask) =>
      later(() => {
        const title = input.title.trim();
        if (title === "") throw problem(422, "Görev adı gerekli");
        const chosen = input.assignedMemberId === null ? pick(undefined) : { memberId: input.assignedMemberId, reason: "Elle atandı" };
        const task: TaskRecord = {
          id: nextId("task"),
          title,
          origin: "manual",
          applianceId: null,
          assignedMemberId: chosen.memberId,
          status: "open",
          effortPoints: input.effortPoints,
          createdAtUtc: now().toISOString(),
          dueAtUtc: null,
          reason: chosen.reason,
          doneAtMs: null,
        };
        tasks.unshift(task);
        announceTask(task);
        return publicTask(task);
      }),
    completeTask: (id) =>
      later(() => {
        const t = findTask(id);
        t.status = "done";
        t.doneAtMs = now().getTime();
        return publicTask(t);
      }),
    skipTask: (id) =>
      later(() => {
        const t = findTask(id);
        t.status = "skipped";
        return publicTask(t);
      }),
    reassignTask: (id, memberId) =>
      later(() => {
        const t = findTask(id);
        const chosen = memberId === null ? pick(undefined) : { memberId, reason: "Elle devredildi" };
        t.assignedMemberId = chosen.memberId;
        t.reason = chosen.reason;
        announceTask(t);
        return publicTask(t);
      }),
    listRules: () => later(() => structuredClone(rules)),
    putRule: (id, patch: RulePatch) =>
      later(() => {
        const rule = rules.find((r) => r.id === id);
        if (!rule) throw problem(404, "Kural bulunamadı");
        if (patch.strategy !== undefined) rule.strategy = patch.strategy;
        if (patch.fixedMemberId !== undefined) rule.fixedMemberId = patch.fixedMemberId;
        if (patch.template !== undefined) rule.template = patch.template;
        if (patch.effortPoints !== undefined) rule.effortPoints = patch.effortPoints;
        if (patch.enabled !== undefined) rule.enabled = patch.enabled;
        return structuredClone(rule);
      }),
    getTaskStats: () =>
      later(() =>
        members.map(
          (m): TaskStat => ({
            memberId: m.id,
            pointsLast7Days: pointsOf(m.id),
            doneLast7Days: tasks.filter((t) => t.assignedMemberId === m.id && t.status === "done" && t.doneAtMs !== null && now().getTime() - t.doneAtMs <= WEEK_MS).length,
          }),
        ),
      ),

    getProfile: (memberId) =>
      later(() => {
        requireMember(memberId);
        const p = structuredClone(profileOf(memberId).profile);
        p.sensitivityOffset = settingsOf(memberId).sensitivityOffset;
        return p;
      }),
    acceptRoutine: (id) =>
      later(() => {
        const { routine } = findRoutine(id);
        routine.status = "accepted";
        return structuredClone(routine);
      }),
    rejectRoutine: (id) =>
      later(() => {
        const { routine } = findRoutine(id);
        routine.status = "rejected";
        return structuredClone(routine);
      }),
    resetLearning: (memberId) =>
      later(() => {
        requireMember(memberId);
        profiles.set(memberId, { profile: emptyProfile() });
        settingsOf(memberId).sensitivityOffset = 0;
        feedbackCounts.delete(memberId);
        return null;
      }),
    seedHistory: (memberId, weeks) =>
      later(() => {
        requireMember(memberId);
        return { events: seedHistory(memberId, weeks) };
      }),

    listNotifications: (memberId) => later(() => structuredClone(notifications.filter((n) => n.memberId === memberId))),
    markNotificationRead: (id) =>
      later(() => {
        const n = notifications.find((x) => x.id === id);
        if (!n) throw problem(404, "Bildirim bulunamadı");
        n.read = true;
        return null;
      }),

    getSummary: () => later(() => summary()),
    getProjection: (homes) =>
      later(() => {
        const s = summary();
        const perHomeLlm = Math.max(s.llm.costUsdMonth, 1.5);
        return {
          homes,
          llmCostUsdMonth: round2(perHomeLlm * homes),
          serverCostTlMonth: round2(homes * 6),
          savedKwhMonth: round1(s.energy.savedKwhWeek * 4.3 * homes),
          savedTlMonth: round1(s.energy.savedTlWeek * 4.3 * homes),
          assumptions: [
            "Her ev, demo evinin ortalama kullanımıyla aynı davranır.",
            "LLM maliyeti ev başına aylık ortalamadan doğrusal ölçeklenir.",
            "Sunucu maliyeti ev başına yaklaşık 6 TL kabul edilmiştir.",
            "Tasarruf, Çeliknaz olmadan aynı saatlerdeki kullanıma göre tahmin edilir.",
          ],
        };
      }),
    listRuns: (limit) => later(() => agent.stats().runs.slice(0, limit)),
  };

  return {
    api,
    dispose: () => {
      stopCommands();
      stopTicks();
      stopLive();
      for (const t of timers) clearTimeout(t);
      timers.clear();
    },
  };
}
