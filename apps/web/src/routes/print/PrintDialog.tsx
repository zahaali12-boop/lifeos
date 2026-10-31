import { Badge, Button, Dialog, DialogContent, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Copy, Download, Link2, Mail, MessageCircle, Printer } from "lucide-react";
import { useEffect, useRef, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { saveFile } from "../../lib/download";
import { formatDateTime, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, SelectField, TextareaField, TextField } from "../common";
import { Tabs } from "../inventory/shared";
import { awaitRender, documentTypeLabel, phoneDigits, useDocumentTypes, type PrintRender } from "./shared";

/** What to print: a document by id, or a report by its parameters (a statement by company, customer and period). */
export interface PrintSubject {
  documentType: string;
  documentId?: string | null;
  parameters?: Record<string, string>;
  /** The number (or the name of a report) shown in the title and in the messages sent. */
  number: string;
  /** The partner whose email and phone fill the send forms. */
  partnerId?: string | null;
  /** The variants the document may print under now (a draft invoice prints pro forma only); every variant when absent. */
  variants?: string[];
}

/** The "Print / send" button of a document's status bar, with the dialog it opens; hidden from members who may not print. */
export function PrintButton({ subject, testId = "print-document" }: { subject: PrintSubject; testId?: string }) {
  const { t } = useTranslation();
  const can = useCan();
  const [open, setOpen] = useState(false);
  if (!can("print.document.render")) {
    return null;
  }
  return (
    <>
      <Button variant="secondary" onClick={() => { setOpen(true); }} data-testid={testId}>
        <Printer aria-hidden="true" />
        {t("print.printSend")}
      </Button>
      {open ? <PrintDialog subject={subject} onClose={() => { setOpen(false); }} /> : null}
    </>
  );
}

type Tab = "preview" | "email" | "whatsapp" | "link" | "history";

/**
 * Print and send (roadmap 5.9): the document previewed from the same template the PDF uses; printed or downloaded
 * as the PDF the worker makes (cached by its fingerprint); emailed with the PDF attached; shared by WhatsApp or a
 * plain link that opens without signing in; and the history of every PDF, email and link made for it.
 */
export function PrintDialog({ subject, onClose }: { subject: PrintSubject; onClose: () => void }) {
  const { t } = useTranslation();
  const can = useCan();
  const queryClient = useQueryClient();
  const types = useDocumentTypes();
  const allVariants = types.data?.find((d) => d.documentType === subject.documentType)?.variants ?? [];
  const variants = subject.variants ? allVariants.filter((v) => subject.variants?.includes(v.code)) : allVariants;
  const [variant, setVariant] = useState(subject.variants?.[0] ?? "standard");
  const [tab, setTab] = useState<Tab>("preview");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [busy, setBusy] = useState<"print" | "download" | null>(null);
  const [email, setEmail] = useState({ to: "", subject: "", message: "" });
  const [whatsapp, setWhatsapp] = useState({ phone: "", message: t("print.whatsappMessage", { number: subject.number }), days: "7" });
  const [linkDays, setLinkDays] = useState("7");
  const [made, setMade] = useState<{ url: string; href: string | null; channel: string } | null>(null);
  const [copied, setCopied] = useState(false);
  const printFrame = useRef<HTMLIFrameElement>(null);
  const [printUrl, setPrintUrl] = useState<string | null>(null);
  const documentId = subject.documentId ?? null;
  const parameters = subject.parameters ?? {};
  const query = { documentType: subject.documentType, ...(documentId ? { documentId } : {}), variant, ...parameters };

  const partner = useQuery({
    queryKey: ["partner", subject.partnerId],
    enabled: Boolean(subject.partnerId),
    queryFn: async () => unwrap(await api.GET("/api/v1/partners/{partnerId}", { params: { path: { partnerId: subject.partnerId ?? "" } } })),
  });
  useEffect(() => {
    if (partner.data) {
      setEmail((e) => (e.to ? e : { ...e, to: partner.data.partner.email ?? "" }));
      setWhatsapp((w) => (w.phone ? w : { ...w, phone: partner.data.partner.phone ?? "" }));
    }
  }, [partner.data]);

  const preview = useQuery({
    queryKey: ["print-preview", query],
    queryFn: async () => unwrap(await api.GET("/api/v1/print/preview", { params: { query }, parseAs: "text" })),
    retry: false,
  });
  const historyKey = { documentType: subject.documentType, ...(documentId ? { documentId } : {}) };
  const renders = useQuery({
    queryKey: ["print-renders", historyKey],
    queryFn: async () => unwrap(await api.GET("/api/v1/print/renders", { params: { query: historyKey } })),
    refetchInterval: (q) => (q.state.data?.some((r) => r.status === "queued") ? 2_000 : false),
  });
  const emails = useQuery({
    queryKey: ["print-emails", historyKey],
    enabled: can("print.document.email"),
    queryFn: async () => unwrap(await api.GET("/api/v1/print/emails", { params: { query: historyKey } })),
    refetchInterval: (q) => (q.state.data?.some((e) => e.status === "queued") ? 2_000 : false),
  });
  const shares = useQuery({
    queryKey: ["print-shares", historyKey],
    enabled: can("print.document.share"),
    queryFn: async () => unwrap(await api.GET("/api/v1/print/shares", { params: { query: historyKey } })),
    // Openings are counted by people without an account, so the list keeps itself fresh while the dialog is open.
    refetchInterval: 5_000,
  });
  const refresh = async (): Promise<void> => {
    await Promise.all([["print-renders"], ["print-emails"], ["print-shares"], ["activities"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("print.failed"))); };

  /** The PDF of the document as it stands: asked for (reused when an identical one exists) and waited for. */
  const ensureRender = async (): Promise<PrintRender> => {
    const requested = unwrap(await api.POST("/api/v1/print/renders", { body: { documentType: subject.documentType, documentId, parameters: Object.keys(parameters).length > 0 ? parameters : null, variant, templateId: null } }));
    const render = requested.status === "queued" ? await awaitRender(requested.id) : requested;
    await queryClient.invalidateQueries({ queryKey: ["print-renders"] });
    if (render.status !== "ready") {
      throw new Error(render.error ?? t("print.renderFailed"));
    }
    return render;
  };
  const pdfBlob = async (render: PrintRender): Promise<{ blob: Blob; headers: Headers }> => {
    const file = await api.GET("/api/v1/print/renders/{renderId}/file", { params: { path: { renderId: render.id } }, parseAs: "blob" });
    return { blob: unwrap(file), headers: file.response.headers };
  };
  const download = async (render?: PrintRender): Promise<void> => {
    setProblem(null);
    setBusy("download");
    try {
      const ready = render ?? await ensureRender();
      const { blob, headers } = await pdfBlob(ready);
      saveFile(blob, headers, ready.fileName);
    } catch (error) {
      fail(error);
    } finally {
      setBusy(null);
    }
  };
  // Printing goes through the PDF: the browser's own viewer paginates it exactly as the template meant, where a
  // sandboxed preview frame (the template's HTML, no scripts) cannot be asked to print.
  const print = async (): Promise<void> => {
    setProblem(null);
    setBusy("print");
    try {
      const { blob } = await pdfBlob(await ensureRender());
      if (printUrl) {
        URL.revokeObjectURL(printUrl);
      }
      setPrintUrl(URL.createObjectURL(blob));
      // A viewer that never reports the frame loaded (a browser without one) must not leave the buttons disabled.
      setTimeout(() => { setBusy(null); }, 10_000);
    } catch (error) {
      fail(error);
      setBusy(null);
    }
  };
  const onPrintFrameLoaded = (): void => {
    setBusy(null);
    try {
      printFrame.current?.contentWindow?.print();
    } catch {
      if (printUrl) {
        window.open(printUrl, "_blank", "noopener");
      }
    }
  };
  useEffect(() => () => { if (printUrl) { URL.revokeObjectURL(printUrl); } }, [printUrl]);

  const sendEmail = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/print/emails", { body: { documentType: subject.documentType, documentId, parameters: Object.keys(parameters).length > 0 ? parameters : null, variant, templateId: null, to: email.to || null, subject: email.subject || null, message: email.message || null } })),
    onSuccess: async () => { setProblem(null); setEmail((e) => ({ ...e, subject: "", message: "" })); await refresh(); },
    onError: fail,
  });
  const share = useMutation({
    mutationFn: async (input: { channel: "whatsapp" | "link"; recipient: string | null; days: string }) => {
      const render = await ensureRender();
      return unwrap(await api.POST("/api/v1/print/shares", { body: { renderId: render.id, channel: input.channel, recipient: input.recipient, expiresInDays: Number(input.days) || 7 } }));
    },
    onSuccess: async (created, input) => {
      setProblem(null);
      setCopied(false);
      const url = created.url ?? "";
      if (input.channel === "whatsapp") {
        const digits = phoneDigits(whatsapp.phone);
        const href = `https://wa.me/${digits}?text=${encodeURIComponent(`${whatsapp.message.trim()}\n${url}`)}`;
        setMade({ url, href, channel: "whatsapp" });
        window.open(href, "_blank", "noopener");
      } else {
        setMade({ url, href: null, channel: "link" });
      }
      await refresh();
    },
    onError: fail,
  });
  const revoke = useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/print/shares/{shareId}/revoke", { params: { path: { shareId: id } } })),
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });
  const copy = async (text: string): Promise<void> => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  };

  const tabs: { id: Tab; label: string; testId: string }[] = [
    { id: "preview", label: t("print.preview"), testId: "print-tab-preview" },
    ...(can("print.document.email") ? [{ id: "email" as const, label: t("print.email"), testId: "print-tab-email" }] : []),
    ...(can("print.document.share") ? [{ id: "whatsapp" as const, label: t("print.whatsapp"), testId: "print-tab-whatsapp" }, { id: "link" as const, label: t("print.link"), testId: "print-tab-link" }] : []),
    { id: "history", label: t("print.history"), testId: "print-tab-history" },
  ];
  const previewError = preview.error ? toFormProblem(preview.error, t("print.previewFailed")).message : null;
  const statusTone = (status: string): "neutral" | "success" | "warning" | "danger" => (status === "sent" || status === "ready" ? "success" : status === "failed" ? "danger" : "warning");
  const submitEmail = (event: FormEvent): void => { event.preventDefault(); sendEmail.mutate(); };
  const submitWhatsapp = (event: FormEvent): void => { event.preventDefault(); share.mutate({ channel: "whatsapp", recipient: whatsapp.phone || null, days: whatsapp.days }); };
  const submitLink = (event: FormEvent): void => { event.preventDefault(); share.mutate({ channel: "link", recipient: null, days: linkDays }); };

  return (
    <Dialog open onOpenChange={(isOpen) => { if (!isOpen) { onClose(); } }}>
      <DialogContent closeLabel={t("common.close")} className="max-w-5xl" data-testid="print-dialog">
        <div className="flex flex-col gap-4">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold">{t("print.title")} · <span dir="auto">{subject.number}</span></DialogTitle>
          </DialogHeader>
          <div className="flex flex-wrap items-end gap-3">
            {variants.length > 1 ? (
              <Field label={t("print.variant")}>
                <SelectField value={variant} onChange={(e) => { setVariant(e.target.value); setMade(null); }} data-testid="print-variant">
                  {variants.map((v) => <option key={v.code} value={v.code}>{localized(v.name)}</option>)}
                </SelectField>
              </Field>
            ) : null}
            <Button onClick={() => { void print(); }} loading={busy === "print"} disabled={busy !== null} data-testid="print-now">
              <Printer aria-hidden="true" />
              {t("print.print")}
            </Button>
            <Button variant="secondary" onClick={() => { void download(); }} loading={busy === "download"} disabled={busy !== null} data-testid="download-pdf">
              <Download aria-hidden="true" />
              {t("print.downloadPdf")}
            </Button>
            {busy ? <span className="text-xs text-fg-muted" role="status">{t("print.rendering")}</span> : null}
          </div>
          <FormError message={problem?.message ?? null} />
          <Tabs tabs={tabs} value={tab} onChange={(id) => { setTab(id as Tab); }} />

          {tab === "preview" ? (
            previewError ? <p className="text-sm text-danger" data-testid="print-preview-error">{previewError}</p> : (
              // The template's HTML runs with an opaque origin: nothing in it can reach the app, its session or the API.
              <iframe title={t("print.preview")} srcDoc={preview.data ?? ""} sandbox="allow-scripts" className="h-[60vh] w-full rounded-md border border-border bg-white" data-testid="print-preview" />
            )
          ) : null}

          {tab === "email" ? (
            <form onSubmit={submitEmail} className="flex flex-col gap-3" data-testid="print-email-form">
              <div className="grid gap-3 sm:grid-cols-2">
                <Field label={t("print.to")} required>
                  <TextField type="email" value={email.to} onChange={(e) => { setEmail({ ...email, to: e.target.value }); }} dir="ltr" required data-testid="email-to" />
                </Field>
                <Field label={t("print.subject")} description={t("print.subjectHelp")}>
                  <TextField value={email.subject} onChange={(e) => { setEmail({ ...email, subject: e.target.value }); }} dir="auto" data-testid="email-subject" />
                </Field>
              </div>
              <Field label={t("print.message")}>
                <TextareaField value={email.message} onChange={(e) => { setEmail({ ...email, message: e.target.value }); }} rows={4} dir="auto" data-testid="email-message" />
              </Field>
              <div className="flex items-center gap-3">
                <Button type="submit" loading={sendEmail.isPending} disabled={!email.to.trim()} data-testid="send-email">
                  <Mail aria-hidden="true" />
                  {t("print.send")}
                </Button>
                <span className="text-xs text-fg-muted">{t("print.emailHelp")}</span>
              </div>
              <h3 className="text-sm font-semibold">{t("print.emailed")}</h3>
              {(emails.data ?? []).length === 0 ? <p className="text-sm text-fg-muted">{t("print.noEmails")}</p> : (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("print.to")}</TableHead>
                      <TableHead>{t("print.subject")}</TableHead>
                      <TableHead>{t("common.status")}</TableHead>
                      <TableHead>{t("print.when")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {(emails.data ?? []).map((e) => (
                      <TableRow key={e.id} data-testid="print-email-row">
                        <TableCell dir="ltr">{e.to}</TableCell>
                        <TableCell dir="auto">{e.subject}</TableCell>
                        <TableCell><Badge tone={statusTone(e.status)}>{t(`print.statuses.${e.status}`, { defaultValue: e.status })}</Badge>{e.error ? <span className="ms-2 text-xs text-danger">{e.error}</span> : null}</TableCell>
                        <TableCell dir="ltr">{formatDateTime(e.sentAt ?? e.requestedAt)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </form>
          ) : null}

          {tab === "whatsapp" ? (
            <form onSubmit={submitWhatsapp} className="flex flex-col gap-3" data-testid="print-whatsapp-form">
              <div className="grid gap-3 sm:grid-cols-2">
                <Field label={t("print.phone")} description={t("print.phoneHelp")} required>
                  <TextField type="tel" value={whatsapp.phone} onChange={(e) => { setWhatsapp({ ...whatsapp, phone: e.target.value }); }} dir="ltr" required data-testid="whatsapp-phone" />
                </Field>
                <Field label={t("print.expiresIn")}>
                  <TextField inputMode="numeric" value={whatsapp.days} onChange={(e) => { setWhatsapp({ ...whatsapp, days: e.target.value }); }} dir="ltr" data-testid="whatsapp-days" />
                </Field>
              </div>
              <Field label={t("print.message")}>
                <TextareaField value={whatsapp.message} onChange={(e) => { setWhatsapp({ ...whatsapp, message: e.target.value }); }} rows={3} dir="auto" data-testid="whatsapp-message" />
              </Field>
              <div className="flex flex-wrap items-center gap-3">
                <Button type="submit" loading={share.isPending && share.variables.channel === "whatsapp"} disabled={phoneDigits(whatsapp.phone).length < 8} data-testid="share-whatsapp">
                  <MessageCircle aria-hidden="true" />
                  {t("print.openWhatsApp")}
                </Button>
                <span className="text-xs text-fg-muted">{t("print.whatsappHelp", { days: whatsapp.days })}</span>
              </div>
              {made?.channel === "whatsapp" && made.href ? (
                <div className="flex flex-col gap-2 rounded-md border border-border p-3" data-testid="whatsapp-made">
                  <p className="text-sm">{t("print.whatsappReady")}</p>
                  <div className="flex flex-wrap items-center gap-2">
                    <a href={made.href} target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1 text-sm font-medium text-accent underline" data-testid="whatsapp-link">
                      <MessageCircle aria-hidden="true" className="size-4" />
                      {t("print.openWhatsApp")}
                    </a>
                    <Button type="button" variant="ghost" size="sm" onClick={() => { void copy(made.url); }} data-testid="copy-share-url">
                      <Copy aria-hidden="true" />
                      {copied ? t("print.copied") : t("print.copyLink")}
                    </Button>
                  </div>
                  <code dir="ltr" className="break-all rounded-sm bg-surface px-2 py-1 font-mono text-xs" data-testid="share-url">{made.url}</code>
                </div>
              ) : null}
            </form>
          ) : null}

          {tab === "link" ? (
            <form onSubmit={submitLink} className="flex flex-col gap-3" data-testid="print-link-form">
              <div className="flex flex-wrap items-end gap-3">
                <Field label={t("print.expiresIn")}>
                  <TextField inputMode="numeric" value={linkDays} onChange={(e) => { setLinkDays(e.target.value); }} dir="ltr" className="w-24" data-testid="link-days" />
                </Field>
                <Button type="submit" loading={share.isPending && share.variables.channel === "link"} data-testid="share-link">
                  <Link2 aria-hidden="true" />
                  {t("print.createLink")}
                </Button>
                <span className="text-xs text-fg-muted">{t("print.linkHelp")}</span>
              </div>
              {made?.channel === "link" ? (
                <div className="flex flex-col gap-2 rounded-md border border-border p-3" data-testid="link-made">
                  <div className="flex items-center gap-2">
                    <code dir="ltr" className="break-all rounded-sm bg-surface px-2 py-1 font-mono text-xs" data-testid="share-url">{made.url}</code>
                    <Button type="button" variant="ghost" size="sm" onClick={() => { void copy(made.url); }} data-testid="copy-share-url">
                      <Copy aria-hidden="true" />
                      {copied ? t("print.copied") : t("print.copyLink")}
                    </Button>
                  </div>
                  <p className="text-xs text-fg-muted">{t("print.linkOnce")}</p>
                </div>
              ) : null}
            </form>
          ) : null}

          {tab === "history" ? (
            <div className="flex flex-col gap-4" data-testid="print-history">
              <section className="flex flex-col gap-2">
                <h3 className="text-sm font-semibold">{t("print.renders")}</h3>
                {(renders.data ?? []).length === 0 ? <p className="text-sm text-fg-muted">{t("print.noRenders")}</p> : (
                  <Table>
                    <TableHeader>
                      <TableRow>
                        <TableHead>{t("print.when")}</TableHead>
                        <TableHead>{t("print.variant")}</TableHead>
                        <TableHead>{t("print.version")}</TableHead>
                        <TableHead>{t("common.status")}</TableHead>
                        <TableHead>{t("print.evidence")}</TableHead>
                        <TableHead />
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {(renders.data ?? []).map((r) => (
                        <TableRow key={r.id} data-testid="print-render-row">
                          <TableCell dir="ltr">{formatDateTime(r.renderedAt ?? r.requestedAt)}</TableCell>
                          <TableCell>{localized(allVariants.find((v) => v.code === r.variant)?.name ?? { en: r.variant })}</TableCell>
                          <TableCell dir="ltr">{String(r.templateVersion)}</TableCell>
                          <TableCell><Badge tone={statusTone(r.status)}>{t(`print.statuses.${r.status}`, { defaultValue: r.status })}</Badge>{r.error ? <span className="ms-2 text-xs text-danger">{r.error}</span> : null}</TableCell>
                          <TableCell>{r.isEvidence ? t("common.yes") : t("common.no")}</TableCell>
                          <TableCell>{r.status === "ready" ? <Button variant="ghost" size="sm" onClick={() => { void download(r); }} disabled={busy !== null}><Download aria-hidden="true" />{t("common.download")}</Button> : null}</TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                )}
              </section>
              {can("print.document.share") ? (
                <section className="flex flex-col gap-2">
                  <h3 className="text-sm font-semibold">{t("print.shares")}</h3>
                  {(shares.data ?? []).length === 0 ? <p className="text-sm text-fg-muted">{t("print.noShares")}</p> : (
                    <Table>
                      <TableHeader>
                        <TableRow>
                          <TableHead>{t("print.when")}</TableHead>
                          <TableHead>{t("print.channel")}</TableHead>
                          <TableHead>{t("print.phone")}</TableHead>
                          <TableHead>{t("print.expires")}</TableHead>
                          <TableHead>{t("print.opens")}</TableHead>
                          <TableHead>{t("common.status")}</TableHead>
                          <TableHead />
                        </TableRow>
                      </TableHeader>
                      <TableBody>
                        {(shares.data ?? []).map((s) => (
                          <TableRow key={s.id} data-testid="print-share-row">
                            <TableCell dir="ltr">{formatDateTime(s.createdAt)}</TableCell>
                            <TableCell>{t(`print.channels.${s.channel}`, { defaultValue: s.channel })}</TableCell>
                            <TableCell dir="ltr">{s.recipient ?? "—"}</TableCell>
                            <TableCell dir="ltr">{formatDateTime(s.expiresAt)}</TableCell>
                            <TableCell dir="ltr" className="tabular" data-testid="share-opens">{String(s.openCount)}</TableCell>
                            <TableCell><Badge tone={s.isLive ? "success" : "neutral"}>{s.revokedAt ? t("print.revoked") : s.isLive ? t("print.live") : t("print.expired")}</Badge></TableCell>
                            <TableCell>{s.isLive ? <Button variant="ghost" size="sm" onClick={() => { revoke.mutate(s.id); }} loading={revoke.isPending} data-testid="revoke-share">{t("print.revoke")}</Button> : null}</TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  )}
                </section>
              ) : null}
              <p className="text-xs text-fg-muted">{t("print.historyHelp", { type: documentTypeLabel(t, subject.documentType) })}</p>
            </div>
          ) : null}
        </div>
        {printUrl ? <iframe ref={printFrame} title={t("print.print")} src={printUrl} onLoad={onPrintFrameLoaded} className="absolute h-0 w-0 border-0" aria-hidden="true" /> : null}
      </DialogContent>
    </Dialog>
  );
}
