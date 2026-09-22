import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Download, Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";
import { DataGrid } from "../grid/DataGrid";
import { formatDate, formatNumber } from "../lib/format";
import { toFormProblem, type FormProblem } from "../lib/problem";
import { FormError, PageHeader, SelectField, TextField } from "./common";

type Rate = components["schemas"]["RateSummary"];

export function RatesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const [form, setForm] = useState({ rateType: "spot", fromCurrency: "USD", toCurrency: "IQD", validFrom: new Date().toISOString().slice(0, 10), rate: "", reason: "" });
  const [problem, setProblem] = useState<FormProblem | null>(null);

  const rates = useQuery({ queryKey: ["rates"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/rates", { params: { query: { limit: 500 } } })) });
  const rateTypes = useQuery({ queryKey: ["rate-types"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/rate-types")) });
  const currencies = useQuery({ queryKey: ["currencies"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/currencies")) });
  const providers = useQuery({ queryKey: ["rate-providers"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/rates/providers")) });

  const save = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/organization/rates", { body: { rateType: form.rateType, fromCurrency: form.fromCurrency, toCurrency: form.toCurrency, validFrom: form.validFrom, rate: form.rate, reason: form.reason || null } })),
    onSuccess: async () => {
      setOpen(false);
      setProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["rates"] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const importRates = useMutation({
    mutationFn: async (provider: string) => unwrap(await api.POST("/api/v1/organization/rates/import", { body: { provider, rateType: "spot" } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["rates"] }),
  });

  const columns = useMemo<ColumnDef<Rate, unknown>[]>(
    () => [
      { id: "validFrom", accessorKey: "validFrom", header: t("rates.validFrom"), size: 120, cell: ({ row }) => formatDate(row.original.validFrom) },
      { id: "rateType", accessorKey: "rateType", header: t("rates.type"), size: 100 },
      { id: "pair", accessorFn: (row) => `${row.fromCurrency} → ${row.toCurrency}`, header: t("rates.pair"), size: 120 },
      { id: "rate", accessorKey: "rate", header: t("rates.rate"), size: 140, cell: ({ row }) => <span className="tabular">{formatNumber(row.original.rate, { maximumFractionDigits: 6 })}</span> },
      { id: "source", accessorKey: "source", header: t("rates.source"), size: 120 },
      { id: "reason", accessorKey: "reason", header: t("common.reason"), size: 200 },
    ],
    [t],
  );

  const submit = (event: FormEvent): void => {
    event.preventDefault();
    save.mutate();
  };

  const activeCurrencies = (currencies.data ?? []).filter((c) => c.isActive);

  return (
    <>
      <PageHeader
        title={t("nav.rates")}
        description={t("rates.description")}
        actions={
          <>
            {(providers.data ?? []).map((provider) => (
              <Button key={provider} variant="secondary" loading={importRates.isPending} onClick={() => { importRates.mutate(provider); }}>
                <Download aria-hidden="true" />
                {t("rates.import", { provider })}
              </Button>
            ))}
            <Button onClick={() => { setProblem(null); setOpen(true); }}>
              <Plus aria-hidden="true" />
              {t("rates.new")}
            </Button>
          </>
        }
      />
      {importRates.data ? (
        <p className="mb-3 text-sm text-fg-muted" role="status">
          {t("rates.imported", { count: importRates.data.imported })}
        </p>
      ) : null}
      <DataGrid<Rate> label="nav.rates" columns={columns} data={rates.data ?? []} rowKey={(row) => row.id} loading={rates.isPending} emptyTitle={t("rates.emptyTitle")} emptyDescription={t("rates.emptyDescription")} />
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent closeLabel={t("common.close")}>
          <form onSubmit={submit} className="flex flex-col gap-4">
            <DialogHeader>
              <DialogTitle className="text-lg font-semibold">{t("rates.new")}</DialogTitle>
            </DialogHeader>
            <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label={t("rates.type")} required error={problem?.fields.rateType}>
                <SelectField value={form.rateType} onChange={(e) => { setForm({ ...form, rateType: e.target.value }); }}>
                  {(rateTypes.data ?? []).map((type) => (
                    <option key={type.code} value={type.code}>
                      {type.code}
                    </option>
                  ))}
                </SelectField>
              </Field>
              <Field label={t("rates.validFrom")} required error={problem?.fields.validFrom}>
                <TextField type="date" value={form.validFrom} onChange={(e) => { setForm({ ...form, validFrom: e.target.value }); }} required dir="ltr" />
              </Field>
              <Field label={t("rates.from")} required error={problem?.fields.fromCurrency}>
                <SelectField value={form.fromCurrency} onChange={(e) => { setForm({ ...form, fromCurrency: e.target.value }); }}>
                  {activeCurrencies.map((c) => (
                    <option key={c.code} value={c.code}>
                      {c.code}
                    </option>
                  ))}
                </SelectField>
              </Field>
              <Field label={t("rates.to")} required error={problem?.fields.toCurrency}>
                <SelectField value={form.toCurrency} onChange={(e) => { setForm({ ...form, toCurrency: e.target.value }); }}>
                  {activeCurrencies.map((c) => (
                    <option key={c.code} value={c.code}>
                      {c.code}
                    </option>
                  ))}
                </SelectField>
              </Field>
              <Field label={t("rates.rate")} required error={problem?.fields.rate}>
                <TextField inputMode="decimal" value={form.rate} onChange={(e) => { setForm({ ...form, rate: e.target.value }); }} required dir="ltr" />
              </Field>
              <Field label={t("common.reason")}>
                <TextField value={form.reason} onChange={(e) => { setForm({ ...form, reason: e.target.value }); }} />
              </Field>
            </div>
            <DialogFooter>
              <Button type="button" variant="secondary" onClick={() => { setOpen(false); }}>
                {t("common.cancel")}
              </Button>
              <Button type="submit" loading={save.isPending}>
                {t("common.save")}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </>
  );
}
