import type { Meta, StoryObj } from "@storybook/react";
import { Badge } from "./badge";

const meta = {
  title: "Data display/Badge",
  component: Badge,
  args: { children: "Posted", tone: "success" },
  argTypes: { tone: { control: "select", options: ["neutral", "accent", "success", "warning", "danger", "info"] } },
} satisfies Meta<typeof Badge>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Success: Story = {};

export const AllTones: Story = {
  render: () => (
    <div className="flex flex-wrap gap-2">
      <Badge tone="neutral">Draft</Badge>
      <Badge tone="accent">Submitted</Badge>
      <Badge tone="success">Posted</Badge>
      <Badge tone="warning">Soft-closed</Badge>
      <Badge tone="danger">Dead letter</Badge>
      <Badge tone="info">Scheduled</Badge>
    </div>
  ),
};

export const Arabic: Story = { args: { children: "مرحّل" }, globals: { direction: "rtl" } };
