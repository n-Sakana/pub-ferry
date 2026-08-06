// Putting a payload on a screen as an endless fountain-coded QR stream.
//
// The upstream sender page does this inline against its own DOM. This is the
// same technique with the DOM taken out, so the desktop app and the phone page
// can each draw it into their own layout without either of them re-deriving
// the tuning.
//
// The tuning, kept from upstream because it was measured rather than guessed:
//   - Frame payload sets the QR version. Denser wins on goodput right up until
//     the receiver stops decoding; 1465 bytes (~V27) is safe across ordinary
//     monitors, 2953 (V40) works phone-to-phone at close range.
//   - The mask pattern is pinned. Any declared mask is valid to a decoder, and
//     skipping the spec's 8-way evaluation makes generation about 4× faster.
//   - Error correction stays at L: in-frame ECC and the fountain solve
//     different problems (corruption versus erasure), and at these sizes
//     "decode whole or discard" plus fountain redundancy is the better trade.

import QRCode from "qrcode";
import { LTEncoder } from "./fountain";
import { rasterizeQr } from "./qr-raster";
import {
  blockLength,
  fitsInOneStream,
  minimumFrameBytes,
  smallestSufficientFrameSize,
  sourceBlockCount,
  MAX_SOURCE_BLOCKS,
} from "./frame-capacity";
import { fnv1a, packFrame, type FrameHeader } from "./protocol";

const QUIET_ZONE_MODULES = 4;
const LOOKAHEAD = 3;

export interface StreamSettings {
  framesPerSecond: number;
  bytesPerFrame: number;
}

export interface StreamStarted {
  /** QR modules per side, quiet zone excluded. Callers size the canvas from
   *  this so a code is never drawn at a fraction of a module. */
  modules: number;
  qrVersion: number;
  sourceBlocks: number;
  /** Seconds one full pass of the theoretical minimum takes, before any
   *  redundancy. What the UI turns into "about N minutes". */
  minimumSeconds: number;
}

export class StreamTooBigError extends Error {
  constructor(
    message: string,
    readonly suggestedBytesPerFrame: number,
  ) {
    super(message);
    this.name = "StreamTooBigError";
  }
}

/**
 * A running stream. `stop()` is idempotent, and calling it is the only way the
 * loop ends — a caller that forgets leaves a canvas repainting forever.
 */
export class OpticalStream {
  private running = false;
  private queue: ImageData[] = [];
  private nextSeq = 0;
  private raf = 0;
  private readonly encoder: LTEncoder;
  private readonly header: FrameHeader;
  private readonly staging: HTMLCanvasElement;
  private version: number | undefined;
  private modules = 0;

  constructor(
    private readonly canvas: HTMLCanvasElement,
    payload: Uint8Array,
    private readonly settings: StreamSettings,
    /** The sizes the caller's own control offers, so a refusal can name one
     *  that is actually in the list rather than the bare arithmetic minimum.
     *  Used once, here — deliberately not a field. */
    offeredFrameSizes: readonly number[],
  ) {
    const frameBytes = settings.bytesPerFrame;
    if (!fitsInOneStream(payload.length, frameBytes)) {
      const suggestion =
        smallestSufficientFrameSize(payload.length, offeredFrameSizes) ??
        minimumFrameBytes(payload.length);
      throw new StreamTooBigError(
        `この量を 1 枚 ${frameBytes} バイトで送るには ` +
          `${sourceBlockCount(payload.length, frameBytes).toLocaleString()} 個に分ける必要があり、` +
          `1 枚が数えられるのは ${MAX_SOURCE_BLOCKS.toLocaleString()} 個までです。` +
          `1 枚あたりの量を ${suggestion} 以上にしてください。`,
        suggestion,
      );
    }
    const sessionId = (Math.floor(Math.random() * 0xffff) + 1) & 0xffff;
    const blockLen = blockLength(frameBytes);
    this.encoder = new LTEncoder(payload, blockLen, sessionId);
    this.header = {
      sessionId,
      seq: 0,
      k: this.encoder.k,
      blockLen,
      totalLen: payload.length,
      payloadFnv: fnv1a(payload),
    };
    this.staging = document.createElement("canvas");
  }

