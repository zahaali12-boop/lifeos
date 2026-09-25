import type { Meta, StoryObj } from "@storybook/react";
import { Plus } from "lucide-react";
import { Button } from "./button";

const meta = {
  title: "Actions/Button",
  component: Button,
  args: { children: "Save company", variant: "primary", size: "md" },
  argTypes: {
    variant: { control: "select", options: ["primary", "secondary", "ghost", "danger", "link"] },
    size: { control: "select", options: ["sm", "md", "lg", "icon"] },
  },
} satisfies Meta<typeof Button>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Primary: Story = {};

export const Secondary: Story = { args: { variant: "secondary", children: "Cancel" } };

export const Danger: Story = { args: { variant: "danger", children: "Delete branch" } };

export const Loading: Story = { args: { loading: true, children: "Posting…" } };

export const WithIcon: Story = {
  args: { children: "New company" },
  render: (args) => (
    <Button {...args}>
      <Plus aria-hidden="true" />
      {args.children}
    </Button>
  ),
};

export const IconOnly: Story = {
  args: { size: "icon", variant: "ghost", "aria-label": "Add" },
  render: (args) => (
    <Button {...args}>
      <Plus aria-hidden="true" />
    </Button>
  ),
};

export const Arabic: Story = { args: { children: "حفظ الشركة" }, globals: { direction: "rtl" } };
