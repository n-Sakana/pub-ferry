// Relative-path validation for received bundles.
//
// Every path in a bundle manifest arrived over the optical channel, which means
// it is whatever the other screen chose to display. The receiver joins it onto a
// destination folder and creates a file there, so this is the function standing
// between "a folder arrived" and "something was written outside the folder you
// picked".
//
// It runs on BOTH ends. The sender doing it is a courtesy — a path it cannot
// build is a path the receiver would reject anyway, so refuse early and say so.
// The receiver doing it is the part that matters, and the receiver never trusts
// that the sender did it.
//
// Deliberately stricter than any one filesystem: the same bundle may be received
// on Windows, on Linux and on a phone, and a path that is a directory traversal
// on one and merely odd on another is still a bug. What passes here is a plain
// forward-slash relative path made of ordinary file names.

/**
 * The whole relative path.
 *
 * Windows' default MAX_PATH is 260 characters for the FULL path, and the
 * receiver joins this onto a destination folder it does not control the length
 * of. 200 leaves room for a destination like `C:\Users\...\受信\` without
 * needing long-path support turned on, and a bundle refused here is refused
 * with a sentence instead of failing at the last file with an OS error code.
 */
export const MAX_PATH_LENGTH = 200;

/**
 * One segment. Deliberately below MAX_PATH_LENGTH so that both limits are
 * reachable: a 190-character file name is refused as a name that is too long,
 * which is a fixable thing to be told, rather than as a path that is too long,
 * which sounds like the folder is too deep.
 */
export const MAX_SEGMENT_LENGTH = 180;

/**
 * How deep a bundle may nest.
 *
 * The length limit alone permits `a/a/a/…` a hundred levels down, which is a
 * hundred directories created from one manifest line and a tree no receiving
 * user asked for. Real folders people hand to each other are a few levels deep.
 */
export const MAX_PATH_DEPTH = 32;

/** Segments Windows refuses to create, with or without an extension —
 *  `CON`, `con.txt` and `CON.` are all the console device. */
const RESERVED_NAMES = new Set([
  "CON", "PRN", "AUX", "NUL",
  "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
  "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
  // Rarely documented but real: COM0/LPT0 are reserved on current Windows,
  // and the superscript digits ¹²³ alias COM1/COM2/COM3.
  "COM0", "LPT0",
]);

/** `<>:"|?*`, backslash, and everything below 0x20 plus DEL. Forward slash is
 *  handled separately because it is the segment separator, not a character. */
