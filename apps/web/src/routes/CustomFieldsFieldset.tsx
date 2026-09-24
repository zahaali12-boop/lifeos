import { Field } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";
import { formatDate, formatNumber, localized } from "../lib/format";
import { SelectField, TextField } from "./common";

export type CustomFieldDefinition = components["schemas"]["CustomFieldView"];
export type CustomFieldValues = Record<string, unknown>;

/** A record's custom-field values as the API returns them (a JSON object), or none. */
export function asCustomFieldValues(value: unknown): CustomFieldValues {
  return typeof value === "object" && value !== null && !Array.isArray(value) ? { ...(value as CustomFieldValues) } : {};
}

/** The active custom fields an administrator defined for a record type, in the order they placed them. */
export function useCustomFieldDefinitions(entityType: string) {
  return useQuery({
    queryKey: ["custom-fields", entityType],
    queryFn: async () => unwrap(await api.GET("/api/v1/collaboration/custom-fields", { params: { query: { entityType } } })),
    select: (fields) => fields.filter((f) => f.active).sort((a, b) => Number(a.position) - Number(b.position) || a.key.localeCompare(b.key)),
    staleTime: 60_000,
  });
}

/** Renders one custom-field control from its definition; values are validated by the API (custom_field.* problems map back here). */
export function CustomFieldControl({ definition, value, onChange, disabled }: { definition: CustomFieldDefinition; value: unknown; onChange: (next: unknown) => void; disabled?: boolean | undefined }) {
  const { t } = useTranslation();
  const control = { name: `cf-${definition.key}`, disabled, "data-testid": `cf-${definition.key}` };
  switch (definition.type) {
    case "boolean":
      return (
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={Boolean(value)} onChange={(e) => { onChange(e.target.checked); }} {...control} />
          {localized(definition.label)}
        </label>
      );
    case "select":
      return (
        <SelectField value={typeof value === "string" ? value : ""} onChange={(e) => { onChange(e.target.value || undefined); }} {...control}>
          <option value="">—</option>
          {definition.options.map((option) => (
            <option key={option.value} value={option.value}>
              {localized(option.label)}
            </option>
          ))}
        </SelectField>
      );
    case "multi_select": {
      const chosen = Array.isArray(value) ? value.filter((v): v is string => typeof v === "string") : [];
      return (
        <div className="flex flex-wrap gap-x-4 gap-y-2" role="group" aria-label={localized(definition.label)} data-testid={`cf-${definition.key}`}>
          {definition.options.map((option) => (
            <label key={option.value} className="flex items-center gap-2 text-sm">
              <input
                type="checkbox"
                name={`cf-${definition.key}`}
                value={option.value}
                disabled={disabled}
                checked={chosen.includes(option.value)}
                onChange={(e) => {
                  const next = e.target.checked ? [...chosen, option.value] : chosen.filter((v) => v !== option.value);
                  onChange(next.length > 0 ? next : undefined);
                }}
              />
              {localized(option.label)}
            </label>
          ))}
        </div>
      );
    }
    case "number":
      return <TextField type="number" inputMode="decimal" value={typeof value === "number" || typeof value === "string" ? String(value) : ""} onChange={(e) => { onChange(e.target.value === "" ? undefined : Number(e.target.value)); }} {...control} dir="ltr" />;
    case "date":
      return <TextField type="date" value={typeof value === "string" ? value : ""} onChange={(e) => { onChange(e.target.value || undefined); }} {...control} dir="ltr" />;
    case "reference":
      return <TextField value={typeof value === "string" ? value : ""} onChange={(e) => { onChange(e.target.value.trim() || undefined); }} placeholder={t("customFields.referencePlaceholder")} {...control} dir="ltr" />;
    default:
      return <TextField value={typeof value === "string" ? value : ""} onChange={(e) => { onChange(e.target.value || undefined); }} maxLength={definition.rules.maxLength == null ? undefined : Number(definition.rules.maxLength)} {...control} />;
  }
}

/**
 * The custom fields of a record type as a block of a form: nothing when the administrator defined none, otherwise one
 * control per active field, required ones marked, the API's refusal of a value shown under its field.
 */
export function CustomFieldsFieldset({ entityType, values, onChange, errors, disabled }: { entityType: string; values: CustomFieldValues; onChange: (next: CustomFieldValues) => void; errors?: Record<string, string> | undefined; disabled?: boolean | undefined }) {
  const { t } = useTranslation();
  const definitions = useCustomFieldDefinitions(entityType);
  const active = definitions.data ?? [];
  if (active.length === 0) {
    return null;
  }
  return (
    <fieldset className="grid gap-4 rounded-md border border-border p-4 sm:grid-cols-2" data-testid={`custom-fields-${entityType}`}>
      <legend className="px-1 text-sm font-medium">{t("customFields.title")}</legend>
      {active.map((definition) => (
        <Field key={definition.id} label={localized(definition.label)} required={definition.required} error={errors?.[`customFields.${definition.key}`]} description={localized(definition.description) || undefined}>
          <CustomFieldControl
            definition={definition}
            value={values[definition.key]}
            disabled={disabled}
            onChange={(next) => {
              // A cleared field leaves the object rather than being sent as null.
              const others = Object.fromEntries(Object.entries(values).filter(([key]) => key !== definition.key));
              onChange(next === undefined ? others : { ...others, [definition.key]: next });
            }}
          />
        </Field>
      ))}
    </fieldset>
  );
}

/** How a stored value reads on a record: option labels for choices, dates and numbers in the reader's format. */
function display(definition: CustomFieldDefinition, value: unknown, yes: string, no: string, list: Intl.ListFormat): string {
  const option = (v: unknown): string => {
    const found = definition.options.find((o) => o.value === v);
    return found ? localized(found.label) : String(v);
  };
  switch (definition.type) {
    case "boolean":
      return value === true ? yes : no;
    case "select":
      return option(value);
    case "multi_select":
      return Array.isArray(value) ? list.format(value.map(option)) : "";
    case "date":
      return typeof value === "string" ? formatDate(value) : "";
    case "number":
      return typeof value === "number" || typeof value === "string" ? formatNumber(value, { maximumFractionDigits: 6 }) : "";
    default:
      return typeof value === "string" ? value : JSON.stringify(value);
  }
}

/** A record's custom-field values, read-only, in the order the administrator placed the fields; nothing when none is set. */
export function CustomFieldValuesList({ entityType, values }: { entityType: string; values: unknown }) {
  const { t, i18n } = useTranslation();
  const definitions = useCustomFieldDefinitions(entityType);
  const given = asCustomFieldValues(values);
  const list = new Intl.ListFormat(i18n.language, { style: "narrow", type: "conjunction" });
  const shown = (definitions.data ?? []).filter((d) => given[d.key] !== undefined && given[d.key] !== null);
  if (shown.length === 0) {
    return null;
  }
  return (
    <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-3" data-testid="custom-field-values">
      {shown.map((definition) => (
        <div key={definition.id}>
          <dt className="text-xs text-fg-muted">{localized(definition.label)}</dt>
          <dd dir="auto" data-testid={`cf-value-${definition.key}`}>{display(definition, given[definition.key], t("common.yes"), t("common.no"), list)}</dd>
        </div>
      ))}
    </dl>
  );
}

