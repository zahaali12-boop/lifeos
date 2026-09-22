import { useEffect, useState } from "react";
import { createApiClient } from "./api/client";

const api = createApiClient({ baseUrl: import.meta.env.VITE_API_BASE_URL });

/**
 * Bootstrap shell for slice 1.1: proves the toolchain end to end (build, lint, test, API reachability) through the
 * generated, typed API client (slice 1.9). The real application shell, design system, i18n and RTL arrive in 1.10.
 */
export function App() {
  const [status, setStatus] = useState<string>("checking");

  useEffect(() => {
    let active = true;
    api
      .GET("/health/ready")
      .then(({ data, error }) => {
        if (active) {
          setStatus(data?.status ?? error?.status ?? "unready");
        }
      })
      .catch(() => {
        if (active) {
          setStatus("unreachable");
        }
      });
    return () => {
      active = false;
    };
  }, []);

  return (
    <main>
      <h1>Quicker</h1>
      <p data-testid="api-status">API: {status}</p>
    </main>
  );
}
