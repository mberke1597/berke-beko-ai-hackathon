// Pure helpers for slice 2 screens: labels, filters, recommendations, projection slider. Unit tested.

import { tr } from "../../i18n/tr.ts";
import { formatClock, formatNumber } from "../labels.ts";

export { formatClock };
import type { Appliance, DeviceCommand, Member } from "../types.ts";
import type {
  AppNotification,
  Band,
  Comfort,
  ComfortCheck,
  HouseholdTask,
  Recommendation,
  RecommendationAction,
  RatePoint,
  Route,
  TaskRule,
  TaskStat,
  TaskStrategy,
} from "./types.ts";

function lookup(table: Readonly<Record<string, string>>, key: string, fallback?: string): string {
  return Object.hasOwn(table, key) ? (table[key] ?? key) : (fallback ?? key);
}

// ---------- formatting ----------

/** 12.5 -> "12,5 TL". */
export function formatTl(value: number): string {
  return `${formatNumber(Math.round(value * 10) / 10)} ${tr.home.units.tl}`;
}

/** 4.2 -> "4,2 kWh". */
export function formatKwh(value: number): string {
  return `${formatNumber(Math.round(value * 10) / 10)} ${tr.home.units.kwh}`;
}

export function formatInteger(value: number): string {
  return new Intl.NumberFormat("tr-TR", { maximumFractionDigits: 0 }).format(value);
}

/** 0.456 -> "%46". */
export function formatRate(rate: number): string {
  return `%${String(Math.round(rate * 100))}`;
}

/** Short Turkish date for week labels, e.g. "3 Kas". */
export function formatWeek(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "";
  return new Intl.DateTimeFormat("tr-TR", { day: "numeric", month: "short" }).format(date);
}

export function formatDistance(meters: number): string {
  if (meters < 1000) return `${String(Math.round(meters / 10) * 10)} m`;
  return `${formatNumber(Math.round(meters / 100) / 10)} km`;
}

export function formatCompact(value: number): string {
  return new Intl.NumberFormat("tr-TR", { notation: "compact", maximumFractionDigits: 1 }).format(value);
}

export function formatUsdMoney(value: number): string {
  return `$${new Intl.NumberFormat("tr-TR", { minimumFractionDigits: 2, maximumFractionDigits: value < 1 ? 4 : 2 }).format(value)}`;
}

// ---------- labels ----------

export const feelingLabel = (feeling: string): string => lookup(tr.home.comfort.feelings, feeling, feeling);
export const bandLabel = (band: Band): string => tr.home.presence.band[band];
export const routeLabel = (route: Route): string => lookup(tr.home.presence.routes, route);
export const strategyLabel = (strategy: TaskStrategy): string => lookup(tr.home.rules.strategies, strategy);
export const triggerLabel = (trigger: string): string => lookup(tr.home.rules.triggers, trigger);

export function originLabel(task: HouseholdTask, applianceName: string | undefined): string {
  if (task.origin === "device_event" && applianceName !== undefined) return tr.home.tasks.originDevice(applianceName);
  return lookup(tr.home.tasks.origins, task.origin);
}

export function memberName(members: readonly Member[], id: string | null): string {
  if (id === null) return tr.home.tasks.auto;
  return members.find((m) => m.id === id)?.displayName ?? id;
}

export const memberInitial = (name: string): string => name.trim().charAt(0).toLocaleUpperCase("tr-TR");

/** Avatar colour slot (1..3) by member order, matching the header avatars. */
export function avatarSlot(members: readonly Member[], id: string): number {
  const index = members.findIndex((m) => m.id === id);
  return (index < 0 ? 0 : index % 3) + 1;
}

// ---------- tasks ----------

export type TaskFilter = "mine" | "everyone" | "done";

/** "Bana": my open tasks; "Herkes": all open tasks; "Tamamlanan": done and skipped. Newest first. */
export function filterTasks(tasks: readonly HouseholdTask[], filter: TaskFilter, memberId: string): HouseholdTask[] {
  const matches = tasks.filter((t) => {
    if (filter === "done") return t.status !== "open";
    if (t.status !== "open") return false;
    return filter === "everyone" || t.assignedMemberId === memberId;
  });
  return matches.sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc));
}

export interface FairnessBar {
  memberId: string;
  name: string;
  points: number;
  done: number;
  /** Bar width 0..100 relative to the highest member. */
  pct: number;
}

/** One bar per member (also members without any task), scaled to the highest points. */
export function fairnessBars(stats: readonly TaskStat[], members: readonly Member[]): FairnessBar[] {
  const rows = members.map((m) => {
    const stat = stats.find((s) => s.memberId === m.id);
    return { memberId: m.id, name: m.displayName, points: stat?.pointsLast7Days ?? 0, done: stat?.doneLast7Days ?? 0 };
  });
  const max = Math.max(1, ...rows.map((r) => r.points));
  return rows.map((r) => ({ ...r, pct: Math.round((r.points / max) * 100) }));
}

/** Inserts or replaces a task by id (live `TaskAssigned` and action results). */
export function upsertTask(tasks: readonly HouseholdTask[], task: HouseholdTask): HouseholdTask[] {
  const index = tasks.findIndex((t) => t.id === task.id);
  if (index < 0) return [task, ...tasks];
  const next = [...tasks];
  next[index] = task;
  return next;
}

