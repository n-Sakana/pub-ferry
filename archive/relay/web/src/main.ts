// The phone's page: the relay.
//
// It is served BY the host it talks to, which is not a detail. A browser will
// not let an https page reach an http address, and a Tailscale address is not
// an origin browsers treat as trustworthy — so a page hosted anywhere else
// simply could not call this API. Being same-origin also means no CORS, and
// the pairing secret is stored under the origin of exactly one host.
//
// The phone never sees a path on any machine. It sees names its owner chose.

import "./phone.css";
import {
  BUNDLE_MEDIA_TYPE,
  describeBundle,
  packBundle,
  verifyBundle,
  type BundleManifest,
} from "../../../shared/bundle";
import { packFile, unpackFile, verifyFile } from "../../../shared/protocol";
import { OpticalStream } from "../../../shared/optical-stream";
import { CameraError, OpticalCapture } from "../../../shared/optical-capture";
import { bytesJa, durationJa } from "../../../shared/format-ja";
import { normalizePairCode } from "../../../shared/relay-auth";
import { RelayClient, RelayRequestError, pairWithHost, type RelayCredentials } from "../../../shared/relay-client";
import type { RelayOutboxItem, RelayRoute } from "../../../shared/relay-contract";

const FRAME_SIZES = [1000, 1465, 2331, 2953];
const STORAGE_KEY = "pub-ferry.relay.credentials";

type ScreenName =
  | "home"
  | "outbox"
  | "display"
  | "capture"
  | "deliver"
  | "done"
  | "failed"
  | "hosts";

const el = <T extends HTMLElement>(id: string): T => document.getElementById(id) as T;
const screens = new Map<ScreenName, HTMLElement>();
for (const section of document.querySelectorAll<HTMLElement>(".screen")) {
  screens.set(section.id.replace(/^screen-/, "") as ScreenName, section);
}

const state = {
  screen: "home" as ScreenName,
  credentials: null as RelayCredentials | null,
  client: null as RelayClient | null,
  routes: [] as RelayRoute[],
  hostLabel: "",
  stream: null as OpticalStream | null,
  capture: null as OpticalCapture | null,
  received: null as { manifest: BundleManifest; container: Uint8Array } | null,
  framesShown: 0,
};

// ---- chrome ----------------------------------------------------------------

const statusDot = el("status-dot");
const statusText = el("status-text");
const actionMain = el<HTMLButtonElement>("action-main");
const actionBack = el<HTMLButtonElement>("action-back");

function setStatus(text: string, tone: "" | "busy" | "done" | "error" = ""): void {
  statusText.textContent = text;
  if (tone) statusDot.dataset.tone = tone;
  else delete statusDot.dataset.tone;
}

function setProgress(fraction: number, tone: "" | "done" | "error" = ""): void {
  el("progress-fill").style.width = `${Math.max(0, Math.min(1, fraction)) * 100}%`;
  const fill = el("progress-fill");
  if (tone) fill.dataset.tone = tone;
  else delete fill.dataset.tone;
}

function setActions(
  main?: { label: string; run: () => void; disabledBecause?: string },
  back?: { label: string; run: () => void },
): void {
  actionMain.hidden = !main;
  actionMain.onclick = null;
  actionMain.removeAttribute("title");
  if (main) {
    actionMain.textContent = main.label;
    actionMain.disabled = Boolean(main.disabledBecause);
    if (main.disabledBecause) actionMain.title = main.disabledBecause;
    else actionMain.onclick = main.run;
  }
  actionBack.hidden = !back;
  if (back) {
    actionBack.textContent = back.label;
    actionBack.onclick = back.run;
  }
}

/**
 * Which way this is going, in three redundant channels: two words, the order
 * of the stops, and a mark on the one that is this phone.
 *
 * The relay's whole job is "receive, then send", so a verb cannot distinguish
 * the two directions — and getting them the wrong way round is the most
 * expensive mistake this product can make. Hence a fixed, always-visible
 * indicator rather than a screen title.
 */
