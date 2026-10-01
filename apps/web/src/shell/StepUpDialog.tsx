import { Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, Field } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { KeyRound, ShieldCheck } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, isApiProblem, unwrap } from "../api";
import { toFormProblem } from "../lib/problem";
import { keyErrorKind, signWithKey, webAuthnAvailable } from "../lib/webauthn";
import { FormError, TextField } from "../routes/common";
import { getSession, setSession, useSession } from "../session/session";
import { settleStepUp, useStepUpRequested } from "../session/stepUp";

/**
 * Asks the signed-in person to confirm who they are (password; an authenticator code or a security key when they use
 * two-step verification) before a sensitive action. On success the session takes the new access token, whose authentication time is now, and the
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
  const methods = useQuery({
    queryKey: ["my-mfa"],
    enabled: open && Boolean(session?.user.hasMfa),
    queryFn: async () => unwrap(await api.GET("/api/v1/me/mfa")),
  });
  const hasKey = (methods.data ?? []).some((m) => m.kind === "webauthn" && m.verifiedAt);
  const keyOnly = hasKey && !(methods.data ?? []).some((m) => m.kind === "totp" && m.verifiedAt);
  const codeMode = useCode ?? session?.user.hasMfa ?? false;

  const close = (confirmed: boolean): void => {
    setSecret("");
    setError(null);
    setUseCode(null);
    settleStepUp(confirmed);
  };
  const confirmed = (tokens: Parameters<typeof setSession>[0]): void => {
    // Step-up renews the access token only; the refresh token the session already holds stays valid.
    setSession({ ...tokens, refreshToken: tokens.refreshToken || (getSession()?.refreshToken ?? "") });
    close(true);
  };
  const confirmWithKey = async (): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      const started = unwrap(await api.POST("/api/v1/me/step-up/webauthn/options"));
      const response = await signWithKey(started.options as Record<string, unknown>);
      confirmed(unwrap(await api.POST("/api/v1/me/step-up", { body: { webAuthnOptionsId: started.optionsId, webAuthnResponse: response } })));
    } catch (failure) {
      setError(isApiProblem(failure) ? toFormProblem(failure, t("stepUp.failed")).message : t(`account.keyErrors.${keyErrorKind(failure)}`));
    } finally {
      setBusy(false);
    }
  };
  const submit = async (event: FormEvent): Promise<void> => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      confirmed(unwrap(await api.POST("/api/v1/me/step-up", { body: codeMode ? { code: secret.trim() } : { password: secret } })));
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
          {hasKey ? (
            <Button type="button" onClick={() => { void confirmWithKey(); }} loading={busy} disabled={!webAuthnAvailable()} data-testid="step-up-key">
              <KeyRound aria-hidden="true" />
              {t("auth.useKey")}
            </Button>
          ) : null}
          {keyOnly ? (
            <FormError message={error} />
          ) : (
            <>
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
            </>
          )}
          <DialogFooter>
            <Button type="button" variant="secondary" onClick={() => { close(false); }}>
              {t("common.cancel")}
            </Button>
            {keyOnly ? null : (
              <Button type="submit" loading={busy} data-testid="step-up-confirm">
                {t("stepUp.confirm")}
              </Button>
            )}
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
