using Quicker.Collaboration.Contracts;
using Quicker.Persistence;

namespace Quicker.Pos.Application;

/// <summary>The company of registers, sessions and orders, so what hangs off them follows the member's company scopes.</summary>
internal sealed class PosRecordCompanies(IUnitOfWorkAccessor unitOfWork) : IRecordCompanies
{
    private static readonly Dictionary<string, string> Tables = new(StringComparer.Ordinal)
    {
        [RegisterService.AuditEntity] = "app.pos_registers",
        [SessionService.AuditEntity] = "app.pos_sessions",
        [OrderService.AuditEntity] = "app.pos_orders",
    };

    public IReadOnlyCollection<string> EntityTypes => Tables.Keys;

    public Task<IReadOnlyDictionary<Guid, Guid>> CompaniesAsync(string entityType, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        unitOfWork.CompaniesAsync(Tables[entityType], ids, cancellationToken);
}
