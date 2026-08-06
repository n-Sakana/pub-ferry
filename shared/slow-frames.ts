// DCS1 — the one-frame-at-a-time mode, for handheld 2D code readers.
//
// A separate mode on purpose. The animated-QR path assumes a camera watching a
// moving screen: frames arrive continuously, most of them decode, and the
// fountain absorbs the misses. A handheld reader is nothing like that. Somebody
// points it at ONE code, pulls the trigger, and the reader types the decoded
// text into whatever has keyboard focus. There is no stream, no frame rate, and
// no tolerance for a missed frame — so the frames are numbered, complete, and
// individually checked instead of fountain-coded.
//
// Two constraints drive the framing, and both come from the hardware:
//
// 1. A keyboard-wedge reader TYPES its result. Anything it types goes through
//    the OS keyboard layout, so a character that needs Shift or AltGr on the
//    host's layout but not on the reader's arrives as a different character.
//    The alphabet here is therefore A-Z, 0-9 and "-" only: every one of those
//    is an unshifted key on both US and JIS layouts, so nothing is mangled.
//    Case is not trusted either — a reader with the wrong caps-lock behaviour
//    types the whole line in lower case, so parsing upper-cases first.
//
// 2. Those same characters are exactly QR's *alphanumeric* mode, which packs
//    two of them into 11 bits instead of one byte each. Base32 costs 1.6
//    characters per byte, and alphanumeric mode buys 1.45 of that back, so the
//    payload density is within ~10% of raw byte mode while surviving the
//    keyboard path. Byte-mode binary through a wedge is simply not decodable.
//
// A frame:
//
//   DCS1-<docId>-<index>-<total>-<crc32>-<payload>
//
//   docId    6 base32 chars. Frames from two different documents never merge.
//   index    decimal, 0-based.
//   total    decimal, frame count.
//   crc32    8 uppercase hex of the payload BYTES (not the base32 text).
//   payload  base32 (RFC 4648, no padding) of this frame's slice.
//
// The CRC is not redundant with QR's own error correction. QR protects the
// symbol; the CRC protects the *line* — a wedge that drops characters on a long
// string, a paste of the wrong line, or a scan of a frame from a different
// print-out are all things ECC never sees. SHA-256 on the reassembled container
// stays the authority; the CRC is what lets the receiver reject one bad frame
// and ask for that one again instead of failing at the end.

const BASE32_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
const BASE32_VALUES = new Map<string, number>(
  [...BASE32_ALPHABET].map((character, index) => [character, index]),
);

export const SLOW_PREFIX = "DCS1";
export const SLOW_DOC_ID_LENGTH = 6;

/** Payload bytes per frame. 800 bytes is 1280 base32 characters plus framing,
 *  which fits QR version 30 at ECC M in alphanumeric mode — dense enough that a
 *  small folder is a couple of dozen scans, sparse enough to read off a phone
 *  screen held at a comfortable distance. */
export const DEFAULT_SLOW_CHUNK_BYTES = 800;

/**
 * The ceiling, and it is a HUMAN ceiling rather than a format one.
 *
 * Every frame here is a person aiming a reader, pulling a trigger, and looking
 * up to check the count. Sixty of those is a few minutes of honest work; four
 * hundred is nobody's afternoon. The format could carry more, and letting it
 * would mean the tool cheerfully starts a job that gets abandoned at frame 180.
 * 60 × 800 bytes is about 48 KB — the size of a document or a handful of
 * configuration files, which is what this mode is actually for.
 */
export const MAX_SLOW_FRAMES = 60;

/** Payload bytes this mode can carry at the default frame size. */
export const MAX_SLOW_PAYLOAD_BYTES = MAX_SLOW_FRAMES * DEFAULT_SLOW_CHUNK_BYTES;

export function base32Encode(bytes: Uint8Array): string {
  let out = "";
  let buffer = 0;
  let bits = 0;
  for (const byte of bytes) {
    buffer = (buffer << 8) | byte;
    bits += 8;
    while (bits >= 5) {
      out += BASE32_ALPHABET[(buffer >>> (bits - 5)) & 31];
      bits -= 5;
    }
  }
  if (bits > 0) out += BASE32_ALPHABET[(buffer << (5 - bits)) & 31];
  return out;
}

export function base32Decode(text: string): Uint8Array | null {
  const clean = text.toUpperCase();
  const out = new Uint8Array(Math.floor((clean.length * 5) / 8));
  let buffer = 0;
  let bits = 0;
  let written = 0;
  for (const character of clean) {
    const value = BASE32_VALUES.get(character);
    if (value === undefined) return null;
    buffer = (buffer << 5) | value;
    bits += 5;
    if (bits >= 8) {
      out[written++] = (buffer >>> (bits - 8)) & 0xff;
      bits -= 8;
    }
  }
  // Leftover bits must be zero padding, not data the length calculation lost.
  if (bits >= 5 || (buffer & ((1 << bits) - 1)) !== 0) return null;
  return written === out.length ? out : out.subarray(0, written);
}

