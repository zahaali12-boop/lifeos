import { Button, Field } from "@quicker/ui";
import { Link, useNavigate } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { toFormProblem } from "../lib/problem";
import { setSession } from "../session/session";
import { FormError, TextField } from "./common";
import { LanguageSwitch } from "./LanguageSwitch";

type Step = { kind: "credentials" } | { kind: "select_tenant"; challengeToken: string; tenants: { slug: string; name: string }[] } | { kind: "mfa"; challengeToken: string };

/** Sign-in with the tenant selection and MFA steps the API may ask for. */
export function LoginPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [step, setStep] = useState<Step>({ kind: "credentials" });
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const finish = (tokens: Parameters<typeof setSession>[0]): void => {
    setSession(tokens);
    void navigate({ to: "/" });
  };

  const submitCredentials = async (event: FormEvent): Promise<void> => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const result = unwrap(await api.POST("/api/v1/auth/login", { body: { email, password, tenantSlug: null } }));
      if (result.status === "ok" && result.tokens) {
        finish(result.tokens);
      } else if (result.status === "select_tenant" && result.challengeToken) {
        setStep({ kind: "select_tenant", challengeToken: result.challengeToken, tenants: (result.tenants ?? []).map((tenant) => ({ slug: tenant.slug, name: tenant.name })) });
      } else if (result.status === "mfa_required" && result.challengeToken) {
        setStep({ kind: "mfa", challengeToken: result.challengeToken });
      } else {
        setError(t("auth.unexpected"));
      }
    } catch (problem) {
      setError(toFormProblem(problem, t("auth.failed")).message);
    } finally {
      setBusy(false);
    }
  };

  const selectTenant = async (slug: string): Promise<void> => {
    if (step.kind !== "select_tenant") {
      return;
    }
    setBusy(true);
    try {
      const result = unwrap(await api.POST("/api/v1/auth/select-tenant", { body: { challengeToken: step.challengeToken, tenantSlug: slug } }));
      if (result.status === "ok" && result.tokens) {
        finish(result.tokens);
      } else if (result.status === "mfa_required" && result.challengeToken) {
        setStep({ kind: "mfa", challengeToken: result.challengeToken });
      }
    } catch (problem) {
      setError(toFormProblem(problem, t("auth.failed")).message);
    } finally {
      setBusy(false);
    }
  };

  const submitMfa = async (event: FormEvent): Promise<void> => {
    event.preventDefault();
    if (step.kind !== "mfa") {
      return;
    }
    setBusy(true);
    try {
      const result = unwrap(await api.POST("/api/v1/auth/mfa/verify", { body: { challengeToken: step.challengeToken, code } }));
      if (result.status === "ok" && result.tokens) {
        finish(result.tokens);
      } else {
        setError(t("auth.failed"));
      }
    } catch (problem) {
      setError(toFormProblem(problem, t("auth.failed")).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex min-h-dvh items-center justify-center bg-canvas p-4">
      <div className="w-full max-w-sm rounded-lg border border-border bg-surface p-6 shadow-sm">
        <div className="mb-6 flex items-center justify-between">
          <h1 className="text-xl font-bold text-accent">Quicker</h1>
          <LanguageSwitch />
        </div>
        {step.kind === "credentials" ? (
          <form onSubmit={(event) => { void submitCredentials(event); }} className="flex flex-col gap-4" aria-labelledby="login-title">
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
              <Button key={tenant.slug} variant="secondary" className="justify-start" loading={busy} onClick={() => { void selectTenant(tenant.slug); }}>
                {tenant.name}
              </Button>
            ))}
          </div>
        ) : (
          <form onSubmit={(event) => { void submitMfa(event); }} className="flex flex-col gap-4">
            <h2 className="text-lg font-semibold">{t("auth.mfaTitle")}</h2>
            <FormError message={error} />
            <Field label={t("auth.mfaCode")} required description={t("auth.mfaHint")}>
              <TextField inputMode="numeric" autoComplete="one-time-code" value={code} onChange={(e) => { setCode(e.target.value); }} required dir="ltr" />
            </Field>
            <Button type="submit" loading={busy} className="w-full">
              {t("auth.verify")}
            </Button>
          </form>
        )}
      </div>
    </div>
  );
}
