// Types and lenient decoders for "Çeliknaz slice 2" (docs/architecture/contracts.md).
// Decoders require the identifying fields and default optional ones, so small backend additions do not break the UI.

export const PRIORITY_MODES = ["saving", "balanced", "comfort"] as const;
export type PriorityMode = (typeof PRIORITY_MODES)[number];

export const NOTIFICATION_LEVELS = ["quiet", "balanced", "detailed"] as const;
export type NotificationLevel = (typeof NOTIFICATION_LEVELS)[number];

export const CLOTHING_LEVELS = ["light", "normal", "heavy"] as const;
export type Clothing = (typeof CLOTHING_LEVELS)[number];

export type Band = "home" | "near" | "away";
export type Route = "at_home" | "commute_out" | "commute_back";
export const ROUTES: readonly Route[] = ["at_home", "commute_out", "commute_back"];

export interface QuietHours {
  from: string;
  to: string;
}

export interface Settings {
  memberId: string;
  priorityMode: PriorityMode;
  notificationLevel: NotificationLevel;
  quietHours: QuietHours;
  clothing: Clothing;
  locationSharing: boolean;
  sensitivityOffset: number;
  estimatedMonthlyTl: Record<PriorityMode, number>;
}

/** Writable settings fields (`PUT /api/me/settings`). */
export interface SettingsPatch {
  priorityMode?: PriorityMode;
  notificationLevel?: NotificationLevel;
  quietHours?: QuietHours;
  clothing?: Clothing;
  locationSharing?: boolean;
}

export interface ContextMember {
  memberId: string;
  band: Band;
}

export interface HomeContext {
  outdoorTempC: number;
  outdoorHumidityPct: number;
  season: string;
  simulatedNowUtc: string;
  priorityMode: PriorityMode;
  members: ContextMember[];
}

export type RecommendationAction = "none" | "window" | "fan" | "dry" | "setpoint";

export interface Alternative {
  action: RecommendationAction;
  setpoint: number | null;
  costPerHourTl: number | null;
}

export interface Recommendation {
  action: RecommendationAction;
  setpoint: number | null;
  mode: string | null;
  fan: string | null;
  reason: string;
  costPerHourTl: number | null;
  alternatives: Alternative[];
}

export interface Conflict {
  present: boolean;
  compromiseSetpoint: number | null;
  perMember: { memberId: string; pmvAtCompromise: number }[];
}

export interface Comfort {
  applianceId: string;
  members: string[];
  roomTempC: number;
  humidityPct: number;
  pmv: number;
  feeling: string;
  inBand: boolean;
  bandPmv: number;
  recommendation: Recommendation;
  conflict: Conflict;
}

export interface ComfortFeedback {
  applianceId: string;
  sensation: -1 | 0 | 1;
  clothing: Clothing | null;
  commandId: string | null;
}

export interface ComfortCheck {
  applianceId: string;
  memberId: string;
  commandId: string | null;
}

export interface Energy {
  todayKwh: number;
  todayTl: number;
  weekKwh: number;
  weekTl: number;
  savedWeekKwh: number;
  savedWeekTl: number;
  perDevice: { applianceId: string; todayKwh: number; todayTl: number }[];
}

export interface CheapestTime {
  startAtUtc: string;
  costTl: number;
  nowCostTl: number;
  savingTl: number;
}

export interface Presence {
  memberId: string;
  band: Band;
  distanceM: number;
  etaMin: number | null;
  updatedAtUtc: string;
}

export type TaskOrigin = "device_event" | "routine" | "manual" | "voice";
export type TaskStatus = "open" | "done" | "skipped";

export interface HouseholdTask {
  id: string;
  title: string;
  origin: TaskOrigin;
  applianceId: string | null;
  assignedMemberId: string;
  status: TaskStatus;
  effortPoints: number;
  createdAtUtc: string;
  dueAtUtc: string | null;
  reason: string;
}

export const TASK_STRATEGIES = ["fair_share", "round_robin", "fixed", "ask"] as const;
export type TaskStrategy = (typeof TASK_STRATEGIES)[number];

export interface TaskRule {
  id: string;
  trigger: string;
  template: string;
  strategy: TaskStrategy;
  fixedMemberId: string | null;
  effortPoints: number;
  enabled: boolean;
}

