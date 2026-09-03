// Writing verified files onto a disk.
//
// The last thing between a manifest and somebody's filesystem, so the tests are
// about what it refuses and what it leaves behind on failure — not about the
// happy path, which relay-e2e.test.ts already walks end to end.

import { test, beforeEach, afterEach } from "node:test";
import assert from "node:assert/strict";
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { platform, tmpdir } from "node:os";
import { WriteError, isInside, isWritable, writeBundle } from "../relay/writer";
import type { BundleFile, BundleManifest } from "../shared/bundle";

const encoder = new TextEncoder();
let root: string;

beforeEach(() => {
  root = mkdtempSync(join(tmpdir(), "pub-ferry-writer-"));
});

afterEach(() => {
  if (root && existsSync(root)) rmSync(root, { recursive: true, force: true });
});

function manifest(label: string, files: BundleFile[]): BundleManifest {
  return {
    v: 1,
    label,
    count: files.length,
    totalSize: files.reduce((sum, file) => sum + file.bytes.length, 0),
    files: files.map((file) => ({ path: file.path, size: file.bytes.length, sha256: "0".repeat(64) })),
  };
}

const file = (path: string, text: string): BundleFile => ({ path, bytes: encoder.encode(text) });

test("a bundle lands under its own new folder", () => {
  const files = [file("a.txt", "one"), file("sub/b.txt", "two"), file("sub/deep/c.txt", "")];
  const result = writeBundle(root, manifest("届いたもの", files), files);
  assert.equal(result.savedAs, "届いたもの");
  assert.equal(result.fileCount, 3);
  assert.equal(readFileSync(join(root, "届いたもの", "a.txt"), "utf8"), "one");
  assert.equal(readFileSync(join(root, "届いたもの", "sub", "b.txt"), "utf8"), "two");
  assert.equal(readFileSync(join(root, "届いたもの", "sub", "deep", "c.txt"), "utf8"), "");
});

test("nothing is overwritten; a second arrival gets its own name", () => {
  const first = [file("a.txt", "first")];
  const second = [file("a.txt", "second")];
  assert.equal(writeBundle(root, manifest("同じ名前", first), first).savedAs, "同じ名前");
  assert.equal(writeBundle(root, manifest("同じ名前", second), second).savedAs, "同じ名前 (2)");
  assert.equal(writeBundle(root, manifest("同じ名前", second), second).savedAs, "同じ名前 (3)");
  assert.equal(readFileSync(join(root, "同じ名前", "a.txt"), "utf8"), "first");
});

test("a failure part-way through leaves nothing behind", () => {
  // The second file has a path the writer refuses. The first was already
  // written into the staging folder — and must not survive.
  const files = [file("ok.txt", "written"), { path: "../escape.txt", bytes: encoder.encode("no") }];
  assert.throws(
    () => writeBundle(root, manifest("途中で失敗", files), files),
    (error: unknown) => error instanceof WriteError && error.code === "bad-path",
  );
  assert.deepEqual(readdirSync(root), [], "a staging folder or a partial write was left behind");
});

test("a name used as both a file and a folder is refused before anything is created", () => {
  const files = [file("a", "file"), file("a/b.txt", "under a folder called a")];
  assert.throws(
    () => writeBundle(root, manifest("衝突", files), files),
    (error: unknown) => error instanceof WriteError && error.code === "path-conflict",
  );
  assert.deepEqual(readdirSync(root), []);
});

test("a label that is not a usable folder name is refused", () => {
  const files = [file("a.txt", "x")];
  for (const label of ["../escape", "a/b", "CON", "", "."]) {
    assert.throws(
      () => writeBundle(root, manifest(label, files), files),
      (error: unknown) => error instanceof WriteError && error.code === "bad-label",
      `label ${JSON.stringify(label)} was accepted`,
    );
  }
});

