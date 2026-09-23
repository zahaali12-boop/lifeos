import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { Field, Input, Select, Textarea, useFieldControl } from "@quicker/ui";
export function PageHeader({ title, description, actions }) {
    return (_jsxs("div", { className: "mb-4 flex flex-wrap items-start justify-between gap-3", children: [_jsxs("div", { children: [_jsx("h1", { className: "text-xl font-semibold tracking-tight", children: title }), description ? _jsx("p", { className: "mt-1 text-sm text-fg-muted", children: description }) : null] }), actions ? _jsx("div", { className: "flex flex-wrap items-center gap-2", children: actions }) : null] }));
}
/** Inputs wired to the enclosing Field's ARIA attributes. */
export function TextField(props) {
    const control = useFieldControl();
    return _jsx(Input, { ...control, ...props });
}
export function SelectField(props) {
    const control = useFieldControl();
    return _jsx(Select, { ...control, ...props });
}
export function TextareaField(props) {
    const control = useFieldControl();
    return _jsx(Textarea, { ...control, ...props });
}
export function FormError({ message }) {
    return message ? (_jsx("p", { role: "alert", className: "rounded-md border border-danger/40 bg-danger-soft px-3 py-2 text-sm text-danger", children: message })) : null;
}
export { Field };
