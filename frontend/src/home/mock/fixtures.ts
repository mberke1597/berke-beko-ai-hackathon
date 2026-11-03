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

function ac(id: string, roomId: string, displayName: string, ac: NonNullable<Appliance["state"]["ac"]>, power: boolean): Appliance {
  return {
    id,
    homeId: DEMO_HOME_ID,
    roomId,
    type: "air_conditioner",
    displayName,
    model: "Beko BK-12 (demo)",
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

/** Program durations in minutes, used by the simulator when a program starts. */
export const PROGRAM_DURATIONS: Readonly<Record<string, number>> = {
  cotton40: 95,
  cotton60: 120,
  synthetic: 70,
  eco: 180,
  quick30: 30,
  wool: 45,
  eco50: 150,
  auto: 120,
  intensive70: 140,
  quick: 35,
  glass: 60,
};

/** Returns a fresh deep copy of the demo home every call. */
export function createDemoHome(): HomeView {
  const appliances: Appliance[] = [
    ac("ac-salon", "salon", "Salon Kliması", { setpoint: 25, mode: "cool", fan: "auto", roomTempC: 24.8, humidityPct: 55 }, true),
    ac("ac-yatak", "yatak-odasi", "Yatak Odası Kliması", { setpoint: 23, mode: "cool", fan: "low", roomTempC: 26.1, humidityPct: 50 }, false),
    ac("ac-cocuk", "cocuk-odasi", "Çocuk Odası Kliması", { setpoint: 24, mode: "auto", fan: "auto", roomTempC: 25.6, humidityPct: 52 }, false),
    {
      id: "dishwasher-1",
      homeId: DEMO_HOME_ID,
      roomId: "mutfak",
      type: "dishwasher",
      displayName: "Bulaşık Makinesi",
      model: "Beko BDFN (demo)",
      capabilities: {
        commands: ["power", "start_program", "pause", "resume", "stop"],
        modes: [],
        fanLevels: [],
        setpoint: null,
        programs: ["eco50", "auto", "intensive70", "quick", "glass"],
      },
      state: {
        power: true,
        status: "running",
        powerW: 1800,
        updatedAtUtc: FIXTURE_TIME,
        ac: null,
        program: { program: "eco50", temperatureC: 50, remainingMin: 42, progressPct: 60, startAtUtc: null },
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
      model: "Beko BBIM (demo)",
      capabilities: {
        commands: ["power", "mode", "set_oven", "pause", "resume", "stop"],
        modes: ["conventional", "fan", "grill"],
        fanLevels: [],
        setpoint: null,
        programs: [],
      },
      state: {
        power: false,
        status: "off",
        powerW: 0,
        updatedAtUtc: FIXTURE_TIME,
        ac: null,
        program: null,
        oven: { mode: "conventional", targetTempC: 180, currentTempC: 24, timerMin: null },
        fridge: null,
      },
    },
    {
      id: "fridge-1",
      homeId: DEMO_HOME_ID,
      roomId: "mutfak",
      type: "refrigerator",
      displayName: "Buzdolabı",
      model: "Beko GN (demo)",
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
      model: "Beko WTV (demo)",
      capabilities: {
        commands: ["power", "start_program", "pause", "resume", "stop"],
        modes: [],
        fanLevels: [],
        setpoint: null,
        programs: ["cotton40", "cotton60", "synthetic", "eco", "quick30", "wool"],
      },
      state: {
        power: true,
        status: "running",
        powerW: 450,
        updatedAtUtc: FIXTURE_TIME,
        ac: null,
        // Close to the end so the "finished" toast shows early in a demo.
        program: { program: "cotton40", temperatureC: 40, remainingMin: 6, progressPct: 94, startAtUtc: null },
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
