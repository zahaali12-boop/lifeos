/// <reference types="vitest/config" />
import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";
import { VitePWA } from "vite-plugin-pwa";

export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
    // The app is installable (roadmap 3.8, ADR-0035 §19): the manifest is public/manifest.webmanifest, linked from
    // index.html in every mode; production builds add the service worker (the app shell precached, fonts cached on
    // first use, a new version taken at the next open). API calls are never cached: the offline queue in the app
    // decides what is kept and replayed.
    VitePWA({
      registerType: "autoUpdate",
      injectRegister: "script-defer",
      manifest: false,
      includeAssets: ["icons/icon.svg", "icons/icon-192.png", "icons/icon-512.png", "icons/apple-touch-icon.png"],
      workbox: {
        globPatterns: ["**/*.{js,css,html,svg}"],
        navigateFallback: "/index.html",
        navigateFallbackDenylist: [/^\/api\//, /^\/health/],
        maximumFileSizeToCacheInBytes: 6 * 1024 * 1024,
        runtimeCaching: [{ urlPattern: ({ request }) => request.destination === "font", handler: "CacheFirst", options: { cacheName: "quicker-fonts", expiration: { maxEntries: 40, maxAgeSeconds: 60 * 60 * 24 * 365 } } }],
      },
    }),
  ],
  server: { port: 5173, strictPort: true },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/setupTests.ts"],
    css: false,
    exclude: ["e2e/**", "node_modules/**"],
  },
});
