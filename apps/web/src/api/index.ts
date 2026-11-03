import { i18n } from "../i18n";
import { clearSession, getSession, setSession, type TokenResponse } from "../session/session";
import { requestStepUp } from "../session/stepUp";
import { createApiClient, defaultBaseUrl, isApiProblem, type ApiProblem } from "./client";

/**
 * The app's single API client: bearer token from the session, Accept-Language from i18n, a silent renewal on 401
 * (the access token lasts minutes, the session days: the refresh token is traded for new tokens once, however many
 * requests failed together, and each is sent again; sign-out only when the session itself is gone), and step-up on
 * `auth.step_up_required`: the person confirms their identity and the same request is sent again with the fresh
 * token, so a sensitive action completes in one click instead of failing.
 */
const baseUrl: string = import.meta.env.VITE_API_BASE_URL ?? defaultBaseUrl();

export const api = createApiClient({
  baseUrl,
  accessToken: () => getSession()?.accessToken ?? null,
  language: () => i18n.language,
});

/** Copies of in-flight requests, kept until their response arrives so a renewal or a step-up can replay them. */
const replayable = new Map<string, Request>();

let renewal: Promise<boolean> | null = null;

/** Trades the session's refresh token for new tokens, once for every request that found the old token expired; false when the session is gone. */
function renewSession(): Promise<boolean> {
  renewal ??= (async () => {
    const session = getSession();
    if (!session?.refreshToken) {
      return false;
    }
    try {
      const response = await globalThis.fetch(`${baseUrl}/api/v1/auth/refresh`, {
        method: "POST",
        headers: { "content-type": "application/json", accept: "application/json" },
        body: JSON.stringify({ refreshToken: session.refreshToken }),
      });
      if (!response.ok) {
        return false;
      }
      setSession((await response.json()) as TokenResponse);
      return true;
    } catch {
      return false;
    } finally {
      renewal = null;
    }
  })();
  return renewal;
}

async function needsStepUp(response: Response): Promise<boolean> {
  if (response.status !== 403) {
    return false;
  }
  try {
    const body: unknown = await response.clone().json();
    return isApiProblem(body) && body.code === "auth.step_up_required";
  } catch {
    return false;
  }
}

api.use({
  onRequest({ request, id }) {
    replayable.set(id, request.clone());
    return undefined;
  },
  async onResponse({ response, id }) {
    const original = replayable.get(id);
    replayable.delete(id);
    if (response.status === 401 && getSession()) {
      if (original && !original.url.endsWith("/api/v1/auth/refresh") && (await renewSession())) {
        original.headers.set("Authorization", `Bearer ${getSession()?.accessToken ?? ""}`);
        return globalThis.fetch(original);
      }
      clearSession();
      return response;
    }
    if (!original || original.method === "GET" || original.method === "HEAD" || !(await needsStepUp(response)) || !(await requestStepUp())) {
      return response;
    }
    const token = getSession()?.accessToken;
    if (token) {
      original.headers.set("Authorization", `Bearer ${token}`);
    }
    return globalThis.fetch(original);
  },
});

/** Unwraps an openapi-fetch result: the data on success, the problem details (or a generic one) thrown otherwise. */
export function unwrap<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (result.error !== undefined) {
    throw (isApiProblem(result.error) ? result.error : { title: result.response.statusText, status: result.response.status }) satisfies ApiProblem;
  }
  if (result.data === undefined) {
    if (result.response.status === 204) {
      return undefined as T;
    }
    throw { title: result.response.statusText, status: result.response.status } satisfies ApiProblem;
  }
  return result.data;
}

export type { ApiProblem };
export { isApiProblem };
