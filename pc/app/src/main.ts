// The desktop app's page.
//
// One screen at a time, one primary action per screen, and the action bar at
// the bottom is the only place a primary action ever lives — so the thing to
// press is always in the same corner, whatever screen you are on.

import "./shell.css";
import {
  BUNDLE_MEDIA_TYPE,
  DEFAULT_MAX_BUNDLE_BYTES,
  describeBundle,
  packBundle,
  readBundleManifest,
  verifyBundle,
  type BundleFile,
  type BundleManifest,
} from "../../../shared/bundle";
import { packFile, unpackFile, verifyFile, type OpticalFile } from "../../../shared/protocol";
import { OpticalStream, StreamTooBigError } from "../../../shared/optical-stream";
import { CameraError, OpticalCapture, hasCamera } from "../../../shared/optical-capture";
import { SlowFrameCollector } from "../../../shared/slow-frames";
import { bytesJa, durationJa } from "../../../shared/format-ja";
import { call, isHosted, onHostEvent, readPickedBytes, saveVerified, HostError } from "./host";

const FRAME_SIZES = [1000, 1465, 2331, 2953];

type ScreenName =
  | "home"
  | "send-pick"
  | "send-show"
  | "send-done"
  | "receive-choose"
  | "receive-camera"
  | "receive-reader"
  | "receive-confirm"
  | "saved"
  | "failed"
  | "settings";

const el = <T extends HTMLElement>(id: string): T => document.getElementById(id) as T;
const screens = new Map<ScreenName, HTMLElement>();
for (const section of document.querySelectorAll<HTMLElement>(".screen")) {
  screens.set(section.id.replace(/^screen-/, "") as ScreenName, section);
}

interface Settings {
  destination: string;
  destinationWritable: boolean;
  destinationExists: boolean;
  deviceLabel: string;
  confirmBeforeSaving: boolean;
  replaceExisting: boolean;
  readerSource: "keyboard" | "serial";
  serialPort: string;
  serialBaud: number;
  framesPerSecond: number;
  bytesPerFrame: number;
}

interface Picked {
  token: string;
  label: string;
  files: { path: string; size: number }[];
  skipped: number;
  totalSize: number;
}

const state = {
  screen: "home" as ScreenName,
  previous: "home" as ScreenName,
  settings: null as Settings | null,
  serialPorts: [] as string[],
  cameraPresent: false,
  picked: null as Picked | null,
  stream: null as OpticalStream | null,
  streamStats: { label: "", count: 0, frames: 0 },
  capture: null as OpticalCapture | null,
  collector: new SlowFrameCollector(),
  badFrames: new Set<number>(),
  received: null as { manifest: BundleManifest; files: BundleFile[] } | null,
};

// ---- chrome ---------------------------------------------------------------

const statusDot = el("status-dot");
const statusText = el("status-text");
const actionMain = el<HTMLButtonElement>("action-main");
const actionAlt = el<HTMLButtonElement>("action-alt");
const actionBack = el<HTMLButtonElement>("action-back");
const progressFill = el("progress-fill");

function setStatus(text: string, tone: "" | "busy" | "done" | "error" = ""): void {
  statusText.textContent = text;
  if (tone) statusDot.dataset.tone = tone;
  else delete statusDot.dataset.tone;
}

function setProgress(fraction: number, tone: "" | "done" | "error" = ""): void {
  progressFill.style.width = `${Math.max(0, Math.min(1, fraction)) * 100}%`;
  if (tone) progressFill.dataset.tone = tone;
  else delete progressFill.dataset.tone;
}

interface ActionSpec {
  label: string;
  run: () => void;
  /** Destroys something the user cannot get back. Drawn differently, because
   *  a discard styled like a Back button is a discard somebody presses. */
  danger?: boolean;
  /** A disabled button always says what would make it usable. A control that
   *  is greyed out with no explanation is a dead end. */
  disabledBecause?: string;
}

function setActions(options: {
  main?: ActionSpec;
  alt?: ActionSpec;
  back?: { label: string; run: () => void };
}): void {
  wire(actionMain, options.main);
  wire(actionAlt, options.alt);
  actionBack.hidden = !options.back;
  if (options.back) {
    actionBack.textContent = options.back.label;
    actionBack.onclick = options.back.run;
  }
}

function wire(button: HTMLButtonElement, spec?: ActionSpec): void {
  button.hidden = !spec;
  button.onclick = null;
  button.removeAttribute("title");
  if (!spec) return;
  button.className = spec.danger ? "btn-danger" : button.id === "action-main" ? "btn-primary" : "btn-secondary";
  button.textContent = spec.label;
  button.disabled = Boolean(spec.disabledBecause);
  if (spec.disabledBecause) button.title = spec.disabledBecause;
  else button.onclick = spec.run;
}

const directionBox = el("direction");
const directionSummary = el("direction-summary");
const directionPath = el("direction-path");

