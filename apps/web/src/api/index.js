import { i18n } from "../i18n";
import { clearSession, getSession } from "../session/session";
import { createApiClient, isApiProblem } from "./client";
/** The app's single API client: bearer token from the session, Accept-Language from i18n, sign-out on 401. */
export const api = createApiClient({
    baseUrl: import.meta.env.VITE_API_BASE_URL,
    accessToken: () => getSession()?.accessToken ?? null,
    language: () => i18n.language,
});
api.use({
    onResponse({ response }) {
        if (response.status === 401 && getSession()) {
            clearSession();
        }
        return response;
    },
});
/** Unwraps an openapi-fetch result: the data on success, the problem details (or a generic one) thrown otherwise. */
export function unwrap(result) {
    if (result.error !== undefined) {
        throw (isApiProblem(result.error) ? result.error : { title: result.response.statusText, status: result.response.status });
    }
    if (result.data === undefined) {
        if (result.response.status === 204) {
            return undefined;
        }
        throw { title: result.response.statusText, status: result.response.status };
    }
    return result.data;
}
export { isApiProblem };
