// The receiving host: an HTTPS server that serves the phone's page and accepts
// transfers into folders its owner registered.
//
// Three things about the shape, because they are load-bearing:
//
// 1. IT SERVES THE PHONE PAGE ITSELF. A browser will not let an https page
//    fetch an http address, and a Tailscale address is not one of the origins
//    browsers treat as trustworthy — so a phone page hosted anywhere else
//    simply cannot reach this. Serving the page from the same origin as the
//    API removes the problem instead of working around it, and removes every
//    CORS question with it.
//
// 2. THE SIGNATURE IS CHECKED BEFORE THE BODY IS READ. Verifying the body hash
//    means having the body, and having the body means an unpaired caller can
//    make this host hold whatever it sends. So the signature — which covers
//    the CLAIMED body hash — is checked first, then the declared length is
//    checked against the ceiling, then the body is streamed and its real hash
//    compared with the claim.
//
// 3. NOTHING IS WRITTEN BEFORE EVERYTHING IS VERIFIED. The bundle's own
//    SHA-256 has to pass, then every file's, and only then does writer.ts run.

import { createServer, type Server } from "node:https";
import type { IncomingMessage, ServerResponse } from "node:http";
import { createHash } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { extname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
  RELAY_API_VERSION,
  RELAY_CLOCK_SKEW_SECONDS,
  RELAY_HEADERS,
  RELAY_MAX_BODY_BYTES,
  RELAY_PATHS,
  isRelayId,
  type RelayHello,
  type RelayInboxResponse,
  type RelayOutboxList,
  type RelayPairResponse,
  type RelayRoute,
} from "../shared/relay-contract";
import { canonicalRequest, timingSafeEqual, verifyRequestSignature } from "../shared/relay-auth";
import { PERMISSIVE_BUNDLE_LIMITS, verifyBundle, type BundleLimits } from "../shared/bundle";
import { unpackFile, verifyFile } from "../shared/protocol";
import type { RelayConfig } from "./config";
import { RelayStore, type PairedDevice } from "./store";
import { isWritable, writeBundle } from "./writer";
import { isInside } from "./writer";

const EMPTY_BODY_SHA256 = createHash("sha256").update(Buffer.alloc(0)).digest("hex");

const MIME: Record<string, string> = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".webmanifest": "application/manifest+json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".ico": "image/x-icon",
  ".wasm": "application/wasm",
  ".woff2": "font/woff2",
};

/** Refuses framing, plugins, and any subresource we did not ship. */
const CSP =
  "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; " +
  "img-src 'self' data: blob:; media-src 'self' blob:; connect-src 'self'; " +
  "object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";

export interface ServerOptions {
  config: RelayConfig;
  store: RelayStore;
  /** Where the phone's built page lives. */
  webRoot?: string;
  log?: (level: "INFO" | "WARN" | "ERROR", message: string) => void;
  bundleLimits?: BundleLimits;
}

const here = fileURLToPath(new URL(".", import.meta.url));

export function defaultWebRoot(): string {
  return resolve(here, "web", "dist");
}

