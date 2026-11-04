import { Button, Field } from "@quicker/ui";
import { Copy, Download } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { encode } from "uqr";
import type { components } from "../../api/schema";
import { FormError, TextField } from "../common";

type TotpEnrollment = components["schemas"]["TotpEnrollResponse"];

/** A QR code drawn as SVG squares, dark on light whatever the theme, so every authenticator app can read it. */
export function QrCode({ value, label, size = 176 }: { value: string; label: string; size?: number }) {
  const { path, modules } = useMemo(() => {
    const qr = encode(value, { ecc: "M", border: 2 });
    return { path: qr.data.flatMap((row, y) => row.map((dark, x) => (dark ? `M${String(x)} ${String(y)}h1v1h-1z` : ""))).join(""), modules: qr.size };
  }, [value]);
  return (
    <svg role="img" aria-label={label} viewBox={`0 0 ${String(modules)} ${String(modules)}`} width={size} height={size} shapeRendering="crispEdges" className="rounded-md" data-testid="totp-qr">
      <rect width={modules} height={modules} fill="#ffffff" />
      <path d={path} fill="#000000" />
    </svg>
  );
}

/** The secret in groups of four, as authenticator apps expect it typed. */
export const groupedSecret = (secret: string): string => secret.replace(/(.{4})/g, "$1 ").trim();

/**
 * Adding an authenticator app: scan the code (or type the key), then enter the six digits it shows to prove it works.
 * Used on the account page and during a sign-in that the workspace requires two-step verification for.
 */
export function TotpSetup({ enrollment, busy, error, onConfirm, onCancel }: { enrollment: TotpEnrollment; busy: boolean; error: string | null; onConfirm: (code: string) => void; onCancel?: () => void }) {
  const { t } = useTranslation();
  const [code, setCode] = useState("");
  const submit = (event: FormEvent): void => { event.preventDefault(); onConfirm(code.trim()); };
  return (
    <form onSubmit={submit} className="flex flex-col gap-3" data-testid="totp-setup">
      <ol className="list-decimal ps-5 text-sm text-fg-muted">
        <li>{t("account.totpStep1")}</li>
        <li>{t("account.totpStep2")}</li>
      </ol>
      <div className="flex flex-wrap items-center gap-4">
        <QrCode value={enrollment.provisioningUri} label={t("account.totpQr")} />
        <div className="flex flex-col gap-1 text-sm">
          <span className="text-fg-muted">{t("account.totpKey")}</span>
          <code dir="ltr" className="rounded bg-surface-sunken px-2 py-1 font-mono text-sm" data-testid="totp-secret">{groupedSecret(enrollment.secret)}</code>
        </div>
      </div>
      <FormError message={error} />
      <Field label={t("account.totpCode")} required>
        <TextField inputMode="numeric" autoComplete="one-time-code" pattern="[0-9]{6}" maxLength={6} value={code} onChange={(e) => { setCode(e.target.value); }} required dir="ltr" className="w-40" data-testid="totp-code" />
      </Field>
      <div className="flex flex-wrap gap-2">
        {onCancel ? <Button type="button" variant="secondary" onClick={onCancel}>{t("common.cancel")}</Button> : null}
        <Button type="submit" loading={busy} data-testid="totp-confirm">{t("account.totpConfirm")}</Button>
      </div>
    </form>
  );
}

/** Recovery codes, shown once: each signs in a single time when the phone or key is not at hand. */
export function RecoveryCodes({ codes, onDone }: { codes: readonly string[]; onDone: () => void }) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState(false);
  const text = codes.join("\n");
  const copy = async (): Promise<void> => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  };
  const download = (): void => {
    const url = URL.createObjectURL(new Blob([`${t("account.recoveryFileTitle")}\n\n${text}\n`], { type: "text/plain;charset=utf-8" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = "quicker-recovery-codes.txt";
    link.click();
    URL.revokeObjectURL(url);
  };
  return (
    <div className="flex flex-col gap-3 rounded-md border border-warning/50 bg-surface p-4" data-testid="recovery-codes">
      <h3 className="text-sm font-semibold">{t("account.recoveryTitle")}</h3>
      <p className="text-sm text-fg-muted">{t("account.recoveryHint")}</p>
      <ul className="grid grid-cols-2 gap-2 sm:grid-cols-5" dir="ltr">
        {codes.map((code) => (
          <li key={code} className="rounded bg-surface-sunken px-2 py-1 text-center font-mono text-sm" data-testid="recovery-code">{code}</li>
        ))}
      </ul>
      <div className="flex flex-wrap gap-2">
        <Button type="button" variant="secondary" size="sm" onClick={() => { void copy(); }}>
          <Copy aria-hidden="true" />
          {copied ? t("account.copied") : t("account.copy")}
        </Button>
        <Button type="button" variant="secondary" size="sm" onClick={download}>
          <Download aria-hidden="true" />
          {t("account.download")}
        </Button>
        <Button type="button" size="sm" onClick={onDone} data-testid="recovery-done">{t("account.recoverySaved")}</Button>
      </div>
    </div>
  );
}
