import type { Meta, StoryObj } from "@storybook/react";
import { Field, useFieldControl } from "./field";
import { Input, Select, Textarea } from "./input";

function BoundInput(props: React.ComponentProps<typeof Input>) {
  const control = useFieldControl();
  return <Input {...control} {...props} />;
}

function BoundSelect(props: React.ComponentProps<typeof Select>) {
  const control = useFieldControl();
  return <Select {...control} {...props} />;
}

function BoundTextarea(props: React.ComponentProps<typeof Textarea>) {
  const control = useFieldControl();
  return <Textarea {...control} {...props} />;
}

const meta = {
  title: "Forms/Field",
  component: Field,
  args: { label: "Legal name", required: true, children: null },
  render: (args) => (
    <div className="max-w-sm">
      <Field {...args}>
        <BoundInput placeholder="Al-Rafidain Trading Co." />
      </Field>
    </div>
  ),
} satisfies Meta<typeof Field>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Default: Story = {};

export const WithDescription: Story = { args: { description: "As registered with the chamber of commerce." } };

export const WithServerError: Story = { args: { error: "A company with this code already exists." } };

export const SelectField: Story = {
  args: { label: "Functional currency", required: true, description: "Cannot change once the company exists." },
  render: (args) => (
    <div className="max-w-sm">
      <Field {...args}>
        <BoundSelect defaultValue="IQD">
          <option value="IQD">IQD — Iraqi dinar</option>
          <option value="USD">USD — US dollar</option>
          <option value="AED">AED — UAE dirham</option>
        </BoundSelect>
      </Field>
    </div>
  ),
};

export const TextareaField: Story = {
  args: { label: "Notes", required: false },
  render: (args) => (
    <div className="max-w-sm">
      <Field {...args}>
        <BoundTextarea placeholder="Anything the team should know" />
      </Field>
    </div>
  ),
};

export const ArabicMixedContent: Story = {
  args: { label: "الاسم القانوني", required: true, description: "يكتشف الحقل اتجاه النص تلقائياً" },
  globals: { direction: "rtl" },
  render: (args) => (
    <div className="max-w-sm">
      <Field {...args}>
        <BoundInput defaultValue="شركة الرافدين Trading" />
      </Field>
    </div>
  ),
};
