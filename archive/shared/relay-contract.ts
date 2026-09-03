// The contract between the phone and a receiving host (a Windows PC or the
// Linux VPS). One definition, three implementations that must agree: the Node
// host in relay/, the phone page in relay-app/, and the tests.
//
// Shape of the thing: the phone never browses a filesystem. A host decides
// locally which folders it is willing to receive into, gives each a name a
// human picked, and the phone only ever sees that list of names. There is no
// path in any request the phone can make.

export const RELAY_API_VERSION = 1;

export const RELAY_PATHS = {
  hello: "/api/v1/hello",
  pair: "/api/v1/pair",
  outbox: "/api/v1/outbox",
  /** `${outbox}/${id}` */
  outboxItem: (id: string) => `/api/v1/outbox/${id}`,
  /** `${inbox}/${routeId}` */
  inbox: (routeId: string) => `/api/v1/inbox/${routeId}`,
  /** A device removing its own registration. A phone that is about to be sold
   *  or reset can hand its access back without anyone reaching the host. */
  unpair: "/api/v1/unpair",
} as const;

export const RELAY_HEADERS = {
  device: "x-decimen-device",
  timestamp: "x-decimen-timestamp",
  nonce: "x-decimen-nonce",
  bodyDigest: "x-decimen-body-sha256",
  signature: "x-decimen-signature",
} as const;

/** Requests older or newer than this are refused. Wide enough for a phone that
 *  has not synced its clock in a while, narrow enough that the replay cache
 *  stays small. */
export const RELAY_CLOCK_SKEW_SECONDS = 120;

/** Hard ceiling on a request body, enforced BEFORE the body is buffered. */
export const RELAY_MAX_BODY_BYTES = 64 * 1024 * 1024;

/** Ids are opaque to the phone and appear in URLs, so they are restricted to
 *  characters that cannot mean anything else in a path. */
export const RELAY_ID_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;

export interface RelayRoute {
  /** Opaque, stable. Never a path. */
  id: string;
  /** What a human called this destination. Shown on the phone. */
  label: string;
  /** True when the host has checked it can write there right now. */
  writable: boolean;
}

export interface RelayHello {
  api: number;
  /** What this host calls itself. Shown on the phone so two hosts are telling
   *  apart at a glance. */
  hostLabel: string;
  appVersion: string;
  routes: RelayRoute[];
}

export interface RelayOutboxItem {
  id: string;
  /** The transfer's own name — the folder or file the sender chose. */
  label: string;
  fileCount: number;
  totalSize: number;
  /** Seconds since the epoch, host clock. */
  stagedAt: number;
}

export interface RelayOutboxList {
  items: RelayOutboxItem[];
}

export interface RelayPairRequest {
  code: string;
  /** What the phone calls itself, so the host's list of paired devices is
   *  readable. Length-capped by the host. */
  deviceLabel: string;
}

export interface RelayPairResponse {
  deviceId: string;
  /** Base64url, 32 bytes. The phone stores this and never sends it again. */
  secret: string;
  hostLabel: string;
  routes: RelayRoute[];
}

export interface RelayInboxResponse {
  saved: true;
  routeId: string;
  /** Where it landed, relative to the route's own folder — never an absolute
   *  path, because the phone has no business knowing the host's layout. */
  savedAs: string;
  fileCount: number;
  totalSize: number;
  /** Files written under a different name because something was already there. */
  renamed: number;
}

export interface RelayErrorBody {
  error: string;
  /** A sentence for a human, in Japanese. */
  message: string;
}

export const RELAY_MAX_DEVICE_LABEL = 64;
export const RELAY_MAX_ROUTE_LABEL = 64;
export const RELAY_MAX_HOST_LABEL = 64;

/** Trim and bound a label that will be displayed. Control characters are
 *  stripped: a route label reaches the phone's UI, and the host operator is
 *  trusted but a mistyped file name is not a reason to break the layout. */
export function sanitizeLabel(value: unknown, max: number): string {
  if (typeof value !== "string") return "";
  let out = "";
  for (const character of value) {
    const code = character.codePointAt(0)!;
    // C0/C1 controls, DEL, zero-width and bidi-override characters. A label
    // is one line of display text; none of these belong in one.
    if (code < 0x20 || code === 0x7f) continue;
    if (code >= 0x80 && code <= 0x9f) continue;
    if (code >= 0x200b && code <= 0x200f) continue;
    if (code >= 0x202a && code <= 0x202e) continue;
    if (code >= 0x2066 && code <= 0x2069) continue;
    if (code === 0xfeff || code === 0x00ad) continue;
    out += character;
  }
  return out.normalize("NFC").trim().slice(0, max);
}

export function isRelayId(value: unknown): value is string {
  return typeof value === "string" && RELAY_ID_PATTERN.test(value);
}
