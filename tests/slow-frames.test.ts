import { test } from "node:test";
import assert from "node:assert/strict";
import {
  DEFAULT_SLOW_CHUNK_BYTES,
  MAX_SLOW_FRAMES,
  SlowFrameCollector,
  base32Decode,
  base32Encode,
  crc32,
  encodeSlowFrames,
  parseSlowFrame,
  slowDocId,
} from "../shared/slow-frames";

const bytes = (length: number, seed = 1): Uint8Array => {
  const out = new Uint8Array(length);
  let state = seed;
  for (let i = 0; i < length; i++) {
    state = (state * 1103515245 + 12345) >>> 0;
    out[i] = state >>> 24;
  }
  return out;
};

test("the frame alphabet is only characters a keyboard-wedge reader can type", () => {
  // A-Z, 0-9 and "-": every one an unshifted key on both US and JIS layouts,
  // and exactly QR's alphanumeric set. A symbol outside this survives neither
  // a layout mismatch nor alphanumeric encoding.
  for (const frame of encodeSlowFrames(bytes(2500))) {
    assert.match(frame, /^[A-Z0-9-]+$/, `frame contains a character a wedge could mangle: ${frame}`);
  }
});

test("a payload survives being cut into frames and put back together", () => {
  for (const length of [1, 799, 800, 801, 4321]) {
    const payload = bytes(length, length);
    const frames = encodeSlowFrames(payload);
    const collector = new SlowFrameCollector();
    for (const frame of frames) assert.equal(collector.accept(frame).kind, "accepted");
    assert.equal(collector.isComplete, true);
    assert.deepEqual(Array.from(collector.assemble()!), Array.from(payload));
  }
});

test("frames may arrive in any order", () => {
  const payload = bytes(3000);
  const frames = encodeSlowFrames(payload);
  const shuffled = [...frames].reverse();
  const collector = new SlowFrameCollector();
  for (const frame of shuffled) collector.accept(frame);
  assert.deepEqual(Array.from(collector.assemble()!), Array.from(payload));
});

test("a frame read twice is counted once and reported as a duplicate", () => {
  const frames = encodeSlowFrames(bytes(2000));
  const collector = new SlowFrameCollector();
  assert.equal(collector.accept(frames[0]!).kind, "accepted");
  const again = collector.accept(frames[0]!);
  assert.equal(again.kind, "duplicate");
  assert.equal(collector.received, 1);
});

test("a missing frame is named, not merely counted", () => {
  const frames = encodeSlowFrames(bytes(3000));
  const collector = new SlowFrameCollector();
  for (let i = 0; i < frames.length; i++) if (i !== 2) collector.accept(frames[i]!);
  assert.equal(collector.isComplete, false);
  assert.equal(collector.assemble(), null);
  assert.deepEqual(collector.missing(), [3]); // 1-based for a human
});

test("a corrupted line is rejected on the spot instead of at the end", () => {
  const frames = encodeSlowFrames(bytes(2000));
  const original = frames[0]!;
  // One character of the payload flipped: exactly what a wedge dropping a
  // keystroke looks like.
  const flipped = original.slice(0, -1) + (original.endsWith("A") ? "B" : "A");
  assert.deepEqual(parseSlowFrame(flipped), { error: "crc-mismatch" });
  const collector = new SlowFrameCollector();
  const result = collector.accept(flipped);
  assert.equal(result.kind, "rejected");
  assert.equal(collector.received, 0);
});

test("a mangled frame NUMBER is caught, because the checksum covers it", () => {
  // The corruption that would otherwise be silent: the block is intact, so a
  // payload-only checksum passes and the block is filed at the wrong offset.
  // The transfer then completes and fails its hash at the very end.
  const frames = encodeSlowFrames(bytes(3000));
  const parts = frames[1]!.split("-");
  parts[2] = "0";
  assert.deepEqual(parseSlowFrame(parts.join("-")), { error: "crc-mismatch" });
});

test("frames from a different document are refused, not merged", () => {
  const a = encodeSlowFrames(bytes(2000, 1));
  const b = encodeSlowFrames(bytes(2000, 2));
  assert.notEqual(a[0]!.split("-")[1], b[0]!.split("-")[1]);
  const collector = new SlowFrameCollector();
  collector.accept(a[0]!);
  const foreign = collector.accept(b[1]!);
  assert.equal(foreign.kind, "other-document");
  assert.equal(collector.received, 1);
});

