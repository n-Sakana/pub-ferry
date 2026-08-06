import { test } from "node:test";
import assert from "node:assert/strict";
import {
  BUNDLE_MEDIA_TYPE,
  BundleError,
  DEFAULT_BUNDLE_LIMITS,
  checkBundleLabel,
  looksLikeBundle,
  packBundle,
  readBundleManifest,
  verifyBundle,
  type BundleFile,
  type BundleLimits,
} from "../shared/bundle";
import { packFile, unpackFile, verifyFile } from "../shared/protocol";

const encoder = new TextEncoder();
const file = (path: string, text: string): BundleFile => ({ path, bytes: encoder.encode(text) });

const SAMPLE: BundleFile[] = [
  file("a.txt", "hello"),
  file("sub/b.txt", "world"),
  file("sub/deep/c.bin", "\u0000\u0001\u0002binary-ish"),
];

async function packed(): Promise<Uint8Array> {
  return (await packBundle("screens", SAMPLE)).bytes;
}

/** Swap the manifest of a real bundle for a doctored one, keeping the framing
 *  correct — so what is under test is the validation and not the framing. */
function withManifest(bytes: Uint8Array, mutate: (manifest: any) => void): Uint8Array {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const manifestLen = view.getUint32(4, true);
  const manifest = JSON.parse(new TextDecoder().decode(bytes.subarray(8, 8 + manifestLen)));
  mutate(manifest);
  const next = encoder.encode(JSON.stringify(manifest));
  const body = bytes.subarray(8 + manifestLen);
  const out = new Uint8Array(8 + next.length + body.length);
  out.set(bytes.subarray(0, 4), 0);
  new DataView(out.buffer).setUint32(4, next.length, true);
  out.set(next, 8);
  out.set(body, 8 + next.length);
  return out;
}

async function rejects(bytes: Uint8Array, code: string, limits?: BundleLimits): Promise<void> {
  await assert.rejects(
    () => verifyBundle(bytes, limits ?? DEFAULT_BUNDLE_LIMITS),
    (error: unknown) => {
      assert.ok(error instanceof BundleError, `expected a BundleError, got ${String(error)}`);
      assert.equal(error.code, code);
      return true;
    },
  );
}

test("a folder survives the round trip byte for byte", async () => {
  const { bytes, manifest } = await packBundle("screens", SAMPLE);
  assert.ok(looksLikeBundle(bytes));
  assert.equal(manifest.count, 3);
  assert.equal(manifest.label, "screens");
  const verified = await verifyBundle(bytes);
  assert.equal(verified.files.length, 3);
  for (let i = 0; i < SAMPLE.length; i++) {
    assert.equal(verified.files[i]!.path, SAMPLE[i]!.path);
    assert.deepEqual(
      Array.from(verified.files[i]!.bytes),
      Array.from(SAMPLE[i]!.bytes),
      `bytes differ for ${SAMPLE[i]!.path}`,
    );
  }
});

test("a bundle rides the upstream optical container untouched", async () => {
  // The whole point of the design: the fountain layer and the frame header
  // never learn that a folder exists.
  const { bytes } = await packBundle("screens", SAMPLE);
  const container = await packFile("screens.dcb1", BUNDLE_MEDIA_TYPE, bytes);
  const unpacked = await unpackFile(container.container);
  assert.equal(await verifyFile(unpacked), true);
  assert.equal(unpacked.type, BUNDLE_MEDIA_TYPE);
  const verified = await verifyBundle(unpacked.bytes);
  assert.equal(verified.manifest.count, 3);
});

test("an empty file inside a folder is carried, not dropped", async () => {
  const { bytes } = await packBundle("mixed", [file("empty.txt", ""), file("a.txt", "x")]);
  const verified = await verifyBundle(bytes);
  assert.equal(verified.files[0]!.bytes.length, 0);
  assert.equal(verified.files[1]!.bytes.length, 1);
});

test("hostile paths never survive packing", async () => {
  for (const path of ["../escape.txt", "/etc/passwd", "C:/x", "a\\b", "CON.txt", "a.txt "]) {
    await assert.rejects(
      () => packBundle("x", [file(path, "x")]),
      (error: unknown) => error instanceof BundleError && error.code === "bad-path",
      `packing accepted ${JSON.stringify(path)}`,
    );
  }
});

test("hostile paths never survive unpacking either — the receiver checks for itself", async () => {
  for (const path of ["../escape.txt", "/etc/passwd", "sub/../../x", "CON", "a\\b"]) {
    const doctored = withManifest(await packed(), (manifest) => {
      manifest.files[0].path = path;
    });
    await rejects(doctored, "bad-path");
  }
});

test("a manifest that lies about its own arithmetic is refused before any byte is used", async () => {
  await rejects(
    withManifest(await packed(), (m) => {
      m.count = 2;
    }),
    "count-mismatch",
  );
  await rejects(
    withManifest(await packed(), (m) => {
      m.totalSize = m.totalSize + 1;
    }),
    "size-mismatch",
  );
  await rejects(
    withManifest(await packed(), (m) => {
      m.files[0].size = m.files[0].size + 1;
      m.totalSize = m.totalSize + 1;
    }),
    "length-mismatch",
  );
});