function setDirection(kind: "outbound" | "inbound" | null, at: 0 | 1 | 2 = 1): void {
  const box = el("direction");
  box.hidden = kind === null;
  if (kind === null) return;
  const host = state.hostLabel || "母艦";
  const nodes =
    kind === "outbound"
      ? [`${host}`, "このスマホ", "向かいの画面"]
      : ["向かいの画面", "このスマホ", `${host}`];
  el("direction-summary").textContent = kind === "outbound" ? "内 → 外" : "外 → 内";
  const path = el("direction-path");
  path.replaceChildren();
  nodes.forEach((label, index) => {
    if (index > 0) {
      const arrow = document.createElement("li");
      arrow.className = "direction-arrow";
      arrow.setAttribute("aria-hidden", "true");
      arrow.textContent = "→";
      path.append(arrow);
    }
    const item = document.createElement("li");
    item.className = "direction-node";
    item.dataset.here = String(index === at);
    item.dataset.done = String(index < at);
    item.textContent = label;
    path.append(item);
  });
}

function show(name: ScreenName): void {
  state.screen = name;
  for (const [id, section] of screens) section.hidden = id !== name;
  el("main").scrollTop = 0;
}

// ---- credentials ------------------------------------------------------------

function loadCredentials(): RelayCredentials | null {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (!stored) return null;
    const parsed = JSON.parse(stored) as RelayCredentials;
    if (typeof parsed?.deviceId !== "string" || typeof parsed.secret !== "string") return null;
    // The origin is pinned to this page's own: a stored credential from a
    // different host has no business being used against this one.
    return { ...parsed, origin: location.origin };
  } catch {
    return null;
  }
}

function storeCredentials(credentials: RelayCredentials | null): void {
  if (credentials) localStorage.setItem(STORAGE_KEY, JSON.stringify(credentials));
  else localStorage.removeItem(STORAGE_KEY);
  state.credentials = credentials;
  state.client = credentials ? new RelayClient(credentials) : null;
}

// ---- entry --------------------------------------------------------------------

async function boot(): Promise<void> {
  el<HTMLButtonElement>("open-hosts").onclick = () => void openHosts();
  el<HTMLButtonElement>("go-outbound").onclick = () => void openOutbox();
  el<HTMLButtonElement>("go-inbound").onclick = () => void startCapture();
  el<HTMLButtonElement>("unpair").onclick = () => void unpair();
  el<HTMLInputElement>("pair-code").addEventListener("input", updatePairAction);

  storeCredentials(loadCredentials());
  if (!state.client) {
    await openHosts();
    return;
  }
  try {
    const hello = await state.client.hello();
    state.hostLabel = hello.hostLabel;
    state.routes = hello.routes;
    el("host-label").textContent = hello.hostLabel;
    goHome();
  } catch (error) {
    if (error instanceof RelayRequestError && error.status === 401) {
      storeCredentials(null);
      await openHosts();
      el("pair-error").replaceChildren(
        alertBox("warn", "登録がやり直しになりました", "母艦側で登録が取り消されたようです。もう一度登録してください。"),
      );
      return;
    }
    goHome();
    el("home-notice").replaceChildren(
      alertBox("error", "母艦につながりません", message(error), [
        "母艦で受信が動いているか確かめる",
        "この端末が Tailscale につながっているか確かめる",
      ]),
    );
  }
}

function goHome(): void {
  stopEverything();
  show("home");
  setDirection(null);
  setProgress(0);
  el("home-notice").replaceChildren();
  const paired = state.client !== null;
  el<HTMLButtonElement>("go-outbound").disabled = false;
  el<HTMLButtonElement>("go-inbound").disabled = false;
  setStatus(
    paired ? `${state.hostLabel}に登録済みです` : "まずこの端末を登録します",
    paired ? "" : "error",
  );
  setActions(undefined, undefined);
}

// ---- outbound: host → phone → far screen ---------------------------------------

