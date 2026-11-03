import { tr } from "../i18n/tr.ts";
import { commandErrorMessage } from "./api.ts";
import { buildCommand } from "./commands.ts";
import type { HomeSession } from "./session.ts";
import type { Appliance, CommandSource, DeviceCommand } from "./types.ts";

export type CommandOutcome = { ok: true; appliance: Appliance } | { ok: false; message: string };

/** Validates, sends and applies a device command. The store is updated with the returned appliance. */
export async function sendDeviceCommand(
  session: HomeSession,
  appliance: Appliance,
  command: DeviceCommand,
  source: CommandSource = "app",
): Promise<CommandOutcome> {
  const check = buildCommand(appliance, command, source, session.s2.memberId);
  if (!check.ok) {
    return { ok: false, message: check.reason === "unsupported" ? tr.home.errors.unsupported : tr.home.errors.invalid };
  }
  try {
    const api = await session.api();
    const result = await api.sendCommand(appliance.id, check.request);
    session.store.upsert(result.appliance);
    return { ok: true, appliance: result.appliance };
  } catch (error) {
    return { ok: false, message: commandErrorMessage(error) };
  }
}
