import createClient, {} from "openapi-fetch";
/** Same-origin by default: the API is served next to the app unless VITE_API_BASE_URL says otherwise. */
function defaultBaseUrl() {
    return typeof window === "undefined" ? "http://localhost" : window.location.origin;
}
export function createApiClient(options = {}) {
    // fetch is resolved per call, so a test double installed after the client was created is honoured.
    const client = createClient({ baseUrl: options.baseUrl ?? defaultBaseUrl(), fetch: (input) => globalThis.fetch(input) });
    const auth = {
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
export function isApiProblem(value) {
    return typeof value === "object" && value !== null && ("code" in value || "title" in value);
}
