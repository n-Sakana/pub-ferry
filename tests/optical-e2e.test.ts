// The whole path, through light.
//
// A folder is bundled, hashed, fountain-coded, drawn as QR frames, written to a
// video file, presented to Chromium as a webcam, decoded by the page's own
// worker pool and fountain decoder, verified, handed to a real relay host over
// a real signed request, and written to a real folder. Then the bytes on disk
// are compared with the bytes that went in.
//
// The only substitution is the light itself. Everything from getUserMedia
// onwards is the code that ships.
//
// Skipped, loudly, when no Chromium is available — a test that silently does
// nothing is worse than no test.

import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { tmpdir, homedir } from "node:os";
import type { Server } from "node:https";
import { createRelayServer } from "../relay/server";
import { RelayStore } from "../relay/store";
import { validateConfig } from "../relay/config";
import { buildSamplePayload, writeQrVideo } from "../tools/make-qr-video";

const WEB_ROOT = resolve("relay/web/dist");

/** A Chromium already on this machine. playwright-core ships none. */
function findChromium(): string | null {
  if (process.env.PUB_TRANSFER_CHROMIUM && existsSync(process.env.PUB_TRANSFER_CHROMIUM)) {
    return process.env.PUB_TRANSFER_CHROMIUM;
  }
  const base = join(homedir(), "AppData", "Local", "ms-playwright");
  if (!existsSync(base)) return null;
  const builds = readdirSync(base)
    .filter((name) => name.startsWith("chromium-"))
    .sort()
    .reverse();
  for (const build of builds) {
    const candidate = join(base, build, "chrome-win64", "chrome.exe");
    if (existsSync(candidate)) return candidate;
  }
  return null;
}

const chromiumPath = findChromium();
let fakeCameraProblem: string | null = null;

/**
 * Does this machine's Chromium actually present a fake camera?
 *
 * Asked rather than assumed. On the machine this was written on it does not:
 * `--use-fake-device-for-media-capture` is accepted and getUserMedia still
 * answers NotFoundError, so the whole test would otherwise spend two minutes
 * waiting for a decode that can never happen and then report a failure that
 * says nothing about the product. Probing turns that into a skip with the
 * reason attached, which is the honest result.
 */
async function probeFakeCamera(executablePath: string, certDir: string): Promise<string | null> {
  const { chromium } = (await import("playwright-core")) as typeof import("playwright-core");
  const { createServer } = (await import("node:https")) as typeof import("node:https");
  const { getCertificate } = (await import("@vitejs/plugin-basic-ssl")) as {
    getCertificate: (dir: string) => Promise<string>;
  };
  const pem = await getCertificate(certDir);
  const probeServer = createServer({ cert: pem, key: pem }, (_request, response) => {
    response.writeHead(200, { "content-type": "text/html; charset=utf-8" });
    response.end("<!doctype html><title>probe</title>");
  });
  await new Promise<void>((done) => probeServer.listen(0, "127.0.0.1", done));
  const probePort = (probeServer.address() as { port: number }).port;
  const browser = await chromium.launch({
    executablePath,
    args: [
      "--use-fake-ui-for-media-stream",
      "--use-fake-device-for-media-capture",
      "--ignore-certificate-errors",
    ],
  });
  try {
    const context = await browser.newContext({ ignoreHTTPSErrors: true, permissions: ["camera"] });
    const page = await context.newPage();
    await page.goto(`https://127.0.0.1:${probePort}/`);
    const result = (await page.evaluate(
      `(async () => {
        try {
          const s = await navigator.mediaDevices.getUserMedia({ video: true });
          s.getTracks().forEach((t) => t.stop());
          return null;
        } catch (e) { return e.name + ': ' + e.message; }
      })()`,
    )) as string | null;
    return result;
  } finally {
    await browser.close();
    probeServer.close();
  }
}

let ready = false;

/** Why this did not run. A skip with no reason is a test nobody notices is
 *  gone, and this one covers the part of the product that needs a camera. */
function reasonNotRun(): string {
  if (chromiumPath === null) {
    return "Chromium が見つかりません（PUB_TRANSFER_CHROMIUM に実行ファイルを指定してください）";
  }
  if (!existsSync(join(WEB_ROOT, "index.html"))) {
    return `${WEB_ROOT} がありません（npm run build:relay-web を先に実行してください）`;
  }
  return (
    `この環境の Chromium は擬似カメラを提供しません（getUserMedia の応答: ${fakeCameraProblem}）。` +
    "カメラの使える環境で実行すると、この試験は光学経路をそのまま通します。"
  );
}