async function openOutbox(): Promise<void> {
  if (!(await requirePairing())) return;
  show("outbox");
  setDirection("outbound", 0);
  setStatus("送信待ちを読み込んでいます…", "busy");
  setActions(undefined, { label: "戻る", run: goHome });
  try {
    const list = await state.client!.outbox();
    const box = el("outbox-list");
    box.replaceChildren();
    el("outbox-empty").hidden = list.items.length > 0;
    for (const item of list.items) box.append(outboxRow(item));
    setStatus(
      list.items.length > 0 ? "流すものを選びます" : "母艦に送信待ちがありません",
    );
  } catch (error) {
    setStatus(message(error), "error");
    el("outbox-list").replaceChildren(alertBox("error", "読み込めませんでした", message(error)));
  }
}

function outboxRow(item: RelayOutboxItem): HTMLElement {
  const button = document.createElement("button");
  button.className = "entry";
  button.type = "button";
  button.innerHTML = "";
  const body = document.createElement("span");
  body.className = "entry-body";
  const title = document.createElement("span");
  title.className = "entry-title";
  title.textContent = item.label;
  const note = document.createElement("span");
  note.className = "entry-note";
  note.textContent = `${item.fileCount.toLocaleString()} 個のファイル・${bytesJa(item.totalSize)}`;
  body.append(title, note);
  button.append(body);
  button.onclick = () => void displayItem(item);
  return button;
}

async function displayItem(item: RelayOutboxItem): Promise<void> {
  setStatus("母艦から受け取っています…", "busy");
  try {
    const container = await state.client!.fetchOutboxItem(item.id);
    show("display");
    setDirection("outbound", 1);
    const stream = new OpticalStream(
      el<HTMLCanvasElement>("display-canvas"),
      container,
      { framesPerSecond: 20, bytesPerFrame: 1465 },
      FRAME_SIZES,
    );
    state.stream = stream;
    const started = stream.start();
    fitStage();
    el("display-caption").textContent =
      `${item.label} — ${item.fileCount.toLocaleString()} 個・${bytesJa(item.totalSize)}` +
      ` — 全部で約 ${durationJa(started.minimumSeconds * 1.25)}`;
    setStatus("表示しています。向かいの画面が読み終わるまで消さないでください", "busy");
    state.framesShown = 0;
    tickShown();
    setActions({ label: "表示を終わる", run: stopDisplay }, undefined);
  } catch (error) {
    setStatus(message(error), "error");
    showFailure("流せませんでした", message(error), ["もう一度選び直してください。"]);
  }
}

let shownTimer = 0;
function tickShown(): void {
  window.clearInterval(shownTimer);
  shownTimer = window.setInterval(() => {
    if (!state.stream) return;
    state.framesShown = state.stream.framesShown;
    el("display-shown").textContent = `${state.framesShown.toLocaleString()} 枚`;
  }, 500);
}

function stopDisplay(): void {
  window.clearInterval(shownTimer);
  state.stream?.stop();
  state.stream = null;
  show("done");
  setDirection("outbound", 2);
  el("done-title").textContent = "表示を終わりました";
  el("done-alert").dataset.tone = "warn";
  el("done-headline").textContent = "受け取れたかどうかは、この画面ではわかりません";
  el("done-body").textContent =
    "向かいのパソコンの画面に「保存しました」が出ていることを確かめてください。";
  setStatus("表示を終わりました");
  setActions({ label: "最初の画面へ", run: goHome }, undefined);
}

function fitStage(): void {
  const stream = state.stream;
  if (!stream) return;
  const stage = el("display-stage");
  const style = getComputedStyle(stage);
  const inner =
    stage.clientWidth -
    Number.parseFloat(style.paddingLeft) -
    Number.parseFloat(style.paddingRight);
  stream.resize(Math.max(160, inner));
}

// ---- inbound: far screen → phone → host -------------------------------------------

