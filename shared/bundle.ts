// DCB1 — a folder of files as one byte string.
//
// This is a PAYLOAD format, not a wire format. A bundle is handed to the
// upstream `packFile()` with the media type below, so it rides the existing
// DCF2 container, the existing LT fountain and the existing 20-byte frame
// header without changing a single byte of any of them. An upstream receiver
// that meets one of these streams reconstructs it correctly and offers it as a
// file it does not recognise; only the unpacking is ours.
//
// Layout:
//
//   0   "DCB1"                       4 bytes
//   4   u32  manifestLen             little-endian
//   8   manifest                     UTF-8 JSON, manifestLen bytes
//   8+manifestLen  file bytes        concatenated in manifest order
//
// The manifest is JSON rather than a packed struct on purpose: paths are
// variable-length text, the whole thing is verified before use, and a human
// reading a hex dump of a failed transfer can see what the sender claimed.

import { checkRelativePath, findPathSetConflict } from "./relative-path";
import { MAX_FILE_BYTES, type OpticalFile } from "./protocol";

export const BUNDLE_MEDIA_TYPE = "application/vnd.decimen.bundle+dcb1";
export const BUNDLE_VERSION = 1;

const MAGIC = new Uint8Array([0x44, 0x43, 0x42, 0x31]); // "DCB1"
const HEADER_LEN = 8;

/**
 * Ceilings. Every one of them is checked against the DECLARED value first (so a
 * hostile manifest cannot make us allocate) and against the MEASURED value
 * after (so a lying manifest cannot get past the first check).
 */
export interface BundleLimits {
  maxFiles: number;
  maxTotalBytes: number;
  maxManifestBytes: number;
}

/**
 * The default ceiling is NOT the format's ceiling, and the difference matters.
 *
 * packFile() stops at MAX_FILE_BYTES (64 MB) and the frame numbering can carry
 * that much at the offered frame sizes, so the format would allow it. What will
 * not carry it is the actual channel and the actual relay device:
 *
 *   TIME.  The upstream README's headline 128 KB/s is phone-to-phone at close
 *          range. A monitor at arm's length is a fraction of that. 64 MB is
 *          nine minutes at the headline rate and half an hour in a room, with
 *          the phone screen on and the page in front the whole time.
 *   MEMORY. The phone is the relay, and the receiving side holds the solved
 *          blocks, the assembled container, the slice unpackFile() takes, the
 *          copy digest() makes, and the gunzip output at once. At 64 MB that
 *          is a quarter of a gigabyte in a mobile tab, and the tab dies.
 *   NO RESUME. The decoder is in-memory. A backgrounded tab loses everything.
 *
 * So the default is 16 MB, the transfer estimate is shown before anything
 * starts, and raising it is a deliberate setting with the reason attached.
 */
export const DEFAULT_MAX_BUNDLE_BYTES = 16 * 1024 * 1024;

/** The most the format and packFile() together can carry, for the setting's
 *  upper stop. Leaves room for the DCF2 header, the manifest and this header,
 *  so a bundle accepted here is never rejected one layer up. */
export const HARD_MAX_BUNDLE_BYTES = MAX_FILE_BYTES - 2 * 1024 * 1024;

export const DEFAULT_BUNDLE_LIMITS: BundleLimits = {
  maxFiles: 2000,
  maxTotalBytes: DEFAULT_MAX_BUNDLE_BYTES,
  maxManifestBytes: 1024 * 1024,
};

/** Limits for a receiver that will accept anything the format can carry —
 *  the headless host on a VPS, which is not memory-constrained and is not
 *  the thing holding a camera steady. */
export const PERMISSIVE_BUNDLE_LIMITS: BundleLimits = {
  maxFiles: 2000,
  maxTotalBytes: HARD_MAX_BUNDLE_BYTES,
  maxManifestBytes: 1024 * 1024,
};

export interface BundleEntry {
  /** Forward-slash relative path. Validated by relative-path.ts on both ends. */
  path: string;
  size: number;
  /** Lowercase hex SHA-256 of this file's bytes. */
  sha256: string;
}

