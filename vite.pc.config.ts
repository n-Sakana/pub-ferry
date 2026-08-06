import { defineConfig } from "vite";
import { resolve } from "node:path";

// The desktop app's page.
//
// Its own config rather than a mode of the site's: the site is a PWA with a
// service worker, a manifest and a set of build plugins that do exact-match
// surgery on its markup. None of that applies here — this page is loaded from
// a virtual host inside a WebView2 window — and adding a fourth entry to that
// build would mean every one of those plugins had to learn about a page they
// have nothing to say about.
//
// `base: "./"` because the page is served from the root of a virtual host and
// nothing must resolve against a URL the host does not answer.
export default defineConfig({
  root: resolve(__dirname, "pc/app"),
  base: "./",
  publicDir: false,
  build: {
    outDir: resolve(__dirname, "pc/app/dist"),
    emptyOutDir: true,
    target: "chrome110",
    rollupOptions: {
      input: resolve(__dirname, "pc/app/index.html"),
    },
  },
  worker: {
    // The window has no module-worker guarantee to make on old runtimes, and
    // an iife worker is one fewer thing that can differ between the dev server
    // and the built page.
    format: "iife",
  },
  server: {
    // Only used by `npm run dev:pc`, which is for laying out the page against
    // a browser. The real app never reaches a dev server.
    port: 5183,
  },
});