const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c >>> 0;
  }
  return table;
})();

/** CRC-32 (IEEE 802.3), the same polynomial gzip and PNG use. */
export function crc32(bytes: Uint8Array): number {
  let crc = 0xffffffff;
  for (const byte of bytes) crc = CRC_TABLE[(crc ^ byte) & 0xff]! ^ (crc >>> 8);
  return (crc ^ 0xffffffff) >>> 0;
}

function crcHex(value: number): string {
  return value.toString(16).toUpperCase().padStart(8, "0");
}

/** A document id from the payload itself, so re-encoding the same bytes gives
 *  the same id and a half-finished scan can be resumed from a fresh print-out. */
export function slowDocId(payload: Uint8Array): string {
  // FNV-1a over the payload, folded into base32. Not a security property —
  // this only has to make accidental collisions between two documents someone
  // is scanning in the same session vanishingly unlikely.
  let h = 0x811c9dc5;
  for (const byte of payload) {
    h ^= byte;
    h = Math.imul(h, 0x01000193);
  }
  let mixed = (h ^ payload.length) >>> 0;
  let id = "";
  for (let i = 0; i < SLOW_DOC_ID_LENGTH; i++) {
    id += BASE32_ALPHABET[mixed & 31];
    mixed = (mixed >>> 5) | (Math.imul(mixed, 0x9e3779b1) & 0xffff0000);
    mixed >>>= 0;
  }
  return id;
}

export interface SlowFrame {
  docId: string;
  index: number;
  total: number;
  payload: Uint8Array;
}

export function encodeSlowFrames(
  payload: Uint8Array,
  chunkBytes: number = DEFAULT_SLOW_CHUNK_BYTES,
): string[] {
  if (payload.length === 0) throw new Error("送る中身がありません。");
  if (!Number.isInteger(chunkBytes) || chunkBytes < 16 || chunkBytes > 2000) {
    throw new Error("1 枚あたりのバイト数の指定が不正です。");
  }
  const total = Math.ceil(payload.length / chunkBytes);
  if (total > MAX_SLOW_FRAMES) {
    throw new Error(
      `1 枚ずつ読む方法では ${MAX_SLOW_FRAMES} 枚までです（この内容は ${total} 枚必要）。` +
        `カメラで受け取る方法を使ってください。`,
    );
  }
  const docId = slowDocId(payload);
  const frames: string[] = [];
  for (let index = 0; index < total; index++) {
    const slice = payload.subarray(index * chunkBytes, Math.min((index + 1) * chunkBytes, payload.length));
    const body = `${SLOW_PREFIX}-${docId}-${index}-${total}-${base32Encode(slice)}`;
    frames.push(insertCrc(body));
  }
  return frames;
}

const asciiEncoder = new TextEncoder();

/**
 * The checksum covers the WHOLE line, not just the payload.
 *
 * A CRC over the payload alone leaves the frame number unprotected, and a
 * mangled number is the one corruption that does real damage: the block is
 * intact, its checksum passes, and it is filed at the wrong offset. The
 * transfer then completes and fails SHA-256 at the very end with nothing to
 * point at. Covering the header makes that frame get rejected on the spot,
 * where the operator can simply scan it again.
 */
function insertCrc(body: string): string {
  const crc = crcHex(crc32(asciiEncoder.encode(body)));
  const cut = body.lastIndexOf("-");
  return `${body.slice(0, cut)}-${crc}${body.slice(cut)}`;
}

export type SlowParseError =
  | "not-a-frame"
  | "bad-fields"
  | "bad-index"
  | "bad-payload"
  | "crc-mismatch";

export function parseSlowFrame(text: string): { frame: SlowFrame } | { error: SlowParseError } {
  // Readers append Enter or Tab, and some prepend a prefix character. Trim
  // whitespace, upper-case for caps-lock inversion, and drop anything before
  // the marker so a stray keystroke ahead of the scan is not fatal.
  const upper = text.trim().toUpperCase();
  const start = upper.indexOf(SLOW_PREFIX + "-");
  if (start === -1) return { error: "not-a-frame" };
  const parts = upper.slice(start).split("-");
  if (parts.length !== 6) return { error: "bad-fields" };
  const [, docId, indexText, totalText, crcText, payloadText] = parts as [
    string, string, string, string, string, string,
  ];
  if (!/^[A-Z2-7]{6}$/.test(docId)) return { error: "bad-fields" };
  if (!/^\d{1,4}$/.test(indexText) || !/^\d{1,4}$/.test(totalText)) return { error: "bad-fields" };
  if (!/^[0-9A-F]{8}$/.test(crcText)) return { error: "bad-fields" };
  const index = Number(indexText);
  const total = Number(totalText);
  if (total < 1 || total > MAX_SLOW_FRAMES || index >= total) return { error: "bad-index" };
  const payload = base32Decode(payloadText);
  if (payload === null || payload.length === 0) return { error: "bad-payload" };
  const body = `${SLOW_PREFIX}-${docId}-${indexText}-${totalText}-${payloadText}`;
  if (crcHex(crc32(asciiEncoder.encode(body))) !== crcText) return { error: "crc-mismatch" };
  return { frame: { docId, index, total, payload } };
}

