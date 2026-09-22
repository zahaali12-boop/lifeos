import js from "@eslint/js";
import reactHooks from "eslint-plugin-react-hooks";
import globals from "globals";
import tseslint from "typescript-eslint";

export default tseslint.config(
  { ignores: ["dist", "coverage", "src/api/schema.d.ts"] },
  js.configs.recommended,
  ...tseslint.configs.strictTypeChecked,
  ...tseslint.configs.stylisticTypeChecked,
  {
    files: ["**/*.{ts,tsx}"],
    languageOptions: {
      parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname },
      globals: { ...globals.browser },
    },
    plugins: { "react-hooks": reactHooks },
    rules: {
      ...reactHooks.configs.recommended.rules,
      "@typescript-eslint/no-unnecessary-condition": "error",
      "@typescript-eslint/consistent-type-imports": "error",
      // The app talks to the API only through the generated client (src/api): no ad-hoc fetch calls.
      "no-restricted-globals": ["error", { name: "fetch", message: "Use the typed client from src/api/client.ts." }],
    },
  },
  {
    files: ["src/api/**/*.ts", "**/*.test.{ts,tsx}", "src/setupTests.ts"],
    rules: { "no-restricted-globals": "off" },
  },
  {
    files: ["**/*.test.{ts,tsx}", "src/setupTests.ts"],
    languageOptions: { globals: { ...globals.node } },
  },
  {
    files: ["**/*.{js,mjs,cjs}"],
    ...tseslint.configs.disableTypeChecked,
    languageOptions: { globals: { ...globals.node } },
  },
);