/** The direction indicator: a two-word summary, then the nodes in the order
 *  the bytes travel, with a mark on the one that is here. Three channels, so
 *  it survives being read at a glance, in greyscale, or by a screen reader. */
function setDirection(kind: "send" | "receive" | null): void {
  directionBox.hidden = kind === null;
  if (kind === null) return;
  const here = state.settings?.deviceLabel || "この PC";
  const nodes =
    kind === "send"
      ? [
          { label: here, here: true },
          { label: "向かいのカメラ", here: false },
        ]
      : [
          { label: "向かいの画面", here: false },
          { label: here, here: true },
        ];
  directionSummary.textContent = kind === "send" ? "送る" : "受け取る";
  directionPath.replaceChildren();
  nodes.forEach((node, index) => {
    if (index > 0) {
      const arrow = document.createElement("li");
      arrow.className = "direction-arrow";
      arrow.setAttribute("aria-hidden", "true");
      arrow.textContent = "→";
      directionPath.append(arrow);
    }
    const item = document.createElement("li");
    item.className = "direction-node";
    item.dataset.here = String(node.here);
    item.textContent = node.label;
    if (node.here) item.append(document.createTextNode("（ここ）"));
    directionPath.append(item);
  });
}

function show(name: ScreenName): void {
  if (state.screen !== name) state.previous = state.screen;
  state.screen = name;
  for (const [id, section] of screens) section.hidden = id !== name;
  el("main").scrollTop = 0;
}

// ---- entry ----------------------------------------------------------------

async function boot(): Promise<void> {
  el<HTMLButtonElement>("toggle-theme").onclick = toggleTheme;
  applyStoredTheme();
  el<HTMLButtonElement>("open-settings").onclick = () => void openSettings();
  for (const button of document.querySelectorAll<HTMLElement>("[data-go]")) {
    button.onclick = () => goto(button.dataset.go as ScreenName);
  }
  el<HTMLButtonElement>("pick-files").onclick = () => void pick("pickFiles");
  el<HTMLButtonElement>("pick-folder").onclick = () => void pick("pickFolder");
  el<HTMLButtonElement>("start-camera").onclick = () => void startCamera();
  el<HTMLButtonElement>("start-reader").onclick = () => void startReader();
  el<HTMLButtonElement>("change-destination").onclick = () => void changeDestination();
  el<HTMLButtonElement>("settings-change-destination").onclick = () => void changeDestination();
  el<HTMLButtonElement>("open-logs").onclick = () => void call("openFolder", { which: "logs" });
  wireSettingsControls();
  wireReaderInput();
  window.addEventListener("resize", fitStage);

  if (!isHosted()) {
    setStatus("デスクトップアプリの外で開かれています", "error");
    showFailure(
      "この画面はアプリの中で開いてください",
      "ファイルを選ぶ・保存する操作は、アプリ本体が行います。ブラウザーだけでは動きません。",
      ["pc\\pub-transfer.bat をダブルクリックして起動してください。"],
    );
    return;
  }

  try {
    const startup = await call<{ settings: Settings; serialPorts: string[] }>("getStartup");
    state.settings = startup.settings;
    state.serialPorts = startup.serialPorts;
  } catch (error) {
    setStatus(message(error), "error");
    return;
  }
  state.cameraPresent = await hasCamera();
  applySettingsToUi();
  goto("home");
}

function goto(name: ScreenName): void {
  stopEverything();
  switch (name) {
    case "home":
      show("home");
      setDirection(null);
      setProgress(0);
      // The text itself is written by applySettingsToUi, so changing the
      // destination and coming back here shows the new one. Setting it only on
      // arrival left the home screen advertising the previous folder.
      applySettingsToUi();
      setStatus("はじめに、送るか受け取るかを選びます");
      setActions({});
      break;
    case "send-pick":
      show("send-pick");
      setDirection("send");
      setProgress(0);
      el("send-limit").textContent = `一度に送れるのは ${bytesJa(DEFAULT_MAX_BUNDLE_BYTES)} までです。`;
      renderPicked();
      break;
    case "receive-choose":
      show("receive-choose");
      setDirection("receive");
      setProgress(0);
      renderReceiveChoices();
      break;
    case "settings":
      void openSettings();
      break;
    default:
      show(name);
  }
}

// ---- sending ---------------------------------------------------------------

async function pick(action: "pickFiles" | "pickFolder"): Promise<void> {
  el("send-error").replaceChildren();
  try {
    const result = await call<Picked & { cancelled: boolean }>(action);
    if (result.cancelled) return;
    state.picked = result;
    renderPicked();
  } catch (error) {
    el("send-error").replaceChildren(alertBox("error", "選べませんでした", message(error)));
    state.picked = null;
    renderPicked();
  }
}

