using Dapper;
using Quicker.Onboarding.Contracts;
using Quicker.Persistence;

namespace Quicker.Onboarding.Application;

/// <summary>Counts what a new tenant has set up so far, read straight from the modules' tables under row-level security.</summary>
public sealed class SetupStatusService(IUnitOfWorkAccessor unitOfWork)
{
    public async Task<SetupStatus> ReadAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var row = await uow.Connection.QuerySingleAsync<Row>(new CommandDefinition("""
            WITH chosen AS (
                SELECT id, chart_id FROM app.org_companies
                WHERE (@company::uuid IS NULL OR id = @company::uuid)
                ORDER BY CASE WHEN id = @company::uuid THEN 0 ELSE 1 END, created_at
                LIMIT 1)
            SELECT (SELECT id FROM chosen) AS CompanyId,
                   (SELECT count(*) FROM app.org_companies)::int AS Companies,
                   COALESCE((SELECT chart_id IS NOT NULL FROM chosen), false) AS HasChart,
                   (SELECT count(*) FROM app.tax_regimes)::int AS TaxRegimes,
                   (SELECT count(*) FROM app.ptr_partners WHERE is_customer)::int AS Customers,
                   (SELECT count(*) FROM app.ptr_partners WHERE is_supplier)::int AS Suppliers,
                   (SELECT count(*) FROM app.itm_items)::int AS Items,
                   (SELECT count(*) FROM app.gl_manual_journals j WHERE j.kind = 'opening' AND j.status = 'posted' AND j.company_id = (SELECT id FROM chosen))::int AS OpeningJournals
            """, new { company = companyId }, uow.Transaction, cancellationToken: cancellationToken));
        return new SetupStatus(row.CompanyId, row.Companies, row.HasChart, row.TaxRegimes, row.Customers, row.Suppliers, row.Items, row.OpeningJournals);
    }

    private sealed class Row
    {
        public Guid? CompanyId { get; init; }
        public int Companies { get; init; }
        public bool HasChart { get; init; }
        public int TaxRegimes { get; init; }
        public int Customers { get; init; }
        public int Suppliers { get; init; }
        public int Items { get; init; }
        public int OpeningJournals { get; init; }
    }
}
