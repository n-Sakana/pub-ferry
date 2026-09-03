import { defineConfig } from "vite";
import { resolve } from "node:path";

// The phone's relay page, built into the folder the relay host serves.
//
// Its own config for the same reason the desktop app has one: the upstream
// site is a PWA with a service worker and a set of build plugins that do
// exact-match surgery on its markup, and this page shares none of that. It is
// installed from, and served by, the receiving host — which is what lets it
// call that host's API at all (a page served anywhere else is a different
// origin over a scheme browsers will not mix).
export default defineConfig({
  root: resolve(__dirname, "relay/web"),
  base: "./",
  // The manifest and the icons are referenced by URL from the manifest and
  // from the OS, so they are copied verbatim rather than hashed into assets/.
  publicDir: resolve(__dirname, "relay/web/public"),
  build: {
    outDir: resolve(__dirname, "relay/web/dist"),
    emptyOutDir: true,
    target: "es2022",
    rollupOptions: {
      input: resolve(__dirname, "relay/web/index.html"),
    },
  },
  worker: { format: "iife" },
  server: { port: 5184, host: true },
});