function renderPicked(): void {
  const picked = state.picked;
  el("pick-summary").hidden = picked === null;
  if (!picked) {
    setStatus("送るファイルかフォルダーを選びます");
    setActions({
      main: { label: "送信をはじめる", run: () => undefined, disabledBecause: "先に送るものを選んでください" },
      back: { label: "戻る", run: () => goto("home") },
    });
    return;
  }
  el("pick-title").textContent = picked.label;
  el("pick-count").textContent = `${picked.files.length.toLocaleString()} 個`;
  el("pick-size").textContent = bytesJa(picked.totalSize);
  el("pick-time").textContent = estimateSendTime(picked.totalSize);
  const skipped = el("pick-skipped");
  skipped.hidden = picked.skipped === 0;
  skipped.textContent = `${picked.skipped} 個は送れないため外しました（リンクや読めないファイル）。`;
  el("pick-list-count").textContent = `${picked.files.length.toLocaleString()} 件`;
  const list = el("pick-list");
  list.replaceChildren();
  for (const file of picked.files.slice(0, 500)) list.append(fileRow(file.path, file.size));
  if (picked.files.length > 500) list.append(fileRow(`ほか ${picked.files.length - 500} 件`, -1));

  const tooBig = picked.totalSize > DEFAULT_MAX_BUNDLE_BYTES;
  setStatus(tooBig ? "選んだものが大きすぎます" : "準備できました", tooBig ? "error" : "");
  setActions({
    main: {
      label: "送信をはじめる",
      run: () => void startSending(),
      disabledBecause: tooBig
        ? `一度に送れるのは ${bytesJa(DEFAULT_MAX_BUNDLE_BYTES)} までです`
        : undefined,
    },
    back: { label: "戻る", run: () => goto("home") },
  });
}

function estimateSendTime(bytes: number): string {
  const fps = Number(el<HTMLSelectElement>("tx-speed").value);
  const perFrame = Number(el<HTMLSelectElement>("tx-density").value) - 20;
  // Frames needed is the payload divided across frames, times the fountain's
  // typical overhead. Deliberately quoted a little long: an estimate that
  // keeps slipping reads as a stall.
  const frames = Math.ceil(bytes / perFrame) * 1.25;
  return `約 ${durationJa(frames / fps)}`;
}

async function startSending(): Promise<void> {
  const picked = state.picked;
  if (!picked) return;
  setStatus("送るものをまとめています…", "busy");
  try {
    const raw = await readPickedBytes(picked.token);
    let offset = 0;
    const files: BundleFile[] = picked.files.map((file) => {
      const bytes = raw.subarray(offset, offset + file.size);
      offset += file.size;
      return { path: file.path, bytes };
    });
    const bundle = await packBundle(picked.label, files, { skipped: picked.skipped });
    const container = await packFile(`${picked.label}.dcb1`, BUNDLE_MEDIA_TYPE, bundle.bytes);

    show("send-show");
    setDirection("send");
    const canvas = el<HTMLCanvasElement>("send-canvas");
    const stream = new OpticalStream(
      canvas,
      container.container,
      {
        framesPerSecond: Number(el<HTMLSelectElement>("tx-speed").value),
        bytesPerFrame: Number(el<HTMLSelectElement>("tx-density").value),
      },
      FRAME_SIZES,
    );
    state.stream = stream;
    const started = stream.start();
    state.streamStats = { label: picked.label, count: picked.files.length, frames: 0 };
    el("show-label").textContent = picked.label;
    el("show-count").textContent = `${picked.files.length.toLocaleString()} 個`;
    el("send-caption").textContent =
      `${picked.files.length.toLocaleString()} 個のファイル・${bytesJa(picked.totalSize)}` +
      ` — 全部で約 ${durationJa(started.minimumSeconds * 1.25)}`;
    fitStage();
    setStatus("表示しています。相手が読み終わるまで消さないでください", "busy");
    setProgress(0);
    setActions({
      main: { label: "表示を終わる", run: stopSending },
    });
    tickShown();
  } catch (error) {
    show("send-pick");
    const advice =
      error instanceof StreamTooBigError
        ? [`「1 枚あたりの量」を ${error.suggestedBytesPerFrame} 以上にしてください。`]
        : [];
    el("send-error").replaceChildren(
      alertBox("error", "送信をはじめられませんでした", message(error), advice),
    );
    setStatus("送信をはじめられませんでした", "error");
    renderPicked();
  }
}

let shownTimer = 0;
function tickShown(): void {
  window.clearInterval(shownTimer);
  shownTimer = window.setInterval(() => {
    if (!state.stream) return;
    state.streamStats.frames = state.stream.framesShown;
    el("send-shown").textContent = `${state.streamStats.frames.toLocaleString()} 枚を表示しました`;
  }, 500);
}

function stopSending(): void {
  window.clearInterval(shownTimer);
  state.stream?.stop();
  state.stream = null;
  show("send-done");
  el("done-label").textContent = state.streamStats.label;
  el("done-count").textContent = `${state.streamStats.count.toLocaleString()} 個`;
  el("done-frames").textContent = `${state.streamStats.frames.toLocaleString()} 枚`;
  setProgress(0);
  setStatus("表示を終わりました");
  setActions({
    main: { label: "もう一度表示する", run: () => void startSending() },
    alt: { label: "別のものを送る", run: () => goto("send-pick") },
    back: { label: "最初の画面へ", run: () => goto("home") },
  });
}

