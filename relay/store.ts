// Paired devices, live pairing codes, the replay cache and the outbox.
//
// The device secrets live here, so this file is written 0600 and is the one
// thing in the product that must never reach a log, a response body or an
// error message.

import { chmodSync, mkdirSync, readFileSync, readdirSync, renameSync, rmSync, statSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { platform } from "node:os";
import {
  PAIR_CODE_MAX_ATTEMPTS,
  PAIR_CODE_TTL_SECONDS,
  fromBase64Url,
  generateDeviceId,
  generatePairCode,
  normalizePairCode,
  randomBytes,
  timingSafeEqual,
  toBase64Url,
} from "../shared/relay-auth";
import { RELAY_CLOCK_SKEW_SECONDS, RELAY_MAX_DEVICE_LABEL, sanitizeLabel } from "../shared/relay-contract";
import { outboxDir, statePath } from "./paths";

export interface PairedDevice {
  id: string;
  label: string;
  /** Base64url, 32 bytes. */
  secret: string;
  pairedAt: number;
  lastSeenAt?: number;
}

interface PairingCode {
  code: string;
  expiresAt: number;
  attemptsLeft: number;
}

interface StateFile {
  v: 1;
  devices: PairedDevice[];
  /**
   * The live pairing code lives in the state file rather than in memory,
   * because the two things that touch it are usually two PROCESSES: `serve`
   * is running in one terminal and the operator types `pair` in another. A
   * code held in the server's memory would be a code the CLI cannot create
   * and a code the server cannot see.
   */
  pairing?: PairingCode | null;
}

const now = () => Math.floor(Date.now() / 1000);
let tempCounter = 0;

export class RelayStore {
  private devices: PairedDevice[] = [];
  private pairing: PairingCode | null = null;
  /** Modification time of the state we last read, so a change made by another
   *  process (a revoked device, a fresh pairing code) is picked up without
   *  re-reading the file on every single request. */
  private loadedAt = -1;
  /** (deviceId, nonce) seen inside the clock window. Replaying a signed
   *  request is otherwise perfectly valid until the timestamp ages out. */
  private readonly nonces = new Map<string, number>();

  constructor() {
    this.load();
  }

  /** Re-read when another process has written since we last looked. */
  private refresh(): void {
    let mtime: number;
    try {
      mtime = statSync(statePath()).mtimeMs;
    } catch {
      return;
    }
    if (mtime === this.loadedAt) return;
    this.load();
  }

  private load(): void {
    try {
      const file = statePath();
      const parsed = JSON.parse(readFileSync(file, "utf8")) as StateFile;
      this.loadedAt = statSync(file).mtimeMs;
      if (parsed?.v !== 1 || !Array.isArray(parsed.devices)) return;
      this.devices = parsed.devices.filter(
        (device) =>
          typeof device?.id === "string" &&
          typeof device.secret === "string" &&
          fromBase64Url(device.secret)?.length === 32,
      );
      const pairing = parsed.pairing;
      this.pairing =
        pairing &&
        typeof pairing.code === "string" &&
        typeof pairing.expiresAt === "number" &&
        typeof pairing.attemptsLeft === "number" &&
        pairing.expiresAt > now()
          ? pairing
          : null;
    } catch {
      // No state yet, or state we cannot read. Starting with no paired devices
      // is the safe reading of both.
    }
  }

  private save(): void {
    const file = statePath();
    mkdirSync(dirname(file), { recursive: true });
    const payload: StateFile = { v: 1, devices: this.devices, pairing: this.pairing };
    // Written via a temp file so a crash mid-write cannot leave a state file
    // that parses as "no devices are paired" and silently drops access.
    // Unique per write: a fixed name is shared by serve, pair, revoke and
    // route add, and one process's write racing another's rename publishes a
    // half-written state file that load() then silently reads as "no devices".
    const temp = `${file}.${process.pid}.${tempCounter++}.tmp`;
    writeFileSync(temp, `${JSON.stringify(payload, null, 2)}\n`, { encoding: "utf8", mode: 0o600 });
    if (platform() !== "win32") chmodSync(temp, 0o600);
    renameSync(temp, file);
    try {
      this.loadedAt = statSync(file).mtimeMs;
    } catch {
      this.loadedAt = -1;
    }
  }

  listDevices(): { id: string; label: string; pairedAt: number; lastSeenAt?: number }[] {
    this.refresh();
    return this.devices.map(({ id, label, pairedAt, lastSeenAt }) => ({
      id,
      label,
      pairedAt,
      lastSeenAt,
    }));
  }

  findDevice(id: unknown): PairedDevice | null {
    this.refresh();
    if (typeof id !== "string") return null;
    // Walks the whole list rather than returning early, so the time taken does
    // not depend on where a guessed id would have been.
    let found: PairedDevice | null = null;
    for (const device of this.devices) {
      if (timingSafeEqual(device.id, id)) found = device;
    }
    return found;
  }

  secretOf(device: PairedDevice): Uint8Array {
    const bytes = fromBase64Url(device.secret);
    if (!bytes || bytes.length !== 32) throw new Error("stored device secret is unusable");
    return bytes;
  }

  /**
   * Record that a device was heard from.
   *
   * Deliberately re-reads first and writes rarely. Writing the whole in-memory
   * state on every authenticated request meant a phone that polls could put a
   * stale device list back over a revocation made seconds earlier in another
   * terminal — undoing the revocation exactly when the device is active, which
   * is the case one revokes for — and could overwrite a freshly printed
   * pairing code with `null` before anybody had time to type it.
   */
  touchDevice(device: PairedDevice): void {
    const stamp = now();
    if (device.lastSeenAt !== undefined && stamp - device.lastSeenAt < 60) return;
    this.refresh();
    const current = this.devices.find((candidate) => candidate.id === device.id);
    // Revoked while this request was in flight. Do not put it back.
    if (!current) return;
    current.lastSeenAt = stamp;
    device.lastSeenAt = stamp;
    this.save();
  }

  revokeDevice(id: string): boolean {
    this.refresh();
    const before = this.devices.length;
    this.devices = this.devices.filter((device) => device.id !== id);
    if (this.devices.length === before) return false;
    this.save();
    return true;
  }

  // ---- pairing ---------------------------------------------------------

  /** Replaces any live code: only one is ever valid, so a code left on a
   *  screen an hour ago cannot still be used after a new one is printed. */
  startPairing(): { code: string; expiresAt: number } {
    this.refresh();
    this.pairing = {
      code: generatePairCode(),
      expiresAt: now() + PAIR_CODE_TTL_SECONDS,
      attemptsLeft: PAIR_CODE_MAX_ATTEMPTS,
    };
    this.save();
    return { code: this.pairing.code, expiresAt: this.pairing.expiresAt };
  }

  pairingStatus(): { active: boolean; expiresIn: number; attemptsLeft: number } {
    this.refresh();
    if (!this.pairing || this.pairing.expiresAt <= now()) {
      return { active: false, expiresIn: 0, attemptsLeft: 0 };
    }
    return {
      active: true,
      expiresIn: this.pairing.expiresAt - now(),
      attemptsLeft: this.pairing.attemptsLeft,
    };
  }

  cancelPairing(): void {
    this.pairing = null;
    this.save();
  }

  /**
   * Redeem a code.
   *
   * A wrong guess costs an attempt, and running out DESTROYS the code rather
   * than merely slowing the next guess down — the owner can always print
   * another one, and an attacker cannot wait out a backoff.
   */
  redeemPairing(
    rawCode: unknown,
    rawLabel: unknown,
  ): { ok: true; device: PairedDevice } | { ok: false; reason: "expired" | "wrong" } {
    this.refresh();
    if (!this.pairing || this.pairing.expiresAt <= now()) {
      if (this.pairing) {
        this.pairing = null;
        this.save();
      }
      return { ok: false, reason: "expired" };
    }
    const code = typeof rawCode === "string" ? normalizePairCode(rawCode) : null;
    if (code === null) {
      // Not even shaped like a code. Costing an attempt would let anybody on
      // the tailnet destroy every code the owner prints with five requests of
      // pure garbage — this endpoint takes no signature.
      return { ok: false, reason: "wrong" };
    }
    if (!timingSafeEqual(code, this.pairing.code)) {
      this.pairing.attemptsLeft--;
      if (this.pairing.attemptsLeft <= 0) this.pairing = null;
      this.save();
      return { ok: false, reason: "wrong" };
    }
    this.pairing = null;
    const device: PairedDevice = {
      id: generateDeviceId(),
      label: sanitizeLabel(rawLabel, RELAY_MAX_DEVICE_LABEL) || "登録された端末",
      secret: toBase64Url(randomBytes(32)),
      pairedAt: now(),
    };
    this.devices.push(device);
    this.save();
    return { ok: true, device };
  }

  // ---- replay ----------------------------------------------------------

  /** False when this (device, nonce) has already been used inside the window. */
  claimNonce(deviceId: string, nonce: string): boolean {
    const cutoff = now() - RELAY_CLOCK_SKEW_SECONDS * 2;
    for (const [key, seenAt] of this.nonces) {
      if (seenAt < cutoff) this.nonces.delete(key);
    }
    const key = `${deviceId}:${nonce}`;
    if (this.nonces.has(key)) return false;
    this.nonces.set(key, now());
    return true;
  }

  // ---- outbox ----------------------------------------------------------

  /** Stage a bundle for a phone to collect. Returns the id it was filed under. */
  addToOutbox(label: string, bundle: Uint8Array, fileCount: number): string {
    const dir = outboxDir();
    mkdirSync(dir, { recursive: true });
    const id = toBase64Url(randomBytes(9));
    const meta = { id, label, fileCount, totalSize: bundle.length, stagedAt: now() };
    writeFileSync(join(dir, `${id}.dcb1`), bundle);
    writeFileSync(join(dir, `${id}.json`), `${JSON.stringify(meta, null, 2)}\n`, "utf8");
    return id;
  }

  listOutbox(): { id: string; label: string; fileCount: number; totalSize: number; stagedAt: number }[] {
    const dir = outboxDir();
    let names: string[];
    try {
      names = readdirSync(dir);
    } catch {
      return [];
    }
    const items = [];
    for (const name of names) {
      if (!name.endsWith(".json")) continue;
      try {
        const meta = JSON.parse(readFileSync(join(dir, name), "utf8"));
        if (typeof meta?.id !== "string") continue;
        // The metadata is ours, but the payload is what is actually served —
        // an entry whose payload has gone is not offered.
        statSync(join(dir, `${meta.id}.dcb1`));
        items.push({
          id: meta.id,
          label: sanitizeLabel(meta.label, 64) || "転送",
          fileCount: Number(meta.fileCount) || 0,
          totalSize: Number(meta.totalSize) || 0,
          stagedAt: Number(meta.stagedAt) || 0,
        });
      } catch {
        continue;
      }
    }
    return items.sort((a, b) => b.stagedAt - a.stagedAt);
  }

  readOutbox(id: string): Uint8Array | null {
    if (!this.listOutbox().some((item) => item.id === id)) return null;
    try {
      return new Uint8Array(readFileSync(join(outboxDir(), `${id}.dcb1`)));
    } catch {
      return null;
    }
  }

  removeFromOutbox(id: string): boolean {
    if (!this.listOutbox().some((item) => item.id === id)) return false;
    rmSync(join(outboxDir(), `${id}.dcb1`), { force: true });
    rmSync(join(outboxDir(), `${id}.json`), { force: true });
    return true;
  }
}
