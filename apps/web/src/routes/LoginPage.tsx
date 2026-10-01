import { Button, Field } from "@quicker/ui";
import { Link, useNavigate } from "@tanstack/react-router";
import { KeyRound } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, isApiProblem, unwrap } from "../api";
import type { components } from "../api/schema";
import { toFormProblem } from "../lib/problem";
import { keyErrorKind, signWithKey, webAuthnAvailable } from "../lib/webauthn";
import { setSession } from "../session/session";
import { RecoveryCodes, TotpSetup } from "./account/TwoStep";
import { FormError, TextField } from "./common";
import { LanguageSwitch } from "./LanguageSwitch";

type LoginResponse = components["schemas"]["LoginResponse"];
type TotpEnrollment = components["schemas"]["TotpEnrollResponse"];

type Step =
  | { kind: "credentials" }
  | { kind: "select_tenant"; challengeToken: string; tenants: { slug: string; name: string }[] }
  | { kind: "mfa"; challengeToken: string; methods: string[]; afterEnrolment: boolean }
  | { kind: "enroll"; challengeToken: string; enrollment: TotpEnrollment | null }
  | { kind: "recovery_codes"; codes: string[]; next: LoginResponse };

/** Six digits are an authenticator code; anything else is taken as a recovery code (xxxx-xxxx). */
const isTotpCode = (value: string): boolean => /^\d{6}$/.test(value.trim());

/**
 * Sign-in with the steps the API may ask for: choosing a workspace, two-step verification with an authenticator code,
 * a security key or a recovery code, and setting up an authenticator app when the workspace requires two-step
 * verification and the person has none yet.
 */
