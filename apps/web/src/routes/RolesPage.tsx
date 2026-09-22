import { Badge, Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";
import { DataGrid } from "../grid/DataGrid";
import { formatNumber, localized } from "../lib/format";
import { PageHeader } from "./common";

type Role = components["schemas"]["RoleSummary"];

/** Roles and their grants (read-only in the shell; editing arrives with the role designer in the identity screens). */
export function RolesPage() {
  const { t } = useTranslation();
  const [selected, setSelected] = useState<Role | null>(null);
  const roles = useQuery({ queryKey: ["roles"], queryFn: async () => unwrap(await api.GET("/api/v1/roles")) });

  const columns = useMemo<ColumnDef<Role, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("roles.code"), size: 140 },
      { id: "name", accessorFn: (row) => localized(row.name), header: t("roles.name"), size: 220 },
      { id: "description", accessorKey: "description", header: t("roles.descriptionColumn"), size: 300 },
      { id: "grants", accessorFn: (row) => row.grants.length, header: t("roles.grants"), size: 100, cell: ({ row }) => <span className="tabular">{formatNumber(row.original.grants.length)}</span> },
      { id: "isSystem", accessorKey: "isSystem", header: t("roles.kind"), size: 110, cell: ({ row }) => <Badge tone={row.original.isSystem ? "accent" : "neutral"}>{row.original.isSystem ? t("roles.system") : t("roles.custom")}</Badge> },
    ],
    [t],
  );

  return (
    <>
      <PageHeader title={t("nav.roles")} description={t("roles.description")} />
      <DataGrid<Role> label="nav.roles" columns={columns} data={roles.data ?? []} rowKey={(row) => row.id} loading={roles.isPending} onOpen={setSelected} emptyTitle={t("roles.emptyTitle")} />
      <Dialog open={selected !== null} onOpenChange={(open) => { if (!open) { setSelected(null); } }}>
        <DialogContent closeLabel={t("common.close")}>
          {selected ? (
            <>
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">
                  {selected.code} · {localized(selected.name)}
                </DialogTitle>
                <DialogDescription className="text-sm text-fg-muted">{selected.description}</DialogDescription>
              </DialogHeader>
              <ul className="flex max-h-80 flex-wrap gap-1.5 overflow-y-auto" aria-label={t("roles.grants")}>
                {selected.grants.map((grant) => (
                  <li key={grant}>
                    <Badge tone="neutral" dir="ltr">
                      {grant}
                    </Badge>
                  </li>
                ))}
              </ul>
            </>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
