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
import { useCatalogue, useReports, type ReportSummary } from "./shared";

/** The saved reports a member owns or has been given (founder programme item 9b): open one in the builder, or start a new one from any model of the catalogue. */
export function ReportsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const can = useCan();
  const reports = useReports();
  const catalogue = useCatalogue();
  const canManage = can("reporting.report.manage");

  const columns = useMemo<ColumnDef<ReportSummary, unknown>[]>(() => {
    const modelName = (code: string): string => {
      const model = catalogue.data?.find((m) => m.code === code);
      return model ? localized(model.name) : code;
    };
    const access = (r: ReportSummary): string => {
      if (r.isOwner) {
        return r.isShared ? t("reports.access.sharedByMe") : t("reports.access.mine");
      }
      return r.canEdit ? t("reports.access.sharedToEdit") : t("reports.access.sharedToView");
    };
    return [
      { id: "name", accessorFn: (r) => localized(r.name), header: t("reports.name"), size: 260, cell: ({ row }) => <span dir="auto" className="font-medium">{localized(row.original.name)}</span> },
      { id: "model", accessorFn: (r) => modelName(r.model), header: t("reports.model"), size: 170 },
      { id: "kind", accessorFn: (r) => t(`reports.kinds.${r.kind}`), header: t("reports.kind"), size: 110 },
      { id: "access", accessorFn: access, header: t("reports.access.title"), size: 170 },
      { id: "description", accessorFn: (r) => localized(r.description), header: t("reports.descriptionColumn"), size: 320, cell: ({ row }) => <span dir="auto" className="text-fg-muted">{localized(row.original.description)}</span> },
      { id: "updated", accessorFn: (r) => r.updatedAt, header: t("reports.updated"), size: 170, cell: ({ row }) => formatDateTime(row.original.updatedAt) },
    ];
  }, [t, catalogue.data]);

  const open = (report: ReportSummary): void => { void navigate({ to: "/reports/builder", search: { report: report.id } }); };
  const start = (model?: string): void => { void navigate({ to: "/reports/builder", search: model ? { model } : {} }); };

  return (
    <div>
      <PageHeader
        title={t("nav.reports")}
        description={t("reports.pageDescription")}
        actions={canManage ? <Button onClick={() => { start(); }} data-testid="new-report"><Plus aria-hidden="true" /> {t("reports.newReport")}</Button> : null}
      />
      {canManage && catalogue.data && catalogue.data.length > 0 ? (
        <div className="mb-4 flex flex-wrap items-center gap-2" data-testid="report-models">
          <span className="text-sm text-fg-muted">{t("reports.startFrom")}</span>
          {catalogue.data.map((model) => (
            <Button key={model.code} variant="secondary" size="sm" onClick={() => { start(model.code); }} data-testid={`start-${model.code}`}>
              {localized(model.name)}
            </Button>
          ))}
        </div>
      ) : null}
      <DataGrid<ReportSummary>
        label="nav.reports"
        columns={columns}
        data={reports.data ?? []}
        rowKey={(r) => r.id}
        loading={reports.isPending}
        emptyTitle={t("reports.empty")}
        emptyDescription={t("reports.emptyDescription")}
        emptyAction={canManage ? <Button onClick={() => { start(); }}>{t("reports.newReport")}</Button> : undefined}
        onOpen={open}
        exportable={false}
        searchable
      />
    </div>
  );
}
