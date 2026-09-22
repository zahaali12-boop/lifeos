import { composeStories } from "@storybook/react";
import { render } from "@testing-library/react";
import type { Meta, StoryObj } from "@storybook/react";
import { axe } from "vitest-axe";

/**
 * axe on every story, in LTR and RTL: a component that ships must have no WCAG 2.2 AA violation in either direction.
 * Stories are discovered from the same glob Storybook uses, so a new story is covered the moment it exists.
 */
const modules = import.meta.glob<Record<string, unknown> & { default: Meta }>("./components/**/*.stories.tsx", { eager: true });

const cases = Object.entries(modules).flatMap(([file, module]) => {
  const stories = composeStories(module as unknown as { default: Meta } & Record<string, StoryObj>);
  return Object.entries(stories).map(([name, Story]) => ({ id: `${file.replace("./components/", "")} › ${name}`, Story }));
});

describe.each(["ltr", "rtl"] as const)("stories have no accessibility violations (%s)", (direction) => {
  beforeEach(() => {
    document.documentElement.setAttribute("dir", direction);
    document.documentElement.setAttribute("lang", direction === "rtl" ? "ar" : "en");
  });

  it.each(cases)("$id", async ({ Story }) => {
    // Components are rendered inside a landmark, as a page would; the region rule is about pages, not components.
    const { container, unmount } = render(
      <main>
        <Story />
      </main>,
    );
    try {
      const results = await axe(document.body.contains(container) ? document.body : container, {
        // jsdom has no layout, so contrast is checked in Storybook's a11y panel and by design in the tokens; "region" is a
        // page-level rule (portals render outside the landmark) and is checked by the Playwright journeys instead.
        rules: { "color-contrast": { enabled: false }, region: { enabled: false } },
      });
      const violations = results.violations.map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(" ")).join(", ")})`);
      expect(violations).toEqual([]);
    } finally {
      unmount();
    }
  });
});
