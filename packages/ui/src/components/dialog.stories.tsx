import type { Meta, StoryObj } from "@storybook/react";
import { Button } from "./button";
import { Dialog, DialogClose, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from "./dialog";

const meta = {
  title: "Overlays/Dialog",
  component: DialogContent,
  args: { closeLabel: "Close" },
  render: (args) => (
    <Dialog defaultOpen>
      <DialogTrigger asChild>
        <Button variant="secondary">Reopen period</Button>
      </DialogTrigger>
      <DialogContent {...args}>
        <DialogHeader>
          <DialogTitle className="text-lg font-semibold">Reopen September 2026?</DialogTitle>
          <DialogDescription className="text-sm text-fg-muted">The period is soft-closed. Reopening is recorded in the audit trail with your reason.</DialogDescription>
        </DialogHeader>
        <p className="text-sm">Modules: GL, AR. Reason required.</p>
        <DialogFooter>
          <DialogClose asChild>
            <Button variant="secondary">Cancel</Button>
          </DialogClose>
          <Button>Reopen</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  ),
} satisfies Meta<typeof DialogContent>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Open: Story = {};

export const Arabic: Story = { args: { closeLabel: "إغلاق" }, globals: { direction: "rtl" } };