async function startCapture(): Promise<void> {
  if (!(await requirePairing())) return;
  show("capture");
  setDirection("inbound", 1);
  el("capture-error").replaceChildren();
  el("viewfinder").hidden = false;
  el("capture-progress").hidden = false;
  el("capture-placeholder").hidden = false;
  setStatus("カメラを準備しています…", "busy");
  setActions({ label: "読み取りをやめる", run: goHome }, undefined);

  const capture = new OpticalCapture({
    video: el<HTMLVideoElement>("capture-video"),
    createWorker: () => new Worker(new URL("./decode-worker.ts", import.meta.url), { type: "module" }),
    handlers: {
      onProgress: (progress) => {
        el("capture-placeholder").hidden = true;
        el("capture-percent").textContent = String(Math.round(progress.fraction * 100));
        el("capture-eta").textContent =
          progress.etaSeconds === undefined ? "計測中" : durationJa(progress.etaSeconds);
        el("capture-size").textContent = bytesJa(progress.bytesTotal);
        el("capture-hint").textContent = "";
        setProgress(progress.fraction);
        setStatus("受け取っています", "busy");
      },
      onQuiet: (seconds) => {
        if (seconds < 4) return;
        el("capture-hint").textContent =
          "まだ何も読めていません。向かいの画面全体が映るまで下がってみてください。";
      },
      onComplete: (payload) => void onCaptured(payload),
    },
  });
  state.capture = capture;
  try {
    await capture.start();
    el("capture-placeholder").hidden = true;
    setStatus("向かいの画面にカメラを向けてください", "busy");
  } catch (error) {
    state.capture = null;
    showCameraProblem(error);
  }
}

function showCameraProblem(error: unknown): void {
  const problem = error instanceof CameraError ? error.problem : "failed";
  el("viewfinder").hidden = true;
  el("capture-progress").hidden = true;
  const advice: Record<string, string[]> = {
    denied: [
      "ブラウザーのアドレスバーの鍵アイコンからカメラを許可する",
      "端末の設定でこのサイトのカメラを許可する",
      "許可したあと、もう一度この画面を開く",
    ],
    none: ["カメラのある端末で開いてください。"],
    busy: ["カメラを使っている別のアプリを閉じてから、もう一度開いてください。"],
    insecure: ["母艦が出している https の URL から開いてください。"],
    failed: ["もう一度開いてみてください。"],
  };
  el("capture-error").replaceChildren(
    alertBox(
      problem === "none" ? "warn" : "error",
      problem === "none" ? "この端末にカメラがありません" : "カメラを使えませんでした",
      message(error),
      advice[problem] ?? advice.failed!,
    ),
  );
  setStatus("カメラを使えませんでした", problem === "none" ? "" : "error");
  setActions({ label: "最初の画面へ", run: goHome }, undefined);
}

async function onCaptured(payload: Uint8Array): Promise<void> {
  state.capture = null;
  setStatus("内容を確かめています…", "busy");
  try {
    const container = await unpackFile(payload);
    // The container's own SHA-256 first. Nothing inside it is looked at until
    // it passes.
    if (!(await verifyFile(container))) throw new Error("受け取った中身が一致しません。");
    const manifest =
      container.type === BUNDLE_MEDIA_TYPE
        ? (await verifyBundle(container.bytes)).manifest
        : await wrapSingleFile(container.name, container.bytes);
    const forward =
      container.type === BUNDLE_MEDIA_TYPE
        ? payload
        : await rebundle(container.name, container.bytes);
    state.received = { manifest, container: forward };
  } catch (error) {
    showFailure("受け取った中身が、送られたものと一致しません", message(error), [
      "向かいの画面でもう一度表示してもらう",
      "画面の明るさを上げ、カメラを近づける",
    ]);
    return;
  }
  renderDeliver();
}

/** A single file from the upstream sender is wrapped so the host receives the
 *  same shape either way. */
async function wrapSingleFile(name: string, bytes: Uint8Array): Promise<BundleManifest> {
  const { manifest } = await packBundle(stripExtension(name) || "受け取ったもの", [
    { path: name, bytes },
  ]);
  return manifest;
}

async function rebundle(name: string, bytes: Uint8Array): Promise<Uint8Array> {
  const label = stripExtension(name) || "受け取ったもの";
  const { bytes: bundle } = await packBundle(label, [{ path: name, bytes }]);
  return (await packFile(`${label}.dcb1`, BUNDLE_MEDIA_TYPE, bundle)).container;
}

