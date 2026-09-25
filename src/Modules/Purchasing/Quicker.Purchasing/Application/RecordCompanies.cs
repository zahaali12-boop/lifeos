using Quicker.Collaboration.Contracts;
using Quicker.Persistence;
using Quicker.Purchasing.Contracts;

namespace Quicker.Purchasing.Application;

/// <summary>The company of purchasing documents, so what hangs off them follows the member's company scopes.</summary>
internal sealed class PurchasingRecordCompanies(IUnitOfWorkAccessor unitOfWork) : IRecordCompanies
{
    private static readonly Dictionary<string, string> Tables = new(StringComparer.Ordinal)
    {
        [PurchaseDocumentTypes.Requisition] = "app.pur_requisitions",
        [PurchaseDocumentTypes.Rfq] = "app.pur_rfqs",
        [PurchaseDocumentTypes.BlanketAgreement] = "app.pur_blanket_agreements",
        [PurchaseDocumentTypes.Order] = "app.pur_orders",
        [PurchaseDocumentTypes.Receipt] = "app.pur_receipts",
        [PurchaseDocumentTypes.Invoice] = "app.pur_invoices",
        [PurchaseDocumentTypes.LandedCost] = "app.pur_landed_cost_docs",
        [PurchaseDocumentTypes.Return] = "app.pur_returns",
    };

    public IReadOnlyCollection<string> EntityTypes => Tables.Keys;

    public Task<IReadOnlyDictionary<Guid, Guid>> CompaniesAsync(string entityType, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        unitOfWork.CompaniesAsync(Tables[entityType], ids, cancellationToken);
}
