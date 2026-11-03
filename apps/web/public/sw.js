/* global self */
// The app's service worker: enough for the browser to offer "install" or "add to home screen" (ADR-0035 §19).
// It caches nothing and answers no request itself, so an update is live the next time the app is opened.
self.addEventListener("install", () => {
  self.skipWaiting();
});
self.addEventListener("activate", (event) => {
  event.waitUntil(self.clients.claim());
});
self.addEventListener("fetch", () => {
  // Every request goes to the network as it would without a service worker.
});
