// Draws the phone PWA's app icons.
//
// This exists because the icons must be THIS product's mark. The upstream
// project ships its own logo under `public/`, and copying it here would put
// someone else's mark on a home screen under a different product name — a
// misattribution, not a shortcut. So the icon is generated from the same
// design tokens the app itself uses: the accent plate and the "PT" initials,
// exactly what `.brand-mark` renders in the topbar.
//
//   node --import tsx tools/make-relay-icons.ts
//
// Rendering goes through the Chromium already on this machine (the same one
// the screen drivers use, named by PUB_TRANSFER_CHROMIUM) rather than adding
// an image library to the dependency list for three PNGs.

import { chromium } from "playwright-core";
import { existsSync } from "node:fs";
import { resolve } from "node:path";

const ACCENT = "#16628f";
const ON_ACCENT = "#ffffff";
const outDir = resolve("relay/web/public");

/** `any` icons are shown as drawn. `maskable` ones get cropped to whatever
 *  shape the platform likes, so their content has to sit inside the middle
 *  80% or the initials lose their edges on a circular mask. */
interface Icon {
  file: string;
  size: number;
  maskable: boolean;
}

const ICONS: Icon[] = [
  { file: "icon-192.png", size: 192, maskable: false },
  { file: "icon-512.png", size: 512, maskable: false },
  { file: "icon-maskable-512.png", size: 512, maskable: true },
];

function page(icon: Icon): string {
  // A maskable icon bleeds the plate to the edges and shrinks the text into
  // the safe zone; a plain one keeps the rounded plate visible with a margin.
  const inset = icon.maskable ? 0 : Math.round(icon.size * 0.06);
  const radius = icon.maskable ? 0 : Math.round(icon.size * 0.22);
  const fontSize = Math.round(icon.size * (icon.maskable ? 0.3 : 0.38));
  return `<style>
    html, body { margin: 0; padding: 0; background: transparent; }
    .plate {
      position: absolute; inset: ${inset}px;
      border-radius: ${radius}px;
      background: ${ACCENT};
      display: grid; place-items: center;
      color: ${ON_ACCENT};
      font-family: "Segoe UI", system-ui, sans-serif;
      font-size: ${fontSize}px;
      font-weight: 700;
      letter-spacing: 0.02em;
    }
  </style><div class="plate">PT</div>`;
}

async function main(): Promise<void> {
  const executablePath = process.env.PUB_TRANSFER_CHROMIUM;
  if (executablePath && !existsSync(executablePath)) {
    throw new Error(`PUB_TRANSFER_CHROMIUM が指す実行ファイルがありません: ${executablePath}`);
  }
  const browser = await chromium.launch({ executablePath: executablePath || undefined });
  try {
    for (const icon of ICONS) {
      const view = await browser.newPage({
        viewport: { width: icon.size, height: icon.size },
        deviceScaleFactor: 1,
      });
      await view.setContent(page(icon));
      await view.screenshot({ path: resolve(outDir, icon.file), omitBackground: true });
      await view.close();
      console.log(`  ${icon.file}  ${icon.size}x${icon.size}${icon.maskable ? " (maskable)" : ""}`);
    }
  } finally {
    await browser.close();
  }
  console.log(`${ICONS.length} 個の図案を ${outDir} に書きました。`);
}

void main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
