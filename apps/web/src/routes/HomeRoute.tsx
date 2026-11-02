import { useSession } from "../session/session";
import { DashboardPage } from "./DashboardPage";
import { TraderHomePage } from "./trader/HomePage";

/** The home of the workspace's edition (ADR-0035 §10): the trader's day in plain words, or the full dashboard. */
export function HomeRoute() {
  const session = useSession();
  return session?.tenant.edition === "trader" ? <TraderHomePage /> : <DashboardPage />;
}
