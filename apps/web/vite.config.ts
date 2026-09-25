/// <reference types="vitest/config" />
import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";
import { VitePWA } from "vite-plugin-pwa";

export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
    // The scanner is installable (roadmap 3.8): a manifest that opens on /m, the app shell precached, fonts cached on first use.
    // API calls are never cached: the offline queue in the app decides what is kept and replayed.
    VitePWA({
      registerType: "autoUpdate",
      injectRegister: "script-defer",
      includeAssets: ["icons/icon.svg"],
      manifest: {
        name: "Quicker",
        short_name: "Quicker",
        description: "Quicker ERP: stock counts and transfers by barcode, online or offline.",
        start_url: "/m",
        scope: "/",
        display: "standalone",
        orientation: "portrait",
        background_color: "#ffffff",
        theme_color: "#0f766e",
        icons: [{ src: "icons/icon.svg", sizes: "any", type: "image/svg+xml", purpose: "any" }],
      },
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
