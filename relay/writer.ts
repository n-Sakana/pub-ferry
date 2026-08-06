// Writing a verified bundle onto a disk, safely.
//
// Everything before this point has been checking a byte string. This is where
// the byte string becomes files on somebody's machine, so it re-checks rather
// than trusting the checks upstream of it, and it works in an order where a
// failure at any step leaves nothing behind.
//
// The order matters and is the whole design:
//
//   1. Every path is validated AGAIN, here, against the same rules.
//   2. A staging folder is created INSIDE the destination folder — not in the
//      system temp directory. A rename across volumes is a recursive copy, and
//      a recursive copy is not atomic; a rename within one folder is.
//   3. Files are created with the "fail if it exists" flag, so a name we did
//      not expect stops the write instead of silently replacing something.
//   4. Every resolved path is compared against the destination root before it
//      is opened, which is what catches a junction that was ALREADY in the
//      destination folder pointing somewhere else. String checks cannot see
//      that; only resolving can.
//   5. On success the staging folder is renamed to its final name. On any
//      failure it is removed whole.

import { accessSync, closeSync, constants, existsSync, lstatSync, mkdirSync, openSync, renameSync, rmSync, statSync, writeFileSync, writeSync } from "node:fs";
import { join, resolve, sep } from "node:path";
import { randomBytes } from "node:crypto";
import { platform } from "node:os";
import { checkRelativePath, findPathSetConflict, pathCollisionKey } from "../shared/relative-path";
import type { BundleFile, BundleManifest } from "../shared/bundle";

export class WriteError extends Error {
  constructor(
    message: string,
    readonly code: string,
  ) {
    super(message);
    this.name = "WriteError";
  }
}

export interface WriteResult {
  /** Folder name created under the destination, relative to it. Never an
   *  absolute path: the phone has no business learning the host's layout. */
  savedAs: string;
  fileCount: number;
  totalSize: number;
  /** Always 0 here — a bundle lands in its own new folder, so nothing it
   *  contains can collide with anything that was already there. Kept in the
   *  shape because the desktop app can also write into an existing folder. */
  renamed: number;
}

/**
 * Is `candidate` really inside `root`?
 *
 * Resolved, not string-compared: the destination folder may already contain a
 * junction or a symlink that somebody else put there, and a perfectly innocent
 * relative path through it lands outside. The trailing separator matters —
 * without it, `C:\dataEVIL` passes a prefix test against `C:\data`.
 */
export function isInside(root: string, candidate: string): boolean {
  const normalisedRoot = resolve(root);
  const normalisedCandidate = resolve(candidate);
  if (normalisedCandidate === normalisedRoot) return true;
  const withSeparator = normalisedRoot.endsWith(sep) ? normalisedRoot : normalisedRoot + sep;
  return normalisedCandidate.startsWith(withSeparator);
}

/** A directory that is a reparse point (a junction, or a symlink) is a door
 *  out of the folder we were given, whoever created it. */
function assertNotALink(path: string): void {
  let info;
  try {
    info = lstatSync(path);
  } catch {
    return; // does not exist yet, which is the ordinary case
  }
  if (info.isSymbolicLink()) {
    throw new WriteError(
      "保存先の途中にリンクがあります。別の場所を指している可能性があるため中止しました。",
      "link-in-path",
    );
  }
}

/** A name that is free inside `parent`, trying `name`, `name (2)`, … */
function freeName(parent: string, name: string): string {
  if (!existsSync(join(parent, name))) return name;
  for (let n = 2; n < 1000; n++) {
    const candidate = `${name} (${n})`;
    if (!existsSync(join(parent, candidate))) return candidate;
  }
  throw new WriteError("保存先に空いている名前が見つかりませんでした。", "no-free-name");
}

/**
 * Mark of the Web.
 *
 * These files came from another machine over a channel nobody authenticates at
 * the content level. Without this stream Office opens a received .docm with
 * macros live and SmartScreen never runs. Writing it is the difference between
 * "the user double-clicked a file from the internet" behaving like it should
 * and behaving like a local file they made themselves.
 *
 * Best-effort: a destination on a filesystem without alternate data streams
 * (a FAT32 stick, a network share) simply cannot carry it, and that is not a
 * reason to fail a transfer that is otherwise complete.
 */
function markOfTheWeb(filePath: string): boolean {
  if (platform() !== "win32") return false;
  try {
    writeFileSync(`${filePath}:Zone.Identifier`, "[ZoneTransfer]\r\nZoneId=3\r\n", "utf8");
    return true;
  } catch {
    return false;
  }
}

/**
 * Write a verified bundle into `destinationRoot`, under a new folder.
 *
 * `files` must already have come out of verifyBundle() — that is, every
 * SHA-256 has matched. This function does not hash; it is the writer, and
 * mixing the two would make it possible to write before verifying.
 */
