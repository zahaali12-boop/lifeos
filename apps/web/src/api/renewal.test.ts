import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearSession, getSession, setSession, type TokenResponse } from "../session/session";
import { api } from "./index";

const tokens = (accessToken: string, refreshToken: string): TokenResponse => ({
  accessToken,
  refreshToken,
  expiresInSeconds: 600,
  tenant: { id: "00000000-0000-7000-8000-000000000001", slug: "acme", name: "Acme", defaultLanguage: "en", edition: "trader" },
  user: { id: "00000000-0000-7000-8000-000000000002", email: "owner@acme.test", displayName: "Owner", locale: "en", digitStyle: "latin", hasPassword: true, mfaEnabled: false },
  membershipId: "00000000-0000-7000-8000-000000000003",
} as unknown as TokenResponse);

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

/** Answers from the queue in order and records what was sent, so the test can see the renewal and the replay. */
function stubFetch(...answers: Response[]) {
  const sent: { url: string; auth: string | null; body: string }[] = [];
  const fetch = vi.fn(async (input: Request | string, init?: RequestInit) => {
    const request = input instanceof Request ? input : new Request(input, init);
    sent.push({ url: request.url, auth: request.headers.get("Authorization"), body: await request.text() });
    const answer = answers.shift();
    if (answer === undefined) {
      throw new Error("no answer queued");
    }
    return answer;
  });
  vi.stubGlobal("fetch", fetch);
  return sent;
}

describe("the session renewal", () => {
  beforeEach(() => {
    setSession(tokens("old-access", "refresh-1"));
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    clearSession();
  });

  it("trades the refresh token for new ones on a 401 and sends the request again with the new token", async () => {
    const sent = stubFetch(json({ title: "Unauthorized", status: 401 }, 401), json(tokens("new-access", "refresh-2")), json({ id: "me" }));
    const result = await api.GET("/api/v1/me");
    expect(result.response.status).toBe(200);
    expect(sent.map((s) => s.url.replace(/^https?:\/\/[^/]+/, ""))).toEqual(["/api/v1/me", "/api/v1/auth/refresh", "/api/v1/me"]);
    expect(sent[0]?.auth).toBe("Bearer old-access");
    expect(JSON.parse(sent[1]?.body ?? "{}")).toEqual({ refreshToken: "refresh-1" });
    expect(sent[2]?.auth).toBe("Bearer new-access");
    expect(getSession()?.refreshToken).toBe("refresh-2");
  });

  it("signs out when the session itself is gone", async () => {
    stubFetch(json({ title: "Unauthorized", status: 401 }, 401), json({ code: "auth.session_expired", status: 403 }, 403));
    const result = await api.GET("/api/v1/me");
    expect(result.response.status).toBe(401);
    expect(getSession()).toBeNull();
  });
});
