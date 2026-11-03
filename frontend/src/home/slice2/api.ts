import { api, decodeNoContent, type ApiClient, type RequestOptions } from "../../api/client.ts";
import {
  decodeCheapestTime,
  decodeComfort,
  decodeContext,
  decodeEnergy,
  decodeNotificationList,
  decodePresenceList,
  decodeProfile,
  decodeProjection,
  decodeRoutine,
  decodeRule,
  decodeRuleList,
  decodeRunSummaries,
  decodeSettings,
  decodeSummary,
  decodeTask,
  decodeTaskList,
  decodeTaskStats,
  type CheapestTime,
  type Comfort,
  type ComfortFeedback,
  type DashboardSummary,
  type Energy,
  type HomeContext,
  type HouseholdTask,
  type NewTask,
  type AppNotification,
  type Presence,
  type Profile,
  type Projection,
  type Route,
  type Routine,
  type RulePatch,
  type RunSummary,
  type Settings,
  type SettingsPatch,
  type TaskRule,
  type TaskStat,
  type TaskStatus,
} from "./types.ts";

/** All slice 2 endpoints. Implemented by the real API and by the in-browser mock engine. */
export interface Slice2Api {
  getSettings(memberId: string, options?: RequestOptions): Promise<Settings>;
  putSettings(memberId: string, patch: SettingsPatch, options?: RequestOptions): Promise<null>;
  getContext(options?: RequestOptions): Promise<HomeContext>;

  getComfort(applianceId: string, memberIds?: readonly string[], options?: RequestOptions): Promise<Comfort>;
  postComfortFeedback(memberId: string, feedback: ComfortFeedback, options?: RequestOptions): Promise<null>;
  getEnergy(options?: RequestOptions): Promise<Energy>;
  getCheapestTime(applianceId: string, programId: string, deadlineUtc: string, options?: RequestOptions): Promise<CheapestTime>;

  getPresence(options?: RequestOptions): Promise<Presence[]>;
  setRoute(memberId: string, route: Route, options?: RequestOptions): Promise<null>;

  listTasks(status: TaskStatus, options?: RequestOptions): Promise<HouseholdTask[]>;
  createTask(task: NewTask, options?: RequestOptions): Promise<HouseholdTask>;
  completeTask(id: string, options?: RequestOptions): Promise<HouseholdTask>;
  skipTask(id: string, options?: RequestOptions): Promise<HouseholdTask>;
  reassignTask(id: string, memberId: string | null, options?: RequestOptions): Promise<HouseholdTask>;
  listRules(options?: RequestOptions): Promise<TaskRule[]>;
  putRule(id: string, patch: RulePatch, options?: RequestOptions): Promise<TaskRule>;
  getTaskStats(options?: RequestOptions): Promise<TaskStat[]>;

  getProfile(memberId: string, options?: RequestOptions): Promise<Profile>;
  acceptRoutine(id: string, options?: RequestOptions): Promise<Routine>;
  rejectRoutine(id: string, options?: RequestOptions): Promise<Routine>;
  resetLearning(memberId: string, options?: RequestOptions): Promise<null>;
  seedHistory(memberId: string, weeks: number, options?: RequestOptions): Promise<{ events: number }>;

  listNotifications(memberId: string, options?: RequestOptions): Promise<AppNotification[]>;
  markNotificationRead(id: string, options?: RequestOptions): Promise<null>;

  getSummary(options?: RequestOptions): Promise<DashboardSummary>;
  getProjection(homes: number, options?: RequestOptions): Promise<Projection>;
  listRuns(limit: number, options?: RequestOptions): Promise<RunSummary[]>;
}

const enc = encodeURIComponent;

function withQuery(path: string, params: Record<string, string | undefined>): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) if (value !== undefined) query.set(key, value);
  const text = query.toString();
  return text === "" ? path : `${path}?${text}`;
}

const decodeSeed = (value: unknown): { events: number } | undefined =>
  typeof value === "object" && value !== null && typeof (value as { events?: unknown }).events === "number"
    ? { events: (value as { events: number }).events }
    : undefined;

/** Slice 2 API backed by the backend REST endpoints. */
export function createHttpSlice2Api(client: ApiClient = api): Slice2Api {
  return {
    getSettings: (memberId, o) => client.get(withQuery("/api/me/settings", { memberId }), decodeSettings, o),
    putSettings: (memberId, patch, o) => client.put(withQuery("/api/me/settings", { memberId }), patch, decodeNoContent, o),
    getContext: (o) => client.get("/api/homes/me/context", decodeContext, o),

    getComfort: (id, memberIds, o) =>
      client.get(withQuery(`/api/devices/${enc(id)}/comfort`, { memberIds: memberIds && memberIds.length > 0 ? memberIds.join(",") : undefined }), decodeComfort, o),
    postComfortFeedback: (memberId, body, o) => client.post(withQuery("/api/me/comfort-feedback", { memberId }), body, decodeNoContent, o),
    getEnergy: (o) => client.get("/api/homes/me/energy", decodeEnergy, o),
    getCheapestTime: (applianceId, programId, deadlineUtc, o) =>
      client.get(withQuery("/api/homes/me/cheapest-time", { applianceId, programId, deadlineUtc }), decodeCheapestTime, o),

    getPresence: (o) => client.get("/api/homes/me/presence", decodePresenceList, o),
    setRoute: (memberId, route, o) => client.put(`/api/mock/location/${enc(memberId)}/route`, { route }, decodeNoContent, o),

    listTasks: (status, o) => client.get(withQuery("/api/homes/me/tasks", { status }), decodeTaskList, o),
    createTask: (task, o) => client.post("/api/homes/me/tasks", task, decodeTask, o),
    completeTask: (id, o) => client.post(`/api/tasks/${enc(id)}/done`, {}, decodeTask, o),
    skipTask: (id, o) => client.post(`/api/tasks/${enc(id)}/skip`, {}, decodeTask, o),
    reassignTask: (id, memberId, o) => client.post(`/api/tasks/${enc(id)}/reassign`, { memberId }, decodeTask, o),
    listRules: (o) => client.get("/api/homes/me/task-rules", decodeRuleList, o),
    putRule: (id, patch, o) => client.put(`/api/homes/me/task-rules/${enc(id)}`, patch, decodeRule, o),
    getTaskStats: (o) => client.get("/api/homes/me/task-stats", decodeTaskStats, o),

    getProfile: (memberId, o) => client.get(withQuery("/api/me/profile", { memberId }), decodeProfile, o),
    acceptRoutine: (id, o) => client.post(`/api/me/routines/${enc(id)}/accept`, {}, decodeRoutine, o),
    rejectRoutine: (id, o) => client.post(`/api/me/routines/${enc(id)}/reject`, {}, decodeRoutine, o),
    resetLearning: (memberId, o) => client.delete(withQuery("/api/me/learning", { memberId }), decodeNoContent, o),
    seedHistory: (memberId, weeks, o) => client.post("/api/demo/seed-history", { memberId, weeks }, decodeSeed, o),

    listNotifications: (memberId, o) => client.get(withQuery("/api/me/notifications", { memberId }), decodeNotificationList, o),
    markNotificationRead: (id, o) => client.post(`/api/me/notifications/${enc(id)}/read`, {}, decodeNoContent, o),

    getSummary: (o) => client.get("/api/dashboard/summary", decodeSummary, o),
    getProjection: (homes, o) => client.get(withQuery("/api/dashboard/projection", { homes: String(homes) }), decodeProjection, o),
    listRuns: (limit, o) => client.get(withQuery("/api/runs", { limit: String(limit) }), decodeRunSummaries, o),
  };
}
