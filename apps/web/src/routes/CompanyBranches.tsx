import { Button, Field, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { localized } from "../lib/format";
import { toFormProblem, type FormProblem } from "../lib/problem";
import { FormError, TextField } from "./common";
import { DocStatus } from "./inventory/shared";

interface BranchForm {
  code: string;
  nameEn: string;
  nameAr: string;
  isActive: boolean;
}

const emptyBranch: BranchForm = { code: "", nameEn: "", nameAr: "", isActive: true };

/** A company's branches (roadmap 1.4): each one is also a value of the BRANCH dimension, so postings can be analysed by branch. */
export function CompanyBranches({ companyId }: { companyId: string }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState<{ id: string | null; form: BranchForm } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const branches = useQuery({
    queryKey: ["branches", companyId],
    queryFn: async () => unwrap(await api.GET("/api/v1/organization/companies/{companyId}/branches", { params: { path: { companyId } } })),
  });
  const save = useMutation({
    mutationFn: async (input: { id: string | null; form: BranchForm }) => {
      const body = { code: input.form.code, name: { en: input.form.nameEn, ...(input.form.nameAr ? { ar: input.form.nameAr } : {}) }, isActive: input.form.isActive };
      return input.id
        ? unwrap(await api.PUT("/api/v1/organization/companies/{companyId}/branches/{branchId}", { params: { path: { companyId, branchId: input.id } }, body }))
        : unwrap(await api.POST("/api/v1/organization/companies/{companyId}/branches", { params: { path: { companyId } }, body }));
    },
    onSuccess: async () => {
      setEditing(null);
      setProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["branches", companyId] });
      await queryClient.invalidateQueries({ queryKey: ["dimension-values"] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const submit = (event: FormEvent): void => {
    event.preventDefault();
    if (editing) {
      save.mutate(editing);
    }
  };
  const setForm = (patch: Partial<BranchForm>): void => { setEditing((prev) => (prev ? { ...prev, form: { ...prev.form, ...patch } } : prev)); };

  return (
    <section className="flex flex-col gap-3 rounded-md border border-border p-4" data-testid="company-branches">
      <div className="flex items-center gap-2">
        <h3 className="text-sm font-semibold">{t("branches.title")}</h3>
        {editing ? null : (
          <Button type="button" variant="secondary" size="sm" className="ms-auto" onClick={() => { setProblem(null); setEditing({ id: null, form: { ...emptyBranch } }); }} data-testid="new-branch">
            <Plus aria-hidden="true" />
            {t("branches.new")}
          </Button>
        )}
      </div>
      <p className="text-sm text-fg-muted">{t("branches.hint")}</p>
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{t("branches.code")}</TableHead>
            <TableHead>{t("branches.name")}</TableHead>
            <TableHead>{t("common.status")}</TableHead>
            <TableHead />
          </TableRow>
        </TableHeader>
        <TableBody>
          {(branches.data ?? []).map((b) => (
            <TableRow key={b.id} data-testid="branch-row">
              <TableCell dir="ltr">{b.code}</TableCell>
              <TableCell>{localized(b.name)}</TableCell>
              <TableCell><DocStatus status={b.isActive ? "active" : "inactive"} /></TableCell>
              <TableCell>
                <Button type="button" variant="ghost" size="sm" onClick={() => { setProblem(null); setEditing({ id: b.id, form: { code: b.code, nameEn: b.name.en ?? "", nameAr: b.name.ar ?? "", isActive: b.isActive } }); }}>
                  {t("common.edit")}
                </Button>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {editing ? (
        <form onSubmit={submit} className="flex flex-col gap-3">
          <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
          <div className="grid gap-3 sm:grid-cols-3">
            <Field label={t("branches.code")} required error={problem?.fields.code}>
              <TextField value={editing.form.code} onChange={(e) => { setForm({ code: e.target.value.toUpperCase() }); }} required dir="ltr" data-testid="branch-code" />
            </Field>
            <Field label={t("branches.nameEn")} required error={problem?.fields.name}>
              <TextField value={editing.form.nameEn} onChange={(e) => { setForm({ nameEn: e.target.value }); }} required data-testid="branch-name-en" />
            </Field>
            <Field label={t("branches.nameAr")}>
              <TextField value={editing.form.nameAr} onChange={(e) => { setForm({ nameAr: e.target.value }); }} dir="rtl" lang="ar" />
            </Field>
          </div>
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={editing.form.isActive} onChange={(e) => { setForm({ isActive: e.target.checked }); }} />
            {t("branches.active")}
          </label>
          <div className="flex gap-2">
            <Button type="button" variant="secondary" size="sm" onClick={() => { setEditing(null); }}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" size="sm" loading={save.isPending} data-testid="save-branch">
              {t("branches.save")}
            </Button>
          </div>
        </form>
      ) : null}
    </section>
  );
}