export function createRelayServer(options: ServerOptions): Server {
  const { config, store } = options;
  const webRoot = resolve(options.webRoot ?? defaultWebRoot());
  const bundleLimits = options.bundleLimits ?? {
    ...PERMISSIVE_BUNDLE_LIMITS,
    maxTotalBytes: config.maxBundleBytes,
  };
  const log = options.log ?? (() => undefined);

  const credentials = {
    cert: readFileSync(config.tls.certFile),
    key: readFileSync(config.tls.keyFile),
  };

  const server = createServer(credentials, (request, response) => {
    handle(request, response).catch((error: unknown) => {
      log("ERROR", `unhandled: ${error instanceof Error ? error.message : String(error)}`);
      if (!response.headersSent) send(request, response, 500, { error: "internal", message: "内部エラーです。" });
      else response.end();
    });
  });

  function send(
    request: IncomingMessage,
    response: ServerResponse,
    status: number,
    body: unknown,
    extra: Record<string, string> = {},
  ): void {
    const payload = Buffer.from(JSON.stringify(body), "utf8");
    // A refusal that happens BEFORE the body was read leaves bytes the client
    // still intends to send. Keeping that connection alive hands the next
    // request a socket with somebody else's payload queued in front of it, so
    // the connection is closed instead of reused.
    const pending = !request.readableEnded ? { connection: "close" } : {};
    response.writeHead(status, {
      "content-type": "application/json; charset=utf-8",
      "content-length": payload.length,
      "cache-control": "no-store",
      "x-content-type-options": "nosniff",
      "content-security-policy": CSP,
      ...pending,
      ...extra,
    });
    response.end(payload);
  }

  /**
   * Cross-origin policy: none by default.
   *
   * The phone loads its page from this host, so every legitimate browser call
   * is same-origin and needs no header at all. An Origin we do not recognise
   * is refused outright rather than merely un-answered, so a page on some
   * other site cannot use a logged-in browser to reach this host. `pair` is
   * never reachable cross-origin at all, whatever the allowlist says: it is
   * the one endpoint that takes no signature.
   */
  function originAllowed(request: IncomingMessage, isPairing: boolean): boolean {
    const origin = request.headers.origin;
    if (typeof origin !== "string" || origin.length === 0) return true; // not a browser fetch
    const host = request.headers.host;
    if (typeof host === "string" && origin === `https://${host}`) return true;
    if (isPairing) return false;
    return config.allowedAppOrigins.includes(origin);
  }

  function corsHeaders(request: IncomingMessage): Record<string, string> {
    const origin = request.headers.origin;
    if (typeof origin !== "string") return {};
    const host = request.headers.host;
    if (typeof host === "string" && origin === `https://${host}`) return {};
    if (!config.allowedAppOrigins.includes(origin)) return {};
    return {
      "access-control-allow-origin": origin,
      "access-control-allow-headers": Object.values(RELAY_HEADERS).join(", ") + ", content-type",
      "access-control-allow-methods": "GET, POST, OPTIONS",
      "access-control-max-age": "600",
      vary: "Origin",
    };
  }

  /**
   * The destinations, with whether each can be written to right now.
   *
   * The answer is cached for a few seconds. Every hello and every pairing asks
   * for this, and probing a folder on each one means touching the filesystem
   * several times a second for information that changes when somebody plugs a
   * drive in — measured in minutes, not milliseconds.
   */
  const writableCache = new Map<string, { at: number; value: boolean }>();
  const WRITABLE_TTL_MS = 5000;
  function routesFor(): RelayRoute[] {
    const now = Date.now();
    return config.routes.map((route) => {
      const cached = writableCache.get(route.path);
      if (cached && now - cached.at < WRITABLE_TTL_MS) {
        return { id: route.id, label: route.label, writable: cached.value };
      }
      const value = isWritable(route.path);
      writableCache.set(route.path, { at: now, value });
      return { id: route.id, label: route.label, writable: value };
    });
  }

  type AuthOk = { ok: true; device: PairedDevice; bodyDigest: string };
  type AuthFail = { ok: false; status: number; body: Record<string, unknown> };

  async function authenticate(request: IncomingMessage, target: string): Promise<AuthOk | AuthFail> {
    const header = (name: string): string =>
      typeof request.headers[name] === "string" ? (request.headers[name] as string) : "";
    const deviceId = header(RELAY_HEADERS.device);
    const timestampText = header(RELAY_HEADERS.timestamp);
    const nonce = header(RELAY_HEADERS.nonce);
    const bodyDigest = header(RELAY_HEADERS.bodyDigest).toLowerCase();
    const signature = header(RELAY_HEADERS.signature);
    const serverTime = Math.floor(Date.now() / 1000);

    const unauthorized = (message: string): AuthFail => ({
      ok: false,
      status: 401,
      // The server's own clock goes back with every refusal so a phone that
      // has been in aeroplane mode can correct itself instead of showing an
      // unexplainable failure.
      body: { error: "unauthorized", message, serverTime },
    });

    if (!deviceId || !nonce || !signature || !/^[0-9a-f]{64}$/.test(bodyDigest)) {
      return unauthorized("この端末はまだ登録されていません。");
    }
    const timestamp = Number(timestampText);
    if (!Number.isSafeInteger(timestamp)) return unauthorized("要求の時刻が不正です。");
    if (Math.abs(serverTime - timestamp) > RELAY_CLOCK_SKEW_SECONDS) {
      return {
        ok: false,
        status: 401,
        body: {
          error: "clock-skew",
          message: "端末と受信先の時計がずれています。時刻を合わせてからやり直してください。",
          serverTime,
        },
      };
    }
    const device = store.findDevice(deviceId);
    if (!device) return unauthorized("この端末はまだ登録されていません。");
    const fields = { method: request.method ?? "GET", path: target, timestamp, nonce, bodyDigest };
    let valid: boolean;
    try {
      valid = await verifyRequestSignature(store.secretOf(device), fields, signature);
    } catch {
      valid = false;
    }
    if (!valid) return unauthorized("要求の署名が一致しません。");
    void canonicalRequest; // the signed string is built inside verifyRequestSignature
    // Claimed AFTER the signature, not before. A device id is not a secret —
    // it travels in a plaintext header and `device list` prints it — so
    // claiming first let anybody holding one fill the replay cache and, worse,
    // pre-claim an observed nonce so the genuine request came back "replayed".
    if (!store.claimNonce(device.id, nonce)) {
      return { ok: false, status: 401, body: { error: "replayed", message: "同じ要求が二重に届きました。", serverTime } };
    }
    store.touchDevice(device);
    return { ok: true, device, bodyDigest };
  }

  /** Streams the body with a hard ceiling, hashing as it goes. */
  function readBody(request: IncomingMessage, maxBytes: number): Promise<Buffer> {
    return new Promise((resolveBody, rejectBody) => {
      const chunks: Buffer[] = [];
      let total = 0;
      let settled = false;
      // Rejecting is not destroying. Tearing the socket down here meant the
      // 413 that follows never reached the client — it saw ECONNRESET and the
      // phone said "cannot reach the host" instead of "that is over the
      // limit". The refusal is sent with `Connection: close`, which is what
      // actually ends the connection, and the caller stops reading.
      const fail = (message: string) => {
        if (settled) return;
        settled = true;
        request.pause();
        rejectBody(new Error(message));
      };
      // A connection that dribbles bytes forever holds this host's memory and
      // its temp space open; the transfer itself is one burst, so a stall is
      // never a legitimate slow sender.
      request.setTimeout(120_000, () => fail("timeout"));
      request.on("data", (chunk: Buffer) => {
        total += chunk.length;
        if (total > maxBytes) return fail("too-large");
        chunks.push(chunk);
      });
      request.on("error", () => fail("aborted"));
      request.on("end", () => {
        if (settled) return;
        settled = true;
        resolveBody(Buffer.concat(chunks));
      });
    });
  }

  function serveStatic(request: IncomingMessage, response: ServerResponse, pathname: string): void {
    let relative: string;
    try {
      // `/%ZZ` survives the URL parser and throws here. An unauthenticated
      // request must not be able to produce a 500 and an ERROR log line; an
      // unknown path inside a single-page app is just the app.
      relative = pathname === "/" ? "index.html" : decodeURIComponent(pathname).replace(/^\/+/, "");
    } catch {
      relative = "index.html";
    }
    const target = resolve(join(webRoot, relative));
    if (!isInside(webRoot, target) || !existsSync(target)) {
      // A single-page app: an unknown path inside the app is the app.
      const index = join(webRoot, "index.html");
      if (!existsSync(index)) {
        send(request, response, 404, { error: "not-found", message: "画面がまだ用意されていません。" });
        return;
      }
      const html = readFileSync(index);
      response.writeHead(200, {
        "content-type": MIME[".html"]!,
        "content-length": html.length,
        "content-security-policy": CSP,
        "x-content-type-options": "nosniff",
        "referrer-policy": "no-referrer",
        "cache-control": "no-cache",
      });
      response.end(html);
      return;
    }
    const body = readFileSync(target);
    response.writeHead(200, {
      "content-type": MIME[extname(target).toLowerCase()] ?? "application/octet-stream",
      "content-length": body.length,
      "content-security-policy": CSP,
      "x-content-type-options": "nosniff",
      "referrer-policy": "no-referrer",
      "cache-control": relative === "index.html" ? "no-cache" : "public, max-age=3600",
    });
    response.end(body);
  }

  async function handle(request: IncomingMessage, response: ServerResponse): Promise<void> {
    const target = request.url ?? "/";
    const url = new URL(target, "https://relay.invalid");
    const pathname = url.pathname;
    const isApi = pathname.startsWith("/api/");
    const isPairing = pathname === RELAY_PATHS.pair;

    if (!originAllowed(request, isPairing)) {
      log("WARN", `refused cross-origin request to ${pathname}`);
      send(request, response, 403, { error: "forbidden", message: "この画面からは操作できません。" });
      return;
    }
    if (request.method === "OPTIONS") {
      response.writeHead(204, corsHeaders(request));
      response.end();
      return;
    }
    if (!isApi) {
      if (request.method !== "GET") {
        send(request, response, 405, { error: "method", message: "この要求は受け付けていません。" });
        return;
      }
      serveStatic(request, response, pathname);
      return;
    }

    const cors = corsHeaders(request);

    // ---- pairing: the only endpoint without a signature ------------------
    if (isPairing) {
      if (request.method !== "POST") {
        send(request, response, 405, { error: "method", message: "この要求は受け付けていません。" }, cors);
        return;
      }
      let payload: { code?: unknown; deviceLabel?: unknown };
      try {
        payload = JSON.parse((await readBody(request, 4096)).toString("utf8"));
      } catch {
        send(request, response, 400, { error: "bad-request", message: "要求を読み取れません。" }, cors);
        return;
      }
      const result = store.redeemPairing(payload?.code, payload?.deviceLabel);
      if (!result.ok) {
        log("WARN", `pairing refused (${result.reason})`);
        send(
          request,
          response,
          403,
          {
            error: result.reason === "expired" ? "code-expired" : "code-wrong",
            message:
              result.reason === "expired"
                ? "登録コードの有効期限が切れています。受信先で新しいコードを出してください。"
                : "登録コードが違います。",
          },
          cors,
        );
        return;
      }
      log("INFO", `paired device ${result.device.id}`);
      const body: RelayPairResponse = {
        deviceId: result.device.id,
        secret: result.device.secret,
        hostLabel: config.hostLabel,
        routes: routesFor(),
      };
      send(request, response, 200, body, cors);
      return;
    }

    const auth = await authenticate(request, target);
    if (!auth.ok) {
      send(request, response, auth.status, auth.body, cors);
      return;
    }

    // ---- hello ------------------------------------------------------------
    if (pathname === RELAY_PATHS.hello && request.method === "GET") {
      const body: RelayHello = {
        api: RELAY_API_VERSION,
        hostLabel: config.hostLabel,
        appVersion: APP_VERSION,
        routes: routesFor(),
      };
      send(request, response, 200, body, cors);
      return;
    }

    // ---- unpair -----------------------------------------------------------
    if (pathname === RELAY_PATHS.unpair && request.method === "POST") {
      store.revokeDevice(auth.device.id);
      log("INFO", `device ${auth.device.id} removed itself`);
      send(request, response, 200, { removed: true }, cors);
      return;
    }

    // ---- outbox -----------------------------------------------------------
    if (pathname === RELAY_PATHS.outbox && request.method === "GET") {
      const body: RelayOutboxList = { items: store.listOutbox() };
      send(request, response, 200, body, cors);
      return;
    }
    if (pathname.startsWith(`${RELAY_PATHS.outbox}/`) && request.method === "GET") {
      const id = pathname.slice(RELAY_PATHS.outbox.length + 1);
      if (!isRelayId(id)) {
        send(request, response, 400, { error: "bad-id", message: "指定が不正です。" }, cors);
        return;
      }
      const bytes = store.readOutbox(id);
      if (!bytes) {
        send(request, response, 404, { error: "not-found", message: "その送信待ちはもうありません。" }, cors);
        return;
      }
      response.writeHead(200, {
        "content-type": "application/octet-stream",
        "content-length": bytes.length,
        "cache-control": "no-store",
        "x-content-type-options": "nosniff",
        ...cors,
      });
      response.end(Buffer.from(bytes));
      return;
    }

    // ---- inbox ------------------------------------------------------------
    if (pathname.startsWith("/api/v1/inbox/") && request.method === "POST") {
      const routeId = pathname.slice("/api/v1/inbox/".length);
      const route = config.routes.find((candidate) => candidate.id === routeId);
      if (!isRelayId(routeId) || !route) {
        send(request, response, 404, { error: "no-route", message: "その送り先は登録されていません。" }, cors);
        return;
      }
      const ceiling = Math.min(config.maxBundleBytes + 1024 * 1024, RELAY_MAX_BODY_BYTES);
      const declared = Number(request.headers["content-length"]);
      // Checked before a byte is read: a chunked upload with no declared
      // length gets no free pass, it gets refused.
      if (!Number.isSafeInteger(declared) || declared <= 0 || declared > ceiling) {
        send(
          request,
          response,
          413,
          {
            error: "too-large",
            message: `一度に送れるのは ${Math.floor(ceiling / 1024 / 1024)} MB までです。`,
          },
          cors,
        );
        return;
      }

      let body: Buffer;
      try {
        body = await readBody(request, ceiling);
      } catch (error) {
        const reason = error instanceof Error ? error.message : "aborted";
        send(
          request,
          response,
          reason === "too-large" ? 413 : 400,
          {
            error: reason,
            message: reason === "too-large" ? "送られた量が上限を超えました。" : "通信が途中で切れました。",
          },
          cors,
        );
        return;
      }
      const actualDigest = createHash("sha256").update(body).digest("hex");
      if (!timingSafeEqual(actualDigest, auth.bodyDigest)) {
        log("WARN", `body digest mismatch from ${auth.device.id}`);
        send(request, response, 400, { error: "digest-mismatch", message: "送信の途中で内容が変わりました。" }, cors);
        return;
      }

      try {
        // Step 0, and it is a step: the upstream container's own SHA-256 has
        // to pass before the bundle inside it is looked at even once.
        const container = await unpackFile(new Uint8Array(body));
        if (!(await verifyFile(container))) {
          throw new Error("照合に失敗しました。");
        }
        const verified = await verifyBundle(container.bytes, bundleLimits);
        const written = writeBundle(route.path, verified.manifest, verified.files);
        log("INFO", `saved ${written.fileCount} files into route ${route.id}`);
        const saved: RelayInboxResponse = {
          saved: true,
          routeId: route.id,
          savedAs: written.savedAs,
          fileCount: written.fileCount,
          totalSize: written.totalSize,
          renamed: written.renamed,
        };
        send(request, response, 200, saved, cors);
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        log("WARN", `rejected a transfer into ${route.id}: ${message}`);
        send(request, response, 422, { error: "rejected", message }, cors);
      }
      return;
    }

    send(request, response, 404, { error: "not-found", message: "その要求は扱えません。" }, cors);
  }

  return server;
}

export const APP_VERSION: string = (() => {
  try {
    const pkg = JSON.parse(readFileSync(resolve(here, "..", "package.json"), "utf8")) as {
      version?: string;
    };
    return pkg.version ?? "0.0.0";
  } catch {
    return "0.0.0";
  }
})();

export { EMPTY_BODY_SHA256 };
