// Where the relay keeps its things, on every platform it runs on.
//
// One override for all of it (DECIMEN_RELAY_HOME) so a test, a second instance
// and a service account can each have their own without touching the real one.

import { homedir, platform } from "node:os";
import { join, resolve } from "node:path";

export function relayHome(): string {
  const override = process.env.DECIMEN_RELAY_HOME;
  if (override && override.trim().length > 0) return resolve(override);
  if (platform() === "win32") {
    const base = process.env.LOCALAPPDATA ?? join(homedir(), "AppData", "Local");
    return join(base, "Decimen", "relay");
  }
  const base = process.env.XDG_CONFIG_HOME ?? join(homedir(), ".config");
  return join(base, "decimen", "relay");
}

export const configPath = () => join(relayHome(), "config.json");
/** Devices and their secrets. Written 0600; never logged, never served. */
export const statePath = () => join(relayHome(), "state.json");
/** Transfers staged for a phone to collect. */
export const outboxDir = () => join(relayHome(), "outbox");
export const certDir = () => join(relayHome(), "tls");
export const logDir = () => join(relayHome(), "logs");
