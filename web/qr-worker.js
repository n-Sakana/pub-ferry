/* Ferry camera decoder. The bundled ZXing-WASM 2.2.4 reader is unchanged. */
"use strict";

var readerReady = false;
var core;
var readBarcodes;

async function readTransfer(image, harder, binarizer, session) {
  var results = await readBarcodes(image, {
    formats: ["QRCode"],
    // A pairing/link QR may be in the same camera image as the transfer QR.
    maxNumberOfSymbols: 4,
    tryHarder: harder,
    tryRotate: true,
    tryInvert: harder,
    tryDownscale: harder,
    binarizer: binarizer || "LocalAverage"
  });
  return results.find(function (result) {
    var frame = result.isValid && core.parseFrame(result.bytes);
    return frame && (!session || frame.session === session);
  });
}

self.onmessage = async function (event) {
  var message = event.data || {};
  if (!readerReady) {
    self.postMessage({ id: message.id, bytes: null });
    return;
  }
  try {
    var image = new ImageData(new Uint8ClampedArray(message.buf), message.w, message.h);
    var result = await readTransfer(image, false, null, message.session);
    if (!result) result = await readTransfer(image, true, null, message.session);
    // A different threshold can recover some screen glare/uneven illumination.
    // Bound this additional expensive search rather than doing it on every frame.
    if (!result && message.id % 4 === 0) result = await readTransfer(image, true, "GlobalHistogram", message.session);
    if (!result) {
      self.postMessage({ id: message.id, bytes: null });
      return;
    }
    // Never transfer the WASM heap itself. Copy only this symbol's bytes.
    var bytes = new Uint8Array(result.bytes);
    self.postMessage({ id: message.id, bytes: bytes, position: result.position }, [bytes.buffer]);
  } catch (error) {
    self.postMessage({ id: message.id, bytes: null, error: String(error && error.message || error),
      fatal: error && error.name === "RuntimeError" });
  }
};

(async function initialize() {
  try {
    importScripts("/optical-core.js?v=__FERRY_BUILD_ID__", "/zxing-reader.js?v=__FERRY_BUILD_ID__");
    core = self.FerryOptical;
    readBarcodes = self.FerryZXingRead;
    await readBarcodes(new ImageData(8, 8), { formats: ["QRCode"] });
    readerReady = true;
    self.postMessage({ id: -1, ready: true });
  } catch (error) {
    // Initialization failure is not a camera alignment problem.
    self.postMessage({ id: -1, fatal: true, error: String(error && error.message || error) });
  }
})();
