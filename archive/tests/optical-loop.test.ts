// The optical chain, end to end, without a camera.
//
// A folder goes in; QR frames come out; the same QR decoder the pages use
// reads the pixels back; the fountain reassembles them; the container is
// verified; the bundle is verified; and the bytes are compared with what went
// in. The only thing not exercised is getUserMedia — the lens, not the code —
// which is covered separately in optical-e2e.test.ts wherever a fake capture
// device is available.
//
// This is the test that would catch a change to the frame header, the soliton
// distribution, the raster, the container or the bundle format, and it runs
// anywhere Node runs.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import QRCode from "qrcode";
import { LTDecoder, LTEncoder } from "../shared/fountain";
import { rasterizeQr } from "../shared/qr-raster";
import { blockLength } from "../shared/frame-capacity";
import { fnv1a, packFile, packFrame, parseFrame, unpackFile, verifyFile, type FrameHeader } from "../shared/protocol";
import { BUNDLE_MEDIA_TYPE, packBundle, verifyBundle, type BundleFile } from "../shared/bundle";

const QUIET = 4;
const BYTES_PER_FRAME = 1000;

let decoderReady: Promise<typeof import("zxing-wasm/reader")> | null = null;

/** zxing, with its WebAssembly handed over as bytes — Node has no fetch for a
 *  file inside node_modules, and locating it by URL is a browser assumption. */
async function decoder(): Promise<typeof import("zxing-wasm/reader")> {
  if (!decoderReady) {
    decoderReady = (async () => {
      const zxing = await import("zxing-wasm/reader");
      const wasm = readFileSync(resolve("node_modules/zxing-wasm/dist/reader/zxing_reader.wasm"));
      zxing.prepareZXingModule({
        overrides: {
          wasmBinary: wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength),
        },
      });
      return zxing;
    })();
  }
  return decoderReady;
}

/** A QR frame as RGBA pixels, scaled the way a screen would show it. */
function drawFrame(bytes: Uint8Array, scale = 4): { data: Uint8ClampedArray; width: number; height: number } {
  const qr = QRCode.create([{ data: bytes, mode: "byte" } as unknown as QRCode.QRCodeSegment], {
    errorCorrectionLevel: "L",
    maskPattern: 4,
  });
  const raster = rasterizeQr(qr.modules.size, qr.modules.data, QUIET);
  const side = raster.size * scale;
  const out = new Uint8ClampedArray(side * side * 4);
  const view = new Uint32Array(out.buffer);
  for (let y = 0; y < side; y++) {
    const sourceRow = Math.floor(y / scale) * raster.size;
    const targetRow = y * side;
    for (let x = 0; x < side; x++) {
      view[targetRow + x] = raster.pixels[sourceRow + Math.floor(x / scale)]!;
    }
  }
  return { data: out, width: side, height: side };
}

async function decodeFrame(image: { data: Uint8ClampedArray; width: number; height: number }): Promise<Uint8Array | null> {
  const { readBarcodes } = await decoder();
  const results = await readBarcodes(image, { formats: ["QRCode"], maxNumberOfSymbols: 1 });
  const found = results.find((result) => result.isValid && result.bytes.length > 0);
  return found ? new Uint8Array(found.bytes) : null;
}

const encoder = new TextEncoder();

/** Bytes gzip cannot shrink, so the payload size is the payload size. */
function noise(length: number): Uint8Array {
  const out = new Uint8Array(length);
  let state = 0x2545f491;
  for (let i = 0; i < length; i++) {
    state = (Math.imul(state, 1103515245) + 12345) >>> 0;
    out[i] = state >>> 24;
  }
  return out;
}

async function sampleBundle(): Promise<{ container: Uint8Array; files: BundleFile[]; label: string }> {
  const label = "光学ループ";
  const files: BundleFile[] = [
    { path: "notes.txt", bytes: encoder.encode("一行目\n二行目\n三行目\n") },
    { path: "sub/data.json", bytes: encoder.encode(JSON.stringify({ ok: true, n: 42 })) },
    { path: "sub/empty.txt", bytes: new Uint8Array(0) },
    // Deliberately incompressible and deliberately big enough that the payload
    // needs tens of frames. A sample that fits in one frame exercises the
    // raster and nothing else — no fountain, no ordering, no loss tolerance —
    // and passes in 48 milliseconds while measuring almost nothing.
    { path: "binary.bin", bytes: noise(24 * 1024) },
  ];
  const bundle = await packBundle(label, files);
  const container = await packFile(`${label}.dcb1`, BUNDLE_MEDIA_TYPE, bundle.bytes);
  return { container: container.container, files, label };
}

/** Runs frames through draw → decode → fountain until the transfer completes,
 *  dropping every `dropEvery`-th frame to stand in for a missed capture. */
