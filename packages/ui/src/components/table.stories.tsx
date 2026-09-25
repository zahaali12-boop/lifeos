import type { Meta, StoryObj } from "@storybook/react";
import { Badge } from "./badge";
import { Checkbox } from "./checkbox";
import { EmptyState } from "./empty-state";
import { Button } from "./button";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "./table";

const rows = [
  { code: "SI-2026-000118", partner: "Al-Rafidain Trading", total: "1,250,000.00 IQD", status: "Posted", tone: "success" as const },
  { code: "SI-2026-000119", partner: "Basra Steel", total: "18,400.00 USD", status: "Draft", tone: "neutral" as const },
  { code: "SI-2026-000120", partner: "Erbil Foods", total: "3,900,000.00 IQD", status: "Submitted", tone: "accent" as const },
];

const meta = {
  title: "Data display/Table",
  component: Table,
  render: () => (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead className="w-10">
            <Checkbox aria-label="Select all" checked="indeterminate" />
          </TableHead>
          <TableHead>Number</TableHead>
          <TableHead>Partner</TableHead>
          <TableHead className="text-end">Total</TableHead>
          <TableHead>Status</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((row, index) => (
          <TableRow key={row.code} data-state={index === 0 ? "selected" : undefined}>
            <TableCell>
              <Checkbox aria-label={`Select ${row.code}`} checked={index === 0} />
            </TableCell>
            <TableCell className="font-medium">{row.code}</TableCell>
            <TableCell>{row.partner}</TableCell>
            <TableNumberCell>{row.total}</TableNumberCell>
            <TableCell>
              <Badge tone={row.tone}>{row.status}</Badge>
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  ),
} satisfies Meta<typeof Table>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Invoices: Story = {};

export const Arabic: Story = { globals: { direction: "rtl" } };

export const Empty: Story = {
  render: () => <EmptyState title="No invoices yet" description="Invoices you issue appear here with their status and totals." action={<Button>New invoice</Button>} />,
};
