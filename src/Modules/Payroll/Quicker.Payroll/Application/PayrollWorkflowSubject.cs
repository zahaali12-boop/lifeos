using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Payroll.Contracts;
using Quicker.Workflow.Contracts;

namespace Quicker.Payroll.Application;

/// <summary>Payroll runs as the workflow engine sees them (ADR-0020, A-181): approval by the net, the gross, the employer's cost or the head count before the run is posted.</summary>
public sealed class PayrollWorkflowSubject(RunService runs) : IWorkflowSubjectProvider
{
    public string EntityType => PayrollDocumentTypes.Run;

    public LocalizedText Label { get; } = LocalizedText.Bilingual("Payroll run", "مسير رواتب");

    public IReadOnlyList<WorkflowField> Fields { get; } =
    [
        new("amount", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Net pay of the run", "صافي رواتب المسير")),
        new("gross", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Gross pay of the run", "إجمالي رواتب المسير")),
        new("employerCost", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Gross plus employer contributions", "الإجمالي مع مساهمات صاحب العمل")),
        new("currency", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Currency", "العملة")),
        new("employees", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Number of employees", "عدد الموظفين")),
        new("period", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Period (yyyy-mm)", "الفترة (سنة-شهر)")),
        new("payDate", WorkflowFieldTypes.Date, LocalizedText.Bilingual("Pay date", "تاريخ الدفع")),
    ];

    public IReadOnlyList<string> BlockKinds { get; } = [];

    public async Task<WorkflowSubject?> LoadAsync(Guid entityId, CancellationToken cancellationToken = default)
    {
        var run = await runs.LoadAsync(entityId, cancellationToken);
        return run is null ? null : await runs.SubjectAsync(run, cancellationToken);
    }

    public Task<Result> OnDecidedAsync(WorkflowDecision decision, CancellationToken cancellationToken = default) => runs.DecideAsync(decision, cancellationToken);
}
