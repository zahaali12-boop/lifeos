import type { StorybookConfig } from "@storybook/react-vite";

const config: StorybookConfig = {
  framework: "@storybook/react-vite",
  stories: ["../src/**/*.stories.@(ts|tsx)"],
  addons: ["@storybook/addon-docs", "@storybook/addon-a11y"],
  async viteFinal(baseConfig) {
    const { default: tailwindcss } = await import("@tailwindcss/vite");
    return { ...baseConfig, plugins: [...(baseConfig.plugins ?? []), tailwindcss()] };
  },
};

export default config;
