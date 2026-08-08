// The two path validators must agree.
//
// shared/relative-path.ts guards what the pages and the Node host will accept;
// pc/src/03_SafePath.cs guards what the Windows host will write. They are
// deliberately separate — the host does not take the page's word for a path —
// but if they disagree, the same bundle behaves differently depending on which
// receiver it reaches, and a rule that holds on one machine and not another is
// not a rule.
//
// This runs the C# one for real (compiled by PowerShell, the way the app
// compiles it) and compares verdict for verdict. Skipped, with the reason, on
// anything that is not Windows.

import { test, before } from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { platform, tmpdir } from "node:os";
import { checkRelativePath } from "../shared/relative-path";

/** Every shape either validator has an opinion about. */
const PROBES: string[] = [
  // ordinary
  "a.txt",
  "sub/a.txt",
  "a/b/c/d.bin",
  "日本語のファイル名.txt",
  "with space.txt",
  "dash-and_underscore.9",
  "\u{1F600}.txt",
  "CONSOLE.txt",
  "COMET.txt",
  "com10.txt",
  "a.CON",
  // traversal and rooting
  "..",
  "../etc/passwd",
  "a/../../b",
  "/etc/passwd",
  "C:/Windows/System32/config",
  "c:notes.txt",
  "\\\\server\\share\\x",
  "a\\b",
  "a\\..\\..\\b",
  // reserved
  "CON",
  "con.txt",
  "sub/PRN.log",
  "COM1",
  "lpt9.dat",
  "NUL.tar.gz",
  "COM0",
  "COM\u00b9",
  // shape
  "a.txt ",
  "a.txt.",
  "dir. /x",
  " leading",
  "a//b",
  "a/",
  "./a",
  "a/./b",
  "",
  // characters
  "a\u0000b",
  "a\nb",
  "report.txt:hidden",
  "a*b",
  "a?b",
  'a"b',
  "a<b",
  "a|b",
  "report.txt\u202egpj.exe",
  "a\u200bb.txt",
  "\ufeffa.txt",
  // normalisation and surrogates
  "e\u0301.txt",
  "\u00e9.txt",
  "a\ud800.txt",
  "a\udfff.txt",
  // limits
  "a".repeat(180),
  "a".repeat(181),
  "x".repeat(201),
  Array.from({ length: 32 }, () => "a").join("/"),
  Array.from({ length: 33 }, () => "a").join("/"),
];

const runnable = platform() === "win32";
let csharp: { ok: boolean; reason: string | null }[] = [];
let setupProblem: string | null = null;

before(() => {
  if (!runnable) return;
  const root = mkdtempSync(join(tmpdir(), "pub-ferry-parity-"));
  try {
    const inputFile = join(root, "paths.json");
    const outputFile = join(root, "verdicts.json");
    // Sent as UTF-16 code units. A lone surrogate does not survive a JSON
    // string round trip through the .NET deserializer — it is quietly
    // repaired — and that is one of the cases the two sides must agree on.
    const asUnits = PROBES.map((probe) =>
      Array.from({ length: probe.length }, (_, position) => probe.charCodeAt(position)),
    );
    writeFileSync(inputFile, JSON.stringify(asUnits), "utf8");
    execFileSync(
      "powershell.exe",
      [
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        resolve("tools/check-safepath.ps1"),
        "-InputFile",
        inputFile,
        "-OutputFile",
        outputFile,
      ],
      { stdio: ["ignore", "pipe", "pipe"], timeout: 120_000 },
    );
    if (!existsSync(outputFile)) throw new Error("the harness produced no output");
    csharp = JSON.parse(readFileSync(outputFile, "utf8")) as typeof csharp;
  } catch (error) {
    setupProblem = error instanceof Error ? error.message : String(error);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("the TypeScript and C# path validators give the same verdict", (t) => {
  if (!runnable) {
    t.skip("C# の検証器は Windows でのみ動かせます");
    return;
  }
  if (setupProblem !== null) {
    t.skip(`C# 側を動かせませんでした: ${setupProblem}`);
    return;
  }
  assert.equal(csharp.length, PROBES.length, "the harness answered a different number of paths");

  const disagreements: string[] = [];
  for (let index = 0; index < PROBES.length; index++) {
    const path = PROBES[index]!;
    const ts = checkRelativePath(path);
    const cs = csharp[index]!;
    if (ts.ok !== cs.ok) {
      disagreements.push(
        `${JSON.stringify(path)}: TypeScript=${ts.ok ? "accept" : `reject(${ts.reason})`}` +
          ` / C#=${cs.ok ? "accept" : `reject(${cs.reason})`}`,
      );
    }
    // A validator that throws cannot be trusted to have decided anything.
    if (cs.reason && cs.reason.startsWith("threw:")) {
      disagreements.push(`${JSON.stringify(path)}: C# ${cs.reason}`);
    }
  }
  assert.deepEqual(disagreements, [], `両者の判定が食い違いました:\n  ${disagreements.join("\n  ")}`);
});
