import { useEffect, useState } from "react";

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? "";

interface Readiness {
  status: string;
  migrations?: number;
}

/**
 * Bootstrap shell for slice 1.1: proves the toolchain end to end (build, lint, test, API reachability).
 * The real application shell, design system, i18n and RTL arrive in slice 1.10.
 */
export function App() {
  const [readiness, setReadiness] = useState<Readiness | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    fetch(`${apiBaseUrl}/health/ready`, { signal: controller.signal })
      .then(async (response) => (await response.json()) as Readiness)
      .then(setReadiness)
      .catch(() => {
        setReadiness({ status: "unreachable" });
      });
    return () => {
      controller.abort();
    };
  }, []);

  return (
    <main>
      <h1>Quicker</h1>
      <p data-testid="api-status">API: {readiness?.status ?? "checking"}</p>
    </main>
  );
}
