import { Button } from "@quicker/ui";
import type { ColumnDef } from "@tanstack/react-table";
import { useNavigate } from "@tanstack/react-router";
import { Plus } from "lucide-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { DataGrid } from "../../grid/DataGrid";
import { formatDateTime, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { PageHeader } from "../common";
import { useCompanyContext } from "../inventory/shared";
import { useDashboards, type Dashboard } from "./shared";

/** The dashboards a member owns or that are shared with the workspace (founder programme item 9d): open one, or start a new one. */
export function DashboardsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const can = useCan();
  const { companyId } = useCompanyContext();
  const dashboards = useDashboards(companyId);
  const canManage = can("reporting.report.manage");

  const columns = useMemo<ColumnDef<Dashboard, unknown>[]>(() => [
    { id: "name", accessorFn: (d) => localized(d.name), header: t("dashboards.name"), size: 260, cell: ({ row }) => <span dir="auto" className="font-medium">{localized(row.original.name)}</span> },
    { id: "widgets", accessorFn: (d) => d.widgets.length, header: t("dashboards.widgets"), size: 100 },
    { id: "period", accessorFn: (d) => t(`dashboards.periods.${d.defaultPeriod}`), header: t("dashboards.defaultPeriod"), size: 150 },
    { id: "sharing", accessorFn: (d) => (d.isOwner ? (d.sharing === "tenant" ? t("dashboards.sharedByMe") : t("dashboards.mine")) : t("dashboards.sharedWithMe")), header: t("dashboards.access"), size: 170 },
    { id: "home", accessorFn: (d) => d.showOnHome, header: t("dashboards.onHome"), size: 110 },
    { id: "updated", accessorFn: (d) => d.updatedAt, header: t("reports.updated"), size: 170, cell: ({ row }) => formatDateTime(row.original.updatedAt) },
  ], [t]);

  return (
    <div>
      <PageHeader
        title={t("nav.dashboards")}
        description={t("dashboards.pageDescription")}
        actions={canManage ? <Button onClick={() => { void navigate({ to: "/dashboards/view", search: {} }); }} data-testid="new-dashboard"><Plus aria-hidden="true" /> {t("dashboards.newDashboard")}</Button> : null}
      />
      <DataGrid<Dashboard>
        columns={columns}
        data={dashboards.data ?? []}
        rowKey={(d) => d.id}
        entityType="dashboard"
        label={t("nav.dashboards")}
        loading={dashboards.isLoading}
        onOpen={(d) => { void navigate({ to: "/dashboards/view", search: { dashboard: d.id } }); }}
        emptyTitle={t("dashboards.emptyTitle")}
        emptyDescription={t("dashboards.emptyDescription")}
        groupable={false}
        exportable={false}
      />
    </div>
  );
}
