import { Button, Field } from "@quicker/ui";
import { Link, useNavigate } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { currentLanguage } from "../i18n";
import { toFormProblem, type FormProblem } from "../lib/problem";
import { setSession } from "../session/session";
import { FormError, TextField } from "./common";
import { LanguageSwitch } from "./LanguageSwitch";

/** Creates a workspace (tenant) with its owner and signs them in. */
export function SignupPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [values, setValues] = useState({ tenantName: "", slug: "", ownerName: "", ownerEmail: "", password: "" });
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [busy, setBusy] = useState(false);

  const update = (key: keyof typeof values) => (event: React.ChangeEvent<HTMLInputElement>) => {
    const value = event.target.value;
    setValues((prev) => ({ ...prev, [key]: value, ...(key === "tenantName" && !prev.slug ? {} : {}) }));
  };

  const submit = async (event: FormEvent): Promise<void> => {
    event.preventDefault();
    setBusy(true);
    setProblem(null);
    try {
      const tokens = unwrap(await api.POST("/api/v1/auth/signup", { body: { ...values, language: currentLanguage() } }));
      setSession(tokens);
      void navigate({ to: "/" });
    } catch (error) {
      setProblem(toFormProblem(error, t("auth.failed")));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex min-h-dvh items-center justify-center bg-canvas p-4">
      <div className="w-full max-w-md rounded-lg border border-border bg-surface p-6 shadow-sm">
        <div className="mb-6 flex items-center justify-between">
          <h1 className="text-xl font-bold text-accent">Quicker</h1>
          <LanguageSwitch />
        </div>
        <form onSubmit={(event) => { void submit(event); }} className="flex flex-col gap-4" aria-labelledby="signup-title">
          <h2 id="signup-title" className="text-lg font-semibold">
            {t("auth.createWorkspace")}
          </h2>
          <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
          <Field label={t("auth.workspaceName")} required error={problem?.fields.tenantName}>
            <TextField name="tenantName" value={values.tenantName} onChange={update("tenantName")} required autoComplete="organization" />
          </Field>
          <Field label={t("auth.slug")} required description={t("auth.slugHint")} error={problem?.fields.slug}>
            <TextField name="slug" value={values.slug} onChange={update("slug")} required pattern="[a-z0-9-]{3,40}" dir="ltr" />
          </Field>
          <Field label={t("auth.yourName")} required error={problem?.fields.ownerName}>
            <TextField name="ownerName" value={values.ownerName} onChange={update("ownerName")} required autoComplete="name" />
          </Field>
          <Field label={t("auth.email")} required error={problem?.fields.ownerEmail ?? problem?.fields.email}>
            <TextField type="email" name="ownerEmail" value={values.ownerEmail} onChange={update("ownerEmail")} required autoComplete="email" dir="ltr" />
          </Field>
          <Field label={t("auth.password")} required description={t("auth.passwordHint")} error={problem?.fields.password}>
            <TextField type="password" name="password" value={values.password} onChange={update("password")} required autoComplete="new-password" minLength={12} dir="ltr" />
          </Field>
          <Button type="submit" loading={busy} className="w-full">
            {t("auth.createWorkspace")}
          </Button>
          <p className="text-center text-sm text-fg-muted">
            {t("auth.haveAccount")}{" "}
            <Link to="/login" className="text-accent underline-offset-4 hover:underline">
              {t("auth.signIn")}
            </Link>
          </p>
        </form>
      </div>
    </div>
  );
}
