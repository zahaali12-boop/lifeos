import { jsx as _jsx } from "react/jsx-runtime";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider, createMemoryHistory, createRootRoute, createRoute, createRouter } from "@tanstack/react-router";
import { render, screen, waitFor } from "@testing-library/react";
import { setLanguage } from "../i18n";
import { LoginPage } from "./LoginPage";
function renderLogin() {
    const rootRoute = createRootRoute();
    const loginRoute = createRoute({ getParentRoute: () => rootRoute, path: "/login", component: LoginPage });
    const signupRoute = createRoute({ getParentRoute: () => rootRoute, path: "/signup", component: () => null });
    const router = createRouter({ routeTree: rootRoute.addChildren([loginRoute, signupRoute]), history: createMemoryHistory({ initialEntries: ["/login"] }) });
    return render(_jsx(QueryClientProvider, { client: new QueryClient(), children: _jsx(RouterProvider, { router: router }) }));
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
});
