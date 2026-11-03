// Seeded demo home, matching contracts.md "Çeliknaz slice 1 / Seeded demo home (stable ids)".

import type { Appliance, Capabilities, HomeView } from "../types.ts";

export const DEMO_HOME_ID = "home-1";
export const FIXTURE_TIME = "2026-11-03T10:00:00Z";

const acCapabilities = (): Capabilities => ({
  commands: ["power", "setpoint", "mode", "fan"],
  modes: ["cool", "heat", "dry", "fan", "auto", "eco"],
  fanLevels: ["auto", "low", "medium", "high"],
  setpoint: { min: 16, max: 30, step: 0.5 },
  programs: [],
});

function ac(
  id: string,
  roomId: string,
  displayName: string,
  model: string,
  ac: NonNullable<Appliance["state"]["ac"]>,
  power: boolean,
): Appliance {
  return {
    id,
    homeId: DEMO_HOME_ID,
    roomId,
    type: "air_conditioner",
    displayName,
    model,
    capabilities: acCapabilities(),
    state: {
      power,
      status: power ? "running" : "off",
      powerW: power ? 820 : 0,
      updatedAtUtc: FIXTURE_TIME,
      ac,
      program: null,
      oven: null,
      fridge: null,
    },
  };
}

export interface ProgramProfile {
  name: string;
  temperatureC: number;
  durationMin: number;
  powerW: number;
}

/** Same fictional profiles as the backend `ProgramCatalog`. */
export const WASHER_PROGRAMS: readonly ProgramProfile[] = [
  { name: "cotton40", temperatureC: 40, durationMin: 120, powerW: 450 },
  { name: "cotton60", temperatureC: 60, durationMin: 150, powerW: 650 },
  { name: "eco40", temperatureC: 40, durationMin: 180, powerW: 300 },
  { name: "synthetic40", temperatureC: 40, durationMin: 90, powerW: 400 },
  { name: "quick30", temperatureC: 30, durationMin: 30, powerW: 500 },
];

export const DISHWASHER_PROGRAMS: readonly ProgramProfile[] = [
  { name: "eco50", temperatureC: 50, durationMin: 180, powerW: 350 },
  { name: "auto", temperatureC: 55, durationMin: 120, powerW: 600 },
  { name: "intensive70", temperatureC: 70, durationMin: 140, powerW: 900 },
  { name: "quick35", temperatureC: 35, durationMin: 30, powerW: 800 },
];

export function findProgram(type: Appliance["type"], name: string): ProgramProfile | undefined {
  const list = type === "washing_machine" ? WASHER_PROGRAMS : type === "dishwasher" ? DISHWASHER_PROGRAMS : [];
  return list.find((p) => p.name === name);
}

export const KITCHEN_TEMP_C = 22;

/** Returns a fresh deep copy of the demo home every call. */
export function createDemoHome(): HomeView {
  const appliances: Appliance[] = [
    ac("ac-salon", "salon", "Salon Kliması", "Beko BK-12 (demo)", { setpoint: 25, mode: "cool", fan: "auto", roomTempC: 24.8, humidityPct: 55 }, true),
    ac("ac-yatak", "yatak-odasi", "Yatak Odası Kliması", "Beko BK-09 (demo)", { setpoint: 24, mode: "cool", fan: "auto", roomTempC: 27.5, humidityPct: 58 }, false),
    ac("ac-cocuk", "cocuk-odasi", "Çocuk Odası Kliması", "Beko BK-09 (demo)", { setpoint: 23, mode: "cool", fan: "auto", roomTempC: 27, humidityPct: 57 }, false),
    {
      id: "dishwasher-1",
      homeId: DEMO_HOME_ID,
      roomId: "mutfak",
      type: "dishwasher",
      displayName: "Bulaşık Makinesi",
      model: "Beko BDF-1500 (demo)",
      capabilities: {
        commands: ["power", "start_program", "pause", "resume", "stop"],
        modes: [],
        fanLevels: [],
        setpoint: null,
        programs: DISHWASHER_PROGRAMS.map((p) => p.name),
      },
      state: {
        power: true,
        status: "idle",
        powerW: 0,
        updatedAtUtc: FIXTURE_TIME,
        ac: null,
        // Idle washers keep the selected program at full duration (contract).
        program: { program: "eco50", temperatureC: 50, remainingMin: 180, progressPct: 0, startAtUtc: null },
        oven: null,
        fridge: null,
      },
    },
    {
      id: "oven-1",
      homeId: DEMO_HOME_ID,
      roomId: "mutfak",
      type: "oven",
      displayName: "Fırın",
      model: "Beko BBIM-13300 (demo)",
      capabilities: {
        commands: ["power", "mode", "set_oven", "pause", "resume", "stop"],
        modes: ["conventional", "fan", "grill"],
        fanLevels: [],
        setpoint: { min: 50, max: 250, step: 5 },
        programs: [],
      },
      state: {
        power: false,
        status: "off",
        powerW: 0,
        updatedAtUtc: FIXTURE_TIME,
        ac: null,
        program: null,
        oven: { mode: "conventional", targetTempC: 180, currentTempC: KITCHEN_TEMP_C, timerMin: 0 },
        fridge: null,
      },
    },
    {
      id: "fridge-1",
      homeId: DEMO_HOME_ID,
      roomId: "mutfak",
      type: "refrigerator",
      displayName: "Buzdolabı",
      model: "Beko RCNA-406 (demo)",
      capabilities: { commands: [], modes: [], fanLevels: [], setpoint: null, programs: [] },
      state: {
        power: true,
        status: "running",
        powerW: 90,
        updatedAtUtc: FIXTURE_TIME,
        ac: null,
        program: null,
        oven: null,
        fridge: { fridgeTempC: 4, freezerTempC: -18, doorOpen: false },
      },
    },
    {
      id: "washer-1",
      homeId: DEMO_HOME_ID,
      roomId: "camasir-odasi",
      type: "washing_machine",
      displayName: "Çamaşır Makinesi",
      model: "Beko WTV-9612 (demo)",
      capabilities: {
        commands: ["power", "start_program", "pause", "resume", "stop"],
        modes: [],
        fanLevels: [],
        setpoint: null,
        programs: WASHER_PROGRAMS.map((p) => p.name),
      },
      state: {
        power: true,
        status: "running",
        powerW: 450,
        updatedAtUtc: FIXTURE_TIME,
        ac: null,
        program: { program: "cotton40", temperatureC: 40, remainingMin: 42, progressPct: 65, startAtUtc: null },
        oven: null,
        fridge: null,
      },
    },
  ];

  return {
    home: { id: DEMO_HOME_ID, name: "Evim", city: "İstanbul" },
    rooms: [
      { id: "salon", name: "Salon" },
      { id: "yatak-odasi", name: "Yatak Odası" },
      { id: "cocuk-odasi", name: "Çocuk Odası" },
      { id: "mutfak", name: "Mutfak" },
      { id: "camasir-odasi", name: "Çamaşır Odası" },
    ],
    members: [
      { id: "ayse", displayName: "Ayşe", role: "owner", isDefault: true },
      { id: "mehmet", displayName: "Mehmet", role: "member", isDefault: false },
      { id: "zeynep", displayName: "Zeynep", role: "member", isDefault: false },
    ],
    appliances,
  };
}
