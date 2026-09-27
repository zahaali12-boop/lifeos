using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Workflow.Contracts;

namespace Quicker.Sales.Application;

/// <summary>Sales orders as the workflow engine sees them: the credit-limit block of confirmation (ADR-0020, hard scenario 6).</summary>
public sealed class SalesOrderWorkflowSubject(OrderService orders) : IWorkflowSubjectProvider
{
    public string EntityType => OrderService.DocumentType;

    public LocalizedText Label { get; } = LocalizedText.Bilingual("Sales order", "أمر بيع");

    public IReadOnlyList<WorkflowField> Fields { get; } =
    [
        new("amount", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Order total", "إجمالي الأمر")),
        new("currency", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Currency", "العملة")),
        new("customerCode", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Customer code", "رمز العميل")),
        new("blockKind", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Block kind", "نوع الإيقاف")),
        new("lineCount", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Number of lines", "عدد البنود")),
    ];

    public IReadOnlyList<string> BlockKinds { get; } = [OrderService.CreditLimitBlockKind];

    public async Task<WorkflowSubject?> LoadAsync(Guid entityId, CancellationToken cancellationToken = default)
    {
        var order = await orders.LoadAsync(entityId, cancellationToken);
        return order is null ? null : await orders.SubjectAsync(order, cancellationToken);
    }

    public Task<Result> OnDecidedAsync(WorkflowDecision decision, CancellationToken cancellationToken = default) => orders.DecideAsync(decision, cancellationToken);
}