async function runLoop(
  container: Uint8Array,
  options: { dropEvery?: number; corruptEvery?: number; maxFrames?: number } = {},
): Promise<{ payload: Uint8Array; framesDrawn: number; framesDecoded: number }> {
  const blockLen = blockLength(BYTES_PER_FRAME);
  const sessionId = 0x1234;
  const ltEncoder = new LTEncoder(container, blockLen, sessionId);
  const header: FrameHeader = {
    sessionId,
    seq: 0,
    k: ltEncoder.k,
    blockLen,
    totalLen: container.length,
    payloadFnv: fnv1a(container),
  };
  const ltDecoder = new LTDecoder(header.k, header.blockLen, header.sessionId, header.totalLen);
  const maxFrames = options.maxFrames ?? ltEncoder.k * 4 + 40;

  let drawn = 0;
  let decoded = 0;
  for (let seq = 0; seq < maxFrames && !ltDecoder.isComplete; seq++) {
    drawn++;
    if (options.dropEvery && seq % options.dropEvery === 0) continue; // never reached the camera
    const image = drawFrame(packFrame({ ...header, seq }, ltEncoder.encode(seq)));
    if (options.corruptEvery && seq % options.corruptEvery === 0) {
      // A smear across the middle of the code, the way a shutter catches a
      // screen mid-refresh. The decoder either recovers it or discards it;
      // either way the fountain must still finish.
      const middle = Math.floor(image.height / 2) * image.width * 4;
      image.data.fill(128, middle, middle + image.width * 4 * 6);
    }
    const bytes = await decodeFrame(image);
    if (!bytes) continue;
    const parsed = parseFrame(bytes);
    if (!parsed) continue;
    decoded++;
    ltDecoder.addFrame(parsed.header.seq, parsed.block);
  }
  const payload = ltDecoder.assemble();
  assert.ok(payload, `the transfer did not complete within ${maxFrames} frames`);
  return { payload, framesDrawn: drawn, framesDecoded: decoded };
}

test("a folder survives being drawn as QR codes and read back", async () => {
  const sample = await sampleBundle();
  const { payload, framesDecoded } = await runLoop(sample.container);
  // A payload that fits in a single frame would prove nothing about the
  // fountain, so the sample is sized to need tens of them.
  assert.ok(framesDecoded > 15, `only ${framesDecoded} frames decoded — the sample is too small to be a test`);
  assert.deepEqual(Array.from(payload), Array.from(sample.container));

  const container = await unpackFile(payload);
  assert.equal(await verifyFile(container), true);
  assert.equal(container.type, BUNDLE_MEDIA_TYPE);
  const verified = await verifyBundle(container.bytes);
  assert.equal(verified.manifest.label, sample.label);
  assert.equal(verified.files.length, sample.files.length);
  for (let i = 0; i < sample.files.length; i++) {
    assert.equal(verified.files[i]!.path, sample.files[i]!.path);
    assert.deepEqual(
      Array.from(verified.files[i]!.bytes),
      Array.from(sample.files[i]!.bytes),
      `bytes differ for ${sample.files[i]!.path}`,
    );
  }
});

test("dropped frames cost time, not correctness", async () => {
  const sample = await sampleBundle();
  // Every third frame never arrives — a receiver that cannot keep up, or a
  // camera that spent that moment refocusing.
  const { payload, framesDrawn, framesDecoded } = await runLoop(sample.container, { dropEvery: 3 });
  assert.ok(framesDrawn > framesDecoded, "the drop was not actually applied");
  assert.deepEqual(Array.from(payload), Array.from(sample.container));
});

test("a smeared frame is discarded, and the transfer still finishes", async () => {
  const sample = await sampleBundle();
  const { payload } = await runLoop(sample.container, { corruptEvery: 4 });
  assert.deepEqual(Array.from(payload), Array.from(sample.container));
});

test("frames arriving out of order reassemble identically", async () => {
  const sample = await sampleBundle();
  const blockLen = blockLength(BYTES_PER_FRAME);
  const sessionId = 0x5678;
  const ltEncoder = new LTEncoder(sample.container, blockLen, sessionId);
  const header: FrameHeader = {
    sessionId,
    seq: 0,
    k: ltEncoder.k,
    blockLen,
    totalLen: sample.container.length,
    payloadFnv: fnv1a(sample.container),
  };
  // Draw a generous set, then feed them backwards.
  const images: { data: Uint8ClampedArray; width: number; height: number }[] = [];
  for (let seq = 0; seq < ltEncoder.k * 2 + 20; seq++) {
    images.push(drawFrame(packFrame({ ...header, seq }, ltEncoder.encode(seq))));
  }
  const ltDecoder = new LTDecoder(header.k, header.blockLen, header.sessionId, header.totalLen);
  for (let i = images.length - 1; i >= 0 && !ltDecoder.isComplete; i--) {
    const bytes = await decodeFrame(images[i]!);
    if (!bytes) continue;
    const parsed = parseFrame(bytes);
    if (parsed) ltDecoder.addFrame(parsed.header.seq, parsed.block);
  }
  const payload = ltDecoder.assemble();
  assert.ok(payload);
  assert.deepEqual(Array.from(payload!), Array.from(sample.container));
});
