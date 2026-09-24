import { isApiProblem, type ApiProblem } from "../api/client";

/**
 * Maps the API's problem details to form state: `why.field` names a custom field, and codes of the shape
 * `<entity>.<field>_<reason>` (company.code_taken, webhook.url_invalid) name a form field. Anything else is a
 * form-level message. The message shown is the server's `detail`, which is already worded for people.
 */
export interface FormProblem {
  message: string;
  code?: string;
  fields: Record<string, string>;
}

export function toFormProblem(error: unknown, fallback: string): FormProblem {
  if (!isApiProblem(error)) {
    return { message: fallback, fields: {} };
  }
  const problem: ApiProblem = error;
  const message = problem.detail ?? problem.title ?? fallback;
  const fields: Record<string, string> = {};
  const whyField = problem.why?.field;
  if (typeof whyField === "string") {
    fields[`customFields.${whyField}`] = message;
  } else if (problem.code) {
    const [, rest] = problem.code.split(".", 2);
    const match = rest ? /^([a-z0-9]+(?:_[a-z0-9]+)*?)_(taken|invalid|required|unknown|locked|missing|blocked|too_long|too_short)$/.exec(rest) : null;
    if (match?.[1]) {
      fields[snakeToCamel(match[1])] = message;
    }
  }
  const result: FormProblem = { message, fields };
  if (problem.code) {
    result.code = problem.code;
  }
  return result;
}

function snakeToCamel(value: string): string {
  return value.replace(/_([a-z0-9])/g, (_, c: string) => c.toUpperCase());
}