export function LoginPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [step, setStep] = useState<Step>({ kind: "credentials" });
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [tenantSlug, setTenantSlug] = useState<string | null>(null);
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const finish = (tokens: Parameters<typeof setSession>[0]): void => {
    setSession(tokens);
    void navigate({ to: "/" });
  };

  const startEnrolment = async (challengeToken: string): Promise<void> => {
    setStep({ kind: "enroll", challengeToken, enrollment: null });
    const enrollment = unwrap(await api.POST("/api/v1/auth/mfa/enroll/totp", { body: { challengeToken } }));
    setStep({ kind: "enroll", challengeToken, enrollment });
  };

  /** Moves to whatever the API asked for next. */
  const proceed = async (result: LoginResponse, afterEnrolment = false): Promise<void> => {
    setCode("");
    if (result.status === "ok" && result.tokens) {
      finish(result.tokens);
    } else if (result.status === "select_tenant" && result.challengeToken) {
      setStep({ kind: "select_tenant", challengeToken: result.challengeToken, tenants: (result.tenants ?? []).map((tenant) => ({ slug: tenant.slug, name: tenant.name })) });
    } else if (result.status === "mfa_required" && result.challengeToken) {
      setStep({ kind: "mfa", challengeToken: result.challengeToken, methods: result.mfaMethods ?? [], afterEnrolment });
    } else if (result.status === "mfa_enrollment_required" && result.challengeToken) {
      await startEnrolment(result.challengeToken);
    } else {
      setError(t("auth.unexpected"));
    }
  };

  const run = async (action: () => Promise<void>): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      await action();
    } catch (problem) {
      setError(isApiProblem(problem) ? toFormProblem(problem, t("auth.failed")).message : t(`account.keyErrors.${keyErrorKind(problem)}`));
    } finally {
      setBusy(false);
    }
  };

  const submitCredentials = (event: FormEvent): void => {
    event.preventDefault();
    void run(async () => { await proceed(unwrap(await api.POST("/api/v1/auth/login", { body: { email, password, tenantSlug: null } }))); });
  };

  const selectTenant = (slug: string): void => {
    if (step.kind !== "select_tenant") {
      return;
    }
    setTenantSlug(slug);
    void run(async () => { await proceed(unwrap(await api.POST("/api/v1/auth/select-tenant", { body: { challengeToken: step.challengeToken, tenantSlug: slug } }))); });
  };

  const submitMfa = (event: FormEvent): void => {
    event.preventDefault();
    if (step.kind !== "mfa") {
      return;
    }
    const proof = isTotpCode(code) ? { code: code.trim() } : { recoveryCode: code.trim() };
    void run(async () => { await proceed(unwrap(await api.POST("/api/v1/auth/mfa/verify", { body: { challengeToken: step.challengeToken, ...proof } }))); });
  };

  const signInWithKey = (): void => {
    if (step.kind !== "mfa") {
      return;
    }
    void run(async () => {
      const started = unwrap(await api.POST("/api/v1/auth/mfa/webauthn/options", { body: { challengeToken: step.challengeToken } }));
      const response = await signWithKey(started.options as Record<string, unknown>);
      await proceed(unwrap(await api.POST("/api/v1/auth/mfa/verify", { body: { challengeToken: step.challengeToken, webAuthnOptionsId: started.optionsId, webAuthnResponse: response } })));
    });
  };

  const confirmEnrolment = (totpCode: string): void => {
    if (step.kind !== "enroll" || !step.enrollment) {
      return;
    }
    const { challengeToken, enrollment } = step;
    void run(async () => {
      const result = unwrap(await api.POST("/api/v1/auth/mfa/enroll/totp/confirm", { body: { challengeToken, methodId: enrollment.methodId, code: totpCode, email, password, tenantSlug } }));
      setStep({ kind: "recovery_codes", codes: [...result.recoveryCodes], next: result.login });
    });
  };

  return (
    <div className="flex min-h-dvh items-center justify-center bg-canvas p-4">
      <div className={`w-full rounded-lg border border-border bg-surface p-6 shadow-sm ${step.kind === "enroll" || step.kind === "recovery_codes" ? "max-w-xl" : "max-w-sm"}`}>
        <div className="mb-6 flex items-center justify-between">
          <h1 className="text-xl font-bold text-accent">Quicker</h1>
          <LanguageSwitch />
        </div>
        {step.kind === "credentials" ? (
          <form onSubmit={submitCredentials} className="flex flex-col gap-4" aria-labelledby="login-title">
            <h2 id="login-title" className="text-lg font-semibold">
              {t("auth.signIn")}
            </h2>
            <FormError message={error} />
            <Field label={t("auth.email")} required>
              <TextField type="email" name="email" autoComplete="email" value={email} onChange={(e) => { setEmail(e.target.value); }} required dir="ltr" />
            </Field>
            <Field label={t("auth.password")} required>
              <TextField type="password" name="password" autoComplete="current-password" value={password} onChange={(e) => { setPassword(e.target.value); }} required dir="ltr" />
            </Field>
            <Button type="submit" loading={busy} className="w-full">
              {t("auth.signIn")}
            </Button>
            <p className="text-center text-sm text-fg-muted">
              {t("auth.noWorkspace")}{" "}
              <Link to="/signup" className="text-accent underline-offset-4 hover:underline">
                {t("auth.createWorkspace")}
              </Link>
            </p>
          </form>
        ) : step.kind === "select_tenant" ? (
          <div className="flex flex-col gap-3">
            <h2 className="text-lg font-semibold">{t("auth.chooseWorkspace")}</h2>
            <FormError message={error} />
            {step.tenants.map((tenant) => (
              <Button key={tenant.slug} variant="secondary" className="justify-start" loading={busy} onClick={() => { selectTenant(tenant.slug); }}>
                {tenant.name}
              </Button>
            ))}
          </div>
        ) : step.kind === "mfa" ? (
          <form onSubmit={submitMfa} className="flex flex-col gap-4" data-testid="mfa-step">
            <h2 className="text-lg font-semibold">{t("auth.mfaTitle")}</h2>
            {step.afterEnrolment ? <p className="text-sm text-fg-muted" data-testid="mfa-next-code">{t("auth.nextCode")}</p> : null}
            <FormError message={error} />
            {step.methods.includes("webauthn") ? (
              <>
                <Button type="button" onClick={signInWithKey} loading={busy} disabled={!webAuthnAvailable()} className="w-full" data-testid="mfa-use-key">
                  <KeyRound aria-hidden="true" />
                  {t("auth.useKey")}
                </Button>
                <p className="text-center text-xs text-fg-muted">{step.methods.includes("totp") ? t("auth.orCode") : t("auth.orRecoveryCode")}</p>
              </>
            ) : null}
            <Field label={t("auth.mfaCode")} required description={step.methods.includes("totp") ? t("auth.mfaHint") : t("auth.recoveryHint")}>
              <TextField autoComplete="one-time-code" value={code} onChange={(e) => { setCode(e.target.value); }} required dir="ltr" data-testid="mfa-code" />
            </Field>
            <Button type="submit" variant={step.methods.includes("webauthn") ? "secondary" : "primary"} loading={busy} className="w-full" data-testid="mfa-verify">
              {t("auth.verify")}
            </Button>
          </form>
        ) : step.kind === "enroll" ? (
          <div className="flex flex-col gap-4" data-testid="mfa-enroll">
            <h2 className="text-lg font-semibold">{t("auth.enrollTitle")}</h2>
            <p className="text-sm text-fg-muted">{t("auth.enrollHint")}</p>
            {step.enrollment ? <TotpSetup enrollment={step.enrollment} busy={busy} error={error} onConfirm={confirmEnrolment} /> : <FormError message={error} />}
          </div>
        ) : (
          <div className="flex flex-col gap-4">
            <h2 className="text-lg font-semibold">{t("auth.enrolledTitle")}</h2>
            <RecoveryCodes codes={step.codes} onDone={() => { const next = step.next; void run(async () => { await proceed(next, true); }); }} />
          </div>
        )}
      </div>
    </div>
  );
}
