import createClient, { type Middleware } from "openapi-fetch";
import type { paths } from "./schema";

/**
 * The only way the web app talks to the API: a client typed by the generated contract (contracts/openapi-v1.json →
 * src/api/schema.d.ts, regenerated with `pnpm generate:api`). Every path, parameter and response is checked at
 * compile time, so a contract change that removes a field breaks the build here before it breaks a user.
 */
export interface ApiClientOptions {
  baseUrl?: string | undefined;
  /** Returns the bearer token for the current session, or null when anonymous. */
  accessToken?: () => string | null;
  /** Sent as Accept-Language so bilingual payloads resolve server side where they must. */
  language?: () => string;
}

export type ApiClient = ReturnType<typeof createClient<paths>>;

/** Same-origin by default: the API is served next to the app unless VITE_API_BASE_URL says otherwise. */
function defaultBaseUrl(): string {
  return typeof window === "undefined" ? "http://localhost" : window.location.origin;
}

export function createApiClient(options: ApiClientOptions = {}): ApiClient {
  // fetch is resolved per call, so a test double installed after the client was created is honoured.
  const client = createClient<paths>({ baseUrl: options.baseUrl ?? defaultBaseUrl(), fetch: (input) => globalThis.fetch(input) });
  const auth: Middleware = {
    onRequest({ request }) {
      const token = options.accessToken?.();
      if (token) {
        request.headers.set("Authorization", `Bearer ${token}`);
      }
      const language = options.language?.();
      if (language) {
        request.headers.set("Accept-Language", language);
      }
      return request;
    },
  };
  client.use(auth);
  return client;
}

/** RFC 9457 problem details as the API sends them: a stable `code` and, for business blocks, a structured `why`. */
export interface ApiProblem {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
  why?: Record<string, unknown>;
}

export function isApiProblem(value: unknown): value is ApiProblem {
  return typeof value === "object" && value !== null && ("code" in value || "title" in value);
}
