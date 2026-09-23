import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { api, unwrap } from "../api";
import { setLanguage } from "../i18n";
import { clearSession, getSession, setSession, type TokenResponse } from "../session/session";
import { StepUpDialog } from "./StepUpDialog";

const tokens: TokenResponse = {
  accessToken: "stale-token",
  refreshToken: "refresh-token",
  expiresInSeconds: 600,
  tenant: { id: "00000000-0000-7000-8000-000000000001", slug: "acme", name: "Acme", defaultLanguage: "en" },
  user: { id: "00000000-0000-7000-8000-000000000002", email: "owner@acme.test", displayName: "Owner", locale: "en", timeZone: "Asia/Baghdad", digitStyle: "latin", hasMfa: false, isPlatformOperator: false },
  membershipId: "00000000-0000-7000-8000-000000000003",
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

interface Seen {
  method: string;
  path: string;
  authorization: string | null;
  body: string;
}

/** A fake API: sensitive writes need a token issued by step-up; step-up accepts one password. */
function stubApi(): Seen[] {
  const seen: Seen[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: Request) => {
      const path = new URL(input.url).pathname;
      const body = await input.text();
      const authorization = input.headers.get("Authorization");
      seen.push({ method: input.method, path, authorization, body });
      if (path === "/api/v1/me/step-up") {
        return (JSON.parse(body) as { password?: string }).password === "correct-horse"
          ? json({ ...tokens, accessToken: "fresh-token", refreshToken: "" })
          : json({ title: "Unprocessable", status: 422, code: "auth.step_up_failed", detail: "The password is not valid." }, 422);
      }
      if (path === "/api/v1/api-keys") {
        return authorization === "Bearer fresh-token"
          ? json({ id: "k1", name: "bridge", key: "qk_x.secret", prefix: "secret", expiresAt: null }, 201)
          : json({ title: "Recent authentication required", status: 403, code: "auth.step_up_required" }, 403);
      }
      return json({}, 404);
    }),
  );
  return seen;
}

describe("StepUpDialog", () => {
  beforeEach(async () => {
    await setLanguage("en");
    setSession(tokens);
  });
  afterEach(() => {
    clearSession();
    vi.unstubAllGlobals();
  });

  it("asks for the password on auth.step_up_required, then replays the same request with the fresh token", async () => {
    const seen = stubApi();
    render(<StepUpDialog />);

    const pending = api.POST("/api/v1/api-keys", { body: { name: "bridge", scopes: [], expiresAt: null, ipAllowlist: [] } });
    const password = await screen.findByLabelText(/Password/);
    expect(screen.getByRole("dialog")).toHaveTextContent("Confirm it's you");

    fireEvent.change(password, { target: { value: "wrong" } });
    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));
    await screen.findByText("The password is not valid.");

    fireEvent.change(password, { target: { value: "correct-horse" } });
    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));
    const created = unwrap(await pending);

    expect(created.key).toBe("qk_x.secret");
    const writes = seen.filter((s) => s.path === "/api/v1/api-keys");
    expect(writes.map((s) => s.authorization)).toEqual(["Bearer stale-token", "Bearer fresh-token"]);
    expect(writes[1]?.body).toBe(writes[0]?.body);
    // The session keeps its refresh token: step-up renews only the access token.
    expect(getSession()).toMatchObject({ accessToken: "fresh-token", refreshToken: "refresh-token" });
    await waitFor(() => {
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    });
  });

  it("returns the original refusal when the person cancels", async () => {
    stubApi();
    render(<StepUpDialog />);

    const pending = api.POST("/api/v1/api-keys", { body: { name: "bridge", scopes: [], expiresAt: null, ipAllowlist: [] } });
    await screen.findByRole("dialog");
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    const result = await pending;
    expect(result.response.status).toBe(403);
    expect(() => unwrap(result)).toThrow();
    expect(getSession()?.accessToken).toBe("stale-token");
  });
});