export function upsertRule(rules: readonly TaskRule[], rule: TaskRule): TaskRule[] {
  return rules.map((r) => (r.id === rule.id ? rule : r));
}

// ---------- notifications ----------

export function unreadCount(notifications: readonly AppNotification[]): number {
  return notifications.filter((n) => !n.read).length;
}

/** Unread first, then newest first. */
export function sortNotifications(notifications: readonly AppNotification[]): AppNotification[] {
  return [...notifications].sort((a, b) => Number(a.read) - Number(b.read) || b.createdAtUtc.localeCompare(a.createdAtUtc));
}

export function upsertNotification(list: readonly AppNotification[], item: AppNotification): AppNotification[] {
  const index = list.findIndex((n) => n.id === item.id);
  if (index < 0) return [item, ...list];
  const next = [...list];
  next[index] = item;
  return next;
}

/** Badge text: nothing for 0, "9+" above nine. */
export function badgeText(count: number): string | undefined {
  if (count <= 0) return undefined;
  return count > 9 ? "9+" : String(count);
}

// ---------- comfort ----------

/** The comfort question belongs to one member: only show it to that member. */
export function shouldAskComfort(check: ComfortCheck, currentMemberId: string): boolean {
  return check.memberId === currentMemberId;
}

export const actionLabel = (action: RecommendationAction): string => tr.home.comfort.actions[action];

/**
 * Commands that apply a comfort recommendation, in order. Empty when nothing can be applied from the app
 * (`none`, `window`) or the appliance does not support the needed command.
 */
export function recommendationCommands(rec: Recommendation, appliance: Appliance): DeviceCommand[] {
  const ac = appliance.state.ac;
  const can = (type: string): boolean => appliance.capabilities.commands.includes(type);
  const commands: DeviceCommand[] = [];
  if (!ac) return commands;
  switch (rec.action) {
    case "setpoint":
      if (rec.mode !== null && rec.mode !== ac.mode && can("mode")) commands.push({ type: "mode", value: rec.mode });
      if (rec.setpoint !== null && can("setpoint")) commands.push({ type: "setpoint", value: rec.setpoint });
      break;
    case "fan":
      if (rec.fan !== null && can("fan")) commands.push({ type: "fan", value: rec.fan });
      break;
    case "dry":
      if (can("mode")) commands.push({ type: "mode", value: "dry" });
      break;
    case "none":
    case "window":
      break;
  }
  return commands;
}

/** Compromise note for several members, e.g. "Uzlaşma ayarı 24,5°: Ayşe +0,2, Mehmet −0,3". */
export function conflictNote(comfort: Comfort, members: readonly Member[]): string | undefined {
  if (!comfort.conflict.present || comfort.conflict.compromiseSetpoint === null) return undefined;
  const signed = (n: number): string => `${n >= 0 ? "+" : "−"}${formatNumber(Math.abs(Math.round(n * 10) / 10))}`;
  const parts = comfort.conflict.perMember.map((m) => `${memberName(members, m.memberId)} ${signed(m.pmvAtCompromise)}`);
  return tr.home.comfort.conflict(`${formatNumber(comfort.conflict.compromiseSetpoint)}°`, parts.join(", "));
}

// ---------- charts ----------

export interface ChartPoint {
  x: number;
  y: number;
}

/** Maps rate points (0..1) into SVG coordinates inside the padded plot area. */
export function ratePoints(points: readonly RatePoint[], width: number, height: number, pad: { top: number; right: number; bottom: number; left: number }): ChartPoint[] {
  const plotW = width - pad.left - pad.right;
  const plotH = height - pad.top - pad.bottom;
  const n = Math.max(1, points.length - 1);
  return points.map((p, i) => ({
    x: Number((pad.left + (points.length === 1 ? plotW / 2 : (i / n) * plotW)).toFixed(1)),
    y: Number((pad.top + (1 - Math.min(1, Math.max(0, p.rate))) * plotH).toFixed(1)),
  }));
}

export function pathFor(points: readonly ChartPoint[]): string {
  return points.map((p, i) => `${i === 0 ? "M" : "L"}${String(p.x)} ${String(p.y)}`).join(" ");
}

// ---------- fleet projection slider ----------

export const MIN_HOMES = 1_000;
export const MAX_HOMES = 1_000_000;

/** Slider position 0..1 -> homes on a log scale between 1k and 1M, rounded to a readable value. */
export function homesFromSlider(position: number): number {
  const p = Math.min(1, Math.max(0, position));
  const raw = MIN_HOMES * (MAX_HOMES / MIN_HOMES) ** p;
  const magnitude = 10 ** Math.floor(Math.log10(raw));
  const step = magnitude >= 10_000 ? magnitude / 2 : magnitude / 10;
  const rounded = Math.round(raw / step) * step;
  return Math.min(MAX_HOMES, Math.max(MIN_HOMES, rounded));
}

export function sliderFromHomes(homes: number): number {
  const clamped = Math.min(MAX_HOMES, Math.max(MIN_HOMES, homes));
  return Math.log(clamped / MIN_HOMES) / Math.log(MAX_HOMES / MIN_HOMES);
}

export const SLIDER_STEPS = 1000;

export function rulesByTrigger(rules: readonly TaskRule[]): TaskRule[] {
  return [...rules].sort((a, b) => a.trigger.localeCompare(b.trigger));
}
