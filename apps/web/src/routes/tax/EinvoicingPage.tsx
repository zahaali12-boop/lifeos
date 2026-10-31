import { Badge, Button, Dialog, DialogContent, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { useOpenRecord } from "../../lib/documents";
import { formatDateTime } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, Tabs, useCompanyContext } from "../inventory/shared";
import { RecordActivity } from "../RecordDiscussion";
import { StatusBar, type Flow } from "../StatusBar";

type Device = components["schemas"]["EinvoiceDeviceSummary"];
type Submission = components["schemas"]["EinvoiceSubmissionSummary"];

interface DeviceForm {
  id: string | null;
  name: string;
  environment: string;
  organizationUnit: string;
  industry: string;
  standard: boolean;
  simplified: boolean;
  autoSubmit: boolean;
  registrationNumber: string;
  street: string;
  buildingNumber: string;
  plot: string;
  district: string;
  city: string;
  postalCode: string;
}

const environments = ["simulator", "sandbox", "simulation", "production"];
const tones: Record<string, "neutral" | "info" | "success" | "warning" | "danger"> = { csr: "neutral", compliance: "info", checked: "warning", active: "success", revoked: "danger", pending: "info", cleared: "success", reported: "success", rejected: "danger", failed: "danger" };

function Status({ status, prefix }: { status: string; prefix: "deviceStatuses" | "submissionStatuses" }) {
  const { t } = useTranslation();
  return <Badge tone={tones[status] ?? "neutral"} data-testid="doc-status">{t(`einvoicing.${prefix}.${status}`, { defaultValue: status })}</Badge>;
}

const emptyForm = (): DeviceForm => ({ id: null, name: "", environment: "simulator", organizationUnit: "", industry: "", standard: true, simplified: true, autoSubmit: true, registrationNumber: "", street: "", buildingNumber: "", plot: "", district: "", city: "", postalCode: "" });

/** The QR of a submission, fetched with the session so the image needs no public address. */
function QrImage({ submissionId, label }: { submissionId: string; label: string }) {
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    let active = true;
    let created: string | null = null;
    void api.GET("/api/v1/einvoicing/submissions/{submissionId}/qr.svg", { params: { path: { submissionId } }, parseAs: "blob" }).then((r) => {
      if (active && r.data) {
        created = URL.createObjectURL(r.data);
        setUrl(created);
      }
    });
    return () => { active = false; if (created) { URL.revokeObjectURL(created); } };
  }, [submissionId]);
  return url ? <img src={url} alt={label} width={160} height={160} className="rounded-md border border-border bg-white p-1" data-testid="einvoice-qr" /> : null;
}

/**
 * E-invoicing (roadmap 8.6, ZATCA phase 2): the devices a company onboards with the scheme (the certificate request,
 * the compliance certificate with an OTP, the compliance checks, the production certificate, renewal) and the
 * submissions of its documents with what the scheme answered, the signed XML and the QR.
 */
