// Camera in, assembled payload out.
//
// The upstream receiver does this against its own DOM and its own status line.
// This is the same loop with the page taken out, so the desktop app and the
// phone page can each present it their own way.
//
// Camera failures are turned into named states rather than raw exceptions,
// because the four that happen are four different situations for the person
// holding the device, and "NotAllowedError" is not a sentence:
//
//   denied      they said no, or the OS says no. Fixable, and the fix differs
//               per platform, so the caller supplies the words.
//   none        there is no camera at all. NOT an error — a desktop without a
//               webcam is a normal machine, and this must not be painted red.
//   busy        another program has it.
//   insecure    the page is not on a secure origin, so the API does not exist.
//   failed      anything else, with the message attached.

import { DecodeWorkerPool, type PoolWorker } from "./worker-pool";
import { LTDecoder } from "./fountain";
import { parseFrame, streamIdentity, type FrameHeader } from "./protocol";
import { estimateTransferProgress } from "./progress";

export type CameraProblem = "denied" | "none" | "busy" | "insecure" | "failed";

export class CameraError extends Error {
  constructor(
    readonly problem: CameraProblem,
    message: string,
  ) {
    super(message);
    this.name = "CameraError";
  }
}

export interface CaptureProgress {
  fraction: number;
  framesCollected: number;
  framesExpected: number;
  bytesTotal: number;
  etaSeconds?: number;
  framesPerSecond: number;
}

export interface CaptureHandlers {
  onProgress(progress: CaptureProgress): void;
  onComplete(payload: Uint8Array): void;
  /** Seconds of camera with nothing decoded. The caller decides what to say. */
  onQuiet(seconds: number): void;
}

export interface CaptureOptions {
  video: HTMLVideoElement;
  createWorker: () => PoolWorker;
  workers?: number;
  captureWidth?: number;
  captureFps?: number;
  handlers: CaptureHandlers;
}

/**
 * Might there be a camera on this machine?
 *
 * A HINT, used to decide whether to offer the camera card — never the thing
 * that refuses a transfer. Enumeration is not reliable enough for that: before
 * any permission has been granted some engines return an empty list rather
 * than an unlabelled one, and answering "no camera" from that is a false
 * negative that locks somebody out of the feature they have. It cost this
 * project a green test that was measuring nothing.
 *
 * So: false only when enumeration WORKED and listed devices, none of them a
 * camera. Anything else is "ask and find out", and getUserMedia's own
 * NotFoundError is what actually settles it.
 */
export async function hasCamera(): Promise<boolean> {
  if (typeof navigator === "undefined" || !navigator.mediaDevices?.getUserMedia) return false;
  if (!navigator.mediaDevices.enumerateDevices) return true;
  try {
    const devices = await navigator.mediaDevices.enumerateDevices();
    if (devices.length === 0) return true;
    return devices.some((device) => device.kind === "videoinput");
  } catch {
    return true;
  }
}

export class OpticalCapture {
  private stream: MediaStream | null = null;
  private pool: DecodeWorkerPool | null = null;
  private decoder: LTDecoder | null = null;
  private identity = "";
  private running = false;
  private raf = 0;
  private readonly canvas = document.createElement("canvas");
  private startedAt = 0;
  private lastDecodeAt = 0;
  private decodedFrames = 0;
  private lastReport = 0;

  constructor(private readonly options: CaptureOptions) {}

  get isRunning(): boolean {
    return this.running;
  }

  async start(): Promise<MediaTrackSettings | undefined> {
    if (typeof navigator === "undefined" || !navigator.mediaDevices?.getUserMedia) {
      // On an insecure origin the API does not exist AT ALL, so this is the
      // http-over-the-LAN case rather than a refusal.
      throw new CameraError(
        "insecure",
        "この画面は安全な接続（https）で開く必要があります。カメラは使えません。",
      );
    }
    // No pre-flight check here on purpose: getUserMedia's own NotFoundError is
    // the authority on whether a camera exists, and a guess in front of it can
    // only ever be wrong in the direction that refuses a working camera.
    const width = this.options.captureWidth ?? 1280;
    const fps = this.options.captureFps ?? 30;
    const base: MediaTrackConstraints = {
      facingMode: "environment",
      width: { ideal: width },
      height: { ideal: Math.round((width * 3) / 4) },
    };
    try {
      try {
        this.stream = await navigator.mediaDevices.getUserMedia({
          audio: false,
          video: { ...base, frameRate: { exact: fps } },
        });
      } catch {
        // Not every camera can be pinned to a rate; asking for it as a
        // preference always works and gets close.
        this.stream = await navigator.mediaDevices.getUserMedia({
          audio: false,
          video: { ...base, frameRate: { ideal: fps } },
        });
      }
    } catch (error) {
      throw toCameraError(error);
    }

    const video = this.options.video;
    video.srcObject = this.stream;
    video.muted = true;
    video.playsInline = true;
    await video.play().catch(() => undefined);

    this.pool = new DecodeWorkerPool(this.options.createWorker, (bytes) => this.onDecoded(bytes));
    this.pool.resize(this.options.workers ?? Math.min(4, navigator.hardwareConcurrency || 2));
    this.running = true;
    this.startedAt = performance.now();
    this.lastDecodeAt = this.startedAt;
    this.loop();
    return this.stream.getVideoTracks()[0]?.getSettings();
  }

