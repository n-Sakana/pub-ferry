// Drives the REAL desktop app.
//
// Not a copy of the page in a browser: this attaches to the WebView2 instance
// inside the running WPF window over the DevTools protocol and clicks the
// actual controls. What it exercises is what ships.
//
//   1. start the app:  pc\pub-transfer.ps1 -DebugPort 9333
//   2. run this:       node --import tsx tools/drive-desktop.ts <evidence-dir>
//
// The captures are of the PAGE, not of the screen rectangle the window occupies.
// That is deliberate and it is a privacy decision, not an aesthetic one: a
// screen-rectangle grab also records whatever happens to be behind and beside
// the window, and the published evidence is only allowed to contain this
// product. tools/capture-window.ps1 still exists for looking at the real window
// chrome locally, and what it produces is NOT publishable for that reason.
//
// Screens that need hardware this machine does not have (a camera) are put
// into their real state through the page's own error path, not by drawing a
// mock-up: __pubTransfer.cameraProblem() calls the same function a refused
// getUserMedia calls.

import { chromium, type CDPSession, type Page } from "playwright-core";
import { mkdirSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";

const port = Number(process.env.PUB_TRANSFER_DEBUG_PORT ?? 9333);
const outDir = resolve(process.argv[2] ?? "evidence/desktop");

interface Shot {
  name: string;
  what: string;
  size: string;
  file: string;
}
const shots: Shot[] = [];

let activePage: Page;
let cdp: CDPSession;
let currentSize = "1180x800 相当";

/** Force the page to a given CSS viewport. `scale` reproduces what Windows
 *  display scaling does to the page: the CSS viewport shrinks by that factor
 *  while the device pixels stay put. */
async function useViewport(width: number, height: number, scale: number, label: string): Promise<void> {
  await cdp.send("Emulation.setDeviceMetricsOverride", {
    width,
    height,
    deviceScaleFactor: scale,
    mobile: false,
  });
  currentSize = label;
  await activePage.waitForTimeout(350);
}

async function capture(name: string, what: string): Promise<void> {
  const file = `${name}.png`;
  await activePage.screenshot({ path: resolve(outDir, file) });
  shots.push({ name, what, size: currentSize, file });
  console.log(`  ${file}  (${currentSize})`);
}

async function settle(page: Page, ms = 500): Promise<void> {
  await page.waitForTimeout(ms);
}

async function main(): Promise<void> {
  mkdirSync(outDir, { recursive: true });
  const browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
  const context = browser.contexts()[0]!;
  const page = context.pages()[0] ?? (await context.waitForEvent("page"));
  activePage = page;
  cdp = await context.newCDPSession(page);
  await page.waitForSelector("#screen-home:not([hidden])", { timeout: 20000 });

  // Neutral identity before anything is captured.
  //
  // The app's defaults are the real machine name and the real user's Documents
  // folder, and both are rendered on screen — so a capture made with the
  // defaults publishes them. This goes through the product's own saveSettings
  // action rather than editing the file, so the host normalises the path the
  // way it normally would and the screens show what a real user would see.
  await page.evaluate(async () => {
    const api = window as unknown as {
      __pubTransfer: { setForTest?: (s: Record<string, unknown>) => Promise<void> };
    };
    await api.__pubTransfer.setForTest?.({
      deviceLabel: "事務所デスクトップ",
      destination: "D:/pub-transfer/受信箱",
    });
  });
  await settle(page, 400);

  const problems: string[] = [];
  page.on("console", (msg) => {
    if (msg.type() === "error") problems.push(msg.text());
  });
  page.on("pageerror", (error) => problems.push(String(error)));

  // ---- standard window, the whole path a person actually walks ----------
  await capture("01-home", "入口（送る／受け取るの二択）");

  await page.click("[data-go='send-pick']");
  await page.waitForSelector("#screen-send-pick:not([hidden])");
  await settle(page);
  await capture("02-send-pick-empty", "送る・まだ何も選んでいない（主操作は操作不能＋理由）");

  // A folder to send, prepared here so the pick is real rather than injected.
  const sample = resolve(outDir, "sample");
  mkdirSync(resolve(sample, "sub"), { recursive: true });
  writeFileSync(resolve(sample, "notes.txt"), "一行目\n二行目\n三行目\n".repeat(40), "utf8");
  writeFileSync(resolve(sample, "sub", "data.json"), JSON.stringify({ ok: true, n: 42 }, null, 2), "utf8");
  writeFileSync(resolve(sample, "sub", "empty.txt"), "", "utf8");

  // The OS file dialog cannot be clicked through the page, so the folder is
  // staged by path. Everything downstream of the dialog is the real path: the
  // same walk, the same exclusions, the same bytes, the same bundle, the same
  // fountain-coded stream on the same canvas.
  await page.evaluate(async (folder: string) => {
    const api = window as unknown as {
      __pubTransfer: { pickForTest(path: string): Promise<void> };
    };
    await api.__pubTransfer.pickForTest(folder);
  }, sample);
  await page.waitForSelector("#pick-summary:not([hidden])");
  await settle(page);
  await capture("02b-send-pick-chosen", "送る・フォルダーを選んだ（件数・合計・所要目安）");

  await page.click("#action-main");
  await page.waitForSelector("#screen-send-show:not([hidden])", { timeout: 20000 });
  await settle(page, 1200);
  await capture("02c-send-showing", "送る・表示中（コードが主役、右は細いレール）");

  await page.click("#action-main"); // 表示を終わる
  await page.waitForSelector("#screen-send-done:not([hidden])");
  await settle(page);
  await capture("02d-send-done", "送る・表示を終えた（完了とは言わない）");

  await page.click("#open-settings");
  await page.waitForSelector("#screen-settings:not([hidden])");
  await settle(page);
  await capture("03-settings", "設定（保存先・呼び名・トグル・読み取り機）");

  await page.click("#action-back");
  await page.waitForSelector("#screen-home:not([hidden])");
  await page.click("[data-go='receive-choose']");
  await page.waitForSelector("#screen-receive-choose:not([hidden])");
  await settle(page);
  await capture("04-receive-choose", "受け取り方の選択（このPCはカメラなし）");

  // The real "no camera" state on this machine, through the real error path.
  await page.evaluate(() => {
    (window as unknown as { __pubTransfer: { cameraProblem(kind: string): void } }).__pubTransfer.cameraProblem("none");
  });
  await settle(page);
  await capture("05-camera-none", "カメラなし（警告色にしない／リーダーへ誘導）");

  await page.evaluate(() => {
    (window as unknown as { __pubTransfer: { cameraProblem(kind: string): void } }).__pubTransfer.cameraProblem("denied");
  });
  await settle(page);
  await capture("06-camera-denied", "カメラ拒否（復旧手順つき）");

  // ---- one frame at a time, driven by real frames -----------------------
  await page.evaluate(() => {
    (window as unknown as { __pubTransfer: { goto(name: string): void } }).__pubTransfer.goto("receive-choose");
  });
  await page.click("#start-reader");
  await page.waitForSelector("#screen-receive-reader:not([hidden])");
  await settle(page);
  await capture("07-reader-empty", "リーダー受信・まだ 1 枚も読んでいない");

  const frames = JSON.parse(process.env.PUB_TRANSFER_SLOW_FRAMES ?? "[]") as string[];
  if (frames.length > 0) {
    // Every frame but a couple, so the missing-number display is real.
    for (let index = 0; index < frames.length; index++) {
      if (index === 2 || index === 5) continue;
      await page.fill("#reader-input", frames[index]!);
      await page.press("#reader-input", "Enter");
    }
    await settle(page);
    await capture("08-reader-partial", "リーダー受信・欠番あり（番号を名指し）");

    for (const index of [2, 5]) {
      await page.fill("#reader-input", frames[index]!);
      await page.press("#reader-input", "Enter");
    }
    await page.waitForSelector("#screen-receive-confirm:not([hidden]), #screen-saved:not([hidden]), #screen-failed:not([hidden])", { timeout: 15000 });
    await settle(page);
    const confirmVisible = await page.isVisible("#screen-receive-confirm");
    await capture(
      confirmVisible ? "09-confirm" : "09-after-reader",
      confirmVisible ? "中身を確かめて保存" : "リーダー受信の結果",
    );
  }

  // ---- narrow window ----------------------------------------------------
  // The CSS viewport a 900x680 window gives, minus the chrome the OS draws.
  await useViewport(884, 636, 1, "900x680 相当（狭幅）");
  await page.evaluate(() => {
    (window as unknown as { __pubTransfer: { goto(name: string): void } }).__pubTransfer.goto("receive-choose");
  });
  await settle(page);
  await capture("10-narrow-receive-choose", "狭いウィンドウ（最小幅）");

  await page.evaluate(() => {
    (window as unknown as { __pubTransfer: { goto(name: string): void } }).__pubTransfer.goto("send-pick");
  });
  await settle(page);
  await capture("11-narrow-send-pick", "狭いウィンドウ・送る（2 カラムが 1 カラムに落ちる）");

  // ---- 125% equivalent --------------------------------------------------
  // Windows display scaling makes the page's CSS viewport 1/1.25 of the device
  // pixels. Emulating the device pixel ratio reproduces exactly that effect on
  // the page; the window chrome is the OS's and is not part of the emulation.
  await useViewport(1092, 614, 1.25, "125% 相当（1366x768 の実機）");
  for (const [screen, label] of [
    ["send-pick", "送る・選ぶ"],
    ["receive-choose", "受け取り方の選択"],
    ["settings", "設定"],
  ] as const) {
    await page.evaluate((name: string) => {
      (window as unknown as { __pubTransfer: { goto(n: string): void } }).__pubTransfer.goto(name);
    }, screen);
    await settle(page, 350);
    await capture(`12-scale125-${screen}`, `${label}（表示倍率 125% 相当）`);
  }
  await cdp.send("Emulation.clearDeviceMetricsOverride");
  writeFileSync(
    resolve(outDir, "shots.json"),
    `${JSON.stringify({ shots, consoleProblems: problems }, null, 2)}\n`,
    "utf8",
  );
  console.log(`${shots.length} 枚を ${outDir} に保存しました。`);
  if (problems.length > 0) {
    console.log("ページのコンソールに問題が出ています:");
    for (const problem of problems) console.log("  " + problem);
  }
  await browser.close();
}

void main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