let root: string;
let inbox: string;
let server: Server;
let origin: string;
let store: RelayStore;
let video: string;

before(async () => {
  if (chromiumPath === null || !existsSync(join(WEB_ROOT, "index.html"))) return;
  const probeRoot = mkdtempSync(join(tmpdir(), "pub-transfer-camera-probe-"));
  try {
    fakeCameraProblem = await probeFakeCamera(chromiumPath, join(probeRoot, "cert"));
  } finally {
    rmSync(probeRoot, { recursive: true, force: true });
  }
  if (fakeCameraProblem !== null) return;
  ready = true;
  root = mkdtempSync(join(tmpdir(), "pub-transfer-optical-"));
  process.env.PUB_TRANSFER_RELAY_HOME = root;
  inbox = join(root, "受信箱");
  mkdirSync(inbox, { recursive: true });

  const { getCertificate } = (await import("@vitejs/plugin-basic-ssl")) as {
    getCertificate: (dir: string) => Promise<string>;
  };
  const pem = await getCertificate(join(root, "cert"));
  writeFileSync(join(root, "server.crt"), pem, "utf8");
  writeFileSync(join(root, "server.key"), pem, "utf8");

  const config = validateConfig({
    v: 1,
    hostLabel: "母艦",
    port: 8842,
    bind: ["127.0.0.1"],
    tls: { certFile: join(root, "server.crt"), keyFile: join(root, "server.key") },
    routes: [{ id: "inbox", label: "受信箱", path: inbox }],
    maxBundleBytes: 4 * 1024 * 1024,
    allowedAppOrigins: [],
  });
  store = new RelayStore();
  server = createRelayServer({ config, store, webRoot: WEB_ROOT });
  await new Promise<void>((done) => server.listen(0, "127.0.0.1", done));
  const address = server.address();
  if (typeof address === "string" || address === null) throw new Error("no address");
  origin = `https://127.0.0.1:${address.port}`;

  video = join(root, "stream.y4m");
  await writeQrVideo(video, 20);
});

after(() => {
  server?.close();
  if (root && existsSync(root)) rmSync(root, { recursive: true, force: true });
  delete process.env.PUB_TRANSFER_RELAY_HOME;
});

test("a folder crosses the optical channel and lands on disk byte for byte", async (t) => {
  if (!ready) {
    t.skip(reasonNotRun());
    return;
  }
  const { chromium } = (await import("playwright-core")) as typeof import("playwright-core");
  const { formatPairCode } = await import("../shared/relay-auth");
  const expected = await buildSamplePayload();

  const browser = await chromium.launch({
    executablePath: chromiumPath!,
    args: [
      "--use-fake-ui-for-media-stream",
      "--use-fake-device-for-media-capture",
      `--use-file-for-fake-video-capture=${video}`,
      "--ignore-certificate-errors",
    ],
  });
  try {
    const context = await browser.newContext({
      viewport: { width: 412, height: 915 },
      isMobile: true,
      hasTouch: true,
      ignoreHTTPSErrors: true,
      permissions: ["camera"],
    });
    const page = await context.newPage();
    const failures: string[] = [];
    page.on("pageerror", (error) => failures.push(String(error)));
    await page.goto(`${origin}/`, { waitUntil: "networkidle" });

    // Pair for real.
    await page.waitForSelector("#screen-hosts:not([hidden])");
    const { code } = store.startPairing();
    await page.fill("#pair-code", formatPairCode(code));
    await page.click("#action-main");
    await page.waitForSelector("#screen-home:not([hidden])", { timeout: 30000 });

    // Read the "screen" — really the video — through the ordinary camera path.
    await page.click("#go-inbound");
    await page.waitForSelector("#screen-deliver:not([hidden])", { timeout: 120000 });

    const summary = await page.evaluate(
      `(() => ({
        label: document.getElementById('deliver-label').textContent,
        count: document.getElementById('deliver-count').textContent,
      }))()`,
    );
    assert.equal((summary as { label: string }).label, expected.label);

    // Hand it to the host over a real signed request.
    await page.click("#route-list .entry");
    await page.waitForSelector("#screen-done:not([hidden])", { timeout: 30000 });

    assert.deepEqual(failures, [], "the page reported errors");

    for (const file of expected.files) {
      const target = join(inbox, expected.label, ...file.path.split("/"));
      assert.ok(existsSync(target), `${file.path} was not written`);
      assert.deepEqual(
        Array.from(new Uint8Array(readFileSync(target))),
        Array.from(file.bytes),
        `bytes differ for ${file.path}`,
      );
    }
  } finally {
    await browser.close();
  }
});