  get sourceBlocks(): number {
    return this.encoder.k;
  }

  /** Frames emitted so far. The only honest progress a SENDER has: the screen
   *  cannot know what the other side received. */
  get framesShown(): number {
    return this.nextSeq;
  }

  start(): StreamStarted {
    if (this.running) throw new Error("already running");
    this.running = true;
    // The first frame decides the QR version, and the canvas cannot be sized
    // until it does.
    this.queue.push(this.makeFrame());
    const started: StreamStarted = {
      modules: this.modules,
      qrVersion: this.version ?? 0,
      sourceBlocks: this.encoder.k,
      minimumSeconds: this.encoder.k / Math.max(1, this.settings.framesPerSecond),
    };
    this.fill();
    this.loop();
    return started;
  }

  stop(): void {
    this.running = false;
    if (this.raf) cancelAnimationFrame(this.raf);
    this.raf = 0;
    this.queue = [];
  }

  /**
   * Size the canvas for the space it has been given.
   *
   * Called on every resize. `cssSide` is the largest square the layout can
   * spare; the canvas is a whole number of device pixels per module, because a
   * module drawn at 3.4 pixels is a module with a ragged edge and a decoder
   * that has to guess.
   */
  resize(cssSide: number): { modulePixels: number } {
    const dpr = window.devicePixelRatio || 1;
    const total = this.modules + 2 * QUIET_ZONE_MODULES;
    const scale = Math.max(1, Math.floor((cssSide * dpr) / total));
    this.staging.width = total;
    this.staging.height = total;
    this.canvas.width = total * scale;
    this.canvas.height = total * scale;
    this.canvas.style.width = `${(total * scale) / dpr}px`;
    this.canvas.style.height = `${(total * scale) / dpr}px`;
    return { modulePixels: scale };
  }

  private makeFrame(): ImageData {
    const bytes = packFrame({ ...this.header, seq: this.nextSeq }, this.encoder.encode(this.nextSeq));
    this.nextSeq++;
    const qr = QRCode.create([{ data: bytes, mode: "byte" } as unknown as QRCode.QRCodeSegment], {
      errorCorrectionLevel: "L",
      version: this.version,
      maskPattern: 4,
    });
    if (this.version === undefined) {
      this.version = qr.version;
      this.modules = qr.modules.size;
    }
    const raster = rasterizeQr(qr.modules.size, qr.modules.data, QUIET_ZONE_MODULES);
    return new ImageData(new Uint8ClampedArray(raster.pixels.buffer), raster.size, raster.size);
  }

  /** Generates at most one frame per tick once primed. Self-scheduling instead
   *  cost hundreds of wake-ups a second doing nothing with a full queue. */
  private fill(max = LOOKAHEAD): void {
    for (let n = 0; n < max && this.queue.length < LOOKAHEAD; n++) {
      this.queue.push(this.makeFrame());
    }
  }

  private loop(): void {
    const interval = 1000 / this.settings.framesPerSecond;
    let nextAt = performance.now();
    const context = this.canvas.getContext("2d")!;
    const stagingContext = this.staging.getContext("2d")!;
    const tick = (now: number): void => {
      if (!this.running) return;
      this.raf = requestAnimationFrame(tick);
      if (now < nextAt) return;
      const frame = this.queue.shift();
      this.fill(1);
      if (!frame) {
        nextAt = now + interval;
        return;
      }
      stagingContext.putImageData(frame, 0, 0);
      context.imageSmoothingEnabled = false;
      context.drawImage(this.staging, 0, 0, this.canvas.width, this.canvas.height);
      nextAt += interval;
      // Fell behind — catch up by resetting rather than bursting, which would
      // put several frames on screen inside one refresh and show none of them
      // long enough to be captured.
      if (now - nextAt > 3 * interval) nextAt = now + interval;
    };
    this.raf = requestAnimationFrame(tick);
  }
}