/**
 * Size the code to the space actually available, and say so when there is not
 * enough of it.
 *
 * The code is a physical optical component: below about three device pixels per
 * module a camera has to guess at the edges. Drawing a smaller one quietly is
 * worse than useless — the transfer fails and nothing on either screen explains
 * why — so the rail says the window is too small instead.
 *
 * The height budget is measured from the real boxes rather than assumed. An
 * assumed one is how the screen ended up a few pixels too tall and grew a
 * scrollbar it had no content for.
 */
function fitStage(): void {
  const stream = state.stream;
  if (!stream || state.screen !== "send-show") return;
  const main = el("main");
  const holder = el("send-stage").parentElement!;
  const stage = el("send-stage");
  const stageStyle = getComputedStyle(stage);
  const mainStyle = getComputedStyle(main);
  const chrome =
    Number.parseFloat(stageStyle.paddingLeft) +
    Number.parseFloat(stageStyle.paddingRight) +
    Number.parseFloat(stageStyle.borderLeftWidth) * 2;
  const verticalChrome =
    Number.parseFloat(stageStyle.paddingTop) +
    Number.parseFloat(stageStyle.paddingBottom) +
    Number.parseFloat(stageStyle.borderTopWidth) * 2 +
    Number.parseFloat(mainStyle.paddingTop) +
    Number.parseFloat(mainStyle.paddingBottom);
  const available = Math.min(
    holder.clientWidth - chrome,
    main.clientHeight -
      el("screen-send-show").querySelector(".screen-head")!.clientHeight -
      el("send-caption").offsetHeight -
      verticalChrome -
      8,
  );
  const { modulePixels } = stream.resize(Math.max(160, available));
  const warning = el("stage-warning");
  warning.hidden = modulePixels >= 3;
  el("stage-warning-body").textContent =
    `1 マスあたり ${modulePixels} ピクセルしかありません。` +
    `ウィンドウを大きくするか、「1 枚あたりの量」を減らしてください。`;
}

// ---- receiving: choosing how ------------------------------------------------

function renderReceiveChoices(): void {
  const camera = el<HTMLButtonElement>("start-camera");
  const cameraTag = el("camera-tag");
  const readerTag = el("reader-tag");
  const absent = el("camera-absent");
  // A machine with no camera is a normal machine, not a broken one. The card
  // stays where it is so the feature is still discoverable; it is disabled
  // because it genuinely cannot be operated, and the reason sits next to it in
  // plain words rather than in a red box.
  camera.disabled = !state.cameraPresent;
  cameraTag.hidden = state.cameraPresent;
  cameraTag.textContent = "この PC では使えません";
  cameraTag.dataset.tone = "quiet";
  readerTag.hidden = state.cameraPresent;
  readerTag.textContent = "おすすめ";
  absent.hidden = state.cameraPresent;
  absent.textContent =
    "この PC にカメラが見つかりません。カメラを付けるか、読み取り機で 1 枚ずつ受け取ってください。";
  setStatus("受け取り方を選びます");
  setActions({ back: { label: "戻る", run: () => goto("home") } });
}

// ---- receiving: camera --------------------------------------------------------

async function startCamera(): Promise<void> {
  show("receive-camera");
  setDirection("receive");
  el("camera-error").replaceChildren();
  el("viewfinder").hidden = false;
  el("camera-progress").hidden = false;
  el("camera-detail").hidden = false;
  el("camera-placeholder").hidden = false;
  el("camera-destination").textContent = state.settings?.destination ?? "";
  el("camera-title").textContent = "相手の画面にカメラを向けてください";
  el("camera-mode").textContent = "カメラ";
  setStatus("カメラを準備しています…", "busy");
  setActions({ main: { label: "読み取りをやめる", run: () => goto("receive-choose") } });

  const capture = new OpticalCapture({
    video: el<HTMLVideoElement>("camera-video"),
    createWorker: () => new Worker(new URL("./decode-worker.ts", import.meta.url), { type: "module" }),
    handlers: {
      onProgress: (progress) => {
        el("camera-placeholder").hidden = true;
        el("camera-percent").textContent = String(Math.round(progress.fraction * 100));
        el("camera-eta").textContent =
          progress.etaSeconds === undefined ? "計測中" : durationJa(progress.etaSeconds);
        el("camera-frames").textContent = `${progress.framesCollected.toLocaleString()} / 約 ${progress.framesExpected.toLocaleString()}`;
        el("camera-size").textContent = bytesJa(progress.bytesTotal);
        el("camera-rate").textContent = `${progress.framesPerSecond.toFixed(1)} 枚`;
        el("camera-hint").textContent = "";
        setProgress(progress.fraction);
        setStatus("受け取っています", "busy");
      },
      onQuiet: (seconds) => {
        if (seconds < 4) return;
        el("camera-hint").textContent =
          "まだ何も読めていません。相手の画面全体が映るまで下がり、明るさを上げてみてください。";
      },
      onComplete: (payload) => void onPayloadReceived(payload),
    },
  });
  state.capture = capture;
  try {
    const settings = await capture.start();
    el("camera-placeholder").hidden = true;
    el("camera-settings").textContent = settings
      ? `${settings.width}×${settings.height} / ${Math.round(settings.frameRate ?? 0)} fps`
      : "—";
    setStatus("相手の画面にカメラを向けてください", "busy");
  } catch (error) {
    state.capture = null;
    showCameraProblem(error);
  }
}