export interface RulePatch {
  strategy?: TaskStrategy;
  fixedMemberId?: string | null;
  template?: string;
  effortPoints?: number;
  enabled?: boolean;
}

export interface TaskStat {
  memberId: string;
  pointsLast7Days: number;
  doneLast7Days: number;
}

export interface NewTask {
  title: string;
  assignedMemberId: string | null;
  effortPoints: number;
}

export type RoutineStatus = "detected" | "suggested" | "accepted" | "rejected";

export interface Routine {
  id: string;
  action: string;
  applianceId: string;
  dayType: "weekday" | "weekend";
  time: string;
  windowMin: number;
  confidence: number;
  evidence: string;
  status: RoutineStatus;
}

export interface Preference {
  context: string;
  setpoint: number;
  confidence: number;
  samples: number;
  text: string;
}

export interface RatePoint {
  weekStartUtc: string;
  rate: number;
}

export interface Profile {
  sensitivityOffset: number;
  preferences: Preference[];
  routines: Routine[];
  overrideRate: RatePoint[];
  goodFeedbackRate: RatePoint[];
}

export type NotificationKind = "info" | "suggestion" | "comfort" | "task" | "alert";
export type NotificationImportance = "critical" | "important" | "info";

export interface NotificationAction {
  type: "accept_routine";
  routineId: string;
}

export interface AppNotification {
  id: string;
  memberId: string;
  kind: NotificationKind;
  level: NotificationImportance;
  title: string;
  text: string;
  createdAtUtc: string;
  read: boolean;
  action: NotificationAction | null;
}

export interface DashboardSummary {
  homes: number;
  activeMembers: number;
  commandsToday: Record<"voice" | "app" | "remote" | "ai" | "schedule", number>;
  conversationsToday: number;
  runsToday: number;
  llm: {
    costUsdToday: number;
    costUsdMonth: number;
    tokensToday: number;
    avgCostUsdPerConversation: number;
    avgLatencyMs: number;
  };
  energy: { savedKwhWeek: number; savedTlWeek: number };
  learning: { overrideRateTrend: RatePoint[]; goodFeedbackTrend: RatePoint[]; suggestionAcceptanceRate: number };
  tools: { tool: string; calls: number; errors: number }[];
  server: { monthlyCostTl: number; costPerActiveUserTl: number };
}

export interface Projection {
  homes: number;
  llmCostUsdMonth: number;
  serverCostTlMonth: number;
  savedKwhMonth: number;
  savedTlMonth: number;
  assumptions: string[];
}

export interface RunSummary {
  runId: string;
  conversationId: string;
  agentId: string;
  status: string;
  startedAtUtc: string;
  durationMs: number;
  costUsd: number;
  traceId: string;
  userText: string;
}

// ---------- decoders ----------

type Rec = Record<string, unknown>;

const isRecord = (v: unknown): v is Rec => typeof v === "object" && v !== null && !Array.isArray(v);
const isString = (v: unknown): v is string => typeof v === "string";
const num = (v: unknown, fallback = 0): number => (typeof v === "number" && Number.isFinite(v) ? v : fallback);
const numOrNull = (v: unknown): number | null => (typeof v === "number" && Number.isFinite(v) ? v : null);
const str = (v: unknown, fallback = ""): string => (isString(v) ? v : fallback);
const strOrNull = (v: unknown): string | null => (isString(v) ? v : null);
const bool = (v: unknown, fallback = false): boolean => (typeof v === "boolean" ? v : fallback);

function oneOf<T extends string>(values: readonly T[], value: unknown, fallback: T): T {
  return isString(value) && (values as readonly string[]).includes(value) ? (value as T) : fallback;
}

