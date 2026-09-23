import { isApiProblem } from "../api/client";
export function toFormProblem(error, fallback) {
    if (!isApiProblem(error)) {
        return { message: fallback, fields: {} };
    }
    const problem = error;
    const message = problem.detail ?? problem.title ?? fallback;
    const fields = {};
    const whyField = problem.why?.field;
    if (typeof whyField === "string") {
        fields[`customFields.${whyField}`] = message;
    }
    else if (problem.code) {
        const [, rest] = problem.code.split(".", 2);
        const match = rest ? /^([a-z0-9]+(?:_[a-z0-9]+)*?)_(taken|invalid|required|unknown|locked|missing|too_long|too_short)$/.exec(rest) : null;
        if (match?.[1]) {
            fields[snakeToCamel(match[1])] = message;
        }
    }
    const result = { message, fields };
    if (problem.code) {
        result.code = problem.code;
    }
    return result;
}
function snakeToCamel(value) {
    return value.replace(/_([a-z0-9])/g, (_, c) => c.toUpperCase());
}
