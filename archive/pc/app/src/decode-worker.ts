// QR decode worker: zxing-cpp compiled to WASM.
//
// One frame in flight per worker; the main thread drops frames when every
// worker is busy. Frames are disposable — the fountain does not care which
// ones arrive.

import { prepareZXingModule, readBarcodes } from "zxing-wasm/reader";
import wasmUrl from "zxing-wasm/reader/zxing_reader.wasm?url";

prepareZXingModule({
  overrides: {
    locateFile: (path: string, prefix: string) =>
      path.endsWith(".wasm") ? wasmUrl : prefix + path,
  },
});

const ctx = self as unknown as {
  onmessage: ((e: MessageEvent) => void) | null;
  postMessage(message: unknown, transfer?: Transferable[]): void;
};

ctx.onmessage = async (event: MessageEvent) => {
  const { id, buf, w, h } = event.data as { id: number; buf: ArrayBuffer; w: number; h: number };
  try {
    const image = new ImageData(new Uint8ClampedArray(buf), w, h);
    const results = await readBarcodes(image, { formats: ["QRCode"], maxNumberOfSymbols: 1 });
    const found = results.find((result) => result.isValid && result.bytes.length > 0);
    ctx.postMessage({ id, bytes: found ? found.bytes : null });
  } catch {
    ctx.postMessage({ id, bytes: null });
  }
};

// Warm the WASM so the first real frame does not pay for instantiation.
void readBarcodes(new ImageData(8, 8), { formats: ["QRCode"] })
  .catch(() => undefined)
  .then(() => ctx.postMessage({ id: -1, bytes: null }));
