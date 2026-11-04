/**
 * The messages the trader sends his customers by WhatsApp (ADR-0035 §25): a reminder of what is owed and a
 * confirmation of a payment received. Each is a template with placeholders in braces, kept per company and language
 * as a setting (the defaults below until the company writes its own), filled from the books and followed by the links
 * to the documents attached. Nothing here talks to the server, so it is tested on its own.
 */
export type MessageKind = "reminder" | "receipt";
export type MessageLanguage = "en" | "ar";

/** The setting that holds a company's own wording: `trader.whatsapp.<kind>.<language>`. */
export const templateSettingKey = (kind: MessageKind, language: MessageLanguage): string => `trader.whatsapp.${kind}.${language}`;

/** The placeholders each message understands, in the order the settings screen lists them. */
export const placeholders: Record<MessageKind, readonly string[]> = {
  reminder: ["name", "company", "amount", "overdue", "invoices", "date"],
  receipt: ["name", "company", "amount", "date", "number", "invoices", "balance"],
};

export const defaultTemplates: Record<MessageKind, Record<MessageLanguage, string>> = {
  reminder: {
    en: "Dear {name},\nA reminder from {company}: your balance with us is {amount}. {overdue}\n{invoices}\nThe details are at the link below. Thank you.",
    ar: "السيد/ة {name} المحترم/ة،\nتذكير من {company}: رصيدكم المستحق لدينا {amount}. {overdue}\n{invoices}\nتجدون التفاصيل في الرابط أدناه. مع الشكر.",
  },
  receipt: {
    en: "Dear {name},\n{company} confirms receiving {amount} from you on {date} (receipt {number}).\n{invoices}\nYour remaining balance is {balance}.\nThe receipt is at the link below. Thank you.",
    ar: "السيد/ة {name} المحترم/ة،\nتؤكد {company} استلام مبلغ {amount} منكم بتاريخ {date} (سند قبض {number}).\n{invoices}\nرصيدكم المتبقي: {balance}.\nتجدون السند في الرابط أدناه. مع الشكر.",
  },
};

/** The words the composed lines need in the message's own language (the UI may be in the other one). */
const words: Record<MessageLanguage, { overdue: string; open: string; due: string; paid: string; stillOpen: string; settled: string; nothing: string; and: string }> = {
  en: { overdue: "Of this, {amount} is past due.", open: "{number} of {date}: {remaining} open", due: "due {date}", paid: "{number}: {amount} paid", stillOpen: "{remaining} still open", settled: "settled", nothing: "nothing", and: " + " },
  ar: { overdue: "منها {amount} متأخرة عن موعدها.", open: "{number} بتاريخ {date}: المتبقي {remaining}", due: "تستحق {date}", paid: "{number}: سُدّد {amount}", stillOpen: "المتبقي {remaining}", settled: "سُدّدت بالكامل", nothing: "لا شيء", and: " + " },
};

/** Fills the braces; a placeholder with no value is left as written, so a typo in a template shows itself. */
export function fillTemplate(template: string, values: Record<string, string>): string {
  return template.replace(/\{([a-zA-Z]+)\}/gu, (whole, key: string) => (key in values ? values[key] ?? "" : whole)).replace(/[ \t]+\n/gu, "\n").replace(/\n{3,}/gu, "\n\n").trim();
}

export interface Formatters {
  money: (amount: number, currency: string) => string;
  date: (date: string) => string;
}

export interface OpenInvoice {
  number: string;
  date: string;
  dueDate: string;
  remaining: number;
  currency: string;
}

export interface ReminderFacts {
  name: string;
  company: string;
  today: string;
  invoices: readonly OpenInvoice[];
}

/** Amounts in one currency at a time: "IQD 300,000.00", or "IQD 300,000.00 + USD 200.00" when the customer owes in two. */
export function sumByCurrency(amounts: readonly { amount: number; currency: string }[], language: MessageLanguage, fmt: Formatters): string {
  const totals = new Map<string, number>();
  for (const a of amounts) {
    totals.set(a.currency, (totals.get(a.currency) ?? 0) + a.amount);
  }
  const parts = [...totals.entries()].filter(([, amount]) => amount !== 0).map(([currency, amount]) => fmt.money(amount, currency));
  return parts.length === 0 ? words[language].nothing : parts.join(words[language].and);
}

/** The values a reminder fills its template with: the total, the overdue sentence (empty when nothing is late) and one line per open invoice, oldest first. */
export function reminderValues(facts: ReminderFacts, language: MessageLanguage, fmt: Formatters): Record<string, string> {
  const w = words[language];
  const open = [...facts.invoices].filter((i) => i.remaining > 0).sort((a, b) => a.dueDate.localeCompare(b.dueDate) || a.date.localeCompare(b.date));
  const late = open.filter((i) => i.dueDate < facts.today);
  const lines = open.map((i) => {
    const line = fillTemplate(w.open, { number: i.number, date: fmt.date(i.date), remaining: fmt.money(i.remaining, i.currency) });
    return `• ${line}${i.dueDate < facts.today ? "" : ` (${fillTemplate(w.due, { date: fmt.date(i.dueDate) })})`}`;
  });
  return {
    name: facts.name,
    company: facts.company,
    amount: sumByCurrency(open.map((i) => ({ amount: i.remaining, currency: i.currency })), language, fmt),
    overdue: late.length === 0 ? "" : fillTemplate(w.overdue, { amount: sumByCurrency(late.map((i) => ({ amount: i.remaining, currency: i.currency })), language, fmt) }),
    invoices: lines.join("\n"),
    date: fmt.date(facts.today),
  };
}

export interface SettledInvoice {
  number: string;
  paid: number;
  remaining: number;
  currency: string;
}

export interface ReceiptFacts {
  name: string;
  company: string;
  number: string;
  date: string;
  amount: number;
  currency: string;
  settled: readonly SettledInvoice[];
  /** What the customer still owes after the payment, every open invoice counted. */
  balance: readonly { amount: number; currency: string }[];
}

/** The values a receipt confirmation fills its template with: what was received, the invoices it paid and what is left on each, the balance after. */
export function receiptValues(facts: ReceiptFacts, language: MessageLanguage, fmt: Formatters): Record<string, string> {
  const w = words[language];
  const lines = facts.settled.map((s) => {
    const paid = fillTemplate(w.paid, { number: s.number, amount: fmt.money(s.paid, s.currency) });
    return `• ${paid} (${s.remaining > 0 ? fillTemplate(w.stillOpen, { remaining: fmt.money(s.remaining, s.currency) }) : w.settled})`;
  });
  return {
    name: facts.name,
    company: facts.company,
    amount: fmt.money(facts.amount, facts.currency),
    date: fmt.date(facts.date),
    number: facts.number,
    invoices: lines.join("\n"),
    balance: sumByCurrency(facts.balance, language, fmt),
  };
}

/** The message with the documents' links under it, each on its own line with what it is. */
export function withLinks(text: string, links: readonly { label: string; url: string }[]): string {
  return [text.trim(), ...links.map((l) => `${l.label}: ${l.url}`)].join("\n");
}

/** Keeps the digits of a phone number the way WhatsApp links want them (country code first, no plus or leading zeros). */
export function phoneDigits(phone: string): string {
  const digits = phone.replace(/\D/gu, "");
  return digits.startsWith("00") ? digits.slice(2) : digits;
}

export function whatsappHref(phone: string, text: string): string {
  return `https://wa.me/${phoneDigits(phone)}?text=${encodeURIComponent(text)}`;
}
