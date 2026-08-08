// Draws public/og.png — the 1200×630 social card behind every page's og:image.
//
//   node --import tsx tools/make-og.ts        (npm run og)
//
// The card is a brand surface: it carries the product name, so it has to be
// regenerable rather than a hand-made binary nobody can update. It was a static
// asset reading "DECIMEN OPTICAL TRANSFER" until the Pub Ferry rename, which is
// exactly the drift this script exists to prevent.
//
// Layout and palette come from shared/design/tokens.css (dark scheme) and the
// mark from public/pub-ferry-logo.svg, so the card and the app stay in step.
// The version in the mocked phone footer is read from package.json; the upstream
// copyright line is reproduced verbatim, as it is in the real UI.
//
// Rendering goes through the Chromium already on this machine (named by
// PUB_FERRY_CHROMIUM), the same approach tools/make-relay-icons.ts uses, rather
// than adding an image library for one PNG.

import { chromium } from "playwright-core";
import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const pkg = JSON.parse(readFileSync(resolve(root, "package.json"), "utf8")) as { version: string };

const WIDTH = 1200;
const HEIGHT = 630;

// --- tokens (shared/design/tokens.css, dark scheme) -------------------------
const BG = "#070a11"; // the logo tile / theme-color ground
const TEXT = "#e8edf3"; // --d-gray-12
const SUB = "#8c98a6"; // --d-gray-9
const MUTED = "#5e6a78"; // --d-gray-8
const FAINT = "#47525f"; // --d-gray-7
const LINE = "#29313b"; // --d-gray-5
const PANEL = "#0e1116"; // --d-gray-0
const SUNKEN = "#1b2129"; // --d-gray-3
const ACCENT = "#2e79a8"; // --d-blue-3
const MARK = "#58c8ff"; // the mark colour in public/pub-ferry-logo.svg
const MONO = '"Cascadia Mono", Consolas, "SF Mono", "Roboto Mono", ui-monospace, monospace';

// The mark, straight out of public/pub-ferry-logo.svg (paths only, recoloured
// by currentColor exactly as the page headers inline it).
const MARK_PATHS =
  '<path d="M2410 6513 l0 -1378 103 101 c540 531 1324 986 1984 1153 402 102 850 95 1268 -21 529 -146 1300 -581 1860 -1050 175 -146 212 -184 199 -205 -37 -63 -502 -432 -754 -600 -969 -644 -1785 -844 -2590 -634 -667 174 -1376 581 -1947 1118 l-123 115 0 -1382 0 -1381 1478 4 c1291 3 1491 5 1588 20 1345 194 2240 1124 2355 2446 17 195 7 620 -19 786 -192 1236 -1030 2069 -2262 2249 -217 32 -411 36 -1772 36 l-1368 0 0 -1377z"/>' +
  '<path d="M4945 5906 c-300 -68 -532 -287 -611 -577 -25 -89 -25 -289 0 -378 141 -513 724 -749 1179 -477 405 242 503 778 209 1148 -100 125 -258 229 -417 273 -82 22 -282 29 -360 11z"/>';

function mark(size: number, colour: string): string {
  return (
    `<svg width="${size}" height="${size}" viewBox="0 0 543 554.1" fill="${colour}" aria-hidden="true">` +
    `<g transform="translate(-241,789) scale(0.1,-0.1)">${MARK_PATHS}</g></svg>`
  );
}

/** A still frame of the QR stream for the mocked camera preview. Seeded, so
 *  re-running this script reproduces the same card byte for byte. */
function qrNoise(cols: number, rows: number): string {
  let seed = 0x5eed_1234;
  const next = (): number => {
    // Numerical Recipes LCG — deterministic across machines and Node versions.
    seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
    return seed / 0x1_0000_0000;
  };
  const cells: string[] = [];
  for (let y = 0; y < rows; y++) {
    for (let x = 0; x < cols; x++) {
      if (next() > 0.5) cells.push(`<rect x="${x}" y="${y}" width="1" height="1"/>`);
    }
  }
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${cols} ${rows}" ` +
    `shape-rendering="crispEdges"><rect width="${cols}" height="${rows}" fill="#ffffff"/>` +
    `<g fill="#0b0e14">${cells.join("")}</g></svg>`;
  return `data:image/svg+xml;base64,${Buffer.from(svg).toString("base64")}`;
}

