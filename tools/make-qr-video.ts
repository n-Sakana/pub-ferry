// Renders a fountain-coded QR stream to a Y4M video file.
//
// This is what makes the camera path testable on a machine with no camera.
// Chromium's --use-file-for-fake-video-capture presents the file as a webcam,
// so getUserMedia, the decode workers, the fountain decoder and the bundle
// verifier all run exactly as they do against a real screen — the only thing
// replaced is the light.
//
//   node --import tsx tools/make-qr-video.ts <out.y4m> [seconds]
//
// Y4M is the format Chromium wants: a text header, then a FRAME marker and a
// raw I420 plane triple per frame. Black and white QR modules become Y = 0 or
// 255 with the chroma planes flat at 128, which is what a camera would see of
// a monochrome screen.

import QRCode from "qrcode";
import { writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { LTEncoder } from "../shared/fountain";
import { blockLength } from "../shared/frame-capacity";
import { fnv1a, packFile, packFrame, type FrameHeader } from "../shared/protocol";
import { BUNDLE_MEDIA_TYPE, packBundle, type BundleFile } from "../shared/bundle";

const WIDTH = 800;
const HEIGHT = 600;
const FPS = 10;
const QUIET = 4;
const BYTES_PER_FRAME = 1000;

export async function buildSamplePayload(): Promise<{ container: Uint8Array; files: BundleFile[]; label: string }> {
  const encoder = new TextEncoder();
  const label = "光学テスト";
  const files: BundleFile[] = [
    { path: "notes.txt", bytes: encoder.encode("一行目\n二行目\n三行目\n") },
    { path: "sub/data.json", bytes: encoder.encode(JSON.stringify({ ok: true, n: 42 })) },
    { path: "sub/empty.txt", bytes: new Uint8Array(0) },
  ];
  const bundle = await packBundle(label, files);
  const container = await packFile(`${label}.dcb1`, BUNDLE_MEDIA_TYPE, bundle.bytes);
  return { container: container.container, files, label };
}

/** One QR frame as a full-size Y plane, centred, integer-scaled. */
function renderFrame(bytes: Uint8Array, luma: Uint8Array): void {
  const qr = QRCode.create([{ data: bytes, mode: "byte" } as unknown as QRCode.QRCodeSegment], {
    errorCorrectionLevel: "L",
    maskPattern: 4,
  });
  const modules = qr.modules.size;
  const total = modules + 2 * QUIET;
  const scale = Math.max(1, Math.floor(Math.min(WIDTH, HEIGHT) / total));
  const side = total * scale;
  const originX = Math.floor((WIDTH - side) / 2);
  const originY = Math.floor((HEIGHT - side) / 2);

  // Mid-grey surround rather than white: a real camera sees a screen against a
  // room, and a decoder that only works on a perfectly white field is a
  // decoder that only works in a test.
  luma.fill(96);
  for (let y = 0; y < side; y++) {
    const row = (originY + y) * WIDTH + originX;
    const moduleY = Math.floor(y / scale) - QUIET;
    for (let x = 0; x < side; x++) {
      const moduleX = Math.floor(x / scale) - QUIET;
      const dark =
        moduleY >= 0 &&
        moduleY < modules &&
        moduleX >= 0 &&
        moduleX < modules &&
        qr.modules.data[moduleY * modules + moduleX];
      luma[row + x] = dark ? 0 : 255;
    }
  }
}

export async function writeQrVideo(target: string, seconds = 12): Promise<{ frames: number }> {
  const { container } = await buildSamplePayload();
  const blockLen = blockLength(BYTES_PER_FRAME);
  const sessionId = 0x4242;
  const encoder = new LTEncoder(container, blockLen, sessionId);
  const header: FrameHeader = {
    sessionId,
    seq: 0,
    k: encoder.k,
    blockLen,
    totalLen: container.length,
    payloadFnv: fnv1a(container),
  };

  const frameCount = seconds * FPS;
  const luma = new Uint8Array(WIDTH * HEIGHT);
  const chroma = new Uint8Array((WIDTH / 2) * (HEIGHT / 2)).fill(128);
  const chunks: Buffer[] = [
    Buffer.from(`YUV4MPEG2 W${WIDTH} H${HEIGHT} F${FPS}:1 Ip A1:1 C420jpeg\n`, "ascii"),
  ];
  for (let seq = 0; seq < frameCount; seq++) {
    renderFrame(packFrame({ ...header, seq }, encoder.encode(seq)), luma);
    chunks.push(Buffer.from("FRAME\n", "ascii"));
    chunks.push(Buffer.from(luma));
    chunks.push(Buffer.from(chroma));
    chunks.push(Buffer.from(chroma));
  }
  writeFileSync(target, Buffer.concat(chunks));
  return { frames: frameCount };
}

if (process.argv[1] && process.argv[1].endsWith("make-qr-video.ts")) {
  const target = resolve(process.argv[2] ?? "evidence/tmp/qr-stream.y4m");
  const seconds = Number(process.argv[3] ?? 12);
  void writeQrVideo(target, seconds).then((result) => {
    console.log(`${result.frames} フレーム / ${WIDTH}x${HEIGHT}@${FPS} を ${target} に書きました。`);
  });
}
