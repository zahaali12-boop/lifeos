import { i18n } from "../i18n";
import { clearSession, getSession } from "../session/session";
import { requestStepUp } from "../session/stepUp";
import { createApiClient, isApiProblem, type ApiProblem } from "./client";

/**
 * The app's single API client: bearer token from the session, Accept-Language from i18n, sign-out on 401, and step-up
 * on `auth.step_up_required`: the person confirms their identity and the same request is sent again with the fresh
 * token, so a sensitive action completes in one click instead of failing.
 */
export const api = createApiClient({
  baseUrl: import.meta.env.VITE_API_BASE_URL,
  accessToken: () => getSession()?.accessToken ?? null,
  language: () => i18n.language,
});

/** Copies of in-flight writes, kept until their response arrives so a step-up can replay them. */
const replayable = new Map<string, Request>();

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
    if (request.method !== "GET" && request.method !== "HEAD") {
      replayable.set(id, request.clone());
    }
    return undefined;
  },
  async onResponse({ response, id }) {
    const original = replayable.get(id);
    replayable.delete(id);
    if (response.status === 401 && getSession()) {
      clearSession();
      return response;
    }
    if (!original || !(await needsStepUp(response)) || !(await requestStepUp())) {
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