export function EinvoicingPage() {
  const { t } = useTranslation();
  const can = useCan();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const canManage = can("einvoicing.device.manage");
  const [tab, setTab] = useState("devices");
  const [openId, setOpenId] = useOpenRecord("/tax/einvoicing");
  const [form, setForm] = useState<DeviceForm | null>(null);
  const [otp, setOtp] = useState("");
  const [otpFor, setOtpFor] = useState<"compliance" | "renew" | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [statusFilter, setStatusFilter] = useState("");

  const devices = useQuery({
    queryKey: ["einvoice-devices", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/einvoicing/devices", { params: { query: { companyId } } })),
  });
  const submissions = useQuery({
    queryKey: ["einvoice-submissions", companyId, statusFilter],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/einvoicing/submissions", { params: { query: { companyId, ...(statusFilter ? { status: statusFilter } : {}) } } })),
  });
  const device = (devices.data ?? []).find((d) => d.id === openId) ?? null;
  const submission = (submissions.data ?? []).find((s) => s.id === openId) ?? null;
  const openSubmission = useQuery({
    queryKey: ["einvoice-submission", openId],
    enabled: Boolean(openId) && !device && !submission,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/einvoicing/submissions/{submissionId}", { params: { path: { submissionId: openId ?? "" } } })),
  });
  const shownSubmission = submission ?? (device ? null : openSubmission.data ?? null);

  const refresh = async (): Promise<void> => {
    await Promise.all([["einvoice-devices"], ["einvoice-submissions"], ["einvoice-submission"], ["sales-invoices"], ["sales-invoice"], ["activities"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const save = useMutation({
    mutationFn: async (f: DeviceForm) => {
      const body = {
        companyId,
        name: f.name,
        environment: f.environment,
        organizationUnit: f.organizationUnit || null,
        industry: f.industry || null,
        invoiceTypes: `${f.standard ? "1" : "0"}${f.simplified ? "1" : "0"}00`,
        autoSubmit: f.autoSubmit,
        seller: { registrationNumber: f.registrationNumber || null, registrationScheme: "CRN", street: f.street || null, buildingNumber: f.buildingNumber || null, plot: f.plot || null, district: f.district || null, city: f.city || null, postalCode: f.postalCode || null },
      };
      return unwrap(f.id ? await api.PUT("/api/v1/einvoicing/devices/{deviceId}", { params: { path: { deviceId: f.id } }, body }) : await api.POST("/api/v1/einvoicing/devices", { body }));
    },
    onSuccess: async (saved) => { setProblem(null); setForm(null); await refresh(); setOpenId(saved.id); },
    onError: fail,
  });
  const act = useMutation({
    mutationFn: async (input: { id: string; action: "compliance-csid" | "compliance-checks" | "production-csid" | "renew" | "revoke" }) => {
      const params = { path: { deviceId: input.id } };
      switch (input.action) {
        case "compliance-csid": return unwrap(await api.POST("/api/v1/einvoicing/devices/{deviceId}/compliance-csid", { params, body: { otp } }));
        case "compliance-checks": return unwrap(await api.POST("/api/v1/einvoicing/devices/{deviceId}/compliance-checks", { params }));
        case "production-csid": return unwrap(await api.POST("/api/v1/einvoicing/devices/{deviceId}/production-csid", { params }));
        case "renew": return unwrap(await api.POST("/api/v1/einvoicing/devices/{deviceId}/renew", { params, body: { otp } }));
        case "revoke": return unwrap(await api.POST("/api/v1/einvoicing/devices/{deviceId}/revoke", { params }));
      }
    },
    onSuccess: async () => { setProblem(null); setOtp(""); setOtpFor(null); await refresh(); },
    onError: fail,
  });
  const download = useMutation({
    mutationFn: async (input: { id: string; cleared: boolean; number: string }) => {
      const blob = unwrap(await api.GET("/api/v1/einvoicing/submissions/{submissionId}/xml", { params: { path: { submissionId: input.id }, query: { cleared: input.cleared } }, parseAs: "blob" }));
      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = `${input.number}${input.cleared ? "-cleared" : ""}.xml`;
      document.body.append(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    },
    onError: fail,
  });

  const deviceColumns = useMemo<ColumnDef<Device, unknown>[]>(
    () => [
      { id: "name", accessorKey: "name", header: t("common.name"), size: 200, cell: ({ row }) => <span dir="auto">{row.original.name}</span> },
      { id: "status", accessorFn: (r) => t(`einvoicing.deviceStatuses.${r.status}`), header: t("common.status"), size: 130, cell: ({ row }) => <Status status={row.original.status} prefix="deviceStatuses" /> },
      { id: "environment", accessorFn: (r) => t(`einvoicing.environments.${r.environment}`), header: t("einvoicing.environment"), size: 130, cell: ({ row }) => t(`einvoicing.environments.${row.original.environment}`) },
      { id: "types", accessorFn: (r) => types(r.invoiceTypes, t), header: t("einvoicing.invoiceTypes"), size: 180, cell: ({ row }) => types(row.original.invoiceTypes, t) },
      { id: "vat", accessorKey: "vatNumber", header: t("einvoicing.vatNumber"), size: 160, cell: ({ row }) => <span dir="ltr">{row.original.vatNumber}</span> },
      { id: "counter", accessorKey: "invoiceCounter", header: t("einvoicing.counter"), size: 100, cell: ({ row }) => <span className="tabular" dir="ltr">{String(row.original.invoiceCounter)}</span> },
      { id: "expires", accessorKey: "certificateExpiresAt", header: t("einvoicing.certificateExpires"), size: 160, cell: ({ row }) => <span dir="ltr">{row.original.certificateExpiresAt ? formatDateTime(row.original.certificateExpiresAt) : "—"}</span> },
    ],
    [t],
  );
  const submissionColumns = useMemo<ColumnDef<Submission, unknown>[]>(
    () => [
      { id: "number", accessorKey: "documentNumber", header: t("einvoicing.document"), size: 160, cell: ({ row }) => <span dir="ltr">{row.original.documentNumber}</span> },
      { id: "status", accessorFn: (r) => t(`einvoicing.submissionStatuses.${r.status}`), header: t("common.status"), size: 120, cell: ({ row }) => <Status status={row.original.status} prefix="submissionStatuses" /> },
      { id: "kind", accessorFn: (r) => t(`einvoicing.kinds.${r.kind}`), header: t("einvoicing.kind"), size: 120, cell: ({ row }) => t(`einvoicing.kinds.${row.original.kind}`) },
      { id: "type", accessorFn: (r) => t(`einvoicing.typeCodes.${r.typeCode}`, { defaultValue: r.typeCode }), header: t("einvoicing.typeCode"), size: 120, cell: ({ row }) => t(`einvoicing.typeCodes.${row.original.typeCode}`, { defaultValue: row.original.typeCode }) },
      { id: "counter", accessorKey: "invoiceCounter", header: t("einvoicing.counter"), size: 90, cell: ({ row }) => <span className="tabular" dir="ltr">{String(row.original.invoiceCounter)}</span> },
      { id: "device", accessorKey: "deviceName", header: t("einvoicing.device"), size: 160, cell: ({ row }) => <span dir="auto">{row.original.deviceName}</span> },
      { id: "warnings", accessorFn: (r) => r.warnings.length, header: t("einvoicing.warnings"), size: 90, cell: ({ row }) => <span className="tabular" dir="ltr">{String(row.original.warnings.length)}</span> },
      { id: "submitted", accessorKey: "submittedAt", header: t("einvoicing.submittedAt"), size: 160, cell: ({ row }) => <span dir="ltr">{formatDateTime(row.original.submittedAt)}</span> },
    ],
    [t],
  );

  const edit = (d: Device | null): void => {
    setProblem(null);
    setForm(d ? { id: d.id, name: d.name, environment: d.environment, organizationUnit: d.organizationUnit, industry: d.industry, standard: d.invoiceTypes.startsWith("1"), simplified: d.invoiceTypes[1] === "1", autoSubmit: d.autoSubmit, registrationNumber: d.seller.registrationNumber ?? "", street: d.seller.street ?? "", buildingNumber: d.seller.buildingNumber ?? "", plot: d.seller.plot ?? "", district: d.seller.district ?? "", city: d.seller.city ?? "", postalCode: d.seller.postalCode ?? "" } : emptyForm());
  };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const flow = (d: Device): Flow => {
    const steps = ["csr", "compliance", "checked", "active"].map((id) => ({ id, label: t(`einvoicing.deviceStatuses.${id}`) }));
    return d.status === "revoked" ? { steps, state: { step: null, label: t("einvoicing.deviceStatuses.revoked"), tone: "danger" } } : { steps, state: { step: d.status, label: t(`einvoicing.deviceStatuses.${d.status}`) } };
  };

  return (
    <>
      <PageHeader
        title={t("nav.einvoicing")}
        description={t("einvoicing.description")}
        actions={canManage && tab === "devices" ? (
          <Button onClick={() => { edit(null); }} disabled={!companyId} data-testid="new-device">
            <Plus aria-hidden="true" />
            {t("einvoicing.newDevice")}
          </Button>
        ) : null}
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        {tab === "submissions" ? (
          <Field label={t("common.status")}>
            <SelectField value={statusFilter} onChange={(e) => { setStatusFilter(e.target.value); }} data-testid="submission-status-filter">
              <option value="">{t("common.all")}</option>
              {["cleared", "reported", "rejected", "failed", "pending"].map((s) => <option key={s} value={s}>{t(`einvoicing.submissionStatuses.${s}`)}</option>)}
            </SelectField>
          </Field>
        ) : null}
      </div>
      <Tabs tabs={[{ id: "devices", label: t("einvoicing.tabs.devices"), testId: "tab-devices" }, { id: "submissions", label: t("einvoicing.tabs.submissions"), testId: "tab-submissions" }]} value={tab} onChange={(id) => { setTab(id); setProblem(null); }} />
      <FormError message={!openId && !form ? problem?.message ?? null : null} />
      {tab === "devices" ? (
        <>
          <p className="mb-3 text-sm text-fg-muted">{t("einvoicing.devicesHelp")}</p>
          <DataGrid<Device> label="einvoicing.tabs.devices" columns={deviceColumns} data={devices.data ?? []} rowKey={(row) => row.id} loading={devices.isPending && Boolean(companyId)} emptyTitle={t("einvoicing.emptyDevices")} emptyDescription={t("einvoicing.emptyDevicesDescription")} onOpen={(row) => { setProblem(null); setOpenId(row.id); }} exportable={false} />
        </>
      ) : (
        <DataGrid<Submission> label="einvoicing.tabs.submissions" columns={submissionColumns} data={submissions.data ?? []} rowKey={(row) => row.id} loading={submissions.isPending && Boolean(companyId)} emptyTitle={t("einvoicing.emptySubmissions")} emptyDescription={t("einvoicing.emptySubmissionsDescription")} onOpen={(row) => { setProblem(null); setOpenId(row.id); }} exportable={false} />
      )}

      <Dialog open={Boolean(device)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setOtpFor(null); setProblem(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {device ? (
            <div className="flex flex-col gap-4" data-testid="device-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="auto">{device.name}</span>
                  <Status status={device.status} prefix="deviceStatuses" />
                  <Badge tone="neutral">{t(`einvoicing.environments.${device.environment}`)}</Badge>
                </DialogTitle>
              </DialogHeader>
              <StatusBar {...flow(device)} actions={canManage ? (
                <>
                  {device.status === "csr" ? <Button onClick={() => { setOtpFor("compliance"); setOtp(""); }} data-testid="request-compliance">{t("einvoicing.actions.requestCompliance")}</Button> : null}
                  {device.status === "compliance" || device.status === "checked" ? <Button onClick={() => { act.mutate({ id: device.id, action: "compliance-checks" }); }} loading={act.isPending} data-testid="run-checks">{t("einvoicing.actions.runChecks")}</Button> : null}
                  {device.status === "checked" ? <Button onClick={() => { act.mutate({ id: device.id, action: "production-csid" }); }} loading={act.isPending} data-testid="request-production">{t("einvoicing.actions.requestProduction")}</Button> : null}
                  {device.status === "active" ? <Button variant="secondary" onClick={() => { setOtpFor("renew"); setOtp(""); }} data-testid="renew-device">{t("einvoicing.actions.renew")}</Button> : null}
                  {device.status !== "revoked" ? <Button variant="ghost" onClick={() => { edit(device); }} data-testid="edit-device">{t("common.edit")}</Button> : null}
                  {device.status !== "revoked" ? <Button variant="ghost" onClick={() => { act.mutate({ id: device.id, action: "revoke" }); }} data-testid="revoke-device">{t("einvoicing.actions.revoke")}</Button> : null}
                </>
              ) : null} />
              <FormError message={problem?.message ?? null} />
              {device.environment === "simulator" ? <p className="rounded-md border border-warning/40 bg-warning-soft px-3 py-2 text-sm" data-testid="simulator-note">{t("einvoicing.simulatorNote")}</p> : null}
              {otpFor ? (
                <form onSubmit={(e) => { e.preventDefault(); act.mutate({ id: device.id, action: otpFor === "compliance" ? "compliance-csid" : "renew" }); }} className="flex flex-wrap items-end gap-3 rounded-md border border-border p-3" data-testid="otp-form">
                  <Field label={t("einvoicing.otp")} description={device.environment === "simulator" ? t("einvoicing.otpSimulatorHelp") : t("einvoicing.otpHelp")} required>
                    <TextField value={otp} onChange={(e) => { setOtp(e.target.value); }} inputMode="numeric" dir="ltr" required data-testid="otp" />
                  </Field>
                  <Button type="submit" loading={act.isPending} data-testid="confirm-otp">{otpFor === "compliance" ? t("einvoicing.actions.requestCompliance") : t("einvoicing.actions.renew")}</Button>
                  <Button type="button" variant="secondary" onClick={() => { setOtpFor(null); }}>{t("common.cancel")}</Button>
                </form>
              ) : null}
              <KeyValues entries={[
                [t("einvoicing.vatNumber"), <span key="v" dir="ltr">{device.vatNumber}</span>],
                [t("einvoicing.serialNumber"), <span key="s" dir="ltr" className="break-all">{device.serialNumber}</span>],
                [t("einvoicing.invoiceTypes"), types(device.invoiceTypes, t)],
                [t("einvoicing.organizationUnit"), device.organizationUnit],
                [t("einvoicing.industry"), device.industry],
                [t("einvoicing.autoSubmit"), device.autoSubmit ? t("common.yes") : t("common.no")],
                [t("einvoicing.seller.registrationNumber"), device.seller.registrationNumber ?? "—"],
                [t("einvoicing.seller.address"), [device.seller.street, device.seller.buildingNumber, device.seller.district, device.seller.city, device.seller.postalCode].filter(Boolean).join(", ") || "—"],
                [t("einvoicing.certificate"), <span key="c" dir="ltr" className="break-all">{device.certificateSubject ?? "—"}</span>],
                [t("einvoicing.certificateIssuer"), <span key="i" dir="ltr" className="break-all">{device.certificateIssuer ?? "—"}</span>],
                [t("einvoicing.certificateExpires"), device.certificateExpiresAt ? formatDateTime(device.certificateExpiresAt) : "—"],
                [t("einvoicing.counter"), <span key="n" className="tabular" dir="ltr">{String(device.invoiceCounter)}</span>],
                [t("einvoicing.lastHash"), <span key="h" dir="ltr" className="break-all text-xs">{device.lastInvoiceHash ?? "—"}</span>],
              ]} />
              {device.complianceChecks.length > 0 ? (
                <section className="flex flex-col gap-2" data-testid="compliance-checks">
                  <h3 className="text-sm font-semibold">{t("einvoicing.complianceChecks")}</h3>
                  <Table>
                    <TableHeader>
                      <TableRow>
                        <TableHead>{t("einvoicing.kind")}</TableHead>
                        <TableHead>{t("einvoicing.typeCode")}</TableHead>
                        <TableHead>{t("common.status")}</TableHead>
                        <TableHead>{t("einvoicing.messages")}</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {device.complianceChecks.map((c, idx) => (
                        <TableRow key={idx} data-testid="compliance-check-row">
                          <TableCell>{t(`einvoicing.kinds.${c.kind}`)}</TableCell>
                          <TableCell>{t(`einvoicing.typeCodes.${c.typeCode}`, { defaultValue: c.typeCode })}</TableCell>
                          <TableCell><Status status={c.status} prefix="submissionStatuses" /></TableCell>
                          <TableCell dir="auto">{[...c.errors, ...c.warnings].join("; ") || "—"}</TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                </section>
              ) : null}
              {device.status === "csr" && device.csr ? (
                <details className="text-xs">
                  <summary className="cursor-pointer text-sm">{t("einvoicing.csr")}</summary>
                  <pre className="mt-2 max-h-48 overflow-auto rounded-md border border-border bg-bg-subtle p-2" dir="ltr">{device.csr}</pre>
                </details>
              ) : null}
              <RecordActivity entityType="einvoice_device" entityId={device.id} />
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(shownSubmission)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setProblem(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {shownSubmission ? (
            <div className="flex flex-col gap-4" data-testid="submission-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{shownSubmission.documentNumber}</span>
                  <Status status={shownSubmission.status} prefix="submissionStatuses" />
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="flex flex-wrap items-start gap-6">
                <KeyValues entries={[
                  [t("einvoicing.kind"), t(`einvoicing.kinds.${shownSubmission.kind}`)],
                  [t("einvoicing.typeCode"), t(`einvoicing.typeCodes.${shownSubmission.typeCode}`, { defaultValue: shownSubmission.typeCode })],
                  [t("einvoicing.device"), shownSubmission.deviceName],
                  [t("einvoicing.counter"), <span key="n" className="tabular" dir="ltr">{String(shownSubmission.invoiceCounter)}</span>],
                  [t("einvoicing.invoiceHash"), <span key="h" dir="ltr" className="break-all text-xs">{shownSubmission.invoiceHash}</span>],
                  [t("einvoicing.previousHash"), <span key="p" dir="ltr" className="break-all text-xs">{shownSubmission.previousHash}</span>],
                  [t("einvoicing.submittedAt"), <span key="s" dir="ltr">{formatDateTime(shownSubmission.submittedAt)}</span>],
                  [t("einvoicing.requestId"), <span key="r" dir="ltr">{shownSubmission.requestId ?? "—"}</span>],
                ]} />
                {shownSubmission.qr ? <QrImage submissionId={shownSubmission.id} label={t("einvoicing.qr")} /> : null}
              </div>
              {shownSubmission.errors.length > 0 ? <ul className="list-disc ps-5 text-sm text-danger" data-testid="submission-errors">{shownSubmission.errors.map((m, idx) => <li key={idx} dir="auto">{m}</li>)}</ul> : null}
              {shownSubmission.warnings.length > 0 ? <ul className="list-disc ps-5 text-sm" data-testid="submission-warnings">{shownSubmission.warnings.map((m, idx) => <li key={idx} dir="auto">{m}</li>)}</ul> : null}
              <div className="flex flex-wrap gap-2">
                <Button variant="secondary" onClick={() => { download.mutate({ id: shownSubmission.id, cleared: false, number: shownSubmission.documentNumber }); }} data-testid="download-xml">{t("einvoicing.downloadXml")}</Button>
                {shownSubmission.hasClearedXml ? <Button variant="secondary" onClick={() => { download.mutate({ id: shownSubmission.id, cleared: true, number: shownSubmission.documentNumber }); }} data-testid="download-cleared-xml">{t("einvoicing.downloadClearedXml")}</Button> : null}
              </div>
              <RecordActivity entityType="einvoice_submission" entityId={shownSubmission.id} />
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4" data-testid="device-form">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("einvoicing.editDevice") : t("einvoicing.newDevice")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <p className="text-sm text-fg-muted">{t("einvoicing.deviceHelp")}</p>
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("common.name")} required>
                  <TextField value={form.name} onChange={(e) => { setForm({ ...form, name: e.target.value }); }} dir="auto" required data-testid="device-name" />
                </Field>
                <Field label={t("einvoicing.environment")} description={t("einvoicing.environmentHelp")}>
                  <SelectField value={form.environment} onChange={(e) => { setForm({ ...form, environment: e.target.value }); }} disabled={Boolean(form.id)} data-testid="device-environment">
                    {environments.map((env) => <option key={env} value={env}>{t(`einvoicing.environments.${env}`)}</option>)}
                  </SelectField>
                </Field>
                <Field label={t("einvoicing.organizationUnit")} description={t("einvoicing.organizationUnitHelp")}>
                  <TextField value={form.organizationUnit} onChange={(e) => { setForm({ ...form, organizationUnit: e.target.value }); }} dir="auto" data-testid="device-ou" />
                </Field>
                <Field label={t("einvoicing.industry")}>
                  <TextField value={form.industry} onChange={(e) => { setForm({ ...form, industry: e.target.value }); }} dir="auto" data-testid="device-industry" />
                </Field>
                <div className="flex flex-col gap-2 text-sm">
                  <span className="font-medium">{t("einvoicing.invoiceTypes")}</span>
                  <label className="flex items-center gap-2"><input type="checkbox" checked={form.standard} onChange={(e) => { setForm({ ...form, standard: e.target.checked }); }} data-testid="device-standard" />{t("einvoicing.kinds.standard")}</label>
                  <label className="flex items-center gap-2"><input type="checkbox" checked={form.simplified} onChange={(e) => { setForm({ ...form, simplified: e.target.checked }); }} data-testid="device-simplified" />{t("einvoicing.kinds.simplified")}</label>
                  <label className="flex items-center gap-2"><input type="checkbox" checked={form.autoSubmit} onChange={(e) => { setForm({ ...form, autoSubmit: e.target.checked }); }} data-testid="device-auto-submit" />{t("einvoicing.autoSubmit")}</label>
                </div>
                <Field label={t("einvoicing.seller.registrationNumber")} description={t("einvoicing.seller.registrationNumberHelp")}>
                  <TextField value={form.registrationNumber} onChange={(e) => { setForm({ ...form, registrationNumber: e.target.value }); }} dir="ltr" data-testid="seller-crn" />
                </Field>
                <Field label={t("einvoicing.seller.street")}>
                  <TextField value={form.street} onChange={(e) => { setForm({ ...form, street: e.target.value }); }} dir="auto" data-testid="seller-street" />
                </Field>
                <Field label={t("einvoicing.seller.buildingNumber")}>
                  <TextField value={form.buildingNumber} onChange={(e) => { setForm({ ...form, buildingNumber: e.target.value }); }} dir="ltr" inputMode="numeric" data-testid="seller-building" />
                </Field>
                <Field label={t("einvoicing.seller.district")}>
                  <TextField value={form.district} onChange={(e) => { setForm({ ...form, district: e.target.value }); }} dir="auto" data-testid="seller-district" />
                </Field>
                <Field label={t("einvoicing.seller.city")}>
                  <TextField value={form.city} onChange={(e) => { setForm({ ...form, city: e.target.value }); }} dir="auto" data-testid="seller-city" />
                </Field>
                <Field label={t("einvoicing.seller.postalCode")}>
                  <TextField value={form.postalCode} onChange={(e) => { setForm({ ...form, postalCode: e.target.value }); }} dir="ltr" inputMode="numeric" data-testid="seller-postal" />
                </Field>
                <Field label={t("einvoicing.seller.plot")}>
                  <TextField value={form.plot} onChange={(e) => { setForm({ ...form, plot: e.target.value }); }} dir="ltr" />
                </Field>
              </div>
              <div className="flex gap-2">
                <Button type="submit" loading={save.isPending} disabled={!form.standard && !form.simplified} data-testid="save-device">{t("common.save")}</Button>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
              </div>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}

function types(flags: string, t: (key: string) => string): string {
  const parts: string[] = [];
  if (flags.startsWith("1")) { parts.push(t("einvoicing.kinds.standard")); }
  if (flags[1] === "1") { parts.push(t("einvoicing.kinds.simplified")); }
  return parts.join(" · ");
}
