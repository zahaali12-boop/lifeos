import { Badge, Button, Field, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { KeyRound, Smartphone, Trash2 } from "lucide-react";
import { useMemo, useState, type FormEvent, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { api, isApiProblem, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { setLanguage } from "../../i18n";
import { formatDateTime, setDigitStyle } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { keyErrorKind, registerKey, webAuthnAvailable } from "../../lib/webauthn";
import { updateSessionUser, useSession } from "../../session/session";
import { FormError, PageHeader, SelectField, TextField } from "../common";
import { RecoveryCodes, TotpSetup } from "./TwoStep";

type Method = components["schemas"]["MfaMethodSummary"];
type SessionRow = components["schemas"]["SessionSummary"];
type TotpEnrollment = components["schemas"]["TotpEnrollResponse"];

function Section({ title, description, children, testId }: { title: string; description?: string; children: ReactNode; testId: string }) {
  return (
    <section className="flex flex-col gap-3 rounded-lg border border-border bg-surface p-4 sm:p-5" data-testid={testId}>
      <div>
        <h2 className="text-base font-semibold">{title}</h2>
        {description ? <p className="text-sm text-fg-muted">{description}</p> : null}
      </div>
      {children}
    </section>
  );
}

/** How a session was signed in, from its authentication methods (RFC 8176 values). */
function signInMethod(amr: string): string {
  const parts = amr.split(" ");
  if (parts.includes("sso")) {
    return "sso";
  }
  if (parts.includes("hwk")) {
    return "key";
  }
  if (parts.includes("recovery")) {
    return "recovery";
  }
  return parts.includes("otp") ? "code" : "password";
}

/** A short device description from the browser's user agent: the browser and the system, not the full string. */
function device(userAgent: string | null | undefined): string {
  if (!userAgent) {
    return "—";
  }
  const has = (part: string): boolean => userAgent.includes(part);
  const browser = has("Edg/") ? "Edge" : has("Firefox/") ? "Firefox" : has("Chrome/") ? "Chrome" : has("Safari/") ? "Safari" : null;
  const system = has("Windows") ? "Windows" : has("Android") ? "Android" : has("iPhone") || has("iPad") ? "iOS" : has("Mac OS X") ? "macOS" : has("Linux") ? "Linux" : null;
  return browser || system ? [browser, system].filter(Boolean).join(" · ") : userAgent.slice(0, 60);
}

/**
 * The signed-in person's own account: their name, language and number style; their password; two-step verification
 * with an authenticator app, security keys or passkeys, and recovery codes; and where they are signed in.
 */
export function AccountPage() {
  const { t } = useTranslation();
  return (
    <>
      <PageHeader title={t("account.title")} description={t("account.description")} />
      <div className="flex max-w-4xl flex-col gap-4">
        <ProfileSection />
        <PasswordSection />
        <TwoStepSection />
        <SessionsSection />
      </div>
    </>
  );
}

function ProfileSection() {
  const { t } = useTranslation();
  const session = useSession();
  const user = session?.user;
  const [form, setForm] = useState({ displayName: user?.displayName ?? "", locale: user?.locale ?? "en", timeZone: user?.timeZone ?? "UTC", digitStyle: user?.digitStyle ?? "western" });
  const [problem, setProblem] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const zones = useMemo(() => {
    const all = Intl.supportedValuesOf("timeZone");
    return all.includes(form.timeZone) ? all : [form.timeZone, ...all];
  }, [form.timeZone]);
  const save = useMutation({
    mutationFn: async () => unwrap(await api.PATCH("/api/v1/me", { body: form })),
    onSuccess: async (updated) => {
      setProblem(null);
      setSaved(true);
      updateSessionUser(updated);
      setDigitStyle(updated.digitStyle === "eastern_arabic" ? "arab" : "latn");
      await setLanguage(updated.locale === "ar" ? "ar" : "en");
    },
    onError: (error) => { setSaved(false); setProblem(toFormProblem(error, t("common.saveFailed")).message); },
  });
  const submit = (event: FormEvent): void => { event.preventDefault(); save.mutate(); };
  return (
    <Section title={t("account.profile")} description={t("account.profileHint")} testId="account-profile">
      <form onSubmit={submit} className="flex flex-col gap-3">
        <FormError message={problem} />
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label={t("account.displayName")} required>
            <TextField value={form.displayName} onChange={(e) => { setSaved(false); setForm({ ...form, displayName: e.target.value }); }} required maxLength={200} data-testid="profile-name" />
          </Field>
          <Field label={t("auth.email")}>
            <TextField value={user?.email ?? ""} readOnly dir="ltr" />
          </Field>
          <Field label={t("shell.language")}>
            <SelectField value={form.locale} onChange={(e) => { setSaved(false); setForm({ ...form, locale: e.target.value }); }} data-testid="profile-locale">
              <option value="en">English</option>
              <option value="ar">العربية</option>
            </SelectField>
          </Field>
          <Field label={t("shell.digits")}>
            <SelectField value={form.digitStyle} onChange={(e) => { setSaved(false); setForm({ ...form, digitStyle: e.target.value }); }} data-testid="profile-digits">
              <option value="western">{t("shell.digitsLatin")} (0123)</option>
              <option value="eastern_arabic">{t("shell.digitsArabic")} (٠١٢٣)</option>
            </SelectField>
          </Field>
          <Field label={t("account.timeZone")} description={t("account.timeZoneHint")}>
            <SelectField value={form.timeZone} onChange={(e) => { setSaved(false); setForm({ ...form, timeZone: e.target.value }); }} dir="ltr" data-testid="profile-zone">
              {zones.map((zone) => (
                <option key={zone} value={zone}>{zone}</option>
              ))}
            </SelectField>
          </Field>
        </div>
        <div className="flex items-center gap-3">
          <Button type="submit" loading={save.isPending} data-testid="profile-save">{t("common.save")}</Button>
          {saved ? <span className="text-sm text-success" role="status" data-testid="profile-saved">{t("account.saved")}</span> : null}
        </div>
      </form>
    </Section>
  );
}

function PasswordSection() {
  const { t } = useTranslation();
  const [form, setForm] = useState({ current: "", next: "", confirm: "" });
  const [problem, setProblem] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const change = useMutation({
    mutationFn: async () => { await api.POST("/api/v1/me/password", { body: { currentPassword: form.current, newPassword: form.next } }).then(unwrap); },
    onSuccess: () => { setProblem(null); setDone(true); setForm({ current: "", next: "", confirm: "" }); },
    onError: (error) => { setDone(false); setProblem(toFormProblem(error, t("common.saveFailed")).message); },
  });
  const submit = (event: FormEvent): void => {
    event.preventDefault();
    if (form.next !== form.confirm) {
      setProblem(t("account.passwordsDiffer"));
      return;
    }
    change.mutate();
  };
  return (
    <Section title={t("account.password")} description={t("account.passwordHint")} testId="account-password">
      <form onSubmit={submit} className="flex flex-col gap-3">
        <FormError message={problem} />
        <div className="grid gap-3 sm:grid-cols-3">
          <Field label={t("account.currentPassword")} required>
            <TextField type="password" autoComplete="current-password" value={form.current} onChange={(e) => { setForm({ ...form, current: e.target.value }); }} required dir="ltr" data-testid="password-current" />
          </Field>
          <Field label={t("account.newPassword")} required>
            <TextField type="password" autoComplete="new-password" value={form.next} onChange={(e) => { setForm({ ...form, next: e.target.value }); }} required dir="ltr" data-testid="password-new" />
          </Field>
          <Field label={t("account.confirmPassword")} required>
            <TextField type="password" autoComplete="new-password" value={form.confirm} onChange={(e) => { setForm({ ...form, confirm: e.target.value }); }} required dir="ltr" data-testid="password-confirm" />
          </Field>
        </div>
        <div className="flex items-center gap-3">
          <Button type="submit" loading={change.isPending} data-testid="password-save">{t("account.changePassword")}</Button>
          {done ? <span className="text-sm text-success" role="status" data-testid="password-changed">{t("account.passwordChanged")}</span> : null}
        </div>
      </form>
    </Section>
  );
}

function TwoStepSection() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const methods = useQuery({ queryKey: ["my-mfa"], queryFn: async () => unwrap(await api.GET("/api/v1/me/mfa")) });
  const [enrollment, setEnrollment] = useState<TotpEnrollment | null>(null);
  const [keyName, setKeyName] = useState<string | null>(null);
  const [codes, setCodes] = useState<readonly string[] | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const verified = (methods.data ?? []).filter((m: Method) => m.verifiedAt);
  const on = verified.length > 0;
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["my-mfa"] });
    const list = queryClient.getQueryData<Method[]>(["my-mfa"]) ?? [];
    updateSessionUser({ hasMfa: list.some((m) => m.verifiedAt) });
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed")).message); };

  const startTotp = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/me/mfa/totp/enroll")),
    onSuccess: (started) => { setProblem(null); setKeyName(null); setEnrollment(started); },
    onError: fail,
  });
  const confirmTotp = useMutation({
    mutationFn: async (code: string) => unwrap(await api.POST("/api/v1/me/mfa/totp/confirm", { body: { methodId: enrollment?.methodId ?? "", code } })),
    onSuccess: async (result) => { setProblem(null); setEnrollment(null); setCodes(result.codes); await refresh(); },
    onError: fail,
  });
  const addKey = useMutation({
    mutationFn: async (name: string) => {
      const started = unwrap(await api.POST("/api/v1/me/mfa/webauthn/register/options"));
      const response = await registerKey(started.options as Record<string, unknown>);
      return unwrap(await api.POST("/api/v1/me/mfa/webauthn/register/verify", { body: { optionsId: started.optionsId, name, response } }));
    },
    onSuccess: async () => { setProblem(null); setKeyName(null); await refresh(); },
    onError: (error) => { if (isApiProblem(error)) { fail(error); } else { setProblem(t(`account.keyErrors.${keyErrorKind(error)}`)); } },
  });
  const remove = useMutation({
    mutationFn: async (methodId: string) => { await api.DELETE("/api/v1/me/mfa/{methodId}", { params: { path: { methodId } } }).then(unwrap); },
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });
  const newCodes = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/me/mfa/recovery-codes")),
    onSuccess: (result) => { setProblem(null); setCodes(result.codes); },
    onError: fail,
  });
  const keysAvailable = webAuthnAvailable();

  return (
    <Section title={t("account.twoStep")} description={t("account.twoStepHint")} testId="account-two-step">
      <p className="flex items-center gap-2 text-sm">
        {t("account.twoStepStatus")}
        <Badge tone={on ? "success" : "warning"} data-testid="two-step-status">{on ? t("account.on") : t("account.off")}</Badge>
      </p>
      <FormError message={problem} />
      {verified.length > 0 ? (
        <Table data-testid="mfa-methods">
          <TableHeader>
            <TableRow>
              <TableHead>{t("account.method")}</TableHead>
              <TableHead>{t("account.added")}</TableHead>
              <TableHead>{t("account.lastUsed")}</TableHead>
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {verified.map((m) => (
              <TableRow key={m.id} data-testid="mfa-method">
                <TableCell>
                  <span className="flex items-center gap-2">
                    {m.kind === "webauthn" ? <KeyRound className="size-4 text-fg-muted" aria-hidden="true" /> : <Smartphone className="size-4 text-fg-muted" aria-hidden="true" />}
                    <span dir="auto">{m.kind === "totp" ? t("account.authenticatorApp") : m.name}</span>
                    {m.kind === "webauthn" ? <span className="text-xs text-fg-muted">{t("account.securityKey")}</span> : null}
                  </span>
                </TableCell>
                <TableCell dir="ltr">{formatDateTime(m.verifiedAt)}</TableCell>
                <TableCell dir="ltr">{m.lastUsedAt ? formatDateTime(m.lastUsedAt) : "—"}</TableCell>
                <TableCell>
                  <Button size="sm" variant="ghost" className="text-danger" onClick={() => { remove.mutate(m.id); }} loading={remove.isPending && remove.variables === m.id} aria-label={t("account.removeMethod", { name: m.kind === "totp" ? t("account.authenticatorApp") : m.name })} data-testid="mfa-remove">
                    <Trash2 aria-hidden="true" />
                  </Button>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      ) : null}
      {codes ? <RecoveryCodes codes={codes} onDone={() => { setCodes(null); }} /> : null}
      {enrollment ? (
        <div className="rounded-md border border-border p-3">
          <TotpSetup enrollment={enrollment} busy={confirmTotp.isPending} error={null} onConfirm={(code) => { confirmTotp.mutate(code); }} onCancel={() => { setEnrollment(null); }} />
        </div>
      ) : null}
      {keyName !== null ? (
        <form onSubmit={(event) => { event.preventDefault(); addKey.mutate(keyName.trim() || t("account.securityKey")); }} className="flex flex-wrap items-end gap-2 rounded-md border border-border p-3" data-testid="key-setup">
          <Field label={t("account.keyName")} description={t("account.keyNameHint")}>
            <TextField value={keyName} onChange={(e) => { setKeyName(e.target.value); }} maxLength={100} data-testid="key-name" />
          </Field>
          <Button type="button" variant="secondary" onClick={() => { setKeyName(null); }}>{t("common.cancel")}</Button>
          <Button type="submit" loading={addKey.isPending} data-testid="key-register">{t("account.registerKey")}</Button>
        </form>
      ) : null}
      {!enrollment && keyName === null ? (
        <div className="flex flex-wrap gap-2">
          {!verified.some((m) => m.kind === "totp") ? (
            <Button variant="secondary" onClick={() => { startTotp.mutate(); }} loading={startTotp.isPending} data-testid="add-totp">
              <Smartphone aria-hidden="true" />
              {t("account.addAuthenticator")}
            </Button>
          ) : null}
          <Button variant="secondary" onClick={() => { setProblem(null); setKeyName(""); }} disabled={!keysAvailable} title={keysAvailable ? undefined : t("account.keyErrors.unsupported")} data-testid="add-key">
            <KeyRound aria-hidden="true" />
            {t("account.addKey")}
          </Button>
          {on ? (
            <Button variant="ghost" onClick={() => { newCodes.mutate(); }} loading={newCodes.isPending} data-testid="new-recovery-codes">
              {t("account.newRecoveryCodes")}
            </Button>
          ) : null}
        </div>
      ) : null}
    </Section>
  );
}

function SessionsSection() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const sessions = useQuery({ queryKey: ["my-sessions"], queryFn: async () => unwrap(await api.GET("/api/v1/me/sessions")) });
  const [problem, setProblem] = useState<string | null>(null);
  const revoke = useMutation({
    mutationFn: async (ids: string[]) => {
      for (const sessionId of ids) {
        await api.DELETE("/api/v1/me/sessions/{sessionId}", { params: { path: { sessionId } } }).then(unwrap);
      }
    },
    onSuccess: async () => { setProblem(null); await queryClient.invalidateQueries({ queryKey: ["my-sessions"] }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed")).message); },
  });
  const rows = sessions.data ?? [];
  const others = rows.filter((s: SessionRow) => !s.isCurrent).map((s) => s.id);
  return (
    <Section title={t("account.sessions")} description={t("account.sessionsHint")} testId="account-sessions">
      <FormError message={problem} />
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{t("account.device")}</TableHead>
            <TableHead>{t("account.address")}</TableHead>
            <TableHead>{t("account.signedIn")}</TableHead>
            <TableHead>{t("account.lastActive")}</TableHead>
            <TableHead />
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((s) => (
            <TableRow key={s.id} data-testid="session-row">
              <TableCell>
                <span className="flex flex-wrap items-center gap-2">
                  <span dir="ltr">{device(s.userAgent)}</span>
                  {s.isCurrent ? <Badge tone="info" data-testid="session-current">{t("account.thisDevice")}</Badge> : null}
                </span>
                <span className="block text-xs text-fg-muted">{t(`account.signInMethods.${signInMethod(s.amr)}`)}</span>
              </TableCell>
              <TableCell dir="ltr">{s.ip ?? "—"}</TableCell>
              <TableCell dir="ltr">{formatDateTime(s.createdAt)}</TableCell>
              <TableCell dir="ltr">{formatDateTime(s.lastUsedAt)}</TableCell>
              <TableCell>
                {s.isCurrent ? null : (
                  <Button size="sm" variant="ghost" onClick={() => { revoke.mutate([s.id]); }} loading={revoke.isPending && revoke.variables.length === 1 && revoke.variables[0] === s.id} data-testid="session-revoke">
                    {t("account.signOutSession")}
                  </Button>
                )}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {others.length > 1 ? (
        <Button variant="secondary" className="self-start" onClick={() => { revoke.mutate(others); }} loading={revoke.isPending} data-testid="sessions-revoke-others">
          {t("account.signOutOthers")}
        </Button>
      ) : null}
    </Section>
  );
}