const FORBIDDEN_CHARS = /[\u0000-\u001f\u007f<>:"|?*\\]/;

/**
 * Characters that are invisible or that reorder the rendering of the text
 * around them. `report.txt\u202Egpj.exe` renders as `report.txtexe.jpg` in every
 * file manager — the extension you see is not the extension you get.
 */
const DECEPTIVE_CHARS =
  /[\u200b-\u200f\u202a-\u202e\u2066-\u2069\ufeff\u00ad\u2028\u2029]/;

export type PathRejection =
  | "empty"
  | "too-long"
  | "segment-too-long"
  | "absolute"
  | "drive-relative"
  | "unc"
  | "backslash"
  | "traversal"
  | "dot-segment"
  | "empty-segment"
  | "forbidden-character"
  | "deceptive-character"
  | "reserved-name"
  | "trailing-dot-or-space"
  | "leading-space"
  | "too-deep"
  | "lone-surrogate"
  | "not-normalized";

export interface PathCheck {
  ok: boolean;
  reason?: PathRejection;
  /** A sentence to show a human. Never contains the path itself — a hostile
   *  path is the last thing to paste into a log or a label unescaped. */
  message?: string;
}

const MESSAGES: Record<PathRejection, string> = {
  "empty": "パスが空です。",
  "too-long": `パスが長すぎます（${MAX_PATH_LENGTH} 文字まで）。`,
  "segment-too-long": `名前が長すぎる部分があります（${MAX_SEGMENT_LENGTH} 文字まで）。`,
  "absolute": "絶対パスは受け取れません。",
  "drive-relative": "ドライブ名付きのパスは受け取れません。",
  "unc": "ネットワークパスは受け取れません。",
  "backslash": "円記号（\\）を含むパスは受け取れません。",
  "traversal": "上位フォルダーを指すパス（..）は受け取れません。",
  "dot-segment": "「.」だけの部分を含むパスは受け取れません。",
  "empty-segment": "区切りが連続しているパスは受け取れません。",
  "forbidden-character": "ファイル名に使えない文字が含まれています。",
  "deceptive-character": "表示を偽装できる不可視文字が含まれています。",
  "reserved-name": "Windows が予約している名前が含まれています。",
  "trailing-dot-or-space": "末尾がドットまたは空白の名前は受け取れません。",
  "leading-space": "先頭が空白の名前は受け取れません。",
  "too-deep": `フォルダーの階層が深すぎます（${MAX_PATH_DEPTH} 段まで）。`,
  "lone-surrogate": "ファイル名に不正な文字が含まれています。",
  "not-normalized": "文字の正規化形が Unicode NFC ではありません。",
};

function reject(reason: PathRejection): PathCheck {
  return { ok: false, reason, message: MESSAGES[reason] };
}

/**
 * Is this a relative path we are willing to create inside a destination folder?
 *
 * Returns a reason rather than throwing, because the caller wants to report how
 * many files were rejected and why, not stop at the first one.
 */
export function checkRelativePath(path: string): PathCheck {
  if (typeof path !== "string" || path.length === 0) return reject("empty");
  if (path.length > MAX_PATH_LENGTH) return reject("too-long");

  // Checked before anything is split: a backslash is a separator on Windows, so
  // `a\..\..\b` is a traversal there and one long file name everywhere else.
  // Refusing it outright means the split below is the only separator rule.
  if (path.includes("\\")) {
    if (path.startsWith("\\\\")) return reject("unc");
    return reject("backslash");
  }
  if (path.startsWith("/")) return reject("absolute");
  // `C:x` is relative to the current directory OF DRIVE C on Windows, which is
  // not the destination folder. `C:/x` is plainly absolute. Both die here.
  if (/^[A-Za-z]:/.test(path)) return reject("drive-relative");

  if (DECEPTIVE_CHARS.test(path)) return reject("deceptive-character");
  if (FORBIDDEN_CHARS.test(path)) return reject("forbidden-character");
  // An unpaired surrogate is not a character. JavaScript will carry one around
  // happily; .NET's IsNormalized THROWS on it, and Linux maps it to U+FFFD on
  // the way to the filesystem — so two names that differ only in their lone
  // surrogate become one file, and the second write fails after the first has
  // already landed. Two receivers disagreeing about the same bundle is exactly
  // what this module exists to prevent.
  if (/[\ud800-\udfff]/.test(path.replace(/[\ud800-\udbff][\udc00-\udfff]/g, ""))) {
    return reject("lone-surrogate");
  }

  // Two spellings of the same file name must not both be creatable, or a bundle
  // can carry `a\u0301.txt` and `á.txt` and quietly overwrite one with the
  // other on a filesystem that normalises. Pin one form and require it.
  if (path.normalize("NFC") !== path) return reject("not-normalized");

  const segments = path.split("/");
  if (segments.length > MAX_PATH_DEPTH) return reject("too-deep");
  for (const segment of segments) {
    if (segment.length === 0) {
      // A trailing slash means "this is a directory", which a file list has no
      // use for; a doubled slash is a path we did not write.
      return reject("empty-segment");
    }
    if (segment.length > MAX_SEGMENT_LENGTH) return reject("segment-too-long");
    if (segment === "..") return reject("traversal");
    if (segment === ".") return reject("dot-segment");
    // Windows silently strips these when creating, so `a. ` and `a` become the
    // same file — a way to have two manifest entries land on one path.
    if (/[. ]$/.test(segment)) return reject("trailing-dot-or-space");
    if (/^ /.test(segment)) return reject("leading-space");

    // The reserved-name check has to look past an extension, and past every
    // extension: `CON.txt.bak` is still CON. Windows compares case-insensitively
    // and ignores trailing dots/spaces, which are already refused above.
    const base = segment.split(".")[0]!.toUpperCase();
    if (RESERVED_NAMES.has(base)) return reject("reserved-name");
    // Superscript digits alias the console device names on Windows.
    if (/^(COM|LPT)[\u00b9\u00b2\u00b3]$/.test(segment.split(".")[0]!.toUpperCase())) {
      return reject("reserved-name");
    }
  }

  return { ok: true };
}

export function isSafeRelativePath(path: string): boolean {
  return checkRelativePath(path).ok;
}

/**
 * The key two manifest entries collide on.
 *
 * Windows and macOS both fold case, so `A.txt` and `a.txt` are one file there
 * and two on Linux. A bundle that carries both is either a mistake or an
 * attempt to have the second write land on the first — refuse it everywhere,
 * so the same bundle behaves the same on every receiver.
 *
 * Normalisation is already pinned to NFC by checkRelativePath, so this only has
 * to fold case.
 */
export function pathCollisionKey(path: string): string {
  return path.toLowerCase();
}

export type PathSetConflict =
  | { kind: "duplicate"; index: number }
  | { kind: "file-and-directory"; index: number };

/**
 * Conflicts a *set* of paths can have that no single path can.
 *
 * Two of them, and the second is the one that gets missed: a manifest carrying
 * both `a` and `a/b.txt` needs `a` to be a file and a directory at once. Whether
 * that fails, half-writes, or silently overwrites depends on the order the
 * writer happens to use — so it is refused before anything is written, and the
 * same bundle is refused on every receiver rather than landing differently on
 * each. Case is folded, because `a` and `A/b.txt` collide on Windows too.
 *
 * Returns the index of the entry that introduced the conflict, so the caller
 * can name it.
 */
export function findPathSetConflict(paths: readonly string[]): PathSetConflict | null {
  const files = new Set<string>();
  const directories = new Set<string>();
  for (let index = 0; index < paths.length; index++) {
    const key = pathCollisionKey(paths[index]!);
    if (files.has(key) || directories.has(key)) {
      return { kind: files.has(key) ? "duplicate" : "file-and-directory", index };
    }
    const segments = key.split("/");
    for (let depth = 1; depth < segments.length; depth++) {
      const prefix = segments.slice(0, depth).join("/");
      if (files.has(prefix)) return { kind: "file-and-directory", index };
      directories.add(prefix);
    }
    files.add(key);
  }
  return null;
}

/**
 * Turn an arbitrary OS path into the relative path a bundle carries.
 *
 * Windows separators become `/`, and a leading `./` is dropped. The result is
 * still put through checkRelativePath by the caller — this normalises spelling,
 * it does not make an unsafe path safe.
 */
export function toBundlePath(osPath: string): string {
  return osPath.replace(/\\/g, "/").replace(/^\.\//, "").normalize("NFC");
}