export function writeBundle(
  destinationRoot: string,
  manifest: BundleManifest,
  files: readonly BundleFile[],
): WriteResult {
  if (!existsSync(destinationRoot)) {
    throw new WriteError(
      "保存先のフォルダーが見つかりません。",
      "destination-missing",
    );
  }
  const root = resolve(destinationRoot);

  const labelCheck = checkRelativePath(manifest.label);
  if (!labelCheck.ok || manifest.label.includes("/")) {
    throw new WriteError("転送の名前が保存先の名前として使えません。", "bad-label");
  }
  for (const file of files) {
    const check = checkRelativePath(file.path);
    if (!check.ok) {
      throw new WriteError(`受け取れないパスが含まれています: ${check.message}`, "bad-path");
    }
  }
  if (findPathSetConflict(files.map((file) => file.path))) {
    throw new WriteError("同じ名前が 2 か所で使われています。", "path-conflict");
  }

  // Inside the destination, so the commit below is a rename within one folder
  // on one volume — the only kind that is atomic.
  // Unpredictable: a predictable name can be pre-created by anybody who can
  // write into the destination, which turns every transfer into an EEXIST.
  const staging = join(root, `.decimen-incoming-${randomBytes(9).toString("hex")}`);
  let written = 0;
  let totalSize = 0;
  const created = new Set<string>();
  try {
    mkdirSync(staging, { recursive: false });
    for (const file of files) {
      const target = join(staging, file.path);
      if (!isInside(staging, target)) {
        throw new WriteError("保存先の外を指すパスがありました。", "escapes-destination");
      }
      // Every level, not just the leaf: a junction three directories up is
      // just as much a way out as one next to the file. Levels we made
      // ourselves on an earlier file are skipped; anything else at that path
      // is checked before it is walked through.
      let level = staging;
      for (const segment of file.path.split("/").slice(0, -1)) {
        level = join(level, segment);
        // Keyed case-insensitively, because the filesystem is. A bundle
        // carrying both  and  is legal — two different
        // files — and a case-sensitive Set missed the second one, tried to
        // create a directory that already existed, and aborted the whole
        // transfer with EEXIST.
        const levelKey = pathCollisionKey(level);
        if (!created.has(levelKey)) {
          assertNotALink(level);
          mkdirSync(level, { recursive: false });
          created.add(levelKey);
        }
        if (!isInside(staging, resolve(level))) {
          throw new WriteError("保存先の外を指すパスがありました。", "escapes-destination");
        }
      }
      assertNotALink(target);
      // "wx" is O_CREAT|O_EXCL: if anything is already there, this throws
      // rather than replacing it. Inside a folder we just created nothing
      // should be — and if something is, that is exactly what we want to hear.
      const handle = openSync(target, "wx", 0o600);
      try {
        if (file.bytes.length > 0) writeSync(handle, file.bytes);
      } finally {
        closeSync(handle);
      }
      markOfTheWeb(target);
      written++;
      totalSize += file.bytes.length;
    }

    const finalName = freeName(root, manifest.label);
    const finalPath = join(root, finalName);
    if (!isInside(root, finalPath)) {
      throw new WriteError("保存先の外へ書き出そうとしました。", "escapes-destination");
    }
    renameSync(staging, finalPath);
    return { savedAs: finalName, fileCount: written, totalSize, renamed: 0 };
  } catch (error) {
    // Nothing half-written is ever left behind: either the whole folder
    // arrives under its final name, or there is no trace of the attempt.
    rmSync(staging, { recursive: true, force: true });
    if (error instanceof WriteError) throw error;
    const reason = error instanceof Error ? error.message : String(error);
    if (/EACCES|EPERM/.test(reason)) {
      throw new WriteError("保存先に書き込む権限がありません。", "no-permission");
    }
    if (/ENOSPC/.test(reason)) {
      throw new WriteError("保存先の空き容量が足りません。", "no-space");
    }
    if (/ENAMETOOLONG|ENOENT/.test(reason)) {
      throw new WriteError(
        "保存先のパスが長すぎるか、途中のフォルダーがありません。",
        "path-unusable",
      );
    }
    throw new WriteError(`保存できませんでした: ${reason}`, "write-failed");
  }
}

/** Can this folder be written into right now? Used to show a destination as
 *  usable or not BEFORE somebody spends ten minutes sending to it. */
/**
 * Can this folder probably be written into?
 *
 * A HINT, shown next to a destination so somebody does not start a ten-minute
 * transfer into an unplugged drive. It is deliberately NOT a proof, and it
 * deliberately creates nothing.
 *
 * The obvious implementation — create a probe file, delete it — was tried and
 * left litter. On Windows a delete only takes effect when the last handle
 * closes, and something on this machine (a scanner, the indexer) holds new
 * files long enough that `.decimen-probe-*` entries were still listed in the
 * destination afterwards. Probing with a directory instead reduced it but did
 * not eliminate it. A hint that leaves debris in the user's receive folder is
 * a worse bargain than a hint that is occasionally optimistic.
 *
 * So this asks the filesystem instead of poking it. The authoritative answer is
 * the write itself, which already reports "保存先に書き込む権限がありません"
 * and leaves nothing behind when it fails.
 */
export function isWritable(path: string): boolean {
  try {
    if (!statSync(path).isDirectory()) return false;
    accessSync(path, constants.W_OK);
    return true;
  } catch {
    return false;
  }
}

export function ensureDirectory(path: string): void {
  mkdirSync(resolve(path), { recursive: true });
}