function showCameraProblem(error: unknown): void {
  const problem = error instanceof CameraError ? error.problem : "failed";
  // The screen's job has changed. It is no longer "watch this camera" — there
  // is no camera — so the viewfinder and the progress figures go away rather
  // than sitting there as a large grey hole and a row of zeros. What is left
  // is the explanation and the way out.
  el("viewfinder").hidden = true;
  el("camera-progress").hidden = true;
  el("camera-detail").hidden = true;
  // The heading is an instruction, and the instruction is no longer possible.
  el("camera-title").textContent =
    problem === "none" ? "カメラで受け取ることができません" : "カメラを使えませんでした";
  el("camera-mode").textContent = "";
  const advice: Record<string, string[]> = {
    denied: [
      "Windows の「設定 → プライバシーとセキュリティ → カメラ」でカメラの使用を許可する",
      "アプリを閉じて、もう一度起動する",
      "それでも直らないときは、読み取り機で 1 枚ずつ受け取る",
    ],
    busy: [
      "カメラを使っている別のアプリ（会議ソフトなど）を閉じる",
      "もう一度「カメラで読み取る」を押す",
    ],
    none: ["カメラを接続する", "または読み取り機で 1 枚ずつ受け取る"],
    insecure: ["アプリから起動し直してください。"],
    failed: ["もう一度試すか、読み取り機で 1 枚ずつ受け取ってください。"],
  };
  const tone = problem === "none" ? "warn" : "error";
  el("camera-error").replaceChildren(
    alertBox(
      tone,
      problem === "none"
        ? "この PC にカメラが見つかりません"
        : problem === "denied"
          ? "カメラの使用が許可されていません"
          : problem === "busy"
            ? "カメラを別のプログラムが使っています"
            : "カメラを開けませんでした",
      message(error),
      advice[problem] ?? advice.failed!,
    ),
  );
  // A computer with no camera is a normal computer. Painting that red says
  // something is broken, and nothing is.
  setStatus(
    problem === "none" ? "この PC にはカメラがありません" : "カメラを使えませんでした",
    problem === "none" ? "" : "error",
  );
  setActions({
    main: { label: "受け取り方を選び直す", run: () => goto("receive-choose") },
  });
}

// ---- receiving: one frame at a time ---------------------------------------------

async function startReader(): Promise<void> {
  show("receive-reader");
  setDirection("receive");
  state.collector.reset();
  state.badFrames.clear();
  el("reader-error").replaceChildren();
  renderReaderProgress();
  const source = state.settings?.readerSource ?? "keyboard";
  el("reader-source-label").textContent =
    source === "serial"
      ? `読み取り方: COM ポート（${state.settings?.serialPort || "未選択"}）`
      : "読み取り方: キーボード入力";
  setActions({ main: { label: "読み取りをやめる", run: () => goto("receive-choose") } });
  setStatus("コードを 1 枚ずつ読み取ってください", "busy");

  if (source === "serial") {
    try {
      await call("openReader", { port: state.settings?.serialPort, baud: state.settings?.serialBaud });
      el("reader-status").textContent = "読み取り機を開きました。トリガーを引いてください。";
    } catch (error) {
      el("reader-error").replaceChildren(
        alertBox("error", "読み取り機を開けませんでした", message(error), [
          "設定で COM ポートを選び直す",
          "読み取り機を挿し直す",
          "キーボードとして受け取る設定に切り替える",
        ]),
      );
      setStatus("読み取り機を開けませんでした", "error");
    }
  } else {
    el("reader-status").textContent = "入力欄にフォーカスがある状態で読み取ってください。";
    el<HTMLInputElement>("reader-input").focus();
  }
}

function wireReaderInput(): void {
  const input = el<HTMLInputElement>("reader-input");
  let composing = false;
  input.addEventListener("compositionstart", () => {
    composing = true;
  });
  input.addEventListener("compositionend", () => {
    composing = false;
  });
  input.addEventListener("keydown", (event) => {
    // A wedge reader ends its scan with Enter (or Tab). While an IME is
    // composing, Enter means "accept the candidate" and is not ours.
    if (composing) return;
    if (event.key !== "Enter" && event.key !== "Tab") return;
    event.preventDefault();
    const text = input.value;
    input.value = "";
    acceptReaderLine(text);
  });
  onHostEvent("readerLine", (data) => acceptReaderLine(String(data.text ?? "")));
  onHostEvent("readerStatus", (data) => {
    el("reader-status").textContent = String(data.message ?? "");
  });
}

