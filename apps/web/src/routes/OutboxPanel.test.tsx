import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { setLanguage } from "../i18n";
import { clearSession, setSession, type TokenResponse } from "../session/session";
import type { components } from "../api/schema";
import { OutboxPanel } from "./OutboxPanel";

const tokens: TokenResponse = {
  accessToken: "operator-token",
  refreshToken: "refresh-token",
  expiresInSeconds: 600,
  tenant: { id: "00000000-0000-7000-8000-000000000001", slug: "acme", name: "Acme", defaultLanguage: "en" },
  user: { id: "00000000-0000-7000-8000-000000000002", email: "ops@acme.test", displayName: "Operator", locale: "en", timeZone: "Asia/Baghdad", digitStyle: "latin", hasMfa: false, isPlatformOperator: true },
  membershipId: "00000000-0000-7000-8000-000000000003",
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

const letter: components["schemas"]["OutboxMessage"] = {
  id: "00000000-0000-7000-8000-00000000000a",
  seq: 41,
  tenantId: tokens.tenant.id,
  occurredAt: "2026-09-23T10:00:00Z",
  eventType: "purchasing.receipt.posted",
  eventVersion: 1,
  aggregateType: "goods_receipt",
  aggregateId: "00000000-0000-7000-8000-00000000000b",
  payload: "{}",
  actor: "user",
  publishedAt: null,
  attempts: 10,
  nextAttemptAt: "2026-09-23T11:00:00Z",
  lastError: "Npgsql: deadlock detected",
  deadAt: "2026-09-23T11:00:00Z",
  discardedAt: null,
  discardedBy: null,
  discardReason: null,
};

/** A fake outbox: one dead letter, discarded when asked with a reason. */
function stubApi(): { method: string; path: string; body: string }[] {
  const seen: { method: string; path: string; body: string }[] = [];
  let discarded: components["schemas"]["OutboxMessage"] | null = null;
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: Request) => {
      const url = new URL(input.url);
      const body = await input.text();
      seen.push({ method: input.method, path: url.pathname, body });
      if (url.pathname === "/api/v1/platform/ops/outbox") {
        const state = url.searchParams.get("state");
        if (state === "dead") {
          return json(discarded ? [] : [letter]);
        }
        return json(state === "discarded" && discarded ? [discarded] : []);
      }
      if (url.pathname === `/api/v1/platform/ops/outbox/${letter.id ?? ""}/discard`) {
        const reason = (JSON.parse(body) as { reason: string }).reason;
        discarded = { ...letter, discardedAt: "2026-09-24T08:00:00Z", discardedBy: "ops@acme.test", discardReason: reason };
        return new Response(null, { status: 204 });
      }
      return json({}, 404);
    }),
  );
  return seen;
}

describe("OutboxPanel", () => {
  beforeEach(async () => {
    await setLanguage("en");
    setSession(tokens);
  });
  afterEach(() => {
    clearSession();
    vi.unstubAllGlobals();
  });

  it("lists dead letters and discards one with a reason, after which it is listed as discarded", async () => {
    const seen = stubApi();
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <OutboxPanel />
      </QueryClientProvider>,
    );

    const row = await screen.findByTestId("outbox-row");
    expect(row).toHaveTextContent("purchasing.receipt.posted");
    expect(row).toHaveTextContent("Npgsql: deadlock detected");
    fireEvent.click(within(row).getByRole("button", { name: "Discard" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByLabelText(/Reason/), { target: { value: "Receipt reposted by hand" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Discard" }));

    await screen.findByTestId("outbox-empty");
    expect(seen.find((s) => s.method === "POST")).toMatchObject({ path: `/api/v1/platform/ops/outbox/${letter.id ?? ""}/discard`, body: JSON.stringify({ reason: "Receipt reposted by hand" }) });

    fireEvent.change(screen.getByTestId("outbox-state"), { target: { value: "discarded" } });
    await waitFor(() => {
      expect(screen.getByTestId("outbox-discard-reason")).toHaveTextContent("Receipt reposted by hand");
    });
  });
});