test("negative, fractional and enormous numbers cannot make the offsets walk backwards", async () => {
  for (const size of [-1, 1.5, Number.MAX_SAFE_INTEGER + 2, 1e999, "5", null]) {
    const doctored = withManifest(await packed(), (m) => {
      m.files[0].size = size;
    });
    await assert.rejects(
      () => verifyBundle(doctored),
      (error: unknown) =>
        error instanceof BundleError && ["bad-size", "size-mismatch"].includes(error.code),
      `size ${String(size)} was not refused`,
    );
  }
});

test("a declared manifest length past the buffer cannot make us read past the end", async () => {
  const bytes = await packed();
  const truncated = bytes.slice();
  new DataView(truncated.buffer).setUint32(4, 0xffffff00, true);
  await rejects(truncated, "bad-manifest-length");

  const justPast = bytes.slice();
  new DataView(justPast.buffer).setUint32(4, bytes.length, true);
  await rejects(justPast, "truncated");
});

test("trailing bytes past the last file are a disagreement, not a curiosity", async () => {
  const bytes = await packed();
  const padded = new Uint8Array(bytes.length + 1);
  padded.set(bytes);
  await rejects(padded, "length-mismatch");
  await rejects(bytes.subarray(0, bytes.length - 1), "length-mismatch");
});

test("a file whose bytes were altered in flight fails its own digest", async () => {
  const bytes = await packed();
  const tampered = bytes.slice();
  tampered[tampered.length - 1] ^= 0xff;
  await rejects(tampered, "digest-mismatch");
});

test("a digest of the wrong shape is refused without being compared", async () => {
  await rejects(
    withManifest(await packed(), (m) => {
      m.files[0].sha256 = "NOTHEX";
    }),
    "bad-digest",
  );
  await rejects(
    withManifest(await packed(), (m) => {
      m.files[0].sha256 = "AB".repeat(32); // uppercase is not the canonical form
    }),
    "bad-digest",
  );
});

test("two entries that would land on one file are refused", async () => {
  await rejects(
    withManifest(await packed(), (m) => {
      m.files[1].path = "A.TXT";
    }),
    "duplicate-path",
  );
});

test("a name used as both a file and a folder is refused", async () => {
  await rejects(
    withManifest(await packed(), (m) => {
      m.files[0].path = "sub";
    }),
    "path-conflict",
  );
});

test("an unknown format version says so instead of guessing", async () => {
  await rejects(
    withManifest(await packed(), (m) => {
      m.v = 99;
    }),
    "unsupported-version",
  );
});

test("the label is a folder name, so it obeys the same rules a name does", async () => {
  assert.equal(checkBundleLabel("screens").ok, true);
  assert.equal(checkBundleLabel("").ok, false);
  assert.equal(checkBundleLabel("a/b").ok, false);
  assert.equal(checkBundleLabel("../escape").ok, false);
  assert.equal(checkBundleLabel("CON").ok, false);
  await assert.rejects(() => packBundle("../escape", SAMPLE));
  await rejects(
    withManifest(await packed(), (m) => {
      m.label = "../escape";
    }),
    "bad-label",
  );
});

test("limits are enforced against the declared value and the measured one", async () => {
  const tiny: BundleLimits = { maxFiles: 2, maxTotalBytes: 8, maxManifestBytes: 512 };
  await assert.rejects(
    () => packBundle("x", SAMPLE, { limits: tiny }),
    (error: unknown) => error instanceof BundleError && error.code === "too-many-files",
  );
  await assert.rejects(
    () => packBundle("x", [file("a", "0123456789")], { limits: tiny }),
    (error: unknown) => error instanceof BundleError && error.code === "too-large",
  );
  // The declared total is checked first, so a manifest claiming more than the
  // ceiling is refused before a single entry is looked at.
  await rejects(await packed(), "bad-total-size", { ...DEFAULT_BUNDLE_LIMITS, maxTotalBytes: 4 });

  // And a manifest that DECLARES a total inside the limit, with every entry
  // individually inside it too, while the entries add up past it: caught by the
  // running total, which exists because a declared value is not evidence.
  const small = (await packBundle("x", [file("a", "aaaaa"), file("b", "bbbbb"), file("c", "ccccc")]))
    .bytes;
  const lying = withManifest(small, (m) => {
    m.totalSize = 12;
  });
  await rejects(lying, "too-large", { ...DEFAULT_BUNDLE_LIMITS, maxTotalBytes: 12 });
});

test("garbage that is not a bundle at all is named as such", async () => {
  await rejects(encoder.encode("not a bundle at all, just some text"), "bad-magic");
  await rejects(new Uint8Array(3), "truncated");
});

test("a manifest that is not valid UTF-8 or not valid JSON is refused", async () => {
  const bytes = await packed();
  const broken = bytes.slice();
  broken[9] = 0xff; // inside the manifest, invalid as UTF-8
  await rejects(broken, "bad-manifest");
});

test("readBundleManifest never reads a byte of the body", async () => {
  // Structure can be shown to a human before paying for a hash of every byte.
  const bytes = await packed();
  const { manifest, bodyOffset } = readBundleManifest(bytes);
  assert.equal(manifest.count, 3);
  assert.equal(bodyOffset + manifest.totalSize, bytes.length);
});
