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

const mutating = new Set(["POST", "PUT", "PATCH", "DELETE"]);

/** How long to wait before sending a write again whose answer the network lost. */
export const RESEND_DELAY_MS = 800;

/** A random version-4 UUID; crypto.getRandomValues also works on plain-HTTP deployments, where randomUUID does not. */
export function newIdempotencyKey(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = ((bytes[6] ?? 0) & 0x0f) | 0x40;
  bytes[8] = ((bytes[8] ?? 0) & 0x3f) | 0x80;
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/**
 * Sends a request; a write whose answer the network lost (the connection dropped, fetch rejected) is sent once more
 * under the same Idempotency-Key, so the API answers with what it already did instead of doing it twice. A cancelled
 * request (AbortError) is not sent again.
 */
async function send(request: Request): Promise<Response> {
  if (!mutating.has(request.method) || !request.headers.has("Idempotency-Key")) {
    return globalThis.fetch(request);
  }
  const again = request.clone();
  try {
    return await globalThis.fetch(request);
  } catch (error) {
    if (!(error instanceof TypeError) || again.signal.aborted) {
      throw error;
    }
    await new Promise((resolve) => setTimeout(resolve, RESEND_DELAY_MS));
    return globalThis.fetch(again);
  }
}

export function createApiClient(options: ApiClientOptions = {}): ApiClient {
  // fetch is resolved per call, so a test double installed after the client was created is honoured.
  const client = createClient<paths>({ baseUrl: options.baseUrl ?? defaultBaseUrl(), fetch: send });
  const auth: Middleware = {
    onRequest({ request }) {
      // Every write carries its own key (ADR-0009): a resend of it, or the replay after a step-up, runs at most once.
      if (mutating.has(request.method) && !request.headers.has("Idempotency-Key")) {
        request.headers.set("Idempotency-Key", newIdempotencyKey());
      }
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
