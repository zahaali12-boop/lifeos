import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { Button, Field } from "@quicker/ui";
import { Link, useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { currentLanguage } from "../i18n";
import { toFormProblem } from "../lib/problem";
import { setSession } from "../session/session";
import { FormError, TextField } from "./common";
import { LanguageSwitch } from "./LanguageSwitch";
/** Creates a workspace (tenant) with its owner and signs them in. */
export function SignupPage() {
    const { t } = useTranslation();
    const navigate = useNavigate();
    const [values, setValues] = useState({ tenantName: "", slug: "", ownerName: "", ownerEmail: "", password: "" });
    const [problem, setProblem] = useState(null);
    const [busy, setBusy] = useState(false);
    const update = (key) => (event) => {
        const value = event.target.value;
        setValues((prev) => ({ ...prev, [key]: value, ...(key === "tenantName" && !prev.slug ? {} : {}) }));
    };
    const submit = async (event) => {
        event.preventDefault();
        setBusy(true);
        setProblem(null);
        try {
            const tokens = unwrap(await api.POST("/api/v1/auth/signup", { body: { ...values, language: currentLanguage() } }));
            setSession(tokens);
            void navigate({ to: "/" });
        }
        catch (error) {
            setProblem(toFormProblem(error, t("auth.failed")));
        }
        finally {
            setBusy(false);
        }
    };
    return (_jsx("div", { className: "flex min-h-dvh items-center justify-center bg-canvas p-4", children: _jsxs("div", { className: "w-full max-w-md rounded-lg border border-border bg-surface p-6 shadow-sm", children: [_jsxs("div", { className: "mb-6 flex items-center justify-between", children: [_jsx("h1", { className: "text-xl font-bold text-accent", children: "Quicker" }), _jsx(LanguageSwitch, {})] }), _jsxs("form", { onSubmit: (event) => { void submit(event); }, className: "flex flex-col gap-4", "aria-labelledby": "signup-title", children: [_jsx("h2", { id: "signup-title", className: "text-lg font-semibold", children: t("auth.createWorkspace") }), _jsx(FormError, { message: problem && Object.keys(problem.fields).length === 0 ? problem.message : null }), _jsx(Field, { label: t("auth.workspaceName"), required: true, error: problem?.fields.tenantName, children: _jsx(TextField, { name: "tenantName", value: values.tenantName, onChange: update("tenantName"), required: true, autoComplete: "organization" }) }), _jsx(Field, { label: t("auth.slug"), required: true, description: t("auth.slugHint"), error: problem?.fields.slug, children: _jsx(TextField, { name: "slug", value: values.slug, onChange: update("slug"), required: true, pattern: "[a-z0-9-]{3,40}", dir: "ltr" }) }), _jsx(Field, { label: t("auth.yourName"), required: true, error: problem?.fields.ownerName, children: _jsx(TextField, { name: "ownerName", value: values.ownerName, onChange: update("ownerName"), required: true, autoComplete: "name" }) }), _jsx(Field, { label: t("auth.email"), required: true, error: problem?.fields.ownerEmail ?? problem?.fields.email, children: _jsx(TextField, { type: "email", name: "ownerEmail", value: values.ownerEmail, onChange: update("ownerEmail"), required: true, autoComplete: "email", dir: "ltr" }) }), _jsx(Field, { label: t("auth.password"), required: true, description: t("auth.passwordHint"), error: problem?.fields.password, children: _jsx(TextField, { type: "password", name: "password", value: values.password, onChange: update("password"), required: true, autoComplete: "new-password", minLength: 12, dir: "ltr" }) }), _jsx(Button, { type: "submit", loading: busy, className: "w-full", children: t("auth.createWorkspace") }), _jsxs("p", { className: "text-center text-sm text-fg-muted", children: [t("auth.haveAccount"), " ", _jsx(Link, { to: "/login", className: "text-accent underline-offset-4 hover:underline", children: t("auth.signIn") })] })] })] }) }));
}
