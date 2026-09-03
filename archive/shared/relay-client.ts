// The client half of the relay contract.
//
// One implementation for the phone page, the desktop app and the tests, so a
// signature that a test accepts is the signature the phone sends. It takes a
// `fetch` rather than reaching for the global one, which is what lets a test
// point it at a server with a self-signed certificate.

import {
  RELAY_HEADERS,
  RELAY_PATHS,
  type RelayHello,
  type RelayInboxResponse,
  type RelayOutboxList,
  type RelayPairResponse,
} from "./relay-contract";
import { fromBase64Url, generateNonce, sha256Hex, signRequest } from "./relay-auth";

export interface RelayCredentials {
  /** `https://host.tailnet.ts.net:8842` — origin only, no trailing slash. */
  origin: string;
  deviceId: string;
  /** Base64url, 32 bytes, as issued by the host at pairing. */
  secret: string;
  hostLabel: string;
}

export class RelayRequestError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly code: string,
    /** The host's clock, when it told us. Lets a client explain a refusal that
     *  is really "your phone's time is wrong" rather than "you are not
     *  registered". */
    readonly serverTime?: number,
  ) {
    super(message);
    this.name = "RelayRequestError";
  }
}

type FetchLike = (input: string, init?: RequestInit) => Promise<Response>;

/**
 * The global fetch, called with the right receiver.
 *
 * Writing `fetchImpl = fetch` as a default looks equivalent and is not: the
 * reference is detached from Window, and Chrome answers a detached call with
 * "Illegal invocation". That surfaced as a pairing failure whose message said
 * nothing about fetch.
 */
const windowFetch: FetchLike = (input, init) => fetch(input, init);

async function readError(response: Response): Promise<RelayRequestError> {
  let code = "error";
  let message = `受信先が応答しませんでした（${response.status}）。`;
  let serverTime: number | undefined;
  try {
    const body = (await response.json()) as { error?: string; message?: string; serverTime?: number };
    if (typeof body.error === "string") code = body.error;
    if (typeof body.message === "string" && body.message.length > 0) message = body.message;
    if (typeof body.serverTime === "number") serverTime = body.serverTime;
  } catch {
    // A non-JSON error body is still an error; the status carries the meaning.
  }
  return new RelayRequestError(message, response.status, code, serverTime);
}

export class RelayClient {
  constructor(
    private readonly credentials: RelayCredentials,
    private readonly fetchImpl: FetchLike = windowFetch,
    /** Seconds to add to this device's clock to match the host's. Learned from
     *  a refusal and applied to every later request, so a phone with a wrong
     *  clock recovers on the second attempt instead of never working. */
    private clockOffset = 0,
  ) {}

  get origin(): string {
    return this.credentials.origin;
  }

  get offset(): number {
    return this.clockOffset;
  }

  private async request(
    method: "GET" | "POST",
    path: string,
    body?: Uint8Array,
    contentType = "application/octet-stream",
  ): Promise<Response> {
    const secret = fromBase64Url(this.credentials.secret);
    if (!secret || secret.length !== 32) {
      throw new RelayRequestError("この受信先の登録が壊れています。登録し直してください。", 0, "bad-credentials");
    }
    const payload = body ?? new Uint8Array(0);
    const bodyDigest = await sha256Hex(payload);
    const timestamp = Math.floor(Date.now() / 1000) + this.clockOffset;
    const nonce = generateNonce();
    const signature = await signRequest(secret, { method, path, timestamp, nonce, bodyDigest });
    const headers: Record<string, string> = {
      [RELAY_HEADERS.device]: this.credentials.deviceId,
      [RELAY_HEADERS.timestamp]: String(timestamp),
      [RELAY_HEADERS.nonce]: nonce,
      [RELAY_HEADERS.bodyDigest]: bodyDigest,
      [RELAY_HEADERS.signature]: signature,
    };
    if (body) headers["content-type"] = contentType;
    const response = await this.fetchImpl(`${this.credentials.origin}${path}`, {
      method,
      headers,
      // A fresh copy: some fetch implementations keep a reference to the view
      // and a caller reusing the buffer would change what was signed.
      body: body ? (Uint8Array.from(body) as BodyInit) : undefined,
    });
    if (response.status === 401) {
      const error = await readError(response);
      // Learn the offset from the refusal, so the next call succeeds.
      if (error.code === "clock-skew" && typeof error.serverTime === "number") {
        this.clockOffset = error.serverTime - Math.floor(Date.now() / 1000);
      }
      throw error;
    }
    if (!response.ok) throw await readError(response);
    return response;
  }

  async hello(): Promise<RelayHello> {
    return (await this.request("GET", RELAY_PATHS.hello)).json() as Promise<RelayHello>;
  }

  async outbox(): Promise<RelayOutboxList> {
    return (await this.request("GET", RELAY_PATHS.outbox)).json() as Promise<RelayOutboxList>;
  }

  async fetchOutboxItem(id: string): Promise<Uint8Array> {
    const response = await this.request("GET", RELAY_PATHS.outboxItem(id));
    return new Uint8Array(await response.arrayBuffer());
  }

  async send(routeId: string, bundleContainer: Uint8Array): Promise<RelayInboxResponse> {
    const response = await this.request("POST", RELAY_PATHS.inbox(routeId), bundleContainer);
    return response.json() as Promise<RelayInboxResponse>;
  }

  async unpair(): Promise<void> {
    await this.request("POST", RELAY_PATHS.unpair);
  }
}

/** Pairing is the one call with no signature — there is nothing to sign with
 *  yet. It is same-origin only on the host side. */
export async function pairWithHost(
  origin: string,
  code: string,
  deviceLabel: string,
  fetchImpl: FetchLike = windowFetch,
): Promise<RelayCredentials> {
  const response = await fetchImpl(`${origin}${RELAY_PATHS.pair}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ code, deviceLabel }),
  });
  if (!response.ok) throw await readError(response);
  const body = (await response.json()) as RelayPairResponse;
  if (typeof body.deviceId !== "string" || typeof body.secret !== "string") {
    throw new RelayRequestError("受信先の応答が不正です。", response.status, "bad-response");
  }
  return {
    origin,
    deviceId: body.deviceId,
    secret: body.secret,
    hostLabel: body.hostLabel,
  };
}