function stripExtension(name: string): string {
  return name.replace(/\.[^.]+$/, "");
}

function renderDeliver(): void {
  const received = state.received;
  if (!received) return;
  show("deliver");
  setDirection("inbound", 1);
  setProgress(1);
  setStatus("渡す先を選びます");
  el("deliver-label").textContent = received.manifest.label;
  el("deliver-count").textContent = describeBundle(received.manifest);
  el("deliver-size").textContent = bytesJa(received.manifest.totalSize);
  const list = el("route-list");
  list.replaceChildren();
  if (state.routes.length === 0) {
    list.append(
      alertBox("warn", "送り先が登録されていません", "母艦のパソコンで受け取り先を登録してください。", [
        'npm run relay -- route add "受信箱" <フォルダー>',
      ]),
    );
  }
  for (const route of state.routes) {
    const button = document.createElement("button");
    button.className = "entry";
    button.type = "button";
    button.disabled = !route.writable;
    const body = document.createElement("span");
    body.className = "entry-body";
    const title = document.createElement("span");
    title.className = "entry-title";
    title.textContent = route.label;
    const note = document.createElement("span");
    note.className = "entry-note";
    note.textContent = route.writable
      ? `${state.hostLabel} に保存します`
      : "いま書き込めません。母艦側で確かめてください。";
    body.append(title, note);
    button.append(body);
    if (route.writable) button.onclick = () => void deliver(route);
    list.append(button);
  }
  setStatus("渡す先を選びます");
  setActions(undefined, { label: "捨てる", run: goHome });
}

async function deliver(route: RelayRoute): Promise<void> {
  const received = state.received;
  if (!received) return;
  setStatus(`${route.label} へ渡しています…`, "busy");
  el("deliver-error").replaceChildren();
  try {
    const result = await state.client!.send(route.id, received.container);
    show("done");
    setDirection("inbound", 2);
    setProgress(1, "done");
    el("done-title").textContent = "渡しました";
    el("done-alert").dataset.tone = "done";
    el("done-headline").textContent = `${state.hostLabel} の「${route.label}」に保存されました`;
    el("done-body").textContent =
      `${result.fileCount.toLocaleString()} 個のファイル・${bytesJa(result.totalSize)}` +
      `（${result.savedAs}）`;
    setStatus("渡しました", "done");
    setActions({ label: "最初の画面へ", run: goHome }, undefined);
    state.received = null;
  } catch (error) {
    // The received bytes are still held: a destination that will not take them
    // is a reason to pick another one, not to throw the transfer away.
    el("deliver-error").replaceChildren(
      alertBox("error", "渡せませんでした", message(error), [
        "別の送り先を選ぶ",
        "受け取った中身はこの画面が持っています",
      ]),
    );
    setStatus("渡せませんでした", "error");
  }
}

// ---- pairing --------------------------------------------------------------------

async function requirePairing(): Promise<boolean> {
  if (state.client) return true;
  await openHosts();
  return false;
}

async function openHosts(): Promise<void> {
  stopEverything();
  show("hosts");
  setDirection(null);
  const paired = state.credentials !== null;
  el("paired-card").hidden = !paired;
  el("pair-card").hidden = paired;
  if (paired) {
    el("paired-host").textContent = state.hostLabel || state.credentials!.hostLabel;
    const routes = el("paired-routes");
    routes.replaceChildren();
    for (const route of state.routes) {
      const row = document.createElement("div");
      row.className = "file-row";
      const name = document.createElement("span");
      name.className = "file-path";
      name.textContent = route.label;
      const status = document.createElement("span");
      status.className = "file-size";
      status.textContent = route.writable ? "使えます" : "書き込めません";
      row.append(name, status);
      routes.append(row);
    }
    setStatus("登録済みです");
    setActions(undefined, { label: "戻る", run: goHome });
    return;
  }
  setStatus("登録コードを入力してください", "error");
  updatePairAction();
}