function acceptReaderLine(text: string): void {
  if (text.trim().length === 0) return;
  const result = state.collector.accept(text);
  switch (result.kind) {
    case "accepted":
      state.badFrames.delete(result.index);
      el("reader-status").textContent = `番号 ${result.index + 1} のコードを読み取りました。`;
      break;
    case "duplicate":
      el("reader-status").textContent = `番号 ${result.index + 1} は読み取り済みです。次のコードへ進んでください。`;
      break;
    case "other-document":
      el("reader-status").textContent = "別の転送のコードです。読み取っていません。";
      break;
    case "inconsistent":
      el("reader-error").replaceChildren(
        alertBox("error", "コードが混ざっています", result.message, [
          "「読み取りをやめる」を押してやり直してください。",
        ]),
      );
      return;
    case "rejected":
      el("reader-status").textContent = result.message;
      break;
  }
  renderReaderProgress();
  if (state.collector.isComplete) {
    const payload = state.collector.assemble();
    if (payload) void onPayloadReceived(payload);
  }
}

function renderReaderProgress(): void {
  const collector = state.collector;
  const total = collector.total;
  el("reader-count").textContent = String(collector.received);
  el("reader-total").textContent = total > 0 ? `/ ${total} 枚` : "枚";
  const grid = el("reader-grid");
  grid.replaceChildren();
  for (let index = 0; index < total; index++) {
    const cell = document.createElement("span");
    cell.className = "frame-cell";
    cell.dataset.state = collector.has(index) ? "have" : state.badFrames.has(index) ? "bad" : "want";
    cell.textContent = String(index + 1);
    grid.append(cell);
  }
  const missing = collector.missing();
  el("reader-missing").textContent =
    total === 0
      ? "まだ 1 枚も読み取っていません。"
      : missing.length === 0
        ? "すべて読み取りました。"
        : `あと ${total - collector.received} 枚 ── ${missing.join(", ")}${
            total - collector.received > missing.length ? " …" : ""
          }`;
  setProgress(total > 0 ? collector.received / total : 0);
}

// ---- receiving: verify and save ------------------------------------------------

async function onPayloadReceived(payload: Uint8Array): Promise<void> {
  stopEverything();
  setStatus("内容を確かめています…", "busy");
  let container: OpticalFile;
  try {
    container = await unpackFile(payload);
    // Step zero, and it IS a step: the container's own SHA-256 has to pass
    // before anything inside it is looked at even once.
    if (!(await verifyFile(container))) {
      throw new Error("受け取った中身が、送られたものと一致しません。");
    }
  } catch (error) {
    showFailure("受け取れませんでした", message(error), [
      "送る側でもう一度表示してもらう",
      "画面の明るさを上げ、カメラを近づける",
      "それでも直らないときは「1 枚あたりの量」を減らしてもらう",
    ]);
    return;
  }

  try {
    if (container.type !== BUNDLE_MEDIA_TYPE) {
      // A single file from the upstream sender still arrives here.
      const single: BundleManifest = {
        v: 1,
        label: stripExtension(container.name) || "受け取ったファイル",
        count: 1,
        totalSize: container.bytes.length,
        files: [{ path: container.name, size: container.bytes.length, sha256: "" }],
      };
      state.received = { manifest: single, files: [{ path: container.name, bytes: container.bytes }] };
    } else {
      readBundleManifest(container.bytes);
      const verified = await verifyBundle(container.bytes);
      state.received = { manifest: verified.manifest, files: verified.files };
    }
  } catch (error) {
    showFailure("受け取った中身が、送られたものと一致しません", message(error), [
      "送る側でもう一度表示してもらう",
      "画面の明るさを上げ、カメラを近づける",
    ]);
    return;
  }

  if (state.settings?.confirmBeforeSaving === false) {
    await saveReceived();
    return;
  }
  renderConfirm();
}

function stripExtension(name: string): string {
  return name.replace(/\.[^.]+$/, "");
}

function renderConfirm(): void {
  const received = state.received;
  if (!received) return;
  show("receive-confirm");
  setDirection("receive");
  setProgress(1);
  el("confirm-label").textContent = received.manifest.label;
  el("confirm-count").textContent = describeBundle(received.manifest);
  el("confirm-size").textContent = bytesJa(received.manifest.totalSize);
  el("confirm-verified").textContent = "送られたものと一致";
  el("confirm-list-count").textContent = `${received.files.length.toLocaleString()} 件`;
  el("confirm-destination").textContent = state.settings?.destination ?? "";
  const list = el("confirm-list");
  list.replaceChildren();
  for (const file of received.files.slice(0, 500)) list.append(fileRow(file.path, file.bytes.length));
  if (received.files.length > 500) {
    list.append(fileRow(`ほか ${received.files.length - 500} 件`, -1));
  }
  setStatus("保存先を確かめて保存します");
  setActions({
    main: { label: "この場所へ保存する", run: () => void saveReceived() },
    alt: { label: "受け取ったものを捨てる", run: () => discardReceived(), danger: true },
  });
}

