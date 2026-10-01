using Microsoft.EntityFrameworkCore;
using Quicker.Inventory.Persistence;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Workflow.Contracts;

namespace Quicker.Inventory.Application;

/// <summary>Stock adjustments as the workflow engine sees them (ADR-0020): the fields a rule may test, and what happens when a request is decided.</summary>
public sealed class AdjustmentWorkflowSubject(InventoryDbContext db, AdjustmentService adjustments) : IWorkflowSubjectProvider
{
    public string EntityType => AdjustmentService.DocumentType;

    public LocalizedText Label { get; } = LocalizedText.Bilingual("Stock adjustment", "تسوية مخزون");

    public IReadOnlyList<WorkflowField> Fields { get; } =
    [
        new("kind", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Kind (positive, negative, scrap, opening)", "النوع (زيادة، نقص، تالف، افتتاحي)")),
        new("warehouseCode", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Warehouse code", "رمز المستودع")),
        new("lineCount", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Number of lines", "عدد البنود")),
        new("quantity", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Total quantity (base units)", "إجمالي الكمية (بالوحدة الأساسية)")),
        new("amount", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Amount at cost", "المبلغ بالتكلفة")),
        new("currency", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Currency", "العملة")),
        new("reference", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Reference", "المرجع")),
        new("postingDate", WorkflowFieldTypes.Date, LocalizedText.Bilingual("Posting date", "تاريخ الترحيل")),
    ];

    public IReadOnlyList<string> BlockKinds { get; } = [];

    public async Task<WorkflowSubject?> LoadAsync(Guid entityId, CancellationToken cancellationToken = default)
    {
        var adjustment = await db.Adjustments.AsNoTracking().Include(static a => a.Lines).SingleOrDefaultAsync(a => a.Id == entityId, cancellationToken);
        return adjustment is null ? null : await adjustments.SubjectAsync(adjustment, cancellationToken);
    }

    public Task<Result> OnDecidedAsync(WorkflowDecision decision, CancellationToken cancellationToken = default) => adjustments.DecideAsync(decision, cancellationToken);
}
