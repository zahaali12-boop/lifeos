import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus, Trash2 } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { useNewFromUrl, useOpenRecord } from "../../lib/documents";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextareaField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { MoreFields } from "../MoreFields";
import { num } from "../purchasing/shared";
import { RecordActivity } from "../RecordDiscussion";
import { codeFromName, EmployeeStatus, useEmployees, usePayComponents, type Employee } from "./shared";

interface ComponentLine {
  componentId: string;
  amount: string;
  ratePct: string;
}

interface EmployeeForm {
  id: string | null;
  partnerId: string;
  partnerCode: string;
  nameEn: string;
  nameAr: string;
  number: string;
  hireDate: string;
  terminationDate: string;
  basicSalary: string;
  jobTitleEn: string;
  jobTitleAr: string;
  departmentValueId: string;
  paymentMethod: string;
  bankName: string;
  iban: string;
  nationalId: string;
  leaveEntitlementDays: string;
  notes: string;
  components: ComponentLine[];
}

/**
 * Employees (founder programme item 8): a person hired into a company with a job, a basic salary and the components
 * they carry. A new employee creates the partner with the employee role and a supplier account in the company, so the
 * net pay can be paid from the bank like any other payable.
 */
export function EmployeesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [status, setStatus] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<EmployeeForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/hr/employees");
  const list = useEmployees(companyId, status);
  const components = usePayComponents(companyId, true);
  const dimensions = useQuery({ queryKey: ["dimensions"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/dimensions")) });
  const department = (dimensions.data ?? []).find((d) => /dep/i.test(d.code));
  const departmentValues = useQuery({
    queryKey: ["dimension-values", department?.id ?? ""],
    enabled: Boolean(department),
    queryFn: async () => unwrap(await api.GET("/api/v1/organization/dimensions/{dimensionId}/values", { params: { path: { dimensionId: department?.id ?? "" } } })),
  });
  const detail = useQuery({
    queryKey: ["employee", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/payroll/employees/{employeeId}", { params: { path: { employeeId: openId ?? "" } } })),
  });
  const currency = companies.find((c) => c.id === companyId)?.functionalCurrency ?? "";
  const refresh = async (): Promise<void> => {
    await Promise.all([["employees"], ["employee"], ["partners"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const save = useMutation({
    mutationFn: async (f: EmployeeForm) => {
      let partnerId = f.partnerId;
      if (!partnerId) {
        // The person first: a partner with the employee role, then the supplier account the net pay is paid through.
        const partner = unwrap(await api.POST("/api/v1/partners", { body: { code: f.partnerCode.trim() || codeFromName(f.nameEn), legalName: { en: f.nameEn, ar: f.nameAr }, kind: "person", isEmployee: true, isSupplier: false, isCustomer: false, defaultLanguage: "en", isActive: true } }));
        partnerId = partner.id;
        unwrap(await api.PUT("/api/v1/partners/{partnerId}/supplier-accounts/{companyId}", { params: { path: { partnerId, companyId } }, body: { currency, leadTimeDays: 0, priceTolerancePct: 0, qtyTolerancePct: 0, requiresPo: false, isActive: true } }));
      }
      const body = {
        companyId,
        partnerId,
        number: f.number.trim() || null,
        hireDate: f.hireDate,
        terminationDate: f.terminationDate || null,
        basicSalary: num(f.basicSalary),
        jobTitle: f.jobTitleEn.trim() || f.jobTitleAr.trim() ? { en: f.jobTitleEn, ar: f.jobTitleAr } : null,
        departmentValueId: f.departmentValueId || null,
        paymentMethod: f.paymentMethod,
        bankName: f.bankName || null,
        iban: f.iban || null,
        nationalId: f.nationalId || null,
        leaveEntitlementDays: f.leaveEntitlementDays.trim() ? Math.trunc(num(f.leaveEntitlementDays)) : 21,
        notes: f.notes || null,
        components: f.components.filter((c) => c.componentId).map((c) => ({ componentId: c.componentId, amount: c.amount.trim() ? num(c.amount) : null, ratePct: c.ratePct.trim() ? num(c.ratePct) : null })),
      };
      return f.id ? unwrap(await api.PUT("/api/v1/payroll/employees/{employeeId}", { params: { path: { employeeId: f.id } }, body })) : unwrap(await api.POST("/api/v1/payroll/employees", { body }));
    },
    onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<Employee, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("hr.employeeNumber"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "name", accessorFn: (r) => localized(r.name), header: t("banking.name"), size: 220, cell: ({ row }) => <span dir="auto">{localized(row.original.name)}</span> },
      { id: "status", accessorFn: (r) => t(`hr.employeeStatuses.${r.status}`), header: t("common.status"), size: 110, cell: ({ row }) => <EmployeeStatus status={row.original.status} /> },
      { id: "job", accessorFn: (r) => localized(r.jobTitle), header: t("hr.jobTitle"), size: 180, cell: ({ row }) => <span dir="auto">{localized(row.original.jobTitle)}</span> },
      { id: "department", accessorKey: "departmentCode", header: t("hr.department"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.departmentCode ?? "—"}</span> },
      { id: "hired", accessorKey: "hireDate", header: t("hr.hireDate"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.hireDate)}</span> },
      { id: "basic", accessorKey: "basicSalary", header: t("hr.basicSalary"), size: 140, meta: { aggregate: "sum" }, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.basicSalary, row.original.currency)}</span> },
      { id: "leave", accessorKey: "leaveBalance", header: t("hr.leaveBalance"), size: 110, cell: ({ row }) => <span className="tabular" dir="ltr">{formatNumber(row.original.leaveBalance)}</span> },
      { id: "payable", accessorKey: "canBePaid", header: t("hr.canBePaid"), size: 110, cell: ({ row }) => (row.original.canBePaid ? t("common.yes") : t("common.no")) },
    ],
    [t],
  );

  const blank = (): EmployeeForm => ({ id: null, partnerId: "", partnerCode: "", nameEn: "", nameAr: "", number: "", hireDate: today(), terminationDate: "", basicSalary: "", jobTitleEn: "", jobTitleAr: "", departmentValueId: "", paymentMethod: "transfer", bankName: "", iban: "", nationalId: "", leaveEntitlementDays: "21", notes: "", components: [] });
  const openForm = (x: Employee): void => {
    setProblem(null);
    setForm({ id: x.id, partnerId: x.partnerId, partnerCode: x.partnerCode, nameEn: x.name.en ?? "", nameAr: x.name.ar ?? "", number: x.number, hireDate: x.hireDate, terminationDate: x.terminationDate ?? "", basicSalary: String(x.basicSalary), jobTitleEn: x.jobTitle.en ?? "", jobTitleAr: x.jobTitle.ar ?? "", departmentValueId: x.departmentValueId ?? "", paymentMethod: x.paymentMethod, bankName: x.bankName ?? "", iban: x.iban ?? "", nationalId: x.nationalId ?? "", leaveEntitlementDays: String(x.leaveEntitlementDays), notes: x.notes ?? "", components: x.components.map((c) => ({ componentId: c.componentId, amount: c.amount === null ? "" : String(c.amount), ratePct: c.ratePct === null ? "" : String(c.ratePct) })) });
  };
  const patchComponent = (index: number, patch: Partial<ComponentLine>): void => { if (form) { setForm({ ...form, components: form.components.map((c, i) => (i === index ? { ...c, ...patch } : c)) }); } };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const assignable = (components.data ?? []).filter((c) => c.calculation !== "basic");
  const e = detail.data;

  useNewFromUrl("/hr/employees", () => { setProblem(null); setForm(blank()); }, Boolean(companyId));
  return (
    <>
      <PageHeader
        title={t("nav.employees")}
        description={t("hr.employeesDescription")}
        actions={
          <Button onClick={() => { setProblem(null); setForm(blank()); }} disabled={!companyId} data-testid="new-employee">
            <Plus aria-hidden="true" />
            {t("hr.newEmployee")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(ev) => { setStatus(ev.target.value); }} data-testid="status-filter">
            <option value="">{t("common.all")}</option>
            <option value="active">{t("hr.employeeStatuses.active")}</option>
            <option value="left">{t("hr.employeeStatuses.left")}</option>
          </SelectField>
        </Field>
      </div>
      <DataGrid<Employee> label="nav.employees" entityType="employee" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("hr.emptyEmployees")} emptyDescription={t("hr.emptyEmployeesDescription")} onOpen={(row) => { setProblem(null); setOpenId(row.id); }} groupable searchable />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("hr.editEmployee") : t("hr.newEmployee")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("hr.nameEn")} required>
                  <TextField value={form.nameEn} onChange={(ev) => { setForm({ ...form, nameEn: ev.target.value }); }} dir="ltr" required disabled={Boolean(form.id)} data-testid="employee-name-en" />
                </Field>
                <Field label={t("hr.nameAr")} required>
                  <TextField value={form.nameAr} onChange={(ev) => { setForm({ ...form, nameAr: ev.target.value }); }} dir="rtl" required disabled={Boolean(form.id)} data-testid="employee-name-ar" />
                </Field>
                <Field label={t("hr.hireDate")} required>
                  <TextField type="date" value={form.hireDate} onChange={(ev) => { setForm({ ...form, hireDate: ev.target.value }); }} dir="ltr" required data-testid="employee-hire-date" />
                </Field>
                <Field label={t("hr.basicSalary")} description={currency} required>
                  <TextField inputMode="decimal" value={form.basicSalary} onChange={(ev) => { setForm({ ...form, basicSalary: ev.target.value }); }} dir="ltr" required data-testid="employee-basic" />
                </Field>
                <Field label={t("hr.jobTitleEn")}>
                  <TextField value={form.jobTitleEn} onChange={(ev) => { setForm({ ...form, jobTitleEn: ev.target.value }); }} dir="ltr" data-testid="employee-job-en" />
                </Field>
                <Field label={t("hr.jobTitleAr")}>
                  <TextField value={form.jobTitleAr} onChange={(ev) => { setForm({ ...form, jobTitleAr: ev.target.value }); }} dir="rtl" data-testid="employee-job-ar" />
                </Field>
                {department ? (
                  <Field label={t("hr.department")} description={localized(department.name)}>
                    <SelectField value={form.departmentValueId} onChange={(ev) => { setForm({ ...form, departmentValueId: ev.target.value }); }} data-testid="employee-department">
                      <option value="">—</option>
                      {(departmentValues.data ?? []).map((v) => (
                        <option key={v.id} value={v.id}>{v.code} · {localized(v.name)}</option>
                      ))}
                    </SelectField>
                  </Field>
                ) : null}
                <Field label={t("hr.paymentMethod")} required>
                  <SelectField value={form.paymentMethod} onChange={(ev) => { setForm({ ...form, paymentMethod: ev.target.value }); }} data-testid="employee-payment-method">
                    <option value="transfer">{t("banking.methods.transfer")}</option>
                    <option value="cash">{t("banking.methods.cash")}</option>
                  </SelectField>
                </Field>
              </div>
              <MoreFields filled={Boolean(form.number || form.partnerCode || form.terminationDate || form.bankName || form.iban || form.nationalId || form.notes)}>
                <div className="grid gap-4 sm:grid-cols-3">
                  <Field label={t("hr.employeeNumber")} description={t("hr.employeeNumberHelp")}>
                    <TextField value={form.number} onChange={(ev) => { setForm({ ...form, number: ev.target.value.toUpperCase() }); }} dir="ltr" disabled={Boolean(form.id)} data-testid="employee-number" />
                  </Field>
                  <Field label={t("hr.partnerCode")} description={t("pos.codeHelp")}>
                    <TextField value={form.partnerCode} onChange={(ev) => { setForm({ ...form, partnerCode: ev.target.value.toUpperCase() }); }} dir="ltr" disabled={Boolean(form.id)} data-testid="employee-partner-code" />
                  </Field>
                  <Field label={t("hr.terminationDate")} description={t("hr.terminationDateHelp")}>
                    <TextField type="date" value={form.terminationDate} onChange={(ev) => { setForm({ ...form, terminationDate: ev.target.value }); }} dir="ltr" data-testid="employee-termination" />
                  </Field>
                  <Field label={t("hr.bankName")}>
                    <TextField value={form.bankName} onChange={(ev) => { setForm({ ...form, bankName: ev.target.value }); }} dir="auto" data-testid="employee-bank" />
                  </Field>
                  <Field label={t("hr.iban")}>
                    <TextField value={form.iban} onChange={(ev) => { setForm({ ...form, iban: ev.target.value.toUpperCase() }); }} dir="ltr" data-testid="employee-iban" />
                  </Field>
                  <Field label={t("hr.nationalId")}>
                    <TextField value={form.nationalId} onChange={(ev) => { setForm({ ...form, nationalId: ev.target.value }); }} dir="ltr" data-testid="employee-national-id" />
                  </Field>
                  <Field label={t("hr.leaveEntitlement")} description={t("hr.leaveEntitlementHelp")}>
                    <TextField inputMode="numeric" value={form.leaveEntitlementDays} onChange={(ev) => { setForm({ ...form, leaveEntitlementDays: ev.target.value }); }} dir="ltr" data-testid="employee-leave-days" />
                  </Field>
                  <Field label={t("hr.notes")} className="sm:col-span-2">
                    <TextareaField rows={1} value={form.notes} onChange={(ev) => { setForm({ ...form, notes: ev.target.value }); }} data-testid="employee-notes" />
                  </Field>
                </div>
              </MoreFields>
              <div className="flex flex-col gap-2" data-testid="employee-components">
                <div className="flex items-center justify-between">
                  <h3 className="text-sm font-semibold">{t("hr.components")}</h3>
                  <Button type="button" variant="ghost" size="sm" onClick={() => { setForm({ ...form, components: [...form.components, { componentId: "", amount: "", ratePct: "" }] }); }} data-testid="add-component">{t("hr.addComponent")}</Button>
                </div>
                <p className="text-sm text-muted-foreground">{t("hr.componentsHelp")}</p>
                {form.components.length > 0 ? (
                  <Table>
                    <TableHeader>
                      <TableRow>
                        <TableHead>{t("hr.component")}</TableHead>
                        <TableHead>{t("hr.ownAmount")}</TableHead>
                        <TableHead>{t("hr.ownRate")}</TableHead>
                        <TableHead />
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {form.components.map((line, index) => {
                        const chosen = assignable.find((c) => c.id === line.componentId);
                        const rated = chosen?.calculation === "percent_of_basic" || chosen?.calculation === "percent_of_gross";
                        return (
                          <TableRow key={String(index)} data-testid="employee-component">
                            <TableCell>
                              <SelectField aria-label={t("hr.component")} value={line.componentId} onChange={(ev) => { patchComponent(index, { componentId: ev.target.value }); }} data-testid={`component-${String(index)}`}>
                                <option value="">—</option>
                                {assignable.map((c) => (
                                  <option key={c.id} value={c.id}>{c.code} · {localized(c.name)}</option>
                                ))}
                              </SelectField>
                            </TableCell>
                            <TableCell><TextField aria-label={t("hr.ownAmount")} inputMode="decimal" value={line.amount} onChange={(ev) => { patchComponent(index, { amount: ev.target.value }); }} dir="ltr" className="w-32" disabled={rated} data-testid={`component-amount-${String(index)}`} /></TableCell>
                            <TableCell><TextField aria-label={t("hr.ownRate")} inputMode="decimal" value={line.ratePct} onChange={(ev) => { patchComponent(index, { ratePct: ev.target.value }); }} dir="ltr" className="w-24" disabled={!rated} data-testid={`component-rate-${String(index)}`} /></TableCell>
                            <TableCell>
                              <Button type="button" variant="ghost" size="sm" aria-label={t("expenses.removeLine")} onClick={() => { setForm({ ...form, components: form.components.filter((_, i) => i !== index) }); }} data-testid={`remove-component-${String(index)}`}><Trash2 aria-hidden="true" /></Button>
                            </TableCell>
                          </TableRow>
                        );
                      })}
                    </TableBody>
                  </Table>
                ) : null}
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} disabled={!form.nameEn.trim() || !form.hireDate || !(num(form.basicSalary) >= 0 && form.basicSalary.trim())} data-testid="save-employee">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {e ? (
            <div className="flex flex-col gap-4" data-testid="employee-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{e.number}</span>
                  <span dir="auto">{localized(e.name)}</span>
                  <EmployeeStatus status={e.status} />
                </DialogTitle>
              </DialogHeader>
              <div className="flex flex-wrap gap-2">
                <Button variant="secondary" onClick={() => { openForm(e); }} data-testid="edit-employee">{t("common.edit")}</Button>
              </div>
              <KeyValues entries={[
                [t("hr.partnerCode"), <span key="partner" dir="ltr">{e.partnerCode}</span>],
                [t("hr.jobTitle"), localized(e.jobTitle) || "—"],
                [t("hr.department"), e.departmentCode ?? "—"],
                [t("hr.hireDate"), formatDate(e.hireDate)],
                ...(e.terminationDate ? [[t("hr.terminationDate"), formatDate(e.terminationDate)] as [string, string]] : []),
                [t("hr.basicSalary"), <span key="basic" data-testid="employee-basic-salary">{formatMoney(e.basicSalary, e.currency)}</span>],
                [t("hr.paymentMethod"), t(`banking.methods.${e.paymentMethod}`)],
                ...(e.bankName ? [[t("hr.bankName"), e.bankName] as [string, string]] : []),
                ...(e.iban ? [[t("hr.iban"), <span key="iban" dir="ltr">{e.iban}</span>] as [string, React.ReactNode]] : []),
                [t("hr.leaveBalance"), <span key="leave" data-testid="employee-leave-balance">{`${formatNumber(e.leaveBalance)} / ${formatNumber(e.leaveEntitlementDays)}`}</span>],
                [t("hr.canBePaid"), e.canBePaid ? t("common.yes") : t("hr.cannotBePaid")],
                ...(e.notes ? [[t("hr.notes"), e.notes] as [string, string]] : []),
              ]} />
              {e.components.length > 0 ? (
                <Table data-testid="employee-component-rows">
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("hr.component")}</TableHead>
                      <TableHead>{t("hr.kind")}</TableHead>
                      <TableHead>{t("hr.ownAmount")}</TableHead>
                      <TableHead>{t("hr.ownRate")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {e.components.map((c) => (
                      <TableRow key={c.id}>
                        <TableCell dir="auto">{c.componentCode} · {localized(c.componentName)}</TableCell>
                        <TableCell>{t(`hr.componentKinds.${c.kind}`, { defaultValue: c.kind })}</TableCell>
                        <TableCell className="tabular" dir="ltr">{c.amount === null ? "—" : formatNumber(c.amount)}</TableCell>
                        <TableCell className="tabular" dir="ltr">{c.ratePct === null ? "—" : `${formatNumber(c.ratePct)} %`}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              ) : null}
              <RecordActivity entityType="employee" entityId={e.id} />
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
