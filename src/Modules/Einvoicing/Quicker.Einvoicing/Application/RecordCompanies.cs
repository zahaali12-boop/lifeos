using Quicker.Collaboration.Contracts;
using Quicker.Persistence;

namespace Quicker.Einvoicing.Application;

/// <summary>The company of e-invoicing devices and submissions, so what hangs off them follows the member's company scopes.</summary>
internal sealed class EinvoicingRecordCompanies(IUnitOfWorkAccessor unitOfWork) : IRecordCompanies
{
    private static readonly Dictionary<string, string> Tables = new(StringComparer.Ordinal)
    {
        [DeviceService.AuditEntity] = "app.einv_devices",
        [SubmissionService.AuditEntity] = "app.einv_submissions",
    };

    public IReadOnlyCollection<string> EntityTypes => Tables.Keys;

    public Task<IReadOnlyDictionary<Guid, Guid>> CompaniesAsync(string entityType, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        unitOfWork.CompaniesAsync(Tables[entityType], ids, cancellationToken);
}