function discardReceived(): void {
  state.received = null;
  goto("receive-choose");
}

async function saveReceived(): Promise<void> {
  const received = state.received;
  if (!received) return;
  setStatus("保存しています…", "busy");
  try {
    const result = await saveVerified(received.manifest.label, received.files);
    show("saved");
    setDirection("receive");
    setProgress(1, "done");
    el("saved-summary").textContent =
      `${result.fileCount.toLocaleString()} 個のファイル・${bytesJa(result.totalSize)} を保存しました。`;
    el("saved-path").textContent = result.destinationLabel;
    setStatus("保存しました", "done");
    setActions({
      main: { label: "保存先のフォルダーを開く", run: () => void call("openFolder", { which: "destination" }) },
      alt: { label: "続けて受け取る", run: () => goto("receive-choose") },
      back: { label: "最初の画面へ", run: () => goto("home") },
    });
    state.received = null;
  } catch (error) {
    // The received bytes are still held: a destination that cannot be written
    // to is a reason to pick another one, not a reason to throw away ten
    // minutes of transfer.
    el("confirm-error").replaceChildren(
      alertBox("error", "保存できませんでした", message(error), [
        "「保存先を変える」で書き込める場所を選ぶ",
        "受け取った中身はそのまま保持しています",
      ]),
    );
    show("receive-confirm");
    setStatus("保存できませんでした", "error");
    setActions({
      main: { label: "保存先を選び直す", run: () => void changeDestination() },
      alt: { label: "受け取ったものを捨てる", run: () => discardReceived(), danger: true },
    });
  }
}

function showFailure(headline: string, body: string, steps: string[]): void {
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
  setActions({
    main: { label: "もう一度受け取る", run: () => goto("receive-choose") },
    back: { label: "最初の画面へ", run: () => goto("home") },
  });
}

// ---- settings -----------------------------------------------------------------

async function openSettings(): Promise<void> {
  stopEverything();
  show("settings");
  setDirection(null);
  applySettingsToUi();
  try {
    const ports = await call<{ ports: string[] }>("listSerialPorts");
    state.serialPorts = ports.ports;
    renderPorts();
  } catch {
    // A machine with no serial subsystem answers with nothing, which is fine.
  }
  setStatus("設定");
  setActions({ back: { label: "戻る", run: () => goto("home") } });
}

function wireSettingsControls(): void {
  el<HTMLInputElement>("settings-label").addEventListener("change", () => {
    void persist({ deviceLabel: el<HTMLInputElement>("settings-label").value });
  });
  el<HTMLInputElement>("settings-confirm").addEventListener("change", (event) => {
    void persist({ confirmBeforeSaving: (event.target as HTMLInputElement).checked });
  });
  el<HTMLInputElement>("settings-replace").addEventListener("change", (event) => {
    void persist({ replaceExisting: (event.target as HTMLInputElement).checked });
  });
  for (const radio of document.querySelectorAll<HTMLInputElement>('input[name="reader-source"]')) {
    radio.addEventListener("change", () => {
      if (radio.checked) void persist({ readerSource: radio.value });
    });
  }
  el<HTMLSelectElement>("settings-port").addEventListener("change", (event) => {
    void persist({ serialPort: (event.target as HTMLSelectElement).value });
  });
}

async function persist(changes: Record<string, unknown>): Promise<void> {
  try {
    state.settings = await call<Settings>("saveSettings", changes);
    applySettingsToUi();
  } catch (error) {
    setStatus(message(error), "error");
  }
}

