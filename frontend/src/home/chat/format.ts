// Formatting for chat action chips and the "Nasıl karar verdi?" timeline. Pure, unit tested.

import { tr } from "../../i18n/tr.ts";
import type { IconName } from "../icons.ts";
import { applianceIconName } from "../icons.ts";
import { acModeLabel, fanLabel, formatDuration, formatTemp, ovenModeLabel, programWithTemp } from "../labels.ts";
import type { Appliance } from "../types.ts";
import type { ChatAction, Step, Totals } from "./types.ts";

type Rec = Record<string, unknown>;
const isRecord = (v: unknown): v is Rec => typeof v === "object" && v !== null && !Array.isArray(v);
const asNumber = (v: unknown): number | undefined => (typeof v === "number" && Number.isFinite(v) ? v : undefined);
const asString = (v: unknown): string | undefined => (typeof v === "string" ? v : undefined);

/** Short Turkish text for what the command set, e.g. "25°", "Kapatıldı", "Fanlı · 180°". */
export function actionValueText(action: ChatAction, appliance: Appliance | undefined): string {
  const { command, value } = action;
  switch (command) {
    case "power":
      return value === true ? tr.home.chat.action.on : tr.home.chat.action.off;
    case "setpoint": {
      const n = asNumber(value);
      return n === undefined ? tr.home.chat.action.changed : formatTemp(n);
    }
    case "mode": {
      const mode = asString(value) ?? "";
      return appliance?.type === "oven" ? ovenModeLabel(mode) : acModeLabel(mode);
    }
    case "fan":
      return `${tr.home.detail.fan}: ${fanLabel(asString(value) ?? "")}`;
    case "start_program": {
      if (!isRecord(value)) return tr.home.chat.action.started;
      const program = asString(value.program) ?? "";
      const temp = asNumber(value.temperatureC) ?? null;
      return tr.home.chat.action.programStarted(programWithTemp(program, temp));
    }
    case "pause":
      return tr.home.status.paused;
    case "resume":
      return tr.home.chat.action.resumed;
    case "stop":
      return tr.home.chat.action.stopped;
    case "set_oven": {
      if (!isRecord(value)) return tr.home.chat.action.changed;
      const parts = [ovenModeLabel(asString(value.mode) ?? "")];
      const temp = asNumber(value.targetTempC);
      if (temp !== undefined) parts.push(formatTemp(temp));
      const timer = asNumber(value.timerMin);
      if (timer !== undefined && timer > 0) parts.push(formatDuration(timer));
      return parts.join(" · ");
    }
    default:
      return tr.home.chat.action.changed;
  }
}

export interface ActionChip {
  icon: IconName;
  /** e.g. "Salon Kliması → 25°" */
  text: string;
  accepted: boolean;
  applianceId: string;
}

/** Chip for an agent action: appliance icon, name and the new value. */
export function actionChip(action: ChatAction, appliance: Appliance | undefined): ActionChip {
  const name = appliance?.displayName ?? action.applianceId;
  const value = actionValueText(action, appliance);
  const text = action.accepted ? `${name} → ${value}` : `${name} → ${value} (${tr.home.chat.action.rejected})`;
  return { icon: appliance ? applianceIconName(appliance.type) : "sparkle", text, accepted: action.accepted, applianceId: action.applianceId };
}

/** 850 -> "850 ms", 1530 -> "1,5 sn". */
export function formatMs(ms: number | null): string {
  if (ms === null) return "—";
  if (ms < 1000) return `${String(Math.round(ms))} ${tr.home.chat.units.ms}`;
  return `${new Intl.NumberFormat("tr-TR", { maximumFractionDigits: 1 }).format(ms / 1000)} ${tr.home.chat.units.s}`;
}

/** USD with enough decimals for tiny LLM costs: 0.0123 -> "$0.0123". */
export function formatUsd(value: number | null): string {
  if (value === null) return "—";
  if (value === 0) return "$0";
  const digits = value < 0.01 ? 4 : 2;
  return `$${value.toFixed(digits)}`;
}

export function formatTokens(tokens: { input: number; output: number } | null): string | undefined {
  if (!tokens) return undefined;
  return tr.home.chat.timeline.tokens(String(tokens.input), String(tokens.output));
}

export function stepStatusLabel(step: Step): string {
  return tr.home.chat.timeline.status[step.status];
}

export function totalsLine(totals: Totals | null): string | undefined {
  if (!totals) return undefined;
  return tr.home.chat.timeline.totals(
    formatMs(totals.durationMs),
    String(totals.inputTokens + totals.outputTokens),
    formatUsd(totals.costUsd),
  );
}
