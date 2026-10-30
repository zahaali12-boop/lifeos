using Quicker.Kernel.Results;
using Quicker.Receivables.Contracts;

namespace Quicker.Receivables.Application;

/// <summary>The module's contract: the open-item bookkeeping of <see cref="ReceivablesService"/> and the applications of <see cref="SettlementService"/> behind one interface.</summary>
internal sealed class ReceivablesFacade(ReceivablesService items, SettlementService settlements) : IReceivables
{
    public Task<ReceivableInfo> OpenAsync(NewReceivable item, CancellationToken cancellationToken = default) => items.OpenAsync(item, cancellationToken);

    public Task<ReceivableInfo?> FindAsync(Guid itemId, CancellationToken cancellationToken = default) => items.FindAsync(itemId, cancellationToken);

    public Task<IReadOnlyList<ReceivableInfo>> ItemsOfAsync(string documentType, Guid documentId, CancellationToken cancellationToken = default) => items.ItemsOfAsync(documentType, documentId, cancellationToken);

    public Task<Result<IReadOnlyList<ReceivableInfo>>> ReverseDocumentAsync(string documentType, Guid documentId, DateOnly reversalDate, CancellationToken cancellationToken = default) => items.ReverseDocumentAsync(documentType, documentId, reversalDate, cancellationToken);

    public Task<Result<ReceivableSettlementInfo>> RecordAsync(RecordReceivableSettlementRequest request, CancellationToken cancellationToken = default) => items.RecordAsync(request, cancellationToken);

    public Task<Result<ReceivableSettlementInfo>> ApplyAsync(ApplyReceivableRequest request, CancellationToken cancellationToken = default) => settlements.ApplyAsync(request, cancellationToken);

    public Task<Result> ReverseSettlementsOfAsync(Guid journalEntryId, DateOnly reversalDate, Guid reversalEntryId, string reason, CancellationToken cancellationToken = default) => items.ReverseSettlementsOfAsync(journalEntryId, reversalDate, reversalEntryId, reason, cancellationToken);

    public Task<decimal> OpenBalanceAsync(Guid companyId, Guid partnerId, string currency, CancellationToken cancellationToken = default) => items.OpenBalanceAsync(companyId, partnerId, currency, cancellationToken);
}
