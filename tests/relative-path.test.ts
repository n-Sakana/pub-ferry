import { test } from "node:test";
import assert from "node:assert/strict";
import {
  MAX_PATH_DEPTH,
  MAX_PATH_LENGTH,
  MAX_SEGMENT_LENGTH,
  checkRelativePath,
  findPathSetConflict,
  isSafeRelativePath,
  pathCollisionKey,
  toBundlePath,
} from "../shared/relative-path";

/** Every one of these is a real way a path has been used to write outside the
 *  folder somebody chose. They are the point of the module. */
const HOSTILE: [string, string][] = [
  ["..", "traversal"],
  ["../etc/passwd", "traversal"],
  ["a/../../b", "traversal"],
  ["a/b/../../../c", "traversal"],
  ["/etc/passwd", "absolute"],
  ["/", "absolute"],
  ["C:/Windows/System32/drivers/etc/hosts", "drive-relative"],
  ["c:notes.txt", "drive-relative"],
  ["\\\\server\\share\\x", "unc"],
  ["a\\..\\..\\b", "backslash"],
  ["a\\b", "backslash"],
  ["CON", "reserved-name"],
  ["con.txt", "reserved-name"],
  ["sub/PRN.log", "reserved-name"],
  ["COM1", "reserved-name"],
  ["lpt9.dat", "reserved-name"],
  ["NUL.tar.gz", "reserved-name"],
  ["a.txt ", "trailing-dot-or-space"],
  ["a.txt.", "trailing-dot-or-space"],
  ["dir. /x", "trailing-dot-or-space"],
  [" leading", "leading-space"],
  ["a//b", "empty-segment"],
  ["a/", "empty-segment"],
  ["./a", "dot-segment"],
  ["a/./b", "dot-segment"],
  ["", "empty"],
  ["a\u0000b", "forbidden-character"],
  ["a\nb", "forbidden-character"],
  // NTFS alternate data stream: `report.txt:hidden` writes a stream nobody sees.
  ["report.txt:hidden", "forbidden-character"],
  ["a*b", "forbidden-character"],
  ["a?b", "forbidden-character"],
  ['a"b', "forbidden-character"],
  ["a<b", "forbidden-character"],
  ["a|b", "forbidden-character"],
  // Right-to-left override: renders as "report.txtexe.jpg" in a file manager.
  ["report.txt\u202egpj.exe", "deceptive-character"],
  ["a\u200bb.txt", "deceptive-character"],
  ["\ufeffa.txt", "deceptive-character"],
];

test("every known escape from the destination folder is refused, with a reason", () => {
  for (const [path, reason] of HOSTILE) {
    const check = checkRelativePath(path);
    assert.equal(check.ok, false, `expected ${JSON.stringify(path)} to be refused`);
    assert.equal(check.reason, reason, `wrong reason for ${JSON.stringify(path)}`);
    assert.ok(check.message && check.message.length > 0);
    // A rejection never echoes the path back. The messages are fixed sentences
    // chosen by reason, so a hostile name cannot reach a label or a log through
    // the error that refused it. (Short inputs like ".." are excluded: the
    // sentence for a traversal names the ".." construct as an explanation.)
    if (path.length >= 5) {
      assert.ok(
        !check.message!.includes(path),
        `the message for ${JSON.stringify(path)} echoes the path back`,
      );
    }
  }
});

test("ordinary relative paths are accepted", () => {
  for (const path of [
    "a.txt",
    "sub/a.txt",
    "a/b/c/d.bin",
    "日本語のファイル名.txt",
    "with space.txt",
    "dash-and_underscore.9",
    "CONSOLE.txt",
    "COMET.txt",
    "LPT.txt",
    "a.CON",
    "com10.txt",
  ]) {
    const check = checkRelativePath(path);
    assert.equal(check.ok, true, `expected ${JSON.stringify(path)} to be accepted: ${check.message}`);
  }
});

test("both length ceilings are reachable, so each can name its own problem", () => {
  // The segment limit sits below the path limit on purpose: a too-long FILE
  // NAME has to be reported as a name, not as a path that is too deep.
  assert.ok(MAX_SEGMENT_LENGTH < MAX_PATH_LENGTH);
  assert.equal(checkRelativePath("a".repeat(MAX_SEGMENT_LENGTH)).ok, true);
  assert.equal(checkRelativePath("a".repeat(MAX_SEGMENT_LENGTH + 1)).reason, "segment-too-long");
  assert.equal(checkRelativePath("x".repeat(MAX_PATH_LENGTH + 1)).reason, "too-long");
});

test("depth is capped independently of length", () => {
  const shallow = Array.from({ length: MAX_PATH_DEPTH }, () => "a").join("/");
  const deep = Array.from({ length: MAX_PATH_DEPTH + 1 }, () => "a").join("/");
  assert.ok(deep.length < MAX_PATH_LENGTH, "the depth rule must fire before the length rule");
  assert.equal(checkRelativePath(shallow).ok, true);
  assert.equal(checkRelativePath(deep).reason, "too-deep");
});

test("a set of individually-safe paths can still conflict", () => {
  // `a` is a file and `a/b.txt` needs `a` to be a directory. Which one wins
  // depends on the order the writer happens to use, so neither does.
  assert.deepEqual(findPathSetConflict(["a", "a/b.txt"]), { kind: "file-and-directory", index: 1 });
  assert.deepEqual(findPathSetConflict(["a/b.txt", "a"]), { kind: "file-and-directory", index: 1 });
  // Case-folded, because Windows folds case.
  assert.deepEqual(findPathSetConflict(["A", "a/b.txt"]), { kind: "file-and-directory", index: 1 });
  assert.deepEqual(findPathSetConflict(["x.txt", "X.TXT"]), { kind: "duplicate", index: 1 });
  assert.equal(findPathSetConflict(["a/b.txt", "a/c.txt", "d.txt"]), null);
  assert.equal(findPathSetConflict([]), null);
});

test("only NFC spellings are accepted, so two names cannot become one file", () => {
  const composed = "\u00e9.txt"; // é
  const decomposed = "e\u0301.txt"; // e + combining acute
  assert.equal(checkRelativePath(composed).ok, true);
  assert.equal(checkRelativePath(decomposed).reason, "not-normalized");
  // ...and they are the same file on a normalising filesystem, which is exactly
  // why only one spelling is allowed through.
  assert.equal(decomposed.normalize("NFC"), composed);
});

test("collision keys fold case, because Windows and macOS do", () => {
  assert.equal(pathCollisionKey("A/B.txt"), pathCollisionKey("a/b.TXT"));
  assert.notEqual(pathCollisionKey("a.txt"), pathCollisionKey("a.bin"));
});

test("toBundlePath normalises spelling without making anything safe", () => {
  assert.equal(toBundlePath("sub\\a.txt"), "sub/a.txt");
  assert.equal(toBundlePath("./a.txt"), "a.txt");
  assert.equal(toBundlePath("e\u0301.txt"), "\u00e9.txt");
  // Still refused afterwards — normalising a traversal does not launder it.
  assert.equal(isSafeRelativePath(toBundlePath("..\\..\\x")), false);
});

test("non-strings and odd inputs do not throw", () => {
  for (const value of [undefined, null, 42, {}, []] as unknown[]) {
    assert.equal(checkRelativePath(value as string).ok, false);
  }
});
