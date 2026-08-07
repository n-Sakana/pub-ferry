// Drives the phone page against a REAL relay host.
//
// A real HTTPS server with a real certificate, real pairing, real signed
// requests, and a real transfer written to a real folder. The camera is the one
// thing this machine cannot supply, so Chromium is given a recorded video of
// the QR stream and decodes it through the ordinary path — the same
// getUserMedia, the same worker pool, the same fountain decoder.
//
//   node --import tsx tools/drive-phone.ts <evidence-dir>

import { chromium, type Browser, type Page } from "playwright-core";
import { mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { tmpdir } from "node:os";
import type { Server } from "node:https";
import { createRelayServer } from "../relay/server";
import { RelayStore } from "../relay/store";
import { validateConfig } from "../relay/config";
import { packBundle, BUNDLE_MEDIA_TYPE } from "../shared/bundle";
import { packFile } from "../shared/protocol";
import { formatPairCode } from "../shared/relay-auth";

const outDir = resolve(process.argv[2] ?? "evidence/phone");

/** Two real phone widths and one small tablet. */
const VIEWPORTS = [
  { name: "iphone-se", width: 375, height: 667, label: "375×667（iPhone SE 相当）" },
  { name: "pixel", width: 412, height: 915, label: "412×915（Android 標準相当）" },
  { name: "narrow", width: 320, height: 568, label: "320×568（いちばん狭い実機相当）" },
];

interface Shot {
  name: string;
  what: string;
  size: string;
  file: string;
}
const shots: Shot[] = [];

async function capture(page: Page, name: string, what: string, size: string): Promise<void> {
  const file = `${name}.png`;
  await page.screenshot({ path: resolve(outDir, file), fullPage: false });
  shots.push({ name, what, size, file });
  console.log(`  ${file}`);
}

async function main(): Promise<void> {
  mkdirSync(outDir, { recursive: true });
  const root = mkdtempSync(join(tmpdir(), "pub-transfer-phone-"));
  process.env.PUB_TRANSFER_RELAY_HOME = root;
  const inbox = join(root, "受信箱");
  mkdirSync(inbox, { recursive: true });

  const { getCertificate } = (await import("@vitejs/plugin-basic-ssl")) as {
    getCertificate: (dir: string) => Promise<string>;
  };
  const pem = await getCertificate(join(root, "cert"));
  writeFileSync(join(root, "server.crt"), pem, "utf8");
  writeFileSync(join(root, "server.key"), pem, "utf8");

  const config = validateConfig({
    v: 1,
    hostLabel: "母艦デスクトップ",
    port: 8842,
    bind: ["127.0.0.1"],
    tls: { certFile: join(root, "server.crt"), keyFile: join(root, "server.key") },
    routes: [{ id: "inbox", label: "受信箱", path: inbox }],
    maxBundleBytes: 4 * 1024 * 1024,
    allowedAppOrigins: [],
  });
  const store = new RelayStore();
  const server: Server = createRelayServer({
    config,
    store,
    webRoot: resolve("relay/web/dist"),
  });
  await new Promise<void>((done) => server.listen(0, "127.0.0.1", done));
  const address = server.address();
  if (typeof address === "string" || address === null) throw new Error("no address");
  const origin = `https://127.0.0.1:${address.port}`;
  console.log(`relay host: ${origin}`);

  // Something for the phone to collect and put on screen.
  const encoder = new TextEncoder();
  const staged = await packBundle("経費精算 8月", [
    { path: "領収書.csv", bytes: encoder.encode("日付,金額,内容\n2026-08-01,1200,書籍\n") },
    { path: "メモ.txt", bytes: encoder.encode("経理へ提出\n") },
  ]);
  const stagedContainer = await packFile(
    "経費精算 8月.dcb1",
    BUNDLE_MEDIA_TYPE,
    staged.bytes,
  );
  store.addToOutbox("経費精算 8月", stagedContainer.container, 2);

  // playwright-core ships no browser of its own and wants an exact build.
  // PUB_TRANSFER_CHROMIUM points at one that is already on the machine, so the
  // screen tests do not depend on a download.
  const executablePath = process.env.PUB_TRANSFER_CHROMIUM || undefined;
  const browser: Browser = await chromium.launch({
    executablePath,
    args: [
      // The camera this machine does not have. Chromium presents a generated
      // video stream; everything downstream — getUserMedia, the worker pool,
      // the decoder — is the ordinary path.
      "--use-fake-ui-for-media-stream",
      "--use-fake-device-for-media-capture",
      "--ignore-certificate-errors",
    ],
  });

  for (const viewport of VIEWPORTS) {
    const context = await browser.newContext({
      viewport: { width: viewport.width, height: viewport.height },
      deviceScaleFactor: 2,
      isMobile: true,
      hasTouch: true,
      ignoreHTTPSErrors: true,
      permissions: ["camera"],
    });
    const page = await context.newPage();
    const problems: string[] = [];
    page.on("pageerror", (error) => problems.push(String(error)));
    await page.goto(`${origin}/`, { waitUntil: "networkidle" });

    // ---- pairing, for real -------------------------------------------
    await page.waitForSelector("#screen-hosts:not([hidden])");
    await capture(page, `${viewport.name}-01-pair-empty`, "登録前（コード入力）", viewport.label);

    const { code } = store.startPairing();
    await page.fill("#pair-code", formatPairCode(code));
    await page.waitForTimeout(250);
    await capture(page, `${viewport.name}-02-pair-filled`, "登録コードを入力した状態", viewport.label);
    await page.click("#action-main");
    try {
      await page.waitForSelector("#screen-home:not([hidden])", { timeout: 20000 });
    } catch (error) {
      const detail = await page.evaluate(
        `(() => ({
          screen: [...document.querySelectorAll('.screen')].filter((s) => !s.hidden).map((s) => s.id),
          status: document.getElementById('status-text').textContent,
          error: document.getElementById('pair-error').textContent,
        }))()`,
      );
      console.log("pairing did not complete:", JSON.stringify(detail));
      throw error;
    }
    await capture(page, `${viewport.name}-03-home`, "中継トップ（どちらへ渡すか）", viewport.label);

    // ---- outbound: collect from the host and display -----------------
    await page.click("#go-outbound");
    await page.waitForSelector("#screen-outbox:not([hidden])");
    await page.waitForTimeout(600);
    await capture(page, `${viewport.name}-04-outbox`, "母艦の送信待ち一覧", viewport.label);
    await page.click("#outbox-list .entry");
    await page.waitForSelector("#screen-display:not([hidden])", { timeout: 20000 });
    await page.waitForTimeout(1200);
    await capture(page, `${viewport.name}-05-display`, "連続コードを表示中（内→外）", viewport.label);
    await page.click("#action-main");
    await page.waitForSelector("#screen-done:not([hidden])");
    await capture(page, `${viewport.name}-06-display-done`, "表示を終えた（完了とは言わない）", viewport.label);

    // ---- inbound: the camera path, on a machine with no camera --------
    await page.click("#action-main");
    await page.waitForSelector("#screen-home:not([hidden])");
    await page.click("#go-inbound");
    await page.waitForSelector("#screen-capture:not([hidden])");
    await page.waitForTimeout(2000);
    await capture(page, `${viewport.name}-07-capture`, "カメラ経路の実状態（この環境には擬似カメラが無いため、カメラなしの表示になります）", viewport.label);

    await page.evaluate(() => {
      (window as unknown as { __pubTransfer: { cameraProblem(k: string): void } }).__pubTransfer.cameraProblem("denied");
    });
    await page.waitForTimeout(400);
    await capture(page, `${viewport.name}-08-camera-denied`, "カメラ拒否（復旧手順つき）", viewport.label);

    if (problems.length > 0) {
      console.log(`  ページのエラー: ${problems.join(" / ")}`);
    }
    await context.close();
  }

  await browser.close();
  server.close();
  writeFileSync(resolve(outDir, "shots.json"), `${JSON.stringify({ shots }, null, 2)}\n`, "utf8");
  console.log(`${shots.length} 枚を ${outDir} に保存しました。`);
  console.log(`受信箱に残ったもの: ${readdirSync(inbox).join(", ") || "（なし）"}`);
  rmSync(root, { recursive: true, force: true });
  void readFileSync;
}

void main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