function updatePairAction(): void {
  const raw = el<HTMLInputElement>("pair-code").value;
  const code = normalizePairCode(raw);
  setActions({
    label: "この端末を登録する",
    run: () => void submitPairing(code!),
    disabledBecause: code === null ? "登録コードを最後まで入力してください" : undefined,
  });
}

async function submitPairing(code: string): Promise<void> {
  el("pair-error").replaceChildren();
  setStatus("登録しています…", "busy");
  try {
    const label = navigator.userAgent.includes("Android")
      ? "Android のスマートフォン"
      : /iPhone|iPad/.test(navigator.userAgent)
        ? "iPhone / iPad"
        : "登録した端末";
    const credentials = await pairWithHost(location.origin, code, label);
    storeCredentials(credentials);
    state.hostLabel = credentials.hostLabel;
    el("host-label").textContent = credentials.hostLabel;
    const hello = await state.client!.hello();
    state.routes = hello.routes;
    goHome();
  } catch (error) {
    const expired = error instanceof RelayRequestError && error.code === "code-expired";
    el("pair-error").replaceChildren(
      alertBox(
        "error",
        expired ? "登録コードの有効期限が切れました" : "登録できませんでした",
        message(error),
        expired
          ? ["母艦のパソコンで新しいコードを出してください。", "コードは 10 分だけ、1 回だけ使えます。"]
          : ["コードを確かめて、もう一度入力してください。"],
      ),
    );
    setStatus("登録できませんでした", "error");
    updatePairAction();
  }
}

async function unpair(): Promise<void> {
  try {
    await state.client?.unpair();
  } catch {
    // The host may already have removed this device. Either way the local copy
    // goes: leaving a secret behind that no longer opens anything is worse
    // than useless.
  }
  storeCredentials(null);
  state.routes = [];
  state.hostLabel = "";
  el("host-label").textContent = "";
  await openHosts();
}

// ---- odds and ends ----------------------------------------------------------------

function showFailure(headline: string, body: string, steps: string[]): void {
  stopEverything();
  show("failed");
  setProgress(1, "error");
  el("failed-headline").textContent = headline;
  el("failed-body").textContent = body;
  const list = el("failed-steps");
  list.replaceChildren();
  for (const step of steps) {
    const item = document.createElement("li");
    item.textContent = step;
    list.append(item);
  }
  setStatus(headline, "error");
  setActions({ label: "最初の画面へ", run: goHome }, undefined);
}

function stopEverything(): void {
  window.clearInterval(shownTimer);
  state.stream?.stop();
  state.stream = null;
  state.capture?.stop();
  state.capture = null;
}

function alertBox(
  tone: "error" | "warn" | "done",
  title: string,
  body: string,
  steps: string[] = [],
): HTMLElement {
  const box = document.createElement("div");
  box.className = "alert";
  box.dataset.tone = tone;
  const heading = document.createElement("p");
  heading.className = "alert-title";
  heading.textContent = title;
  const text = document.createElement("p");
  text.className = "alert-body";
  text.textContent = body;
  box.append(heading, text);
  if (steps.length > 0) {
    const list = document.createElement("ol");
    list.className = "alert-recovery";
    for (const step of steps) {
      const item = document.createElement("li");
      item.textContent = step;
      list.append(item);
    }
    box.append(list);
  }
  return box;
}

function message(error: unknown): string {
  if (error instanceof Error) return error.message;
  return String(error);
}

window.addEventListener("resize", fitStage);
void boot();

// For the automated screen tests. Puts the page into states that need a host
// or a camera this machine does not have, through the page's own code paths.
(window as unknown as { __pubFerry: unknown }).__pubFerry = {
  show,
  goHome,
  setDirection,
  cameraProblem: (kind: "denied" | "none" | "busy") => {
    show("capture");
    setDirection("inbound", 1);
    showCameraProblem(new CameraError(kind, kind === "none" ? "この端末にカメラが見つかりません。" : "カメラの使用が許可されていません。"));
  },
  state,
};