export interface BundleManifest {
  v: number;
  /** What to call this transfer, and the folder name the receiver suggests.
   *  One safe path segment — never a path. */
  label: string;
  count: number;
  totalSize: number;
  files: BundleEntry[];
  /** Files the sender left out because they were not ordinary files
   *  (symlinks, junctions, devices). Reported so the receiver can say so. */
  skipped?: number;
}

export interface BundleFile {
  path: string;
  bytes: Uint8Array;
}

export class BundleError extends Error {
  constructor(
    message: string,
    readonly code: string,
  ) {
    super(message);
    this.name = "BundleError";
  }
}

const textEncoder = new TextEncoder();
// `fatal` so a manifest that is not valid UTF-8 is an error rather than a
// string full of replacement characters that then fails JSON.parse with a
// confusing message.
const textDecoder = new TextDecoder("utf-8", { fatal: true });

async function sha256Hex(bytes: Uint8Array): Promise<string> {
  const stable = Uint8Array.from(bytes);
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", stable));
  let out = "";
  for (const byte of digest) out += byte.toString(16).padStart(2, "0");
  return out;
}

/** Constant-time-ish comparison of two lowercase hex digests. Not a secret, but
 *  there is no reason to leak the position of the first differing byte. */
function digestsEqual(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

function isSafeInteger(value: unknown, max: number): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 && value <= max;
}

/**
 * A transfer label is shown to a human and becomes a folder name, so it goes
 * through the same rules a path segment does — and must be exactly one segment.
 */
export function checkBundleLabel(label: string): { ok: boolean; message?: string } {
  if (typeof label !== "string" || label.trim().length === 0) {
    return { ok: false, message: "転送の名前が空です。" };
  }
  if (label.includes("/")) {
    return { ok: false, message: "転送の名前にフォルダーの区切りは使えません。" };
  }
  const check = checkRelativePath(label);
  if (!check.ok) return { ok: false, message: check.message };
  return { ok: true };
}

function assertNoPathSetConflict(paths: readonly string[]): void {
  const conflict = findPathSetConflict(paths);
  if (!conflict) return;
  throw new BundleError(
    conflict.kind === "duplicate"
      ? "同じ名前のファイルが 2 つ以上含まれています。"
      : "同じ名前がファイルとフォルダーの両方に使われています。",
    conflict.kind === "duplicate" ? "duplicate-path" : "path-conflict",
  );
}

export function isBundle(file: OpticalFile): boolean {
  return file.type === BUNDLE_MEDIA_TYPE;
}

/** Does this byte string start like a bundle? Cheap enough to call on anything. */
export function looksLikeBundle(bytes: Uint8Array): boolean {
  if (bytes.length < HEADER_LEN) return false;
  for (let i = 0; i < MAGIC.length; i++) if (bytes[i] !== MAGIC[i]) return false;
  return true;
}

