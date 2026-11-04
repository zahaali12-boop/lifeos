import { Button, Checkbox, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Spinner } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { MessageCircle } from "lucide-react";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatDate, formatMoney, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, SelectField, TextareaField, TextField } from "../common";
import { useCompanies } from "../accounting/shared";
import { shareRender, type RenderRequest } from "../print/shared";
import { n, printVariants } from "./invoices";
import { defaultTemplates, fillTemplate, phoneDigits, receiptValues, reminderValues, templateSettingKey, whatsappHref, withLinks, type MessageKind, type MessageLanguage, type OpenInvoice } from "./whatsappMessages";

/** What the message is about: a customer's dues (with one invoice to attach when opened from it), or a payment just received. */
export type WhatsAppSubject =
  | { kind: "reminder"; partnerId: string; invoiceId?: string; invoiceNumber?: string }
  | { kind: "receipt"; receiptId: string };

type Partner = components["schemas"]["PartnerSummary"];

interface Attachment {
  id: string;
  label: string;
  request: RenderRequest;
}

const nameIn = (map: Record<string, string> | null | undefined, language: MessageLanguage): string => map?.[language] ?? localized(map);

/**
 * A WhatsApp message to a customer, written for him (ADR-0035 §25): a reminder of what he owes (every open invoice,
 * the oldest first, the part past due named) or the confirmation of a payment just received (the receipt, the invoices
 * it paid and what is left, the balance after), in his language from the company's own wording. The documents chosen
 * (statement, invoice, receipt) are attached as links that open without signing in; the number typed is kept on the
 * customer; the send is logged as a note on him; then WhatsApp opens with the message ready.
 */
