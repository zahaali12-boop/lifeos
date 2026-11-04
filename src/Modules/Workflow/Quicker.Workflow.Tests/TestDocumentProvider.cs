using System.Collections.Concurrent;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Workflow.Contracts;

namespace Quicker.Workflow.Tests;

/// <summary>A test document kept in memory: what a module's provider looks like to the engine, without a module.</summary>
public sealed class TestDocumentProvider : IWorkflowSubjectProvider
{
    public const string Type = "test_document";

    public static readonly ConcurrentDictionary<Guid, TestDocument> Documents = new();

    public static readonly ConcurrentDictionary<Guid, List<WorkflowDecision>> Decisions = new();

    public string EntityType => Type;

    public LocalizedText Label { get; } = LocalizedText.Bilingual("Test document", "مستند اختبار");

    public IReadOnlyList<WorkflowField> Fields { get; } =
    [
        new("amount", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Amount", "المبلغ")),
        new("currency", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Currency", "العملة")),
        new("category", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Category", "الفئة")),
        new("urgent", WorkflowFieldTypes.Boolean, LocalizedText.Bilingual("Urgent", "عاجل")),
        new("dueDate", WorkflowFieldTypes.Date, LocalizedText.Bilingual("Due date", "تاريخ الاستحقاق")),
        new("tags", WorkflowFieldTypes.List, LocalizedText.Bilingual("Tags", "الوسوم")),
    ];

    public IReadOnlyList<string> BlockKinds { get; } = ["credit_limit", "price_floor"];

    public Task<WorkflowSubject?> LoadAsync(Guid entityId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Documents.TryGetValue(entityId, out var document) ? document.ToSubject() : null);

    public Task<Result> OnDecidedAsync(WorkflowDecision decision, CancellationToken cancellationToken = default)
    {
        Decisions.GetOrAdd(decision.EntityId, static _ => []).Add(decision);
        if (decision.Status == WorkflowDecisions.Approved && Documents.TryGetValue(decision.EntityId, out var document) && document.FailOnDecision)
        {
            return Task.FromResult<Result>(Error.Conflict("test_document.cannot_post", "The books refuse this document.").WithWhy(("reason", "test")));
        }

        return Task.FromResult(Result.Success());
    }
}

public sealed record TestDocument(Guid Id, Guid CompanyId, decimal Amount, string Currency, string Category = "general", bool Urgent = false, DateOnly? DueDate = null, IReadOnlyList<string>? Tags = null, bool FailOnDecision = false)
{
    public WorkflowSubject ToSubject() => new(TestDocumentProvider.Type, Id, CompanyId, $"DOC-{Id.ToString("N")[..6].ToUpperInvariant()}", new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["amount"] = Amount,
        ["currency"] = Currency,
        ["category"] = Category,
        ["urgent"] = Urgent,
        ["dueDate"] = DueDate,
        ["tags"] = Tags ?? [],
    });
}
