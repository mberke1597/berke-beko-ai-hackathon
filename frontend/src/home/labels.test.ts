import { describe, expect, it } from "vitest";
import { tr } from "../i18n/tr.ts";
import {
  acModeLabel,
  cardSummary,
  eventMessage,
  formatDuration,
  formatPercent,
  formatTemp,
  programLabel,
  programWithTemp,
  stateTone,
  statusLabel,
  toneLabel,
} from "./labels.ts";
import { createDemoHome } from "./mock/fixtures.ts";
import { APPLIANCE_STATUSES, type Appliance } from "./types.ts";

function appliance(id: string): Appliance {
  const found = createDemoHome().appliances.find((a) => a.id === id);
  if (!found) throw new Error(id);
  return found;
}

describe("formatting", () => {
  it("uses Turkish number and percent style", () => {
    expect(formatTemp(24.8)).toBe("24,8°");
    expect(formatTemp(25)).toBe("25°");
    expect(formatPercent(55.4)).toBe("%55");
  });

  it.each([
    [42, "42 dk"],
    [60, "1 sa"],
    [65, "1 sa 5 dk"],
    [-3, "0 dk"],
  ])("formats %i minutes as %s", (minutes, text) => {
    expect(formatDuration(minutes)).toBe(text);
  });
});

describe("labels", () => {
  it("has a Turkish label for every status", () => {
    for (const status of APPLIANCE_STATUSES) expect(statusLabel(status)).toBe(tr.home.status[status]);
  });

  it("maps known ids and falls back to the raw id", () => {
    expect(acModeLabel("cool")).toBe("Soğutma");
    expect(acModeLabel("constructor")).toBe("constructor");
    expect(programLabel("eco50")).toBe("Eko 50");
    expect(programLabel("mystery")).toBe("mystery");
  });
});

describe("stateTone and card summary", () => {
  it("shows the salon AC as on with mode, setpoint and room values", () => {
    const ac = appliance("ac-salon");
    expect(stateTone(ac)).toBe("running");
    expect(toneLabel(ac)).toBe(tr.home.status.on);
    expect(cardSummary(ac)).toEqual({ primary: "Soğutma · 25°", secondary: "Oda 24,8° · %55", progressPct: undefined });
  });

  it("shows a powered-off AC as off but keeps room values", () => {
    const ac = appliance("ac-yatak");
    expect(stateTone(ac)).toBe("off");
    expect(cardSummary(ac).primary).toBe(tr.home.status.off);
    expect(cardSummary(ac).secondary).toBe("Oda 27,5° · %58");
  });

  it("shows program, remaining time and progress for a running washer", () => {
    expect(cardSummary(appliance("washer-1"))).toEqual({ primary: "Pamuklu 40 · 42 dk", secondary: "42 dk kaldı", progressPct: 65 });
  });

  it("shows an idle dishwasher as ready with its selected program", () => {
    const dw = appliance("dishwasher-1");
    expect(stateTone(dw)).toBe("idle");
    expect(cardSummary(dw)).toEqual({ primary: tr.home.status.idle, secondary: "Eko 50", progressPct: undefined });
  });

  it("does not repeat the temperature in the program name", () => {
    expect(programWithTemp("cotton40", 40)).toBe("Pamuklu 40");
    expect(programWithTemp("quick", 30)).toBe("Hızlı 30°");
    expect(programWithTemp("glass", null)).toBe("Cam");
  });

  it("shows finished and error states", () => {
    const washer = appliance("washer-1");
    washer.state.status = "finished";
    expect(stateTone(washer)).toBe("finished");
    expect(cardSummary(washer).primary).toBe(tr.home.card.finished);
    washer.state.status = "error";
    washer.state.power = false;
    expect(stateTone(washer)).toBe("error");
    expect(toneLabel(washer)).toBe(tr.home.status.error);
  });

  it("shows oven and fridge values", () => {
    const oven = appliance("oven-1");
    expect(cardSummary(oven).primary).toBe(tr.home.status.off);
    oven.state.power = true;
    oven.state.status = "running";
    oven.state.oven = { mode: "fan", targetTempC: 180, currentTempC: 120, timerMin: 30 };
    expect(cardSummary(oven)).toEqual({ primary: "Fanlı · 180° · 30 dk", secondary: "Şu an 120°", progressPct: undefined });

    const fridge = appliance("fridge-1");
    expect(toneLabel(fridge)).toBe(tr.home.status.cooling);
    expect(cardSummary(fridge).primary).toBe("Buzdolabı 4° · Dondurucu -18°");
  });

  it("builds toast text for device events", () => {
    const washer = appliance("washer-1");
    expect(eventMessage({ applianceId: "washer-1", event: "finished", atUtc: "" }, washer)).toBe("Çamaşır Makinesi bitti");
    expect(eventMessage({ applianceId: "x", event: "error", atUtc: "" }, undefined)).toBe("Cihaz: bir sorun oluştu");
  });
});
