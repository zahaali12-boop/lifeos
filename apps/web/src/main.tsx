import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider } from "@tanstack/react-router";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "./i18n";
import "./styles/app.css";
import { applyTheme } from "./shell/AppShell";
import { getDigitStyle, setDigitStyle } from "./lib/format";
import { router } from "./routes/router";

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: false, staleTime: 10_000 } },
});

setDigitStyle(getDigitStyle());

// The service worker makes the app installable on a phone's home screen; it caches nothing (ADR-0035 §19).
if ("serviceWorker" in navigator) {
  window.addEventListener("load", () => {
    navigator.serviceWorker.register("/sw.js").catch(() => undefined);
  });
}
applyTheme((localStorage.getItem("quicker.theme") as "light" | "dark" | null) ?? (window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light"));

const root = document.getElementById("root");
if (!root) {
  throw new Error("The page has no #root element.");
}

createRoot(root).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </StrictMode>,
);