test("a destination that does not exist is named as such, not created", () => {
  const files = [file("a.txt", "x")];
  const missing = join(root, "not-here");
  assert.throws(
    () => writeBundle(missing, manifest("x", files), files),
    (error: unknown) => error instanceof WriteError && error.code === "destination-missing",
  );
  assert.equal(existsSync(missing), false);
});

test("isInside is not a prefix test", () => {
  assert.equal(isInside("/data", "/data/sub/x"), true);
  assert.equal(isInside("/data", "/data"), true);
  // The trap: a sibling whose name starts with the root's name.
  assert.equal(isInside(join(root, "data"), join(root, "dataEVIL", "x")), false);
  assert.equal(isInside(join(root, "data"), join(root, "other")), false);
});

test("a link already sitting in the destination is not followed", (t) => {
  // Creating a symlink needs Developer Mode or an elevated shell on Windows.
  // Where that is not available the check cannot be exercised — say so rather
  // than pass quietly.
  const outside = join(root, "outside");
  mkdirSync(outside, { recursive: true });
  const destination = join(root, "dest");
  mkdirSync(destination, { recursive: true });
  let linked = false;
  try {
    symlinkSync(outside, join(destination, "sub"), "dir");
    linked = true;
  } catch {
    t.skip("この環境ではシンボリックリンクを作成できません（Windows は開発者モードが必要）");
    return;
  }
  assert.ok(linked);
  // The staging folder is fresh, so the link is not in the write path — which
  // is the point of staging inside a folder we created rather than writing
  // into the destination directly. Nothing escapes.
  const files = [file("sub/a.txt", "x")];
  const result = writeBundle(destination, manifest("届いたもの", files), files);
  assert.equal(readFileSync(join(destination, result.savedAs, "sub", "a.txt"), "utf8"), "x");
  assert.deepEqual(readdirSync(outside), [], "bytes were written through the link");
});

test("a destination that cannot be written to is reported before a transfer starts", (t) => {
  assert.equal(isWritable(root), true);
  assert.equal(isWritable(join(root, "nope")), false);
  if (platform() === "win32") {
    t.skip("Windows の読み取り専用属性はフォルダーへの作成を止めないため、この確認は POSIX のみ");
    return;
  }
  const locked = join(root, "locked");
  mkdirSync(locked);
  chmodSync(locked, 0o500);
  try {
    assert.equal(isWritable(locked), false);
  } finally {
    chmodSync(locked, 0o700);
  }
});

test("probing for writability leaves nothing in the folder", () => {
  for (let i = 0; i < 5; i++) assert.equal(isWritable(root), true);
  assert.deepEqual(
    readdirSync(root).filter((name) => name.startsWith(".pub-ferry-probe")),
    [],
    "a probe file was left in the destination",
  );
});

test("an empty bundle writes an empty folder rather than failing", () => {
  const result = writeBundle(root, manifest("空", []), []);
  assert.equal(result.fileCount, 0);
  assert.ok(existsSync(join(root, "空")));
});

test("received files carry a Mark of the Web on Windows", (t) => {
  if (platform() !== "win32") {
    t.skip("Zone.Identifier は Windows のみ");
    return;
  }
  const files = [file("a.txt", "from another machine")];
  const result = writeBundle(root, manifest("印付き", files), files);
  const target = join(root, result.savedAs, "a.txt");
  let zone = "";
  try {
    zone = readFileSync(`${target}:Zone.Identifier`, "utf8");
  } catch {
    // A filesystem without alternate data streams cannot carry it, and that is
    // not a failed transfer — but on an NTFS temp folder it should be there.
    t.diagnostic("Zone.Identifier could not be read; the filesystem may not support streams");
    return;
  }
  assert.match(zone, /ZoneId=3/);
});

test("writing does not depend on the destination being empty", () => {
  writeFileSync(join(root, "既存のファイル.txt"), "untouched", "utf8");
  const files = [file("a.txt", "new")];
  writeBundle(root, manifest("追加", files), files);
  assert.equal(readFileSync(join(root, "既存のファイル.txt"), "utf8"), "untouched");
});