function list<T>(value: unknown, decode: (item: unknown) => T | undefined): T[] | undefined {
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

const BANDS: readonly Band[] = ["home", "near", "away"];

export function decodeSettings(value: unknown): Settings | undefined {
  if (!isRecord(value) || !isString(value.memberId)) return undefined;
  const quiet = isRecord(value.quietHours) ? value.quietHours : {};
  const est = isRecord(value.estimatedMonthlyTl) ? value.estimatedMonthlyTl : {};
  return {
    memberId: value.memberId,
    priorityMode: oneOf(PRIORITY_MODES, value.priorityMode, "saving"),
    notificationLevel: oneOf(NOTIFICATION_LEVELS, value.notificationLevel, "balanced"),
    quietHours: { from: str(quiet.from, "23:00"), to: str(quiet.to, "08:00") },
    clothing: oneOf(CLOTHING_LEVELS, value.clothing, "normal"),
    locationSharing: bool(value.locationSharing, true),
    sensitivityOffset: num(value.sensitivityOffset),
    estimatedMonthlyTl: { saving: num(est.saving), balanced: num(est.balanced), comfort: num(est.comfort) },
  };
}

export function decodeContext(value: unknown): HomeContext | undefined {
  if (!isRecord(value) || typeof value.outdoorTempC !== "number") return undefined;
  const members = list(value.members, (m) =>
    isRecord(m) && isString(m.memberId) ? { memberId: m.memberId, band: oneOf(BANDS, m.band, "home") } : undefined,
  );
  if (!members) return undefined;
  return {
    outdoorTempC: value.outdoorTempC,
    outdoorHumidityPct: num(value.outdoorHumidityPct),
    season: str(value.season),
    simulatedNowUtc: str(value.simulatedNowUtc),
    priorityMode: oneOf(PRIORITY_MODES, value.priorityMode, "saving"),
    members,
  };
}

const ACTIONS: readonly RecommendationAction[] = ["none", "window", "fan", "dry", "setpoint"];

function decodeRecommendation(value: unknown): Recommendation {
  const r = isRecord(value) ? value : {};
  const alternatives = list(r.alternatives, (a) =>
    isRecord(a) ? { action: oneOf(ACTIONS, a.action, "none"), setpoint: numOrNull(a.setpoint), costPerHourTl: numOrNull(a.costPerHourTl) } : undefined,
  );
  return {
    action: oneOf(ACTIONS, r.action, "none"),
    setpoint: numOrNull(r.setpoint),
    mode: strOrNull(r.mode),
    fan: strOrNull(r.fan),
    reason: str(r.reason),
    costPerHourTl: numOrNull(r.costPerHourTl),
    alternatives: alternatives ?? [],
  };
}

function decodeConflict(value: unknown): Conflict {
  const c = isRecord(value) ? value : {};
  const perMember =
    list(c.perMember, (m) => (isRecord(m) && isString(m.memberId) ? { memberId: m.memberId, pmvAtCompromise: num(m.pmvAtCompromise) } : undefined)) ?? [];
  return { present: bool(c.present), compromiseSetpoint: numOrNull(c.compromiseSetpoint), perMember };
}

export function decodeComfort(value: unknown): Comfort | undefined {
  if (!isRecord(value) || !isString(value.applianceId) || typeof value.roomTempC !== "number") return undefined;
  return {
    applianceId: value.applianceId,
    members: list(value.members, (m) => (isString(m) ? m : undefined)) ?? [],
    roomTempC: value.roomTempC,
    humidityPct: num(value.humidityPct),
    pmv: num(value.pmv),
    feeling: str(value.feeling, "neutral"),
    inBand: bool(value.inBand, true),
    bandPmv: num(value.bandPmv, 1),
    recommendation: decodeRecommendation(value.recommendation),
    conflict: decodeConflict(value.conflict),
  };
}

export function decodeComfortCheck(value: unknown): ComfortCheck | undefined {
  if (!isRecord(value) || !isString(value.applianceId) || !isString(value.memberId)) return undefined;
  return { applianceId: value.applianceId, memberId: value.memberId, commandId: strOrNull(value.commandId) };
}

export function decodeEnergy(value: unknown): Energy | undefined {
  if (!isRecord(value) || typeof value.todayKwh !== "number") return undefined;
  const perDevice =
    list(value.perDevice, (d) =>
      isRecord(d) && isString(d.applianceId) ? { applianceId: d.applianceId, todayKwh: num(d.todayKwh), todayTl: num(d.todayTl) } : undefined,
    ) ?? [];
  return {
    todayKwh: value.todayKwh,
    todayTl: num(value.todayTl),
    weekKwh: num(value.weekKwh),
    weekTl: num(value.weekTl),
    savedWeekKwh: num(value.savedWeekKwh),
    savedWeekTl: num(value.savedWeekTl),
    perDevice,
  };
}

export function decodeCheapestTime(value: unknown): CheapestTime | undefined {
  if (!isRecord(value) || !isString(value.startAtUtc)) return undefined;
  return { startAtUtc: value.startAtUtc, costTl: num(value.costTl), nowCostTl: num(value.nowCostTl), savingTl: num(value.savingTl) };
}

function decodePresenceItem(value: unknown): Presence | undefined {
  if (!isRecord(value) || !isString(value.memberId)) return undefined;
  return {
    memberId: value.memberId,
    band: oneOf(BANDS, value.band, "home"),
    distanceM: num(value.distanceM),
    etaMin: numOrNull(value.etaMin),
    updatedAtUtc: str(value.updatedAtUtc),
  };
}

export const decodePresenceList = (value: unknown): Presence[] | undefined => (Array.isArray(value) ? list(value, decodePresenceItem) : undefined);

const ORIGINS: readonly TaskOrigin[] = ["device_event", "routine", "manual", "voice"];
const TASK_STATUSES: readonly TaskStatus[] = ["open", "done", "skipped"];

export function decodeTask(value: unknown): HouseholdTask | undefined {
  if (!isRecord(value) || !isString(value.id) || !isString(value.title)) return undefined;
  return {
    id: value.id,
    title: value.title,
    origin: oneOf(ORIGINS, value.origin, "manual"),
    applianceId: strOrNull(value.applianceId),
    assignedMemberId: str(value.assignedMemberId),
    status: oneOf(TASK_STATUSES, value.status, "open"),
    effortPoints: num(value.effortPoints, 1),
    createdAtUtc: str(value.createdAtUtc),
    dueAtUtc: strOrNull(value.dueAtUtc),
    reason: str(value.reason),
  };
}

export const decodeTaskList = (value: unknown): HouseholdTask[] | undefined => (Array.isArray(value) ? list(value, decodeTask) : undefined);

/** Decodes the SignalR `TaskAssigned` payload `{ task }`. */
export function decodeTaskAssigned(value: unknown): HouseholdTask | undefined {
  return isRecord(value) ? decodeTask(value.task) : undefined;
}

export function decodeRule(value: unknown): TaskRule | undefined {
  if (!isRecord(value) || !isString(value.id)) return undefined;
  return {
    id: value.id,
    trigger: str(value.trigger),
    template: str(value.template),
    strategy: oneOf(TASK_STRATEGIES, value.strategy, "fair_share"),
    fixedMemberId: strOrNull(value.fixedMemberId),
    effortPoints: num(value.effortPoints, 1),
    enabled: bool(value.enabled, true),
  };
}

export const decodeRuleList = (value: unknown): TaskRule[] | undefined => (Array.isArray(value) ? list(value, decodeRule) : undefined);

export const decodeTaskStats = (value: unknown): TaskStat[] | undefined =>
  Array.isArray(value)
    ? list(value, (s) =>
        isRecord(s) && isString(s.memberId) ? { memberId: s.memberId, pointsLast7Days: num(s.pointsLast7Days), doneLast7Days: num(s.doneLast7Days) } : undefined,
      )
    : undefined;

const ROUTINE_STATUSES: readonly RoutineStatus[] = ["detected", "suggested", "accepted", "rejected"];

export function decodeRoutine(value: unknown): Routine | undefined {
  if (!isRecord(value) || !isString(value.id)) return undefined;
  return {
    id: value.id,
    action: str(value.action, "power_on"),
    applianceId: str(value.applianceId),
    dayType: value.dayType === "weekend" ? "weekend" : "weekday",
    time: str(value.time),
    windowMin: num(value.windowMin, 20),
    confidence: num(value.confidence),
    evidence: str(value.evidence),
    status: oneOf(ROUTINE_STATUSES, value.status, "detected"),
  };
}

function decodeRates(value: unknown): RatePoint[] {
  return list(value, (p) => (isRecord(p) && isString(p.weekStartUtc) ? { weekStartUtc: p.weekStartUtc, rate: num(p.rate) } : undefined)) ?? [];
}

export function decodeProfile(value: unknown): Profile | undefined {
  if (!isRecord(value)) return undefined;
  const preferences =
    list(value.preferences, (p) =>
      isRecord(p)
        ? { context: str(p.context), setpoint: num(p.setpoint), confidence: num(p.confidence), samples: num(p.samples), text: str(p.text) }
        : undefined,
    ) ?? [];
  const routines = list(value.routines, decodeRoutine);
  if (!routines) return undefined;
  return {
    sensitivityOffset: num(value.sensitivityOffset),
    preferences,
    routines,
    overrideRate: decodeRates(value.overrideRate),
    goodFeedbackRate: decodeRates(value.goodFeedbackRate),
  };
}

const KINDS: readonly NotificationKind[] = ["info", "suggestion", "comfort", "task", "alert"];
const LEVELS: readonly NotificationImportance[] = ["critical", "important", "info"];

export function decodeNotification(value: unknown): AppNotification | undefined {
  if (!isRecord(value) || !isString(value.id)) return undefined;
  const a = value.action;
  const action: NotificationAction | null =
    isRecord(a) && a.type === "accept_routine" && isString(a.routineId) ? { type: "accept_routine", routineId: a.routineId } : null;
  return {
    id: value.id,
    memberId: str(value.memberId),
    kind: oneOf(KINDS, value.kind, "info"),
    level: oneOf(LEVELS, value.level, "info"),
    title: str(value.title),
    text: str(value.text),
    createdAtUtc: str(value.createdAtUtc),
    read: bool(value.read),
    action,
  };
}

export const decodeNotificationList = (value: unknown): AppNotification[] | undefined =>
  Array.isArray(value) ? list(value, decodeNotification) : undefined;

/** Decodes the SignalR `NotificationCreated` payload `{ notification }`. */
export function decodeNotificationCreated(value: unknown): AppNotification | undefined {
  return isRecord(value) ? decodeNotification(value.notification) : undefined;
}

export function decodeSummary(value: unknown): DashboardSummary | undefined {
  if (!isRecord(value)) return undefined;
  const cmd = isRecord(value.commandsToday) ? value.commandsToday : {};
  const llm = isRecord(value.llm) ? value.llm : {};
  const energy = isRecord(value.energy) ? value.energy : {};
  const learning = isRecord(value.learning) ? value.learning : {};
  const server = isRecord(value.server) ? value.server : {};
  const tools =
    list(value.tools, (t) => (isRecord(t) && isString(t.tool) ? { tool: t.tool, calls: num(t.calls), errors: num(t.errors) } : undefined)) ?? [];
  return {
    homes: num(value.homes, 1),
    activeMembers: num(value.activeMembers),
    commandsToday: { voice: num(cmd.voice), app: num(cmd.app), remote: num(cmd.remote), ai: num(cmd.ai), schedule: num(cmd.schedule) },
    conversationsToday: num(value.conversationsToday),
    runsToday: num(value.runsToday),
    llm: {
      costUsdToday: num(llm.costUsdToday),
      costUsdMonth: num(llm.costUsdMonth),
      tokensToday: num(llm.tokensToday),
      avgCostUsdPerConversation: num(llm.avgCostUsdPerConversation),
      avgLatencyMs: num(llm.avgLatencyMs),
    },
    energy: { savedKwhWeek: num(energy.savedKwhWeek), savedTlWeek: num(energy.savedTlWeek) },
    learning: {
      overrideRateTrend: decodeRates(learning.overrideRateTrend),
      goodFeedbackTrend: decodeRates(learning.goodFeedbackTrend),
      suggestionAcceptanceRate: num(learning.suggestionAcceptanceRate),
    },
    tools,
    server: { monthlyCostTl: num(server.monthlyCostTl), costPerActiveUserTl: num(server.costPerActiveUserTl) },
  };
}

export function decodeProjection(value: unknown): Projection | undefined {
  if (!isRecord(value) || typeof value.homes !== "number") return undefined;
  return {
    homes: value.homes,
    llmCostUsdMonth: num(value.llmCostUsdMonth),
    serverCostTlMonth: num(value.serverCostTlMonth),
    savedKwhMonth: num(value.savedKwhMonth),
    savedTlMonth: num(value.savedTlMonth),
    assumptions: list(value.assumptions, (a) => (isString(a) ? a : undefined)) ?? [],
  };
}

export const decodeRunSummaries = (value: unknown): RunSummary[] | undefined =>
  Array.isArray(value)
    ? list(value, (r) =>
        isRecord(r) && isString(r.runId)
          ? {
              runId: r.runId,
              conversationId: str(r.conversationId),
              agentId: str(r.agentId),
              status: str(r.status),
              startedAtUtc: str(r.startedAtUtc),
              durationMs: num(r.durationMs),
              costUsd: num(r.costUsd),
              traceId: str(r.traceId),
              userText: str(r.userText),
            }
          : undefined,
      )
    : undefined;
