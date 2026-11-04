import { Badge, Button, Field } from "@quicker/ui";
import { useMutation, useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { LogOut, RefreshCw, ShoppingBag } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatDateTime, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { DecimalField, FormError, SelectField } from "../common";
import { useOnline } from "../mobile/context";
import { KeyValues } from "../inventory/shared";
import { leaveRegister, pendingOrders, setTill, useTill, type TillCustomer } from "./store";

/** Loads what the till keeps for offline selling: the register, its catalogue and the customers it may sell to on account. */
async function loadRegister(registerId: string): Promise<void> {
  const register = unwrap(await api.GET("/api/v1/pos/registers/{registerId}", { params: { path: { registerId } } }));
  const catalogue = unwrap(await api.GET("/api/v1/pos/registers/{registerId}/catalogue", { params: { path: { registerId } } }));
  const partners = unwrap(await api.GET("/api/v1/partners", { params: { query: { role: "customer", isActive: true, limit: 200 } } }));
  const customers: TillCustomer[] = partners.items.map((p) => ({ id: p.id, code: p.code, name: p.legalName }));
  const session = register.openSessionId ? unwrap(await api.GET("/api/v1/pos/sessions/{sessionId}", { params: { path: { sessionId: register.openSessionId } } })) : null;
  setTill({ companyId: register.companyId, register, catalogue, customers, session });
}

/** Where the cashier works (A-180): the register and its cached catalogue, the session opened with a float and closed with the count. */
export function PosHomePage() {
  const { t } = useTranslation();
  const till = useTill();
  const online = useOnline();
  const [companyId, setCompanyId] = useState(till.companyId ?? "");
  const [registerId, setRegisterId] = useState("");
  const [openingFloat, setOpeningFloat] = useState("0");
  const [countedCash, setCountedCash] = useState("");
  const [closing, setClosing] = useState(false);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const pending = pendingOrders().length;

  const companies = useQuery({ queryKey: ["companies", ""], queryFn: async () => unwrap(await api.GET("/api/v1/organization/companies")), enabled: online && !till.register });
  const registers = useQuery({
    queryKey: ["pos-registers", companyId],
    queryFn: async () => unwrap(await api.GET("/api/v1/pos/registers", { params: { query: { companyId } } })),
    enabled: online && Boolean(companyId) && !till.register,
  });
  const companyName = (companies.data ?? []).find((c) => c.id === (till.companyId ?? companyId));
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const use = useMutation({ mutationFn: loadRegister, onSuccess: () => { setProblem(null); }, onError: fail });
  const refresh = useMutation({ mutationFn: async () => { if (till.register) { await loadRegister(till.register.id); } }, onSuccess: () => { setProblem(null); }, onError: fail });
  const open = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/pos/registers/{registerId}/sessions", { params: { path: { registerId: till.register?.id ?? "" } }, body: { openingFloat } })),
    onSuccess: (session) => { setProblem(null); setTill({ session }); },
    onError: fail,
  });
  const close = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/pos/sessions/{sessionId}/close", { params: { path: { sessionId: till.session?.id ?? "" } }, body: { countedCash } })),
    onSuccess: (session) => { setProblem(null); setClosing(false); setCountedCash(""); setTill({ session, orders: till.orders.filter((o) => o.status !== "synced") }); },
    onError: fail,
  });

  const money = (value: string | number | null | undefined): string => (value === null || value === undefined ? "—" : `${formatNumber(value, { minimumFractionDigits: Number(till.catalogue?.minorUnits ?? 2), maximumFractionDigits: Number(till.catalogue?.minorUnits ?? 2) })} ${till.register?.currency ?? ""}`);

  if (!till.register) {
    const submit = (event: FormEvent): void => { event.preventDefault(); if (registerId) { use.mutate(registerId); } };
    return (
      <form className="mx-auto flex max-w-xl flex-col gap-4" onSubmit={submit}>
        <div>
          <h1 className="text-xl font-semibold tracking-tight">{t("pos.till.chooseRegister")}</h1>
          <p className="mt-1 text-sm text-fg-muted">{t("pos.till.chooseRegisterHelp")}</p>
        </div>
        {!online ? <p className="rounded-md border border-warning/40 bg-warning-soft px-3 py-2 text-sm" data-testid="till-offline-note">{t("pos.till.needsConnection")}</p> : null}
        <Field label={t("mobile.home.company")}>
          <SelectField className="h-12 text-base" data-testid="till-company" value={companyId} onChange={(event) => { setCompanyId(event.target.value); setRegisterId(""); }}>
            <option value="">{t("mobile.home.chooseCompany")}</option>
            {(companies.data ?? []).map((company) => (
              <option key={company.id} value={company.id}>{company.code} · {localized(company.legalName)}</option>
            ))}
          </SelectField>
        </Field>
        <Field label={t("pos.register")}>
          <SelectField className="h-12 text-base" data-testid="till-register" value={registerId} disabled={!companyId} onChange={(event) => { setRegisterId(event.target.value); }}>
            <option value="">{t("pos.till.pickRegister")}</option>
            {(registers.data ?? []).filter((r) => r.isActive).map((register) => (
              <option key={register.id} value={register.id}>{register.code} · {localized(register.name)}</option>
            ))}
          </SelectField>
        </Field>
        <FormError message={problem?.message ?? null} />
        <Button type="submit" size="lg" className="h-12" disabled={!registerId || !online} loading={use.isPending} data-testid="till-use-register">{t("pos.till.useRegister")}</Button>
      </form>
    );
  }

  const session = till.session;
  const catalogue = till.catalogue;
  return (
    <div className="mx-auto flex max-w-3xl flex-col gap-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold tracking-tight" dir="auto">{localized(till.register.name)}</h1>
          <p className="mt-1 text-sm text-fg-muted">{companyName ? localized(companyName.legalName) : null}{till.register.warehouseCode ? ` · ${till.register.warehouseCode}` : ""} · {till.register.currency}</p>
        </div>
        <Button variant="ghost" className="gap-2" disabled={pending > 0} onClick={() => { leaveRegister(); }} data-testid="till-leave">
          <LogOut aria-hidden="true" />
          {t("pos.till.leaveRegister")}
        </Button>
      </div>
      <FormError message={problem?.message ?? null} />

      <section className="rounded-lg border border-border bg-surface p-4" aria-labelledby="till-catalogue-heading">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 id="till-catalogue-heading" className="font-semibold">{t("pos.till.catalogue")}</h2>
          <Button variant="secondary" size="sm" className="gap-2" disabled={!online} loading={refresh.isPending} onClick={() => { refresh.mutate(); }} data-testid="till-refresh">
            <RefreshCw aria-hidden="true" />
            {t("pos.till.refreshCatalogue")}
          </Button>
        </div>
        {catalogue ? (
          <KeyValues entries={[
            [t("pos.till.items"), <span key="n" dir="ltr">{formatNumber(catalogue.items.length)}</span>],
            [t("pos.till.cachedAt"), <span key="at" dir="ltr">{formatDateTime(catalogue.generatedAt)}</span>],
            [t("pos.cashRounding"), <span key="r" dir="ltr">{formatNumber(catalogue.cashRoundingIncrement)}</span>],
            [t("pos.pricesIncludeTax"), catalogue.pricesIncludeTax ? t("common.yes") : t("common.no")],
          ]} />
        ) : null}
        <p className="mt-2 text-xs text-fg-muted">{t("pos.till.catalogueHelp")}</p>
      </section>

      <section className="rounded-lg border border-border bg-surface p-4" aria-labelledby="till-session-heading">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 id="till-session-heading" className="font-semibold">{t("pos.session")}</h2>
          {session ? <Badge tone={session.status === "open" ? "success" : "neutral"} data-testid="till-session-status">{t(`pos.sessionStatuses.${session.status}`)}</Badge> : null}
        </div>
        {session?.status === "open" ? (
          <div className="mt-3 flex flex-col gap-3">
            <KeyValues entries={[
              [t("pos.number"), <span key="n" dir="ltr">{session.number}</span>],
              [t("pos.cashier"), session.cashierName],
              [t("pos.openedAt"), <span key="o" dir="ltr">{formatDateTime(session.openedAt)}</span>],
              [t("pos.openingFloat"), <span key="f" dir="ltr">{money(session.openingFloat)}</span>],
              [t("pos.till.pendingOrders"), <span key="p" dir="ltr" data-testid="till-pending-count">{formatNumber(pending)}</span>],
            ]} />
            <div className="flex flex-wrap gap-2">
              <Button asChild size="lg" className="h-12 gap-2">
                <Link to="/pos/sell" data-testid="till-start-selling">
                  <ShoppingBag aria-hidden="true" />
                  {t("pos.till.startSelling")}
                </Link>
              </Button>
              <Button variant="secondary" size="lg" className="h-12" disabled={!online || pending > 0} onClick={() => { setClosing(true); }} data-testid="till-close-session">{t("pos.till.closeSession")}</Button>
            </div>
            {pending > 0 ? <p className="text-sm text-warning">{t("pos.till.closeBlocked")}</p> : null}
            {closing ? (
              <form className="flex flex-wrap items-end gap-3 rounded-md border border-border p-3" onSubmit={(event) => { event.preventDefault(); close.mutate(); }}>
                <Field label={t("pos.countedCash")}>
                  <DecimalField dir="ltr" value={countedCash} onChange={(event) => { setCountedCash(event.target.value); }} data-testid="till-counted-cash" required />
                </Field>
                <Button type="submit" loading={close.isPending} data-testid="till-confirm-close">{t("pos.till.confirmClose")}</Button>
                <Button type="button" variant="ghost" onClick={() => { setClosing(false); }}>{t("common.cancel")}</Button>
              </form>
            ) : null}
          </div>
        ) : (
          <div className="mt-3 flex flex-col gap-3">
            {session ? (
              <KeyValues entries={[
                [t("pos.number"), <span key="n" dir="ltr">{session.number}</span>],
                [t("pos.expectedCash"), <span key="e" dir="ltr">{money(session.expectedCash)}</span>],
                [t("pos.countedCash"), <span key="c" dir="ltr">{money(session.countedCash)}</span>],
                [t("pos.difference"), <span key="d" dir="ltr" data-testid="till-difference">{money(session.difference)}</span>],
              ]} />
            ) : null}
            <form className="flex flex-wrap items-end gap-3" onSubmit={(event) => { event.preventDefault(); open.mutate(); }}>
              <Field label={t("pos.openingFloat")}>
                <DecimalField dir="ltr" value={openingFloat} onChange={(event) => { setOpeningFloat(event.target.value); }} data-testid="till-opening-float" />
              </Field>
              <Button type="submit" disabled={!online} loading={open.isPending} data-testid="till-open-session">{t("pos.till.openSession")}</Button>
            </form>
            {!online ? <p className="text-sm text-warning">{t("pos.till.needsConnection")}</p> : null}
          </div>
        )}
      </section>
    </div>
  );
}
