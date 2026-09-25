using Dapper;

namespace Quicker.Persistence;

/// <summary>Reads the company of records from a module's own table (one with <c>id</c> and <c>company_id</c> columns), inside the current unit of work and its row-level security.</summary>
public static class RecordCompanyQuery
{
    /// <summary>The company of each record found; the table is a constant name of the calling module, such as <c>app.pur_orders</c>, never user input.</summary>
    public static async Task<IReadOnlyDictionary<Guid, Guid>> CompaniesAsync(this IUnitOfWorkAccessor unitOfWork, string table, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, Guid>();
        }

        var uow = unitOfWork.Current;
        var rows = await uow.Connection.QueryAsync<(Guid Id, Guid CompanyId)>(new CommandDefinition(
            $"SELECT id, company_id FROM {table} WHERE id = ANY(@ids)", new { ids = ids.Distinct().ToArray() }, uow.Transaction, cancellationToken: cancellationToken));
        return rows.ToDictionary(static r => r.Id, static r => r.CompanyId);
    }
}
