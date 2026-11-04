/**
 * RTL by construction (ADR-0013): physical properties (margin-left, padding-right, left, text-align: left…) are
 * errors; logical ones (margin-inline-start, inset-inline-end, text-align: start) mirror automatically under dir="rtl".
 */
export default {
  extends: ["stylelint-config-standard"],
  plugins: ["stylelint-use-logical"],
  rules: {
    "csstools/use-logical": ["always", { severity: "error" }],
    "at-rule-no-unknown": [true, { ignoreAtRules: ["theme", "layer", "utility", "variant", "custom-variant", "apply", "source", "import", "plugin", "config"] }],
    "import-notation": null,
    "selector-class-pattern": null,
    "custom-property-pattern": null,
    "color-function-notation": null,
    "alpha-value-notation": null,
    "declaration-block-no-redundant-longhand-properties": null,
  },
};