export type SlowAcceptResult =
  | { kind: "accepted"; index: number; received: number; total: number }
  | { kind: "duplicate"; index: number; received: number; total: number }
  | { kind: "other-document"; docId: string }
  | { kind: "inconsistent"; message: string }
  | { kind: "rejected"; error: SlowParseError; message: string };

const PARSE_MESSAGES: Record<SlowParseError, string> = {
  "not-a-frame": "この転送のコードではありません。もう一度読み取ってください。",
  "bad-fields": "コードの内容が壊れています。もう一度読み取ってください。",
  "bad-index": "コードの番号が範囲外です。別の転送のコードかもしれません。",
  "bad-payload": "コードの中身を読み取れませんでした。もう一度読み取ってください。",
  "crc-mismatch": "読み取りに欠けがありました。同じコードをもう一度読み取ってください。",
};

/**
 * Collects frames until the set is complete.
 *
 * Deliberately stateful and deliberately strict: a frame that disagrees with
 * what is already collected (different total, different chunk size) is a frame
 * from a different print-out of the same document, and merging them would
 * produce a byte string that passes every per-frame CRC and fails SHA-256 half
 * an hour later.
 */
export class SlowFrameCollector {
  private frames = new Map<number, Uint8Array>();
  private chunkBytes: number | null = null;
  docId: string | null = null;
  total = 0;

  get received(): number {
    return this.frames.size;
  }

  get isComplete(): boolean {
    return this.total > 0 && this.frames.size === this.total;
  }

  /** Has this frame (0-based) been collected? */
  has(index: number): boolean {
    return this.frames.has(index);
  }

  /** Frame numbers still wanted, 1-based for display. Capped so a UI showing
   *  "missing: …" cannot become a wall of numbers. */
  missing(limit = 12): number[] {
    if (this.total === 0) return [];
    const out: number[] = [];
    for (let index = 0; index < this.total && out.length < limit; index++) {
      if (!this.frames.has(index)) out.push(index + 1);
    }
    return out;
  }

  reset(): void {
    this.frames.clear();
    this.chunkBytes = null;
    this.docId = null;
    this.total = 0;
  }

  accept(text: string): SlowAcceptResult {
    const parsed = parseSlowFrame(text);
    if ("error" in parsed) {
      return { kind: "rejected", error: parsed.error, message: PARSE_MESSAGES[parsed.error] };
    }
    const { docId, index, total, payload } = parsed.frame;

    if (this.docId === null) {
      this.docId = docId;
      this.total = total;
    } else if (docId !== this.docId) {
      return { kind: "other-document", docId };
    } else if (total !== this.total) {
      return {
        kind: "inconsistent",
        message: "枚数の違うコードが混ざっています。最初からやり直してください。",
      };
    }

    // Every frame but the last carries a full chunk. The first one that is not
    // the last frame defines the chunk size; anything disagreeing is from a
    // different encoding of the same document.
    const isLast = index === total - 1;
    if (!isLast) {
      if (this.chunkBytes === null) this.chunkBytes = payload.length;
      else if (payload.length !== this.chunkBytes) {
        return {
          kind: "inconsistent",
          message: "大きさの違うコードが混ざっています。最初からやり直してください。",
        };
      }
    } else if (this.chunkBytes !== null && payload.length > this.chunkBytes) {
      return {
        kind: "inconsistent",
        message: "大きさの違うコードが混ざっています。最初からやり直してください。",
      };
    }

    if (this.frames.has(index)) {
      return { kind: "duplicate", index, received: this.frames.size, total: this.total };
    }
    this.frames.set(index, payload);
    return { kind: "accepted", index, received: this.frames.size, total: this.total };
  }

  /** The reassembled payload, or null while frames are still missing. */
  assemble(): Uint8Array | null {
    if (!this.isComplete) return null;
    let length = 0;
    for (let index = 0; index < this.total; index++) length += this.frames.get(index)!.length;
    const out = new Uint8Array(length);
    let offset = 0;
    for (let index = 0; index < this.total; index++) {
      const chunk = this.frames.get(index)!;
      out.set(chunk, offset);
      offset += chunk.length;
    }
    return out;
  }
}
