// Which addresses this host is willing to be reached on.
//
// The rule is that the answer comes from tailscaled, not from guessing. A scan
// of the interfaces for 100.64.0.0/10 looks right and is wrong in three ways:
// a headscale deployment can use a different pool, the IPv6 address is in
// fd7a:115c:a1e0::/48 and would be missed entirely, and an address in that
// range that is NOT Tailscale's would be bound anyway. `tailscale ip` knows.
//
// There is no fallback. If tailscaled cannot be asked, the server does not
// start, and says why. Binding 0.0.0.0 "just to get going" is exactly the
// failure this is here to prevent.

import { execFileSync } from "node:child_process";
import { existsSync } from "node:fs";
import { platform } from "node:os";
import { isIP } from "node:net";

export class TailscaleError extends Error {
  constructor(
    message: string,
    readonly fix: string,
  ) {
    super(message);
    this.name = "TailscaleError";
  }
}

/** Where the CLI usually is when it is not on PATH. Windows installs it under
 *  Program Files and does not always add it. */
function candidateCommands(): string[] {
  if (platform() === "win32") {
    const programFiles = process.env.ProgramFiles ?? "C:\\Program Files";
    return ["tailscale.exe", `${programFiles}\\Tailscale\\tailscale.exe`];
  }
  return [
    "/usr/bin/tailscale",
    "/usr/local/bin/tailscale",
    "/usr/sbin/tailscale",
    "/opt/tailscale/tailscale",
    "/Applications/Tailscale.app/Contents/MacOS/Tailscale",
  ];
}

function runTailscale(args: string[]): string | null {
  for (const command of candidateCommands()) {
    // An absolute path, or nothing. execFileSync on Windows searches the
    // CURRENT DIRECTORY before PATH, so a bare "tailscale.exe" would run a
    // copy somebody dropped in the folder the app happens to be started from.
    if (!command.includes("/") && !command.includes("\\")) continue;
    if (!existsSync(command)) continue;
    try {
      return execFileSync(command, args, {
        encoding: "utf8",
        timeout: 5000,
        stdio: ["ignore", "pipe", "ignore"],
      }).trim();
    } catch {
      continue;
    }
  }
  return null;
}

export interface BindTarget {
  addresses: string[];
  /** True when the operator overrode the Tailscale-only default. */
  explicit: boolean;
}

/**
 * The addresses to listen on.
 *
 * Both families when both exist: MagicDNS publishes A and AAAA, and a browser
 * doing Happy Eyeballs will often try the v6 one first. Binding only v4 gives
 * a connection refused that looks like the server is down.
 */
export function tailscaleAddresses(): string[] {
  const output = runTailscale(["ip"]);
  if (output === null) {
    throw new TailscaleError(
      "tailscale コマンドが見つからないか、応答しませんでした。",
      "Tailscale をインストールし、`tailscale status` が通る状態にしてください。",
    );
  }
  const addresses = output
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => isIP(line) !== 0);
  if (addresses.length === 0) {
    throw new TailscaleError(
      "この端末に Tailscale のアドレスが割り当てられていません。",
      "`tailscale up` でログインしてから、もう一度起動してください。",
    );
  }
  return addresses;
}

export function resolveBind(bind: "tailscale" | string[]): BindTarget {
  if (bind === "tailscale") return { addresses: tailscaleAddresses(), explicit: false };
  return { addresses: [...bind], explicit: true };
}

/** The MagicDNS name, when tailscaled knows one. Used only to print the URL a
 *  human should open — nothing depends on it resolving. */
export function magicDnsName(): string | null {
  const output = runTailscale(["status", "--json"]);
  if (output === null) return null;
  try {
    const status = JSON.parse(output) as { Self?: { DNSName?: string } };
    const name = status.Self?.DNSName;
    if (typeof name !== "string" || name.length === 0) return null;
    return name.replace(/\.$/, "");
  } catch {
    return null;
  }
}