export function WhatsAppDialog({ subject, companyId, onClose }: { subject: WhatsAppSubject; companyId: string; onClose: () => void }) {
  const { t } = useTranslation();
  const can = useCan();
  const queryClient = useQueryClient();
  const companies = useCompanies();
  const company = companies.data?.find((c) => c.id === companyId);
  const today = new Date().toISOString().slice(0, 10);
  const kind: MessageKind = subject.kind;

  // Everything the message is written from, read afresh each time the dialog opens (a payment recorded a moment ago
  // must already count): the receipt when there is one, the customer, his open invoices, and what the receipt paid.
  const [openedAt] = useState(() => Date.now());
  const facts = useQuery({
    queryKey: ["whatsapp-facts", companyId, subject, openedAt],
    gcTime: 0,
    queryFn: async () => {
      const receipt = subject.kind === "receipt" ? unwrap(await api.GET("/api/v1/banking/receipts/{receiptId}", { params: { path: { receiptId: subject.receiptId } } })) : null;
      const partnerId = subject.kind === "reminder" ? subject.partnerId : (receipt?.partnerId ?? "");
      const [detail, open] = await Promise.all([
        api.GET("/api/v1/partners/{partnerId}", { params: { path: { partnerId } } }).then(unwrap),
        api.GET("/api/v1/receivables/open-items", { params: { query: { companyId, partnerId, status: "live" } } }).then(unwrap),
      ]);
      const settled = await Promise.all((receipt?.lines ?? []).map(async (line) => {
        const item = unwrap(await api.GET("/api/v1/receivables/open-items/{itemId}", { params: { path: { itemId: line.openItemId } } })).item;
        return { number: item.documentNumber, documentId: item.documentId, paid: n(line.amountTc), remaining: n(item.remainingTc), currency: item.currency };
      }));
      return { receipt, partner: detail.partner, open, settled };
    },
  });
  const receipt = facts.data?.receipt ?? null;
  const partnerId = subject.kind === "reminder" ? subject.partnerId : (receipt?.partnerId ?? "");
  const settings = useQuery({
    queryKey: ["settings", companyId],
    queryFn: async () => unwrap(await api.GET("/api/v1/organization/companies/{companyId}/settings", { params: { path: { companyId } } })),
  });

  const [language, setLanguage] = useState<MessageLanguage | null>(null);
  const [phone, setPhone] = useState<string | null>(null);
  const [savePhone, setSavePhone] = useState(true);
  const [days, setDays] = useState("7");
  const [edited, setEdited] = useState<string | null>(null);
  const [chosen, setChosen] = useState<Set<string> | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [sent, setSent] = useState<{ href: string; links: { label: string; url: string }[] } | null>(null);

  const p: Partner | undefined = facts.data?.partner;
  const lang: MessageLanguage = language ?? (p?.defaultLanguage === "ar" ? "ar" : "en");
  const number = phone ?? p?.phone ?? "";
  const customerName = nameIn(p?.tradeName, lang) || nameIn(p?.legalName, lang);
  const companyName = nameIn(company?.tradeName, lang) || nameIn(company?.legalName, lang);

  const invoices: (OpenInvoice & { documentId: string })[] = (facts.data?.open ?? [])
    .filter((r) => r.item.kind === "invoice" && n(r.item.remainingTc) > 0)
    .map((r) => ({ number: r.item.documentNumber, documentId: r.item.documentId, date: r.item.documentDate, dueDate: r.item.dueDate, remaining: n(r.item.remainingTc), currency: r.item.currency }));
  const loading = facts.isPending;

  const fmt = useMemo(() => ({ money: (amount: number, currency: string) => formatMoney(amount, currency), date: (date: string) => formatDate(date) }), []);
  const template = (() => {
    const stored = settings.data?.find((s) => s.key === templateSettingKey(kind, lang))?.value;
    return typeof stored === "string" && stored.trim() !== "" ? stored : defaultTemplates[kind][lang];
  })();
  const composed = useMemo(() => {
    if (loading || !p) {
      return "";
    }
    if (subject.kind === "reminder") {
      return fillTemplate(template, reminderValues({ name: customerName, company: companyName, today, invoices }, lang, fmt));
    }
    if (!receipt) {
      return "";
    }
    return fillTemplate(template, receiptValues({
      name: customerName,
      company: companyName,
      number: receipt.number,
      date: receipt.paymentDate,
      amount: n(receipt.amountTc),
      currency: receipt.currency,
      settled: facts.data?.settled ?? [],
      balance: invoices.map((i) => ({ amount: i.remaining, currency: i.currency })),
    }, lang, fmt));
    // The invoices change identity on every fetch; what matters is the data behind them.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [loading, p, subject.kind, template, customerName, companyName, today, facts.data, receipt, lang, fmt]);
  const text = edited ?? composed;

  // The documents that can go with the message, and the ones ticked unless the sender changes them.
  const variant = printVariants[0] ?? "standard";
  const attachments: Attachment[] = [];
  if (subject.kind === "receipt" && receipt) {
    attachments.push({ id: "receipt", label: t("trader.whatsapp.attach.receipt", { number: receipt.number }), request: { documentType: "bank_receipt", documentId: receipt.id, variant } });
    for (const s of facts.data?.settled ?? []) {
      attachments.push({ id: `invoice-${s.documentId}`, label: t("trader.whatsapp.attach.invoice", { number: s.number }), request: { documentType: "sales_invoice", documentId: s.documentId, variant } });
    }
  }
  if (subject.kind === "reminder" && subject.invoiceId) {
    attachments.push({ id: `invoice-${subject.invoiceId}`, label: t("trader.whatsapp.attach.invoice", { number: subject.invoiceNumber ?? "" }), request: { documentType: "sales_invoice", documentId: subject.invoiceId, variant } });
  }
  if (partnerId) {
    attachments.push({ id: "statement", label: t("trader.whatsapp.attach.statement"), request: { documentType: "customer_statement", documentId: partnerId, parameters: { companyId }, variant } });
  }
  const defaultChosen = subject.kind === "receipt" ? ["receipt"] : subject.invoiceId ? [`invoice-${subject.invoiceId}`] : ["statement"];
  const ticked = chosen ?? new Set(defaultChosen);
  const toggle = (id: string, on: boolean): void => {
    const next = new Set(ticked);
    if (on) {
      next.add(id);
    } else {
      next.delete(id);
    }
    setChosen(next);
  };

  useEffect(() => { setEdited(null); }, [lang]);

  const send = useMutation({
    mutationFn: async () => {
      if (!p) {
        throw new Error("no customer");
      }
      const links: { label: string; url: string }[] = [];
      for (const a of attachments.filter((x) => ticked.has(x.id))) {
        const share = await shareRender(a.request, "whatsapp", number, Number(days) || 7, t("print.renderFailed"));
        links.push({ label: a.label, url: share.url ?? "" });
      }
      const full = withLinks(text, links);
      if (savePhone && number.trim() !== (p.phone ?? "")) {
        unwrap(await api.PUT("/api/v1/partners/{partnerId}", { params: { path: { partnerId: p.id } }, body: { code: p.code, legalName: p.legalName, tradeName: p.tradeName, kind: p.kind, isSupplier: p.isSupplier, isCustomer: p.isCustomer, isEmployee: p.isEmployee, intercompanyCompanyId: p.intercompanyCompanyId, defaultLanguage: p.defaultLanguage, website: p.website, email: p.email, phone: number.trim(), parentPartnerId: p.parentPartnerId, notes: p.notes, customFields: p.customFields, isActive: p.isActive } }));
      }
      if (can("partners.customer.manage")) {
        unwrap(await api.POST("/api/v1/partners/crm-activities", { body: { partnerId: p.id, kind: "note", subject: t(`trader.whatsapp.note.${kind}`, { phone: number.trim() }), body: full, companyId } }));
      }
      return { href: whatsappHref(number, full), links };
    },
    onSuccess: async (made) => {
      setProblem(null);
      setSent(made);
      window.open(made.href, "_blank", "noopener");
      await Promise.all([["print-shares"], ["partner", partnerId], ["activities"], ["customers"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
    },
    onError: (error: unknown) => { setProblem(toFormProblem(error, t("print.failed"))); },
  });
  const submit = (event: FormEvent): void => {
    event.preventDefault();
    send.mutate();
  };
  const ready = !loading && Boolean(p) && phoneDigits(number).length >= 8 && text.trim() !== "";

  return (
    <Dialog open onOpenChange={(isOpen) => { if (!isOpen) { onClose(); } }}>
      <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
        <form onSubmit={submit} className="flex flex-col gap-4" data-testid="whatsapp-dialog">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold">{t(`trader.whatsapp.title.${kind}`)}{customerName ? <> · <span dir="auto">{customerName}</span></> : null}</DialogTitle>
          </DialogHeader>
          <FormError message={problem?.message ?? null} />
          {loading ? <p className="flex items-center gap-2 text-sm text-fg-muted"><Spinner label="" />{t("common.loading")}</p> : null}
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label={t("print.phone")} description={t("print.phoneHelp")} required>
              <TextField type="tel" value={number} onChange={(e) => { setPhone(e.target.value); }} dir="ltr" required data-testid="whatsapp-to" />
            </Field>
            <Field label={t("trader.whatsapp.language")}>
              <SelectField value={lang} onChange={(e) => { setLanguage(e.target.value === "ar" ? "ar" : "en"); }} data-testid="whatsapp-language">
                <option value="ar">العربية</option>
                <option value="en">English</option>
              </SelectField>
            </Field>
          </div>
          {p && number.trim() !== (p.phone ?? "") && number.trim() !== "" ? (
            <label className="flex items-center gap-2 text-sm">
              <Checkbox checked={savePhone} onCheckedChange={(checked) => { setSavePhone(checked === true); }} data-testid="whatsapp-save-phone" />
              {t("trader.whatsapp.savePhone")}
            </label>
          ) : null}
          <fieldset className="flex flex-col gap-2 text-sm" data-testid="whatsapp-attachments">
            <legend className="mb-1 font-medium">{t("trader.whatsapp.attach.title")}</legend>
            {attachments.map((a) => (
              <label key={a.id} className="flex items-center gap-2">
                <Checkbox checked={ticked.has(a.id)} onCheckedChange={(checked) => { toggle(a.id, checked === true); }} data-testid={`whatsapp-attach-${a.id}`} />
                <span dir="auto">{a.label}</span>
              </label>
            ))}
            <span className="text-xs text-fg-muted">{t("print.whatsappHelp", { days })}</span>
          </fieldset>
          <Field label={t("trader.whatsapp.message")} description={edited === null ? t("trader.whatsapp.messageHint") : undefined}>
            <TextareaField value={text} onChange={(e) => { setEdited(e.target.value); }} rows={9} dir="auto" data-testid="whatsapp-text" />
          </Field>
          <div className="flex flex-wrap items-end gap-3">
            {edited !== null ? <Button type="button" variant="link" size="sm" onClick={() => { setEdited(null); }} data-testid="whatsapp-reset-text">{t("trader.whatsapp.resetText")}</Button> : null}
            <Field label={t("trader.whatsapp.linkDays")} className="w-32">
              <TextField inputMode="numeric" value={days} onChange={(e) => { setDays(e.target.value); }} dir="ltr" data-testid="whatsapp-days" />
            </Field>
          </div>
          {sent ? (
            <div className="flex flex-col gap-2 rounded-md border border-success/40 bg-success-soft p-3 text-sm" data-testid="whatsapp-sent">
              <p>{t("print.whatsappReady")}</p>
              <div className="flex flex-wrap items-center gap-2">
                <Button asChild variant="secondary" size="sm"><a href={sent.href} target="_blank" rel="noopener noreferrer" data-testid="whatsapp-open-again"><MessageCircle aria-hidden="true" />{t("trader.whatsapp.openAgain")}</a></Button>
              </div>
              <ul className="flex flex-col gap-1">
                {sent.links.map((l) => <li key={l.url} className="flex flex-wrap gap-1"><span dir="auto">{l.label}:</span> <a href={l.url} target="_blank" rel="noopener noreferrer" className="break-all text-accent underline" dir="ltr">{l.url}</a></li>)}
              </ul>
            </div>
          ) : null}
          <DialogFooter>
            <Button type="button" variant="secondary" onClick={onClose}>{sent ? t("common.close") : t("common.cancel")}</Button>
            <Button type="submit" loading={send.isPending} disabled={!ready || sent !== null} data-testid="whatsapp-send"><MessageCircle aria-hidden="true" />{t("trader.whatsapp.send")}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
