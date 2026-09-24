import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider, createMemoryHistory, createRootRoute, createRoute, createRouter } from "@tanstack/react-router";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { setLanguage } from "../i18n";
import { LoginPage } from "./LoginPage";

function renderLogin() {
  const rootRoute = createRootRoute();
  const loginRoute = createRoute({ getParentRoute: () => rootRoute, path: "/login", component: LoginPage });
  const signupRoute = createRoute({ getParentRoute: () => rootRoute, path: "/signup", component: () => null });
  const router = createRouter({ routeTree: rootRoute.addChildren([loginRoute, signupRoute]), history: createMemoryHistory({ initialEntries: ["/login"] }) });
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

describe("LoginPage", () => {
  it("renders the sign-in form in English, then in Arabic with the document direction flipped", async () => {
    await setLanguage("en");
    renderLogin();
    await waitFor(() => {
      expect(screen.getByRole("heading", { name: "Sign in" })).toBeInTheDocument();
    });
    expect(screen.getByLabelText(/Email/)).toHaveAttribute("dir", "ltr");
    expect(document.documentElement.dir).toBe("ltr");

    await setLanguage("ar");
    await waitFor(() => {
      expect(screen.getByRole("heading", { name: "تسجيل الدخول" })).toBeInTheDocument();
    });
    expect(document.documentElement.dir).toBe("rtl");
    expect(document.documentElement.lang).toBe("ar");
  });

  it("offers the security key and takes a recovery code or an authenticator code at the two-step verification step", async () => {
    await setLanguage("en");
    const verified: Record<string, unknown>[] = [];
    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: Request) => {
        const path = new URL(input.url).pathname;
        const body = (await input.json()) as Record<string, unknown>;
        if (path === "/api/v1/auth/login") {
          return new Response(JSON.stringify({ status: "mfa_required", challengeToken: "challenge", mfaMethods: ["totp", "webauthn"] }), { status: 200, headers: { "content-type": "application/json" } });
        }
        verified.push(body);
        return new Response(JSON.stringify({ title: "Unauthorized", status: 401, code: "auth.mfa_invalid", detail: "The code is not valid." }), { status: 401, headers: { "content-type": "application/problem+json" } });
      }),
    );
    renderLogin();
    await waitFor(() => {
      expect(screen.getByRole("heading", { name: "Sign in" })).toBeInTheDocument();
    });
    fireEvent.change(screen.getByLabelText(/Email/), { target: { value: "owner@example.test" } });
    fireEvent.change(screen.getByLabelText(/Password/), { target: { value: "correct-horse-battery-staple" } });
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    await waitFor(() => {
      expect(screen.getByTestId("mfa-step")).toBeInTheDocument();
    });
    expect(screen.getByTestId("mfa-use-key")).toHaveTextContent("Use security key or passkey");

    fireEvent.change(screen.getByTestId("mfa-code"), { target: { value: "abcd-efgh" } });
    fireEvent.click(screen.getByTestId("mfa-verify"));
    await waitFor(() => {
      expect(verified).toHaveLength(1);
    });
    expect(verified[0]).toEqual({ challengeToken: "challenge", recoveryCode: "abcd-efgh" });
    await waitFor(() => {
      expect(screen.getByRole("alert")).toHaveTextContent("The code is not valid.");
    });

    fireEvent.change(screen.getByTestId("mfa-code"), { target: { value: " 123456 " } });
    fireEvent.click(screen.getByTestId("mfa-verify"));
    await waitFor(() => {
      expect(verified).toHaveLength(2);
    });
    expect(verified[1]).toEqual({ challengeToken: "challenge", code: "123456" });
    vi.unstubAllGlobals();
  });
});