  stop(): void {
    this.running = false;
    if (this.raf) cancelAnimationFrame(this.raf);
    this.raf = 0;
    this.pool?.resize(0);
    this.pool = null;
    for (const track of this.stream?.getTracks() ?? []) track.stop();
    this.stream = null;
    const video = this.options.video;
    video.srcObject = null;
  }

  /** Frames collected so far, or 0 before a stream has been recognised. The
   *  animated path cannot resume: this is for telling somebody how far a run
   *  got, not for continuing it. */
  get framesCollected(): number {
    return this.decoder?.framesNew ?? 0;
  }

  private loop(): void {
    const video = this.options.video;
    const context = this.canvas.getContext("2d", { willReadFrequently: true })!;
    const tick = (): void => {
      if (!this.running) return;
      this.raf = requestAnimationFrame(tick);
      if (video.readyState < 2 || video.videoWidth === 0) return;
      if (this.canvas.width !== video.videoWidth) {
        this.canvas.width = video.videoWidth;
        this.canvas.height = video.videoHeight;
      }
      context.drawImage(video, 0, 0);
      const image = context.getImageData(0, 0, this.canvas.width, this.canvas.height);
      // Busy workers drop the frame rather than queueing it: a stale frame is
      // worth less than the next one, and the fountain absorbs the miss.
      this.pool?.submit(
        { id: 0, buf: image.data.buffer, w: this.canvas.width, h: this.canvas.height },
        [image.data.buffer],
      );
      this.report();
    };
    this.raf = requestAnimationFrame(tick);
  }

  private onDecoded(bytes: Uint8Array): void {
    const parsed = parseFrame(bytes);
    if (!parsed) return;
    const { header, block } = parsed;
    const identity = streamIdentity(header);
    if (identity !== this.identity) {
      // Any disagreement, not just a new session id: 16-bit session ids
      // collide across restarts, and a mismatched frame fed into the old
      // decoder corrupts it silently, surfacing only as a failed checksum
      // after the whole transfer has run.
      this.identity = identity;
      this.decoder = newDecoder(header);
      this.startedAt = performance.now();
      this.decodedFrames = 0;
    }
    const decoder = this.decoder;
    if (!decoder) return;
    decoder.addFrame(header.seq, block);
    this.decodedFrames++;
    this.lastDecodeAt = performance.now();
    if (decoder.isComplete) {
      const payload = decoder.assemble();
      this.stop();
      if (payload) this.options.handlers.onComplete(payload);
    }
  }

  private report(): void {
    const now = performance.now();
    if (now - this.lastReport < 120) return;
    this.lastReport = now;
    const quiet = (now - this.lastDecodeAt) / 1000;
    if (!this.decoder) {
      this.options.handlers.onQuiet(quiet);
      return;
    }
    const elapsed = (now - this.startedAt) / 1000;
    const estimate = estimateTransferProgress(
      this.decoder.k,
      this.decoder.framesNew,
      elapsed,
      this.decoder.solvedCount,
    );
    this.options.handlers.onProgress({
      fraction: estimate.fraction,
      framesCollected: this.decoder.framesNew,
      framesExpected: estimate.expectedFrames,
      bytesTotal: this.decoder.totalLen,
      etaSeconds: estimate.etaSeconds,
      framesPerSecond: elapsed > 0 ? this.decodedFrames / elapsed : 0,
    });
    if (quiet > 1) this.options.handlers.onQuiet(quiet);
  }
}

function newDecoder(header: FrameHeader): LTDecoder {
  return new LTDecoder(header.k, header.blockLen, header.sessionId, header.totalLen);
}

function toCameraError(error: unknown): CameraError {
  const name = error instanceof DOMException ? error.name : "";
  if (name === "NotAllowedError" || name === "SecurityError") {
    return new CameraError("denied", "カメラの使用が許可されていません。");
  }
  if (name === "NotFoundError" || name === "OverconstrainedError") {
    return new CameraError("none", "使えるカメラが見つかりませんでした。");
  }
  if (name === "NotReadableError" || name === "AbortError") {
    return new CameraError("busy", "カメラを別のプログラムが使っています。");
  }
  return new CameraError("failed", error instanceof Error ? error.message : String(error));
}
