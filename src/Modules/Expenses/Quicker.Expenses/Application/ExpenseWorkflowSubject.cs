using Quicker.Expenses.Contracts;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Workflow.Contracts;

namespace Quicker.Expenses.Application;

/// <summary>Expenses as the workflow engine sees them (ADR-0020): approval by amount, kind, category or payee before the accrual is posted.</summary>
public sealed class ExpenseWorkflowSubject(ExpenseService expenses) : IWorkflowSubjectProvider
{
    public string EntityType => ExpenseDocumentTypes.Expense;

    public LocalizedText Label { get; } = LocalizedText.Bilingual("Expense", "مصروف");

    public IReadOnlyList<WorkflowField> Fields { get; } =
    [
        new("amount", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Total including tax", "الإجمالي شاملاً الضريبة")),
        new("currency", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Currency", "العملة")),
        new("kind", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Kind (supplier, employee, petty_cash)", "النوع (مورد، موظف، صندوق نثري)")),
        new("partnerCode", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Supplier or employee code", "رمز المورد أو الموظف")),
        new("lineCount", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Number of lines", "عدد البنود")),
        new("categories", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Category codes, comma-separated", "رموز الفئات مفصولة بفواصل")),
        new("hasTax", WorkflowFieldTypes.Boolean, LocalizedText.Bilingual("Carries tax", "يحمل ضريبة")),
        new("expenseDate", WorkflowFieldTypes.Date, LocalizedText.Bilingual("Expense date", "تاريخ المصروف")),
    ];

    public IReadOnlyList<string> BlockKinds { get; } = [];

    public async Task<WorkflowSubject?> LoadAsync(Guid entityId, CancellationToken cancellationToken = default)
    {
        var expense = await expenses.LoadAsync(entityId, cancellationToken);
        return expense is null ? null : await expenses.SubjectAsync(expense, cancellationToken);
    }

    public Task<Result> OnDecidedAsync(WorkflowDecision decision, CancellationToken cancellationToken = default) => expenses.DecideAsync(decision, cancellationToken);
}