export async function packBundle(
  label: string,
  files: readonly BundleFile[],
  options: { skipped?: number; limits?: BundleLimits } = {},
): Promise<{ bytes: Uint8Array; manifest: BundleManifest }> {
  const limits = options.limits ?? DEFAULT_BUNDLE_LIMITS;
  const labelCheck = checkBundleLabel(label);
  if (!labelCheck.ok) throw new BundleError(labelCheck.message!, "bad-label");
  if (files.length === 0) {
    throw new BundleError("送るファイルがありません。", "empty");
  }
  if (files.length > limits.maxFiles) {
    throw new BundleError(
      `ファイル数が上限を超えています（${files.length.toLocaleString()} 件 / 上限 ${limits.maxFiles.toLocaleString()} 件）。`,
      "too-many-files",
    );
  }

  for (const file of files) {
    const check = checkRelativePath(file.path);
    if (!check.ok) {
      throw new BundleError(`送れないパスが含まれています: ${check.message}`, "bad-path");
    }
  }
  assertNoPathSetConflict(files.map((file) => file.path));

  let totalSize = 0;
  const entries: BundleEntry[] = [];
  for (const file of files) {
    totalSize += file.bytes.length;
    if (totalSize > limits.maxTotalBytes) {
      throw new BundleError(
        `合計サイズが上限を超えています（上限 ${Math.floor(limits.maxTotalBytes / 1024 / 1024)} MB）。`,
        "too-large",
      );
    }
    entries.push({ path: file.path, size: file.bytes.length, sha256: await sha256Hex(file.bytes) });
  }

  const manifest: BundleManifest = {
    v: BUNDLE_VERSION,
    label,
    count: entries.length,
    totalSize,
    files: entries,
    ...(options.skipped ? { skipped: options.skipped } : {}),
  };
  const manifestBytes = textEncoder.encode(JSON.stringify(manifest));
  if (manifestBytes.length > limits.maxManifestBytes) {
    throw new BundleError("ファイル名の一覧が大きすぎます。", "manifest-too-large");
  }

  const bytes = new Uint8Array(HEADER_LEN + manifestBytes.length + totalSize);
  bytes.set(MAGIC, 0);
  new DataView(bytes.buffer).setUint32(4, manifestBytes.length, true);
  bytes.set(manifestBytes, HEADER_LEN);
  let offset = HEADER_LEN + manifestBytes.length;
  for (const file of files) {
    bytes.set(file.bytes, offset);
    offset += file.bytes.length;
  }
  return { bytes, manifest };
}

/**
 * Structural parse: magic, manifest, declared sizes, paths, collisions, limits.
 *
 * Does NOT verify any file's hash — that costs a pass over every byte, and the
 * caller may want to show the file list before paying it. Nothing may be
 * WRITTEN on the strength of this alone; use verifyBundle() first.
 */
export function readBundleManifest(
  bytes: Uint8Array,
  limits: BundleLimits = DEFAULT_BUNDLE_LIMITS,
): { manifest: BundleManifest; bodyOffset: number } {
  if (bytes.length < HEADER_LEN) {
    throw new BundleError("受信データが短すぎます。", "truncated");
  }
  if (!looksLikeBundle(bytes)) {
    throw new BundleError("フォルダー転送の形式ではありません。", "bad-magic");
  }
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const manifestLen = view.getUint32(4, true);
  // Checked against the ceiling BEFORE it is used as a length, so a declared
  // 4 GB manifest cannot make the subarray below do any work.
  if (manifestLen === 0 || manifestLen > limits.maxManifestBytes) {
    throw new BundleError("ファイル一覧の大きさが不正です。", "bad-manifest-length");
  }
  if (HEADER_LEN + manifestLen > bytes.length) {
    throw new BundleError("受信データが途中で切れています。", "truncated");
  }

  let manifest: BundleManifest;
  try {
    const json = textDecoder.decode(bytes.subarray(HEADER_LEN, HEADER_LEN + manifestLen));
    manifest = JSON.parse(json) as BundleManifest;
  } catch {
    throw new BundleError("ファイル一覧を読み取れません。", "bad-manifest");
  }
  if (manifest === null || typeof manifest !== "object" || Array.isArray(manifest)) {
    throw new BundleError("ファイル一覧を読み取れません。", "bad-manifest");
  }
  if (manifest.v !== BUNDLE_VERSION) {
    throw new BundleError(
      `この版のアプリでは扱えない形式です（形式 ${String(manifest.v)}）。アプリを更新してください。`,
      "unsupported-version",
    );
  }
  const labelCheck = checkBundleLabel(manifest.label);
  if (!labelCheck.ok) {
    throw new BundleError(`転送の名前が不正です: ${labelCheck.message}`, "bad-label");
  }
  if (!Array.isArray(manifest.files)) {
    throw new BundleError("ファイル一覧が壊れています。", "bad-manifest");
  }
  if (manifest.files.length > limits.maxFiles) {
    throw new BundleError(
      `ファイル数が上限を超えています（上限 ${limits.maxFiles.toLocaleString()} 件）。`,
      "too-many-files",
    );
  }
  if (manifest.count !== manifest.files.length) {
    throw new BundleError("ファイル数が一覧と合いません。", "count-mismatch");
  }
  if (!isSafeInteger(manifest.totalSize, limits.maxTotalBytes)) {
    throw new BundleError("合計サイズが不正か、上限を超えています。", "bad-total-size");
  }

  let measured = 0;
  for (const entry of manifest.files) {
    if (entry === null || typeof entry !== "object") {
      throw new BundleError("ファイル一覧が壊れています。", "bad-entry");
    }
    const check = checkRelativePath(entry.path);
    if (!check.ok) {
      throw new BundleError(`受け取れないパスが含まれています: ${check.message}`, "bad-path");
    }
    if (!isSafeInteger(entry.size, limits.maxTotalBytes)) {
      throw new BundleError("ファイルサイズが不正です。", "bad-size");
    }
    if (typeof entry.sha256 !== "string" || !/^[0-9a-f]{64}$/.test(entry.sha256)) {
      throw new BundleError("ハッシュ値の形式が不正です。", "bad-digest");
    }
    measured += entry.size;
    if (measured > limits.maxTotalBytes) {
      throw new BundleError("合計サイズが上限を超えています。", "too-large");
    }
  }
  if (measured !== manifest.totalSize) {
    throw new BundleError("合計サイズが個々のサイズの合計と合いません。", "size-mismatch");
  }
  assertNoPathSetConflict(manifest.files.map((entry) => entry.path));

  const bodyOffset = HEADER_LEN + manifestLen;
  // Exact, not "at least": trailing bytes past the last file mean the sender
  // and the receiver disagree about what this bundle is.
  if (bytes.length !== bodyOffset + manifest.totalSize) {
    throw new BundleError("受信データの長さが一覧と合いません。", "length-mismatch");
  }
  return { manifest, bodyOffset };
}

