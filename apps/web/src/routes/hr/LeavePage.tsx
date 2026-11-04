import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { useOpenRecord } from "../../lib/documents";
import { formatDate, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { DecimalField, Field, FormError, PageHeader, SelectField, TextareaField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { num } from "../purchasing/shared";
import { leaveKinds, useEmployees, type Leave } from "./shared";

interface LeaveForm {
  employeeId: string;
  kind: string;
  fromDate: string;
  toDate: string;
  days: string;
  notes: string;
}

/** Leave (founder programme item 8): annual against the entitlement, sick, or unpaid, which reduces the pay of the month it falls in. */
export function LeavePage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [employeeId, setEmployeeId] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<LeaveForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/hr/leave");
  const employees = useEmployees(companyId);
  const list = useQuery({
    queryKey: ["leaves", companyId, employeeId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/payroll/leaves", { params: { query: { companyId, ...(employeeId ? { employeeId } : {}) } } })),
  });
  const open = (list.data ?? []).find((l) => l.id === openId) ?? null;
  const refresh = async (): Promise<void> => {
    await Promise.all([["leaves"], ["employees"], ["employee"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const save = useMutation({
    mutationFn: async (f: LeaveForm) => unwrap(await api.POST("/api/v1/payroll/leaves", { body: { employeeId: f.employeeId, kind: f.kind, fromDate: f.fromDate, toDate: f.toDate, days: f.days.trim() ? num(f.days) : null, notes: f.notes || null } })),
    onSuccess: async () => { setProblem(null); setForm(null); await refresh(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const cancel = useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/payroll/leaves/{leaveId}/cancel", { params: { path: { leaveId: id } } })),
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<Leave, unknown>[]>(
    () => [
      { id: "employee", accessorFn: (r) => `${r.employeeNumber} · ${localized(r.employeeName)}`, header: t("hr.employee"), size: 240, cell: ({ row }) => <span dir="auto">{row.original.employeeNumber} · {localized(row.original.employeeName)}</span> },
      { id: "kind", accessorFn: (r) => t(`hr.leaveKinds.${r.kind}`), header: t("hr.kind"), size: 130 },
      { id: "from", accessorKey: "fromDate", header: t("hr.from"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.fromDate)}</span> },
      { id: "to", accessorKey: "toDate", header: t("hr.to"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.toDate)}</span> },
      { id: "days", accessorKey: "days", header: t("hr.days"), size: 90, meta: { aggregate: "sum" }, cell: ({ row }) => <span className="tabular" dir="ltr">{formatNumber(row.original.days)}</span> },
      { id: "status", accessorFn: (r) => t(`hr.leaveStatuses.${r.status}`), header: t("common.status"), size: 110, cell: ({ row }) => <Badge tone={row.original.status === "approved" ? "success" : "neutral"} data-testid="doc-status">{t(`hr.leaveStatuses.${row.original.status}`)}</Badge> },
      { id: "notes", accessorKey: "notes", header: t("hr.notes"), size: 240, cell: ({ row }) => <span dir="auto">{row.original.notes ?? ""}</span> },
    ],
    [t],
  );

  const blank = (): LeaveForm => ({ employeeId, kind: "annual", fromDate: today(), toDate: today(), days: "", notes: "" });
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };

  return (
    <>
      <PageHeader
        title={t("nav.leave")}
        description={t("hr.leaveDescription")}
        actions={
          <Button onClick={() => { setProblem(null); setForm(blank()); }} disabled={!companyId} data-testid="new-leave">
            <Plus aria-hidden="true" />
            {t("hr.newLeave")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("hr.employee")}>
          <SelectField value={employeeId} onChange={(ev) => { setEmployeeId(ev.target.value); }} data-testid="employee-filter">
            <option value="">{t("common.all")}</option>
            {(employees.data ?? []).map((e) => (
              <option key={e.id} value={e.id}>{e.number} · {localized(e.name)}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      <FormError message={form ? null : (problem?.message ?? null)} />
      <DataGrid<Leave> label="nav.leave" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("hr.emptyLeave")} emptyDescription={t("hr.emptyLeaveDescription")} onOpen={(row) => { setProblem(null); setOpenId(row.id); }} groupable searchable />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("hr.newLeave")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("hr.employee")} required>
                  <SelectField value={form.employeeId} onChange={(ev) => { setForm({ ...form, employeeId: ev.target.value }); }} required data-testid="leave-employee">
                    <option value="">—</option>
                    {(employees.data ?? []).filter((e) => e.status === "active").map((e) => (
                      <option key={e.id} value={e.id}>{e.number} · {localized(e.name)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("hr.kind")} description={t("hr.leaveKindHelp")} required>
                  <SelectField value={form.kind} onChange={(ev) => { setForm({ ...form, kind: ev.target.value }); }} data-testid="leave-kind">
                    {leaveKinds.map((k) => (
                      <option key={k} value={k}>{t(`hr.leaveKinds.${k}`)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("hr.from")} required>
                  <TextField type="date" value={form.fromDate} onChange={(ev) => { setForm({ ...form, fromDate: ev.target.value, toDate: form.toDate < ev.target.value ? ev.target.value : form.toDate }); }} dir="ltr" required data-testid="leave-from" />
                </Field>
                <Field label={t("hr.to")} required>
                  <TextField type="date" value={form.toDate} onChange={(ev) => { setForm({ ...form, toDate: ev.target.value }); }} dir="ltr" required data-testid="leave-to" />
                </Field>
                <Field label={t("hr.days")} description={t("hr.daysHelp")}>
                  <DecimalField value={form.days} onChange={(ev) => { setForm({ ...form, days: ev.target.value }); }} dir="ltr" data-testid="leave-days" />
                </Field>
                <Field label={t("hr.notes")}>
                  <TextareaField rows={1} value={form.notes} onChange={(ev) => { setForm({ ...form, notes: ev.target.value }); }} data-testid="leave-notes" />
                </Field>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} disabled={!form.employeeId || !form.fromDate || !form.toDate} data-testid="save-leave">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          {open ? (
            <div className="flex flex-col gap-4" data-testid="leave-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="auto">{open.employeeNumber} · {localized(open.employeeName)}</span>
                  <Badge tone={open.status === "approved" ? "success" : "neutral"}>{t(`hr.leaveStatuses.${open.status}`)}</Badge>
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("hr.kind"), t(`hr.leaveKinds.${open.kind}`)],
                [t("hr.from"), formatDate(open.fromDate)],
                [t("hr.to"), formatDate(open.toDate)],
                [t("hr.days"), formatNumber(open.days)],
                ...(open.notes ? [[t("hr.notes"), open.notes] as [string, string]] : []),
              ]} />
              {open.status === "approved" ? (
                <div>
                  <Button variant="secondary" onClick={() => { cancel.mutate(open.id); }} loading={cancel.isPending} data-testid="cancel-leave">{t("hr.cancelLeave")}</Button>
                </div>
              ) : null}
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
