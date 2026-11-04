using Microsoft.EntityFrameworkCore;
using Quicker.Kernel.Results;
using Quicker.Partners.Persistence;
using Quicker.Persistence;

namespace Quicker.Partners.Application;

/// <summary>One partner of an import: the partner itself and, for the company the import names, its account's currency, terms, limit and groups by code.</summary>
public sealed record PartnerImportRow(SavePartnerRequest Partner, string? Currency = null, string? PaymentTerms = null, decimal? CreditLimit = null, string? CustomerGroup = null, string? SupplierGroup = null);

public sealed record PartnerImportRequest(IReadOnlyList<PartnerImportRow> Rows);

/// <summary>What an import did, or would do when <see cref="DryRun"/>: partners created and updated, company accounts kept, rows in all.</summary>
public sealed record PartnerImportResult(int Created, int Updated, int Accounts, int Total, bool DryRun = false);

/// <summary>
/// Customers and suppliers from a spreadsheet (setup wizard, A-173): upsert by code, all or nothing, the first
/// failing row named; with a company, each customer row keeps a customer account and each supplier row a supplier
/// account in it (currency, payment terms, credit limit and group by code). A dry run does the same work and rolls
/// it back, so the file is checked as a whole before it is committed.
/// </summary>
public sealed class PartnerImportService(PartnersDbContext db, PartnerService partners, CustomerService customers, SupplierService suppliers, IUnitOfWorkAccessor unitOfWork)
{
    public async Task<Result<PartnerImportResult>> ImportAsync(Guid? companyId, IReadOnlyList<PartnerImportRow> rows, bool dryRun, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return Error.Validation("import.empty", "Nothing to import.");
        }

        const string savepoint = "partner_import";
        var transaction = unitOfWork.Current.Transaction;
        if (dryRun)
        {
            await transaction.SaveAsync(savepoint, cancellationToken);
        }

        var result = await UpsertAllAsync(companyId, rows, cancellationToken);
        if (dryRun)
        {
            await transaction.RollbackAsync(savepoint, cancellationToken);
            db.ChangeTracker.Clear();
            return result.IsSuccess ? result.Value with { DryRun = true } : result;
        }

        return result;
    }

    private async Task<Result<PartnerImportResult>> UpsertAllAsync(Guid? companyId, IReadOnlyList<PartnerImportRow> rows, CancellationToken cancellationToken)
    {
        var created = 0;
        var updated = 0;
        var accounts = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var code = row.Partner.Code?.Trim() ?? string.Empty;
            if (!seen.Add(code))
            {
                return Error.Validation("import.duplicate_code", "The same code appears twice in the import.").WithWhy(("row", i + 1), ("code", code));
            }

            if (!row.Partner.IsCustomer && !row.Partner.IsSupplier)
            {
                return Error.Validation("import.role_required", $"Row {i + 1}: a partner is a customer, a supplier or both (columns customer and supplier).").WithWhy(("row", i + 1), ("code", code));
            }

            var upper = code.ToUpperInvariant();
            var existing = await db.Partners.AsNoTracking().SingleOrDefaultAsync(p => p.Code == upper, cancellationToken);
            var saved = existing is null ? await partners.CreateAsync(row.Partner, cancellationToken) : await partners.UpdateAsync(existing.Id, row.Partner, cancellationToken);
            if (saved.IsFailure)
            {
                return saved.Error!.WithWhy(("row", i + 1), ("code", code));
            }

            if (existing is null)
            {
                created++;
            }
            else
            {
                updated++;
            }

            if (companyId is not { } company)
            {
                continue;
            }

            var terms = await LookupAsync(row.PaymentTerms, "payment_terms", i + 1, code => db.PaymentTerms.Where(t => t.Code == code).Select(static t => (Guid?)t.Id).FirstOrDefaultAsync(cancellationToken));
            if (terms.IsFailure)
            {
                return terms.Error!;
            }

            if (row.Partner.IsCustomer)
            {
                var group = await LookupAsync(row.CustomerGroup, "customer_group", i + 1, code => db.CustomerGroups.Where(g => g.Code == code).Select(static g => (Guid?)g.Id).FirstOrDefaultAsync(cancellationToken));
                if (group.IsFailure)
                {
                    return group.Error!;
                }

                var account = await customers.SaveAccountAsync(saved.Value.Id, company, new SaveCustomerAccountRequest(group.Value, terms.Value, Currency: row.Currency, CreditLimit: row.CreditLimit), cancellationToken);
                if (account.IsFailure)
                {
                    return account.Error!.WithWhy(("row", i + 1), ("code", code));
                }

                accounts++;
            }

            if (row.Partner.IsSupplier)
            {
                var group = await LookupAsync(row.SupplierGroup, "supplier_group", i + 1, code => db.SupplierGroups.Where(g => g.Code == code).Select(static g => (Guid?)g.Id).FirstOrDefaultAsync(cancellationToken));
                if (group.IsFailure)
                {
                    return group.Error!;
                }

                var account = await suppliers.SaveAccountAsync(saved.Value.Id, company, new SaveSupplierAccountRequest(group.Value, terms.Value, Currency: row.Currency), cancellationToken);
                if (account.IsFailure)
                {
                    return account.Error!.WithWhy(("row", i + 1), ("code", code));
                }

                accounts++;
            }
        }

        return new PartnerImportResult(created, updated, accounts, rows.Count);
    }

    /// <summary>The id behind a code in a lookup column (codes are kept upper-case); null when the cell is empty, a failure naming the row when nothing has that code.</summary>
    private static async Task<Result<Guid?>> LookupAsync(string? code, string column, int row, Func<string, Task<Guid?>> find)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return (Guid?)null;
        }

        var found = await find(code.Trim().ToUpperInvariant());
        return found is not null
            ? found
            : Error.Validation("import.lookup_unknown", $"Row {row}: no {column} has the code '{code}'.").WithWhy(("row", row), ("column", column), ("value", code));
    }
}
