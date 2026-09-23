import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { Button, Field } from "@quicker/ui";
import { Link, useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { toFormProblem } from "../lib/problem";
import { setSession } from "../session/session";
import { FormError, TextField } from "./common";
import { LanguageSwitch } from "./LanguageSwitch";
/** Sign-in with the tenant selection and MFA steps the API may ask for. */
export function LoginPage() {
    const { t } = useTranslation();
    const navigate = useNavigate();
    const [step, setStep] = useState({ kind: "credentials" });
    const [email, setEmail] = useState("");
    const [password, setPassword] = useState("");
    const [code, setCode] = useState("");
    const [error, setError] = useState(null);
    const [busy, setBusy] = useState(false);
    const finish = (tokens) => {
        setSession(tokens);
        void navigate({ to: "/" });
    };
    const submitCredentials = async (event) => {
        event.preventDefault();
        setBusy(true);
        setError(null);
        try {
            const result = unwrap(await api.POST("/api/v1/auth/login", { body: { email, password, tenantSlug: null } }));
            if (result.status === "ok" && result.tokens) {
                finish(result.tokens);
            }
            else if (result.status === "select_tenant" && result.challengeToken) {
                setStep({ kind: "select_tenant", challengeToken: result.challengeToken, tenants: (result.tenants ?? []).map((tenant) => ({ slug: tenant.slug, name: tenant.name })) });
            }
            else if (result.status === "mfa_required" && result.challengeToken) {
                setStep({ kind: "mfa", challengeToken: result.challengeToken });
            }
            else {
                setError(t("auth.unexpected"));
            }
        }
        catch (problem) {
            setError(toFormProblem(problem, t("auth.failed")).message);
        }
        finally {
            setBusy(false);
        }
    };
    const selectTenant = async (slug) => {
        if (step.kind !== "select_tenant") {
            return;
        }
        setBusy(true);
        try {
            const result = unwrap(await api.POST("/api/v1/auth/select-tenant", { body: { challengeToken: step.challengeToken, tenantSlug: slug } }));
            if (result.status === "ok" && result.tokens) {
                finish(result.tokens);
            }
            else if (result.status === "mfa_required" && result.challengeToken) {
                setStep({ kind: "mfa", challengeToken: result.challengeToken });
            }
        }
        catch (problem) {
            setError(toFormProblem(problem, t("auth.failed")).message);
        }
        finally {
            setBusy(false);
        }
    };
    const submitMfa = async (event) => {
        event.preventDefault();
        if (step.kind !== "mfa") {
            return;
        }
        setBusy(true);
        try {
            const result = unwrap(await api.POST("/api/v1/auth/mfa/verify", { body: { challengeToken: step.challengeToken, code } }));
            if (result.status === "ok" && result.tokens) {
                finish(result.tokens);
            }
            else {
                setError(t("auth.failed"));
            }
        }
        catch (problem) {
            setError(toFormProblem(problem, t("auth.failed")).message);
        }
        finally {
            setBusy(false);
        }
    };
    return (_jsx("div", { className: "flex min-h-dvh items-center justify-center bg-canvas p-4", children: _jsxs("div", { className: "w-full max-w-sm rounded-lg border border-border bg-surface p-6 shadow-sm", children: [_jsxs("div", { className: "mb-6 flex items-center justify-between", children: [_jsx("h1", { className: "text-xl font-bold text-accent", children: "Quicker" }), _jsx(LanguageSwitch, {})] }), step.kind === "credentials" ? (_jsxs("form", { onSubmit: (event) => { void submitCredentials(event); }, className: "flex flex-col gap-4", "aria-labelledby": "login-title", children: [_jsx("h2", { id: "login-title", className: "text-lg font-semibold", children: t("auth.signIn") }), _jsx(FormError, { message: error }), _jsx(Field, { label: t("auth.email"), required: true, children: _jsx(TextField, { type: "email", name: "email", autoComplete: "email", value: email, onChange: (e) => { setEmail(e.target.value); }, required: true, dir: "ltr" }) }), _jsx(Field, { label: t("auth.password"), required: true, children: _jsx(TextField, { type: "password", name: "password", autoComplete: "current-password", value: password, onChange: (e) => { setPassword(e.target.value); }, required: true, dir: "ltr" }) }), _jsx(Button, { type: "submit", loading: busy, className: "w-full", children: t("auth.signIn") }), _jsxs("p", { className: "text-center text-sm text-fg-muted", children: [t("auth.noWorkspace"), " ", _jsx(Link, { to: "/signup", className: "text-accent underline-offset-4 hover:underline", children: t("auth.createWorkspace") })] })] })) : step.kind === "select_tenant" ? (_jsxs("div", { className: "flex flex-col gap-3", children: [_jsx("h2", { className: "text-lg font-semibold", children: t("auth.chooseWorkspace") }), _jsx(FormError, { message: error }), step.tenants.map((tenant) => (_jsx(Button, { variant: "secondary", className: "justify-start", loading: busy, onClick: () => { void selectTenant(tenant.slug); }, children: tenant.name }, tenant.slug)))] })) : (_jsxs("form", { onSubmit: (event) => { void submitMfa(event); }, className: "flex flex-col gap-4", children: [_jsx("h2", { className: "text-lg font-semibold", children: t("auth.mfaTitle") }), _jsx(FormError, { message: error }), _jsx(Field, { label: t("auth.mfaCode"), required: true, description: t("auth.mfaHint"), children: _jsx(TextField, { inputMode: "numeric", autoComplete: "one-time-code", value: code, onChange: (e) => { setCode(e.target.value); }, required: true, dir: "ltr" }) }), _jsx(Button, { type: "submit", loading: busy, className: "w-full", children: t("auth.verify") })] }))] }) }));
}
