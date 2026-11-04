import { Navigate } from "@tanstack/react-router";
import { useSession } from "../session/session";
import { DashboardPage } from "./DashboardPage";
import { fullProgramChosen, isPhone } from "./phone/shared";
import { TraderHomePage } from "./trader/HomePage";

/**
 * The home of the workspace's edition (ADR-0035 §10): the trader's day in plain words, or the full dashboard. On a
 * phone the trader edition opens its phone site instead (§21), unless the full program was chosen for the session.
 */
export function HomeRoute() {
  const session = useSession();
  if (session?.tenant.edition === "trader" && isPhone() && !fullProgramChosen()) {
    return <Navigate to="/p" replace />;
  }
  return session?.tenant.edition === "trader" ? <TraderHomePage /> : <DashboardPage />;
}
