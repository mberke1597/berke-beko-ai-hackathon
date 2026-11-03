// Maps appliance state to Turkish labels and visual tones. Pure functions, unit tested.

import { tr } from "../i18n/tr.ts";
import type { Appliance, ApplianceEvent, ApplianceStatus, ApplianceType } from "./types.ts";

/** Visual tone of an appliance card. Never used alone: always paired with a text label and an icon. */
export type StateTone = "off" | "idle" | "running" | "paused" | "finished" | "error";

const numberFormat = new Intl.NumberFormat("tr-TR", { maximumFractionDigits: 1 });

/** "24.8" -> "24,8" (Turkish decimal comma). */
export function formatNumber(value: number): string {
  return numberFormat.format(value);
}

export function formatTemp(value: number): string {
  return `${formatNumber(value)}°`;
}

/** Turkish percent puts the sign first: "%55". */
export function formatPercent(value: number): string {
  return `%${String(Math.round(value))}`;
}

/** 42 -> "42 dk", 65 -> "1 sa 5 dk", 120 -> "2 sa". */
export function formatDuration(minutes: number): string {
  const total = Math.max(0, Math.round(minutes));
  const hours = Math.floor(total / 60);
  const rest = total % 60;
  if (hours === 0) return `${String(rest)} ${tr.home.units.minute}`;
  if (rest === 0) return `${String(hours)} ${tr.home.units.hour}`;
  return `${String(hours)} ${tr.home.units.hour} ${String(rest)} ${tr.home.units.minute}`;
}

function lookup(table: Readonly<Record<string, string>>, key: string): string {
  return Object.hasOwn(table, key) ? (table[key] ?? key) : key;
}

export function statusLabel(status: ApplianceStatus): string {
  return tr.home.status[status];
}

export function typeLabel(type: ApplianceType): string {
  return tr.home.types[type];
}

export function acModeLabel(mode: string): string {
  return lookup(tr.home.acModes, mode);
}

export function fanLabel(fan: string): string {
  return lookup(tr.home.fanLevels, fan);
}

export function programLabel(program: string): string {
  return lookup(tr.home.programs, program);
}

export function ovenModeLabel(mode: string): string {
  return lookup(tr.home.ovenModes, mode);
}

/** "cotton40" + 40 -> "Pamuklu 40" (temperature not repeated), "quick" + 30 -> "Hızlı 30°". */
export function programWithTemp(program: string, temperatureC: number | null): string {
  const label = programLabel(program);
  if (temperatureC === null || label.includes(String(temperatureC))) return label;
  return `${label} ${formatTemp(temperatureC)}`;
}

/** Tone derived from status; a powered-off appliance is always "off". */
export function stateTone(appliance: Appliance): StateTone {
  const { status, power } = appliance.state;
  if (status === "error") return "error";
  if (!power && appliance.type !== "refrigerator") return "off";
  return status;
}

/** Short status text shown on the card next to the tone icon. */
export function toneLabel(appliance: Appliance): string {
  const tone = stateTone(appliance);
  if (tone === "running" && appliance.type === "air_conditioner") return tr.home.status.on;
  if ((tone === "idle" || tone === "running") && appliance.type === "refrigerator") return tr.home.status.cooling;
  return tr.home.status[tone];
}

export interface CardSummary {
  /** Main line, e.g. "Soğutma · 25°". */
  primary: string;
  /** Secondary line, e.g. "Oda 24,8° · %55". */
  secondary: string | undefined;
  /** Program progress 0..100, when a program is active. */
  progressPct: number | undefined;
}

/** Key state for an appliance card, by type. */
export function cardSummary(appliance: Appliance): CardSummary {
  const { state } = appliance;
  const tone = stateTone(appliance);

  if (appliance.type === "air_conditioner" && state.ac) {
    const room = `${tr.home.card.room} ${formatTemp(state.ac.roomTempC)} · ${formatPercent(state.ac.humidityPct)}`;
    if (tone === "off") return { primary: tr.home.status.off, secondary: room, progressPct: undefined };
    return { primary: `${acModeLabel(state.ac.mode)} · ${formatTemp(state.ac.setpoint)}`, secondary: room, progressPct: undefined };
  }

  if ((appliance.type === "washing_machine" || appliance.type === "dishwasher") && state.program) {
    const p = state.program;
    const name = programWithTemp(p.program, p.temperatureC);
    if (tone === "running" || tone === "paused") {
      return {
        primary: `${name} · ${formatDuration(p.remainingMin)}`,
        secondary: tone === "paused" ? tr.home.status.paused : tr.home.card.remaining(formatDuration(p.remainingMin)),
        progressPct: Math.min(100, Math.max(0, p.progressPct)),
      };
    }
    if (tone === "finished") return { primary: tr.home.card.finished, secondary: name, progressPct: 100 };
    // Idle keeps the selected program (contract): show it as the secondary line.
    if (tone === "idle") return { primary: toneLabel(appliance), secondary: name, progressPct: undefined };
    return { primary: toneLabel(appliance), secondary: undefined, progressPct: undefined };
  }

  if (appliance.type === "oven" && state.oven) {
    const o = state.oven;
    if (tone === "off") return { primary: tr.home.status.off, secondary: undefined, progressPct: undefined };
    const timer = o.timerMin !== null && o.timerMin > 0 ? ` · ${formatDuration(o.timerMin)}` : "";
    return {
      primary: `${ovenModeLabel(o.mode)} · ${formatTemp(o.targetTempC)}${timer}`,
      secondary: `${tr.home.card.now} ${formatTemp(o.currentTempC)}`,
      progressPct: undefined,
    };
  }

  if (appliance.type === "refrigerator" && state.fridge) {
    const f = state.fridge;
    return {
      primary: `${tr.home.card.fridge} ${formatTemp(f.fridgeTempC)} · ${tr.home.card.freezer} ${formatTemp(f.freezerTempC)}`,
      secondary: f.doorOpen ? tr.home.card.doorOpen : undefined,
      progressPct: undefined,
    };
  }

  return { primary: toneLabel(appliance), secondary: undefined, progressPct: undefined };
}

/** Toast text for a `DeviceEvent`, e.g. "Çamaşır Makinesi bitti". */
export function eventMessage(event: ApplianceEvent, appliance: Appliance | undefined): string {
  const name = appliance?.displayName ?? tr.home.card.unknownDevice;
  return tr.home.events[event.event](name);
}
