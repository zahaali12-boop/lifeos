import type { Decorator, Preview } from "@storybook/react";
import { useEffect } from "react";
import "../src/styles/tokens.css";

function DirectionAndTheme({ direction, theme, children }: { direction: string; theme: string; children: React.ReactNode }) {
  useEffect(() => {
    document.documentElement.setAttribute("dir", direction);
    document.documentElement.setAttribute("lang", direction === "rtl" ? "ar" : "en");
    document.documentElement.setAttribute("data-theme", theme);
  }, [direction, theme]);
  return (
    <div dir={direction} data-theme={theme} className="min-h-40 bg-canvas p-6 text-fg">
      {children}
    </div>
  );
}

/** Every story renders in the direction and theme chosen in the toolbar; the defaults cover LTR light, RTL dark is one click. */
const withDirectionAndTheme: Decorator = (Story, context) => (
  <DirectionAndTheme direction={String(context.globals.direction ?? "ltr")} theme={String(context.globals.theme ?? "light")}>
    <Story />
  </DirectionAndTheme>
);

const preview: Preview = {
  decorators: [withDirectionAndTheme],
  globalTypes: {
    direction: {
      description: "Text direction",
      toolbar: { title: "Direction", icon: "transfer", items: [{ value: "ltr", title: "LTR (English)" }, { value: "rtl", title: "RTL (Arabic)" }], dynamicTitle: true },
    },
    theme: {
      description: "Colour theme",
      toolbar: { title: "Theme", icon: "paintbrush", items: [{ value: "light", title: "Light" }, { value: "dark", title: "Dark" }], dynamicTitle: true },
    },
  },
  initialGlobals: { direction: "ltr", theme: "light" },
  parameters: {
    a11y: { test: "error" },
    controls: { matchers: { color: /(background|color)$/i, date: /Date$/i } },
  },
};

export default preview;
