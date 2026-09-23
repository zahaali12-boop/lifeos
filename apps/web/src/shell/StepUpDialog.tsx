import { Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, Field } from "@quicker/ui";
import { ShieldCheck } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { toFormProblem } from "../lib/problem";
import { TextField } from "../routes/common";
import { getSession, setSession, useSession } from "../session/session";
import { settleStepUp, useStepUpRequested } from "../session/stepUp";

/**
 * Asks the signed-in person to confirm who they are (password, or an authenticator code when they use MFA) before a
 * sensitive action. On success the session takes the new access token, whose authentication time is now, and the
 * waiting request is replayed by the API client.
 */
export function StepUpDialog() {
  const { t } = useTranslation();
  const open = useStepUpRequested();
  const session = useSession();
  const [useCode, setUseCode] = useState<boolean | null>(null);
  const [secret, setSecret] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const codeMode = useCode ?? session?.user.hasMfa ?? false;

  const close = (confirmed: boolean): void => {
    setSecret("");
    setError(null);
    setUseCode(null);
    settleStepUp(confirmed);
  };
  const submit = async (event: FormEvent): Promise<void> => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const tokens = unwrap(await api.POST("/api/v1/me/step-up", { body: codeMode ? { code: secret.trim() } : { password: secret } }));
      // Step-up renews the access token only; the refresh token the session already holds stays valid.
      setSession({ ...tokens, refreshToken: tokens.refreshToken || (getSession()?.refreshToken ?? "") });
      close(true);
    } catch (failure) {
      setError(toFormProblem(failure, t("stepUp.failed")).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={(isOpen) => { if (!isOpen) { close(false); } }}>
      <DialogContent closeLabel={t("common.close")} data-testid="step-up">
        <form onSubmit={(event) => { void submit(event); }} className="flex flex-col gap-4">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2 text-lg font-semibold">
              <ShieldCheck className="size-5 text-accent" aria-hidden="true" />
              {t("stepUp.title")}
            </DialogTitle>
            <DialogDescription>{t("stepUp.description")}</DialogDescription>
          </DialogHeader>
          <Field label={codeMode ? t("stepUp.code") : t("auth.password")} required error={error ?? undefined}>
            {codeMode ? (
              <TextField value={secret} onChange={(e) => { setSecret(e.target.value); }} inputMode="numeric" autoComplete="one-time-code" required autoFocus dir="ltr" data-testid="step-up-secret" />
            ) : (
              <TextField type="password" value={secret} onChange={(e) => { setSecret(e.target.value); }} autoComplete="current-password" required autoFocus dir="ltr" data-testid="step-up-secret" />
            )}
          </Field>
          <button type="button" className="self-start text-sm text-accent underline-offset-4 hover:underline" onClick={() => { setUseCode(!codeMode); setSecret(""); setError(null); }}>
            {codeMode ? t("stepUp.usePassword") : t("stepUp.useCode")}
          </button>
          <DialogFooter>
            <Button type="button" variant="secondary" onClick={() => { close(false); }}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" loading={busy} data-testid="step-up-confirm">
              {t("stepUp.confirm")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
