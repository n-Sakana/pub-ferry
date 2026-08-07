// End to end against a REAL server: a real HTTPS listener, real signatures, a
// real bundle through the real optical container, written to a real folder on
// disk, and the bytes on disk compared with the bytes that went in.
//
// Nothing here is stubbed. The only concession to a test environment is that
// the certificate is self-signed and the client is told not to reject it.

import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import type { Server } from "node:https";
import { mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync, existsSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { createRelayServer } from "../relay/server";
import { RelayStore } from "../relay/store";
import { validateConfig, type RelayConfig } from "../relay/config";
import { RelayClient, RelayRequestError, pairWithHost } from "../shared/relay-client";
import { packBundle, BUNDLE_MEDIA_TYPE } from "../shared/bundle";
import { packFile } from "../shared/protocol";
import { RELAY_HEADERS, RELAY_PATHS } from "../shared/relay-contract";

let root: string;
let server: Server;
let origin: string;
let store: RelayStore;
let config: RelayConfig;
let inbox: string;

import { insecureFetch } from "./helpers/https-fetch";

before(async () => {
  root = mkdtempSync(join(tmpdir(), "pub-transfer-relay-test-"));
  process.env.PUB_TRANSFER_RELAY_HOME = root;
  inbox = join(root, "inbox");
  mkdirSync(inbox, { recursive: true });

  const { getCertificate } = (await import("@vitejs/plugin-basic-ssl")) as {
    getCertificate: (dir: string) => Promise<string>;
  };
  const pem = await getCertificate(join(root, "cert-cache"));
  const certFile = join(root, "server.crt");
  const keyFile = join(root, "server.key");
  writeFileSync(certFile, pem, "utf8");
  writeFileSync(keyFile, pem, "utf8");

  config = validateConfig({
    v: 1,
    hostLabel: "テスト受信ホスト",
    // Not the port it actually listens on — the listener below asks the OS for
    // a free one. The config only has to be a valid config.
    port: 8842,
    bind: ["127.0.0.1"],
    tls: { certFile, keyFile },
    routes: [{ id: "inbox", label: "受信箱", path: inbox }],
    maxBundleBytes: 4 * 1024 * 1024,
    allowedAppOrigins: [],
  });
  store = new RelayStore();
  // An empty web root: this test is about the API, and the static handler's
  // fallback is exercised separately.
  server = createRelayServer({ config, store, webRoot: join(root, "web") });
  await new Promise<void>((done) => server.listen(0, "127.0.0.1", done));
  const address = server.address();
  if (typeof address === "string" || address === null) throw new Error("no address");
  origin = `https://127.0.0.1:${address.port}`;
});

after(() => {
  server?.close();
  if (root && existsSync(root)) rmSync(root, { recursive: true, force: true });
  delete process.env.PUB_TRANSFER_RELAY_HOME;
});

async function pairedClient(label = "テスト端末"): Promise<RelayClient> {
  const { code } = store.startPairing();
  const credentials = await pairWithHost(origin, code, label, insecureFetch);
  return new RelayClient(credentials, insecureFetch);
}

const encoder = new TextEncoder();

async function bundleContainer(
  label: string,
  files: { path: string; text: string }[],
): Promise<Uint8Array> {
  const { bytes } = await packBundle(
    label,
    files.map((file) => ({ path: file.path, bytes: encoder.encode(file.text) })),
  );
  return (await packFile(`${label}.dcb1`, BUNDLE_MEDIA_TYPE, bytes)).container;
}

test("an unpaired caller gets nothing, and is told what to do", async () => {
  const response = await insecureFetch(`${origin}${RELAY_PATHS.hello}`);
  assert.equal(response.status, 401);
  const body = (await response.json()) as { error: string; serverTime: number };
  assert.equal(body.error, "unauthorized");
  // The host's clock comes back so a phone with a wrong one can correct itself.
  assert.equal(typeof body.serverTime, "number");
});

test("a wrong pairing code is refused, and five wrong ones destroy the code", async () => {
  const { code } = store.startPairing();
  const wrong = code === "AAAAAAAAAA" ? "BBBBBBBBBB" : "AAAAAAAAAA";
  for (let attempt = 0; attempt < 5; attempt++) {
    await assert.rejects(() => pairWithHost(origin, wrong, "attacker", insecureFetch));
  }
  // The code is gone even though it had not expired — the owner prints
  // another one, the guesser gets no second window.
  assert.equal(store.pairingStatus().active, false);
  await assert.rejects(
    () => pairWithHost(origin, code, "attacker", insecureFetch),
    (error: unknown) => error instanceof RelayRequestError && error.code === "code-expired",
  );
});

test("a pairing code works exactly once", async () => {
  const { code } = store.startPairing();
  await pairWithHost(origin, code, "一台目", insecureFetch);
  await assert.rejects(() => pairWithHost(origin, code, "二台目", insecureFetch));
});

test("a paired device sees the host and its destinations, and no paths", async () => {
  const client = await pairedClient();
  const hello = await client.hello();
  assert.equal(hello.hostLabel, "テスト受信ホスト");
  assert.deepEqual(hello.routes.map((route) => route.id), ["inbox"]);
  assert.equal(hello.routes[0]!.label, "受信箱");
  assert.equal(hello.routes[0]!.writable, true);
  // Nothing in the response tells the phone where anything is on disk.
  assert.ok(!JSON.stringify(hello).includes(inbox));
});

test("a folder arrives on disk with every byte intact", async () => {
  const client = await pairedClient();
  const files = [
    { path: "notes.txt", text: "一行目\n二行目\n" },
    { path: "sub/data.json", text: '{"ok":true}' },
    { path: "sub/deep/empty.txt", text: "" },
  ];
  const result = await client.send("inbox", await bundleContainer("受信フォルダー", files));
  assert.equal(result.saved, true);
  assert.equal(result.fileCount, 3);
  assert.equal(result.savedAs, "受信フォルダー");

  for (const file of files) {
    const onDisk = readFileSync(join(inbox, result.savedAs, ...file.path.split("/")), "utf8");
    assert.equal(onDisk, file.text, `bytes differ for ${file.path}`);
  }
});

test("a second transfer of the same name does not overwrite the first", async () => {
  const client = await pairedClient();
  await client.send("inbox", await bundleContainer("同名", [{ path: "a.txt", text: "first" }]));
  const second = await client.send(
    "inbox",
    await bundleContainer("同名", [{ path: "a.txt", text: "second" }]),
  );
  assert.equal(second.savedAs, "同名 (2)");
  assert.equal(readFileSync(join(inbox, "同名", "a.txt"), "utf8"), "first");
  assert.equal(readFileSync(join(inbox, "同名 (2)", "a.txt"), "utf8"), "second");
});

test("a body altered after signing is refused, and nothing is written", async () => {
  const client = await pairedClient();
  const container = await bundleContainer("改竄", [{ path: "a.txt", text: "hello" }]);
  const before = readdirSync(inbox).length;
  // Signed over the original bytes; a different body arrives. This is the
  // check that the declared digest in the signature is compared against what
  // actually came down the wire.
  const headers = await signedRequest(client, "POST", RELAY_PATHS.inbox("inbox"), container);
  const tampered = Uint8Array.from(container);
  tampered[tampered.length - 1] ^= 0xff;
  const response = await insecureFetch(`${origin}${RELAY_PATHS.inbox("inbox")}`, {
    method: "POST",
    headers,
    body: tampered as unknown as BodyInit,
  });
  assert.equal(response.status, 400);
  assert.equal(((await response.json()) as { error: string }).error, "digest-mismatch");
  assert.equal(readdirSync(inbox).length, before, "something was written despite the tamper");
});

test("a replayed request is refused the second time", async () => {
  const client = await pairedClient();
  const container = await bundleContainer("再送", [{ path: "a.txt", text: "hello" }]);
  const headers = await signedRequest(client, "POST", RELAY_PATHS.inbox("inbox"), container);
  const first = await insecureFetch(`${origin}${RELAY_PATHS.inbox("inbox")}`, {
    method: "POST",
    headers,
    body: container as unknown as BodyInit,
  });
  assert.equal(first.status, 200);
  const replay = await insecureFetch(`${origin}${RELAY_PATHS.inbox("inbox")}`, {
    method: "POST",
    headers,
    body: container as unknown as BodyInit,
  });
  assert.equal(replay.status, 401);
  assert.equal(((await replay.json()) as { error: string }).error, "replayed");
});

test("a signature from one device does not work with another device's id", async () => {
  const a = await pairedClient("A");
  const b = await pairedClient("B");
  const headers = await signedRequest(a, "GET", RELAY_PATHS.hello);
  headers[RELAY_HEADERS.device] = (b as unknown as { credentials: { deviceId: string } })
    .credentials.deviceId;
  const response = await insecureFetch(`${origin}${RELAY_PATHS.hello}`, { headers });
  assert.equal(response.status, 401);
});

test("a signature for one path does not work on another", async () => {
  const client = await pairedClient();
  const headers = await signedRequest(client, "GET", RELAY_PATHS.hello);
  const response = await insecureFetch(`${origin}${RELAY_PATHS.outbox}`, { headers });
  assert.equal(response.status, 401);
});

test("a revoked device stops working immediately", async () => {
  const client = await pairedClient("捨てる端末");
  await client.hello();
  const id = (client as unknown as { credentials: { deviceId: string } }).credentials.deviceId;
  assert.equal(store.revokeDevice(id), true);
  await assert.rejects(
    () => client.hello(),
    (error: unknown) => error instanceof RelayRequestError && error.status === 401,
  );
});

test("a device can hand its own access back", async () => {
  const client = await pairedClient("自分で返す端末");
  await client.unpair();
  await assert.rejects(() => client.hello());
});

test("a body larger than the ceiling is refused before it is read", async () => {
  const client = await pairedClient();
  const headers = await signedRequest(client, "POST", RELAY_PATHS.inbox("inbox"), new Uint8Array(0));
  const response = await insecureFetch(`${origin}${RELAY_PATHS.inbox("inbox")}`, {
    method: "POST",
    headers: { ...headers, "content-length": String(900 * 1024 * 1024) },
    body: new Uint8Array(16) as unknown as BodyInit,
  });
  assert.equal(response.status, 413);
});

test("a bundle whose contents were altered fails verification, and nothing lands", async () => {
  const client = await pairedClient();
  const { bytes } = await packBundle("壊れた", [{ path: "a.txt", bytes: encoder.encode("hello") }]);
  const broken = Uint8Array.from(bytes);
  broken[broken.length - 1] ^= 0xff; // the file bytes no longer match their digest
  const container = (await packFile("壊れた.dcb1", BUNDLE_MEDIA_TYPE, broken)).container;
  const before = readdirSync(inbox).length;
  await assert.rejects(
    () => client.send("inbox", container),
    (error: unknown) => error instanceof RelayRequestError && error.status === 422,
  );
  assert.equal(readdirSync(inbox).length, before);
});

test("a transfer to a destination that is not registered is refused", async () => {
  const client = await pairedClient();
  const container = await bundleContainer("x", [{ path: "a.txt", text: "a" }]);
  await assert.rejects(
    () => client.send("nope", container),
    (error: unknown) => error instanceof RelayRequestError && error.status === 404,
  );
});

test("the outbox round-trips the exact bytes that were staged", async () => {
  const client = await pairedClient();
  const container = await bundleContainer("持ち出し", [{ path: "a.txt", text: "outbound" }]);
  const id = store.addToOutbox("持ち出し", container, 1);
  const list = await client.outbox();
  assert.ok(list.items.some((item) => item.id === id));
  const fetched = await client.fetchOutboxItem(id);
  assert.deepEqual(Array.from(fetched), Array.from(container));
});

test("a page from another site cannot reach the API with the user's browser", async () => {
  const client = await pairedClient();
  const headers = await signedRequest(client, "GET", RELAY_PATHS.hello);
  const response = await insecureFetch(`${origin}${RELAY_PATHS.hello}`, {
    headers: { ...headers, origin: "https://evil.example" },
  });
  assert.equal(response.status, 403);
});

test("pairing is never reachable cross-origin, whatever is allowlisted", async () => {
  const { code } = store.startPairing();
  const response = await insecureFetch(`${origin}${RELAY_PATHS.pair}`, {
    method: "POST",
    headers: { "content-type": "application/json", origin: "https://evil.example" },
    body: JSON.stringify({ code, deviceLabel: "evil" }),
  });
  assert.equal(response.status, 403);
  store.cancelPairing();
});

test("a clock that is far out is named as a clock problem, and the client recovers", async () => {
  const client = await pairedClient();
  const skewed = new RelayClient(
    (client as unknown as { credentials: never }).credentials,
    insecureFetch,
    -600, // this device thinks it is ten minutes ago
  );
  await assert.rejects(
    () => skewed.hello(),
    (error: unknown) => error instanceof RelayRequestError && error.code === "clock-skew",
  );
  // Having been told the host's time, the next attempt works.
  const hello = await skewed.hello();
  assert.equal(hello.hostLabel, "テスト受信ホスト");
});

/** Build the headers the client would send, without sending anything. */
async function signedRequest(
  client: RelayClient,
  method: "GET" | "POST",
  path: string,
  body?: Uint8Array,
): Promise<Record<string, string>> {
  const { credentials, clockOffset } = client as unknown as {
    credentials: { deviceId: string; secret: string };
    clockOffset: number;
  };
  const { fromBase64Url, generateNonce, sha256Hex, signRequest } = await import("../shared/relay-auth");
  const payload = body ?? new Uint8Array(0);
  const bodyDigest = await sha256Hex(payload);
  const timestamp = Math.floor(Date.now() / 1000) + (clockOffset ?? 0);
  const nonce = generateNonce();
  const signature = await signRequest(fromBase64Url(credentials.secret)!, {
    method,
    path,
    timestamp,
    nonce,
    bodyDigest,
  });
  return {
    [RELAY_HEADERS.device]: credentials.deviceId,
    [RELAY_HEADERS.timestamp]: String(timestamp),
    [RELAY_HEADERS.nonce]: nonce,
    [RELAY_HEADERS.bodyDigest]: bodyDigest,
    [RELAY_HEADERS.signature]: signature,
    ...(body ? { "content-type": "application/octet-stream" } : {}),
  };
}
