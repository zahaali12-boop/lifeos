using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Persistence;
using Quicker.Tax.Contracts;
using Quicker.Tax.Domain;
using Quicker.Tax.Persistence;

namespace Quicker.Tax.Application;

/// <summary>
/// The return computed from the tax ledger (ADR-0018): box figures by the codes' return-box mapping, reconciled to
/// the tax accounts of the general ledger (<see cref="ILedgerReader"/>), and filing, which locks the period. Filing
/// takes the exclusive lock on the company's registration that a posting only shares (<see cref="TaxLedgerService"/>),
/// so a return is never filed while a posting into its period is still in flight, and books the exact figures filed
/// so a later change to the tax ledger or the codes never rewrites a filed return.
/// </summary>
public sealed class TaxReturnService(TaxDbContext db, IUnitOfWorkAccessor unitOfWork, ICompanyDirectory companies, ILedgerReader ledger, TaxAccess access, ICurrentPrincipal principal, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result<TaxReturnPreview>> PreviewAsync(Guid companyId, Guid regimeId, DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken)
    {
        var allowed = access.Require(companyId, TaxPermissions.ReturnRead);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var scope = await ScopeAsync(companyId, regimeId, periodStart, periodEnd, cancellationToken);
        return scope.IsFailure ? scope.Error! : await ComputeAsync(scope.Value, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<TaxReturnDrillDownLine>>> DrillDownAsync(Guid companyId, Guid regimeId, DateOnly periodStart, DateOnly periodEnd, string boxCode, CancellationToken cancellationToken)
    {
        var allowed = access.Require(companyId, TaxPermissions.ReturnRead);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var scope = await ScopeAsync(companyId, regimeId, periodStart, periodEnd, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var s = scope.Value;
        var lines = new List<TaxReturnDrillDownLine>();
        foreach (var e in s.Entries)
        {
            var code = s.Codes[e.TaxCodeId];
            var (baseBox, taxBox) = BoxesOf(code, e.Direction);
            if (string.Equals(baseBox, boxCode, StringComparison.Ordinal) && e.BaseFc != 0m)
            {
                lines.Add(Line(e, code, "base"));
            }

            if (string.Equals(taxBox, boxCode, StringComparison.Ordinal) && e.TaxFc != 0m)
            {
                lines.Add(Line(e, code, "tax"));
            }
        }

        return lines.OrderBy(static l => l.PostingDate).ThenBy(static l => l.SourceDocumentNumber, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<TaxReturnPeriodSummary>> ListPeriodsAsync(Guid? companyId, Guid? regimeId, CancellationToken cancellationToken)
    {
        var reachable = access.CompaniesFor(TaxPermissions.ReturnRead);
        var query = db.ReturnPeriods.AsNoTracking().AsQueryable();
        if (reachable is { } ids)
        {
            query = query.Where(p => ids.Contains(p.CompanyId));
        }

        if (companyId is { } c)
        {
            query = query.Where(p => p.CompanyId == c);
        }

        if (regimeId is { } r)
        {
            query = query.Where(p => p.RegimeId == r);
        }

        var periods = await query.OrderByDescending(static p => p.PeriodStart).ToListAsync(cancellationToken);
        var regimes = await db.Regimes.AsNoTracking().ToDictionaryAsync(static r => r.Id, static r => r.Code, cancellationToken);
        return periods.Select(p => new TaxReturnPeriodSummary(p.Id, p.CompanyId, p.RegimeId, regimes.GetValueOrDefault(p.RegimeId) ?? string.Empty, p.PeriodStart, p.PeriodEnd, p.Status, p.FiledAt, p.FiledBy, p.Reference,
            p.Status == "filed" ? NetPayableOf(p.Totals) : null)).ToList();
    }

    /// <summary>Files the period: takes the exclusive lock the tax ledger's postings only share, refuses an unreconciled return, and books the figures filed.</summary>
    public async Task<Result<TaxReturnPeriodSummary>> FileAsync(FileTaxReturnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var allowed = access.Require(request.CompanyId, TaxPermissions.ReturnFile);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var scope = await ScopeAsync(request.CompanyId, request.RegimeId, request.PeriodStart, request.PeriodEnd, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        if (await db.ReturnPeriods.AnyAsync(p => p.CompanyId == request.CompanyId && p.RegimeId == request.RegimeId && p.Status == "filed"
                && p.PeriodStart <= request.PeriodEnd && p.PeriodEnd >= request.PeriodStart, cancellationToken))
        {
            return Error.Conflict("tax.period_overlap", "Part of this range is already filed as another period.");
        }

        // The exclusive lock the ledger's postings only share (FOR SHARE): filing waits for any posting in flight to
        // commit or roll back, and a posting into this period then waits for filing to finish before it can proceed.
        var uow = unitOfWork.Current;
        var registered = await uow.Connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM app.tax_registrations WHERE tenant_id = @tenant AND company_id = @company AND regime_id = @regime FOR UPDATE",
            new { tenant = uow.Context.TenantId.Value, company = request.CompanyId, regime = request.RegimeId },
            uow.Transaction,
            cancellationToken: cancellationToken));
        if (registered is null)
        {
            return Error.Validation("tax.company_not_registered", "The company is not registered in this regime.").WithWhy(("companyId", request.CompanyId), ("regimeId", request.RegimeId));
        }

        var preview = await ComputeAsync(scope.Value, cancellationToken);
        if (preview.IsFailure)
        {
            return preview.Error!;
        }

        if (!preview.Value.Reconciled)
        {
            return Error.Conflict("tax.return_not_reconciled", "The tax ledger and the tax accounts of the general ledger disagree; a posting or a manual journal outside the tax module needs correcting first.")
                .WithWhy(("reconciliation", preview.Value.Reconciliation.Where(static r => r.Difference != 0m).Select(static r => new { r.TaxCode, r.AccountRole, r.Difference }).ToArray()));
        }

        var now = clock.UtcNow;
        var period = new TaxReturnPeriod
        {
            Id = Guid.CreateVersion7(),
            CompanyId = request.CompanyId,
            RegimeId = request.RegimeId,
            PeriodStart = request.PeriodStart,
            PeriodEnd = request.PeriodEnd,
            Status = "filed",
            FiledAt = now,
            FiledBy = principal.Principal?.MembershipId.Value,
            Reference = request.Reference?.Trim(),
            Totals = JsonSerializer.Serialize(new { boxes = preview.Value.Boxes, preview.Value.NetPayable, preview.Value.Currency }, Json),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ReturnPeriods.Add(period);
        await db.SaveChangesAsync(cancellationToken);
        return new TaxReturnPeriodSummary(period.Id, period.CompanyId, period.RegimeId, scope.Value.Regime.Code, period.PeriodStart, period.PeriodEnd, period.Status, period.FiledAt, period.FiledBy, period.Reference, preview.Value.NetPayable);
    }

    // ------------------------------------------------------------------ computation

    private sealed record Scope(Guid CompanyId, TaxRegime Regime, string FunctionalCurrency, DateOnly PeriodStart, DateOnly PeriodEnd, IReadOnlyDictionary<Guid, TaxCode> Codes, IReadOnlyList<TaxEntry> Entries);

    private async Task<Result<Scope>> ScopeAsync(Guid companyId, Guid regimeId, DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken)
    {
        if (periodEnd < periodStart)
        {
            return Error.Validation("tax_return.period_invalid", "The end of the period is on or after its start.").WithWhy(("periodStart", periodStart), ("periodEnd", periodEnd));
        }

        if (await companies.FindAsync(new CompanyId(companyId), cancellationToken) is not { } company)
        {
            return Error.Validation("tax.company_unknown", "The company does not exist.").WithWhy(("companyId", companyId));
        }

        if (await db.Regimes.AsNoTracking().SingleOrDefaultAsync(r => r.Id == regimeId, cancellationToken) is not { } regime)
        {
            return Error.NotFound("tax_regime", regimeId);
        }

        var codes = await db.Codes.AsNoTracking().Where(c => c.RegimeId == regimeId).ToDictionaryAsync(static c => c.Id, cancellationToken);
        var entries = await db.Entries.AsNoTracking()
            .Where(e => e.CompanyId == companyId && e.RegimeId == regimeId && e.PostingDate >= periodStart && e.PostingDate <= periodEnd)
            .ToListAsync(cancellationToken);
        return new Scope(companyId, regime, company.FunctionalCurrency.Code, periodStart, periodEnd, codes, entries);
    }

    private async Task<Result<TaxReturnPreview>> ComputeAsync(Scope scope, CancellationToken cancellationToken)
    {
        var boxes = new Dictionary<string, (decimal Base, decimal Tax)>(StringComparer.Ordinal);
        void Add(string? box, decimal baseAmount, decimal taxAmount)
        {
            if (box is null)
            {
                return;
            }

            var current = boxes.GetValueOrDefault(box);
            boxes[box] = (current.Base + baseAmount, current.Tax + taxAmount);
        }

        // Reconciliation source: what the tax entries say each code moved on its input/output role. The tax entry's
        // own IsRecoverable/IsReverseCharge (captured when posted) decide, never the code's current settings, which
        // may since have changed.
        var entriesByCodeRole = new Dictionary<(Guid Code, string Role), decimal>();
        void Accumulate(Guid codeId, string role, decimal amount) => entriesByCodeRole[(codeId, role)] = entriesByCodeRole.GetValueOrDefault((codeId, role)) + amount;

        foreach (var e in scope.Entries)
        {
            var code = scope.Codes[e.TaxCodeId];
            var (baseBox, taxBox) = BoxesOf(code, e.Direction);
            Add(baseBox, e.BaseFc, 0m);
            Add(taxBox, 0m, e.TaxFc);

            if (e.IsRecoverable && e.TaxFc != 0m)
            {
                Accumulate(e.TaxCodeId, code.InputAccountRole, e.TaxFc);
            }

            if (e.IsReverseCharge && e.TaxFc != 0m)
            {
                Accumulate(e.TaxCodeId, code.OutputAccountRole, -e.TaxFc);
            }
        }

        var boxList = boxes.Select(static kv => new TaxReturnBox(kv.Key, kv.Value.Base, kv.Value.Tax))
            .OrderBy(static b => int.TryParse(b.Code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue)
            .ThenBy(static b => b.Code, StringComparer.Ordinal)
            .ToList();

        var ledgerMovement = await ledger.TaxMovementAsync(scope.CompanyId, scope.PeriodStart, scope.PeriodEnd, cancellationToken);
        var ledgerByCodeRole = ledgerMovement.Where(m => scope.Codes.ContainsKey(m.TaxCodeId)).ToDictionary(static m => (Code: m.TaxCodeId, Role: m.AccountRole), static m => m.AmountFc);

        var reconciliation = entriesByCodeRole.Keys.Concat(ledgerByCodeRole.Keys).Distinct()
            .Select(p => new TaxCodeReconciliation(p.Code, scope.Codes[p.Code].Code, p.Role, ledgerByCodeRole.GetValueOrDefault(p), entriesByCodeRole.GetValueOrDefault(p), ledgerByCodeRole.GetValueOrDefault(p) - entriesByCodeRole.GetValueOrDefault(p)))
            .OrderBy(static r => r.TaxCode, StringComparer.Ordinal)
            .ThenBy(static r => r.AccountRole, StringComparer.Ordinal)
            .ToList();

        var netPayable = -reconciliation.Sum(static r => r.LedgerMovement);
        var reconciled = reconciliation.All(static r => r.Difference == 0m);
        return new TaxReturnPreview(scope.CompanyId, scope.Regime.Id, scope.Regime.Code, scope.Regime.Name.Values, scope.PeriodStart, scope.PeriodEnd, scope.FunctionalCurrency, boxList, reconciliation, reconciled, netPayable);
    }

    private static (string? Base, string? Tax) BoxesOf(TaxCode code, string direction) =>
        direction == TaxDirections.Sales ? (code.SalesBaseBox, code.SalesTaxBox) : (code.PurchaseBaseBox, code.PurchaseTaxBox);

    private static TaxReturnDrillDownLine Line(TaxEntry e, TaxCode code, string kind) =>
        new(e.Id, kind, e.Direction, e.PostingDate, e.SourceModule, e.SourceDocumentType, e.SourceDocumentId, e.SourceDocumentNumber, e.TaxCodeId, code.Code, e.RatePct, e.BaseFc, e.TaxFc, e.ReversesEntryId is not null);

    private static decimal? NetPayableOf(string totalsJson)
    {
        using var document = JsonDocument.Parse(totalsJson);
        return document.RootElement.TryGetProperty("netPayable", out var value) ? value.GetDecimal() : null;
    }
}