/** The bytes of one entry, as a view — no copy. */
export function bundleFileBytes(
  bytes: Uint8Array,
  manifest: BundleManifest,
  bodyOffset: number,
  index: number,
): Uint8Array {
  let offset = bodyOffset;
  for (let i = 0; i < index; i++) offset += manifest.files[i]!.size;
  const entry = manifest.files[index]!;
  return bytes.subarray(offset, offset + entry.size);
}

export interface VerifiedBundle {
  manifest: BundleManifest;
  files: BundleFile[];
}

/**
 * Full check: structure, then every file's SHA-256.
 *
 * Hash-before-use, with no exceptions: the returned files are the ONLY thing a
 * caller may write, and they exist only if every digest matched. A partial
 * result is deliberately not offered — "13 of 14 files are fine" is a decision
 * for a human with the whole picture, not something a writer should improvise.
 */
export async function verifyBundle(
  bytes: Uint8Array,
  limits: BundleLimits = DEFAULT_BUNDLE_LIMITS,
): Promise<VerifiedBundle> {
  const { manifest, bodyOffset } = readBundleManifest(bytes, limits);
  const files: BundleFile[] = [];
  let offset = bodyOffset;
  for (const entry of manifest.files) {
    const slice = bytes.subarray(offset, offset + entry.size);
    offset += entry.size;
    const actual = await sha256Hex(slice);
    if (!digestsEqual(actual, entry.sha256)) {
      throw new BundleError(
        `ファイルの中身が壊れています（${manifest.files.indexOf(entry) + 1} 件目）。もう一度受信してください。`,
        "digest-mismatch",
      );
    }
    files.push({ path: entry.path, bytes: slice });
  }
  return { manifest, files };
}

/** Human summary for the UI. Deliberately carries no protocol vocabulary. */
export function describeBundle(manifest: BundleManifest): string {
  const files = `${manifest.count.toLocaleString()} 個のファイル`;
  return manifest.skipped ? `${files}（${manifest.skipped} 個は対象外）` : files;
}