function card(): string {
  return `<!doctype html><html><head><meta charset="utf-8" /><style>
  * { margin: 0; padding: 0; box-sizing: border-box; }
  html, body { width: ${WIDTH}px; height: ${HEIGHT}px; }
  body {
    background: ${BG};
    font-family: ${MONO};
    -webkit-font-smoothing: antialiased;
    position: relative;
    overflow: hidden;
  }

  /* ---- left column ---- */
  .copy { position: absolute; left: 64px; top: 64px; width: 700px; }
  .brand { display: flex; align-items: center; gap: 22px; }
  .brand-name {
    font-size: 33px; font-weight: 700; color: ${TEXT};
    letter-spacing: 0.13em; text-transform: uppercase; white-space: nowrap;
  }
  h1 {
    margin-top: 78px;
    font-size: 63px; font-weight: 700; line-height: 1.14;
    color: ${TEXT}; letter-spacing: -0.01em;
  }
  .lede {
    margin-top: 46px;
    font-size: 23px; line-height: 1.5; color: ${SUB};
  }
  .figure {
    margin-top: 36px;
    font-size: 23px; font-weight: 700; color: ${MARK};
  }
  .meta { margin-top: 14px; font-size: 19px; color: ${MUTED}; }

  /* ---- mocked phone ---- */
  .phone {
    position: absolute; right: 64px; top: 30px;
    width: 316px; height: 570px;
    background: ${PANEL};
    border: 1px solid ${LINE};
    border-radius: 24px;
    padding: 13px 14px;
    display: flex; flex-direction: column;
  }
  .p-top { display: flex; align-items: center; justify-content: space-between; }
  .p-brand { display: flex; align-items: center; gap: 6px; }
  .p-brand span {
    font-size: 8.5px; font-weight: 700; color: ${TEXT};
    letter-spacing: 0.13em; text-transform: uppercase;
  }
  .pills { display: flex; border: 1px solid ${LINE}; border-radius: 999px; overflow: hidden; }
  .pills i {
    font-style: normal; font-size: 7.5px; letter-spacing: 0.1em;
    padding: 3px 8px; color: ${MUTED}; text-transform: uppercase;
  }
  .pills i.on { background: ${SUNKEN}; color: ${TEXT}; border-radius: 999px; }
  .rule { height: 1px; background: ${LINE}; margin: 11px -14px 0; }
  .kicker {
    margin-top: 12px; font-size: 7.5px; letter-spacing: 0.16em;
    color: ${MUTED}; text-transform: uppercase;
  }
  h2 { margin-top: 4px; font-size: 17px; font-weight: 700; color: ${TEXT}; }
  .status { margin-top: 7px; font-size: 7.5px; color: ${MUTED}; }
  .preview {
    margin-top: 9px; height: 205px; border-radius: 7px; overflow: hidden;
    background: #ffffff; display: block;
  }
  .preview img { width: 100%; height: 100%; object-fit: cover; display: block; }
  .stats {
    margin-top: 9px; display: flex; align-items: baseline;
    justify-content: space-between; gap: 8px;
  }
  .stats b { font-size: 8.5px; font-weight: 700; color: ${MARK}; }
  .stats span { font-size: 7.5px; color: ${MUTED}; }
  .bar { margin-top: 6px; height: 3px; border-radius: 999px; background: ${SUNKEN}; }
  .bar i { display: block; width: 53%; height: 100%; border-radius: 999px; background: ${ACCENT}; }
  .row {
    margin-top: 8px; border: 1px solid ${LINE}; border-radius: 5px;
    padding: 6px 8px; font-size: 7.5px; letter-spacing: 0.12em;
    color: ${SUB}; text-transform: uppercase;
  }
  .p-foot { margin-top: auto; padding-top: 11px; }
  .p-foot .name { font-size: 7.5px; color: ${MUTED}; }
  .p-foot .legal { margin-top: 3px; font-size: 7px; color: ${FAINT}; line-height: 1.5; }
  .p-links {
    margin-top: 8px; display: flex; gap: 12px;
    font-size: 7.5px; letter-spacing: 0.12em; color: ${SUB}; text-transform: uppercase;
  }
  </style></head><body>

  <div class="copy">
    <div class="brand">${mark(72, MARK)}<div class="brand-name">Pub Ferry</div></div>
    <h1>Transfer files<br />with light.</h1>
    <p class="lede">
      Fountain-coded animated QR codes,<br />
      screen to camera. No network path<br />
      between the two devices.
    </p>
    <p class="figure">129 KB/s phone to phone</p>
    <p class="meta">any file up to 64 MB · SHA-256 verified · decimen.app</p>
  </div>

  <div class="phone">
    <div class="p-top">
      <div class="p-brand">${mark(13, MARK)}<span>Pub Ferry</span></div>
      <div class="pills"><i>Send</i><i class="on">Receive</i></div>
    </div>
    <div class="rule"></div>
    <p class="kicker">Camera → your device</p>
    <h2>Receive</h2>
    <p class="status">camera 960×1280@60 — searching for a stream…</p>
    <div class="preview"><img src="${qrNoise(146, 104)}" alt="" /></div>
    <div class="stats"><b>53% · 1/126 blocks</b><span>About 2s · 77 frames · 130.5 KB/s</span></div>
    <div class="bar"><i></i></div>
    <div class="row">▸ Live diagnostics</div>
    <div class="row">▸ Receive settings</div>
    <div class="p-foot">
      <div class="name">Pub Ferry</div>
      <div class="legal">v${pkg.version} · build b9d233e · © 2026 Evan Crawley (Bash Alarmist) · MIT</div>
      <div class="p-links"><span>GitHub</span><span>Releases</span></div>
    </div>
  </div>

  </body></html>`;
}

async function main(): Promise<void> {
  const executablePath = process.env.PUB_FERRY_CHROMIUM;
  if (executablePath && !existsSync(executablePath)) {
    throw new Error(`PUB_FERRY_CHROMIUM が指す実行ファイルがありません: ${executablePath}`);
  }
  const browser = await chromium.launch({ executablePath: executablePath || undefined });
  try {
    const page = await browser.newPage({
      viewport: { width: WIDTH, height: HEIGHT },
      deviceScaleFactor: 1,
    });
    await page.setContent(card(), { waitUntil: "load" });
    const out = resolve(root, "public/og.png");
    await page.screenshot({ path: out });
    console.log(`public/og.png ${WIDTH}×${HEIGHT}`);
  } finally {
    await browser.close();
  }
}

await main();