test("a re-print of the same document at a different frame size is refused", () => {
  // Same bytes, so the same document id — but the offsets are different, and
  // merging them produces a byte string where every per-frame checksum passes.
  const payload = bytes(3000);
  const wide = encodeSlowFrames(payload, 800);
  const narrow = encodeSlowFrames(payload, 500);
  assert.equal(wide[0]!.split("-")[1], narrow[0]!.split("-")[1]);
  const collector = new SlowFrameCollector();
  collector.accept(wide[0]!);
  const mixed = collector.accept(narrow[1]!);
  assert.equal(mixed.kind, "inconsistent");
});

test("case is not trusted, because CapsLock on the host inverts the whole line", () => {
  const frame = encodeSlowFrames(bytes(500))[0]!;
  const parsed = parseSlowFrame(frame.toLowerCase());
  assert.ok("frame" in parsed);
});

test("a reader's terminator, and a stray keystroke before the scan, are tolerated", () => {
  const frame = encodeSlowFrames(bytes(500))[0]!;
  for (const noisy of [`${frame}\r\n`, `${frame}\t`, `  ${frame}  `, `X${frame}`]) {
    assert.ok("frame" in parseSlowFrame(noisy), `failed on ${JSON.stringify(noisy.slice(0, 12))}`);
  }
});

test("something that is not one of our frames at all says so", () => {
  for (const text of ["", "hello", "1234567890128", "DCS1", "DCS1-A-B"]) {
    const parsed = parseSlowFrame(text);
    assert.ok("error" in parsed, `${JSON.stringify(text)} was parsed as a frame`);
  }
});

test("the mode refuses a job a person will not finish", () => {
  const tooBig = MAX_SLOW_FRAMES * DEFAULT_SLOW_CHUNK_BYTES + 1;
  assert.throws(() => encodeSlowFrames(bytes(tooBig)), /枚まで/);
  assert.doesNotThrow(() => encodeSlowFrames(bytes(MAX_SLOW_FRAMES * DEFAULT_SLOW_CHUNK_BYTES)));
});

test("base32 round-trips every byte value and refuses anything outside its alphabet", () => {
  const all = new Uint8Array(256);
  for (let i = 0; i < 256; i++) all[i] = i;
  assert.deepEqual(Array.from(base32Decode(base32Encode(all))!), Array.from(all));
  assert.equal(base32Decode("ABC!"), null);
  assert.equal(base32Decode("ABC1"), null); // 0/1/8/9 are not in RFC 4648 base32
});

test("golden vectors — the framing is fixed bytes, not merely self-consistent", () => {
  // A print-out made months ago has to still be readable by a current build,
  // so these are pinned rather than derived. Changing any of them is a
  // breaking change to what a scanner reads.
  const payload = new Uint8Array([0, 1, 2, 253, 254, 255]);
  assert.equal(base32Encode(payload), "AAAQF7P674");
  assert.equal(crc32(payload), 0x3c8a83a5);
  assert.equal(slowDocId(payload), "S6VC75");
  assert.deepEqual(encodeSlowFrames(payload, 16), ["DCS1-S6VC75-0-1-36536793-AAAQF7P674"]);

  const twoFrames = new Uint8Array(20);
  for (let i = 0; i < twoFrames.length; i++) twoFrames[i] = (i * 13) & 0xff;
  assert.deepEqual(encodeSlowFrames(twoFrames, 16), [
    "DCS1-5LCR54-0-2-3A6AB042-AAGRUJZUIFHFW2DVQKHZZKNWYM",
    "DCS1-5LCR54-1-2-93B86F05-2DO6V5Y",
  ]);
});

test("reset clears the collector so a fresh document can be started", () => {
  const collector = new SlowFrameCollector();
  collector.accept(encodeSlowFrames(bytes(2000, 1))[0]!);
  collector.reset();
  assert.equal(collector.received, 0);
  assert.equal(collector.docId, null);
  assert.equal(collector.accept(encodeSlowFrames(bytes(2000, 2))[0]!).kind, "accepted");
});
