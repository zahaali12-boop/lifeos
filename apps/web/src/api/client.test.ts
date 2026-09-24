import { afterEach, describe, expect, it, vi } from "vitest";
import { createApiClient, newIdempotencyKey, RESEND_DELAY_MS } from "./client";

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

/** Records every request the client sends, with its body read out, and answers from the queue in order. */
function stubFetch(...answers: (Response | Error | DOMException)[]) {
  const sent: { method: string; key: string | null; body: string }[] = [];
  const fetch = vi.fn(async (input: Request) => {
    sent.push({ method: input.method, key: input.headers.get("Idempotency-Key"), body: await input.text() });
    const answer = answers.shift();
    if (answer === undefined) {
      throw new Error("no answer queued");
    }
    if (answer instanceof Response) {
      return answer;
    }
    throw answer;
  });
  vi.stubGlobal("fetch", fetch);
  return sent;
}

const rateType = { code: "market_close", name: { en: "Market close", ar: "إغلاق السوق" } };

describe("the API client", () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("makes version-4 keys, a new one each time", () => {
    const keys = new Set(Array.from({ length: 50 }, () => newIdempotencyKey()));
    expect(keys.size).toBe(50);
    for (const key of keys) {
      expect(key).toMatch(uuid);
    }
  });

  it("sends every write with its own key and reads without one", async () => {
    const sent = stubFetch(json([]), json({ id: "a" }, 201), json({ id: "b" }, 201));
    const api = createApiClient({ baseUrl: "http://api.test" });
    await api.GET("/api/v1/organization/rate-types");
    await api.POST("/api/v1/organization/rate-types", { body: rateType });
    await api.POST("/api/v1/organization/rate-types", { body: rateType });

    expect(sent.map((r) => r.method)).toEqual(["GET", "POST", "POST"]);
    expect(sent[0]?.key).toBeNull();
    expect(sent[1]?.key).toMatch(uuid);
    expect(sent[2]?.key).toMatch(uuid);
    expect(sent[1]?.key).not.toBe(sent[2]?.key);
  });

  it("sends a write whose answer the network lost once more, under the same key and with the same body", async () => {
    vi.useFakeTimers();
    const sent = stubFetch(new TypeError("Failed to fetch"), json({ id: "a" }, 201));
    const api = createApiClient({ baseUrl: "http://api.test" });
    const pending = api.POST("/api/v1/organization/rate-types", { body: rateType });
    await vi.advanceTimersByTimeAsync(RESEND_DELAY_MS);
    const result = await pending;

    expect(result.response.status).toBe(201);
    expect(sent).toHaveLength(2);
    expect(sent[1]?.key).toBe(sent[0]?.key);
    expect(sent[1]?.body).toBe(sent[0]?.body);
    expect(JSON.parse(sent[1]?.body ?? "")).toEqual(rateType);
  });

  it("gives up after the second loss, and never resends a read, a cancelled write or a write the server answered", async () => {
    vi.useFakeTimers();
    let sent = stubFetch(new TypeError("Failed to fetch"), new TypeError("Failed to fetch"));
    const api = createApiClient({ baseUrl: "http://api.test" });
    const lost = api.POST("/api/v1/organization/rate-types", { body: rateType });
    const outcome = expect(lost).rejects.toThrow("Failed to fetch");
    await vi.advanceTimersByTimeAsync(RESEND_DELAY_MS);
    await outcome;
    expect(sent).toHaveLength(2);

    sent = stubFetch(new TypeError("Failed to fetch"));
    await expect(api.GET("/api/v1/organization/rate-types")).rejects.toThrow("Failed to fetch");
    expect(sent).toHaveLength(1);

    sent = stubFetch(new DOMException("The operation was aborted.", "AbortError"));
    await expect(api.POST("/api/v1/organization/rate-types", { body: rateType })).rejects.toThrow("aborted");
    expect(sent).toHaveLength(1);

    sent = stubFetch(json({ code: "rate_type.code_taken" }, 409));
    const refused = await api.POST("/api/v1/organization/rate-types", { body: rateType });
    expect(refused.response.status).toBe(409);
    expect(sent).toHaveLength(1);
  });

  it("keeps a key the caller chose", async () => {
    const sent = stubFetch(json({ id: "a" }, 201));
    const api = createApiClient({ baseUrl: "http://api.test" });
    await api.POST("/api/v1/organization/rate-types", { body: rateType, headers: { "Idempotency-Key": "import-2026-09-24" } });
    expect(sent[0]?.key).toBe("import-2026-09-24");
  });
});