function applySettingsToUi(): void {
  const settings = state.settings;
  if (!settings) return;
  el("brand-device").textContent = settings.deviceLabel;
  el("home-destination").textContent = `受け取ったものは「${settings.destination}」に保存されます。`;
  el<HTMLInputElement>("settings-label").value = settings.deviceLabel;
  el("settings-destination").textContent = settings.destination;
  // A destination that cannot be written to is a blocking condition, so it is
  // not styled as one more grey hint among the others.
  const destinationState = el("settings-destination-state");
  const blocked = settings.destinationExists && !settings.destinationWritable;
  destinationState.textContent = !settings.destinationExists
    ? "このフォルダーはまだありません。最初に受け取ったときに作られます。"
    : settings.destinationWritable
      ? "この場所に書き込めます。"
      : "この場所に書き込めません。受け取ったものを保存できないので、別の場所を選んでください。";
  destinationState.classList.toggle("note--blocking", blocked);
  el<HTMLInputElement>("settings-confirm").checked = settings.confirmBeforeSaving;
  el<HTMLInputElement>("settings-replace").checked = settings.replaceExisting;
  // Both states are spelled out, and the one in force is the one in normal
  // weight — a switch whose label only says what happens when you flip it is a
  // switch you have to flip to read.
  el("state-confirm").innerHTML = settings.confirmBeforeSaving
    ? "いま: <b>保存の前に一覧を見せます</b>／切ると、確かめ次第すぐ保存します"
    : "いま: <b>確かめ次第すぐ保存します</b>／入れると、保存の前に一覧を見せます";
  el("word-confirm").textContent = settings.confirmBeforeSaving ? "見せる" : "自動";
  el("state-replace").innerHTML = settings.replaceExisting
    ? "いま: <b>同じ名前を置き換えます</b>（元には戻せません）／切ると別名で残します"
    : "いま: <b>別名で残します</b>（例: 名前 (2)）／入れると同じ名前を置き換えます";
  el("word-replace").textContent = settings.replaceExisting ? "置き換え" : "別名";

  for (const radio of document.querySelectorAll<HTMLInputElement>('input[name="reader-source"]')) {
    radio.checked = radio.value === settings.readerSource;
  }
  const serial = settings.readerSource === "serial";
  el("serial-fields").hidden = !serial;
  el("reader-source-note").textContent = serial
    ? "読み取り機が COM ポートに文字を送る設定になっている必要があります。"
    : "読み取り機がキーボードとして文字を打つ設定（HID ウェッジ）で使います。日本語入力は切ってください。";
  renderPorts();
}

function renderPorts(): void {
  const select = el<HTMLSelectElement>("settings-port");
  select.replaceChildren();
  if (state.serialPorts.length === 0) {
    const option = document.createElement("option");
    option.textContent = "使える COM ポートがありません";
    option.value = "";
    select.append(option);
    el("serial-hint").textContent = "読み取り機を接続してから、もう一度この画面を開いてください。";
    return;
  }
  for (const port of state.serialPorts) {
    const option = document.createElement("option");
    option.value = port;
    option.textContent = port;
    option.selected = port === state.settings?.serialPort;
    select.append(option);
  }
  el("serial-hint").textContent = "読み取り機がつながっているポートを選んでください。";
}

async function changeDestination(): Promise<void> {
  try {
    const next = await call<Settings & { cancelled?: boolean }>("chooseDestination");
    if (next.cancelled) return;
    state.settings = next;
    applySettingsToUi();
    if (state.screen === "receive-confirm") renderConfirm();
    if (state.screen === "home") goto("home");
  } catch (error) {
    setStatus(message(error), "error");
  }
}

// ---- odds and ends -------------------------------------------------------------

function stopEverything(): void {
  window.clearInterval(shownTimer);
  state.stream?.stop();
  state.stream = null;
  state.capture?.stop();
  state.capture = null;
  void call("closeReader").catch(() => undefined);
}

function fileRow(path: string, size: number): HTMLElement {
  const row = document.createElement("div");
  row.className = "file-row";
  const name = document.createElement("span");
  name.className = "file-path";
  name.textContent = path;
  name.title = path;
  const bytes = document.createElement("span");
  bytes.className = "file-size";
  bytes.textContent = size < 0 ? "" : bytesJa(size);
  row.append(name, bytes);
  return row;
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
  if (error instanceof HostError) return error.message;
  if (error instanceof Error) return error.message;
  return String(error);
}

function applyStoredTheme(): void {
  const stored = localStorage.getItem("pub-transfer.theme");
  if (stored === "dark" || stored === "light") {
    document.documentElement.dataset.theme = stored;
  }
}

function toggleTheme(): void {
  const next = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
  document.documentElement.dataset.theme = next;
  localStorage.setItem("pub-transfer.theme", next);
}

void boot();

// Exposed so the automated screen tests can put the real window into a state
// that needs hardware this machine does not have. Reachable only through the
// DevTools protocol, which pub-transfer.ps1 opens only when asked.
(window as unknown as { __pubTransfer: unknown }).__pubTransfer = {
  show: (name: ScreenName) => show(name),
  goto,
  readerLine: acceptReaderLine,
  /** Apply settings through the ordinary saveSettings path. Used by the
   *  evidence capture so published screens carry a neutral device name and
   *  destination instead of this machine's. */
  setForTest: async (changes: Record<string, unknown>) => {
    await persist(changes);
  },
  /** Stage a folder by path. The host refuses this unless the app was started
   *  for testing; everything after it is the ordinary sending path. */
  pickForTest: async (path: string) => {
    const result = await call<Picked>("stageForTest", { path });
    state.picked = result;
    goto("send-pick");
    renderPicked();
  },
  cameraProblem: (kind: "denied" | "none" | "busy") => {
    show("receive-camera");
    showCameraProblem(new CameraError(kind, kind === "none" ? "このパソコンにカメラがありません。" : "カメラの使用が許可されていません。"));
  },
  failure: showFailure,
  state,
};
