using System.Text.Json;
using Dapper;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Persistence;
using Quicker.Reporting.Contracts;

namespace Quicker.Reporting.Application;

/// <summary>
/// The sales lines read model (founder programme item 1f): every line of a company's invoices and credit notes in a
/// date range with the attributes a grid groups by, read straight from the modules' tables under row-level security.
/// Posted documents by default; drafts and cancelled ones on request. Capped, so a year of a busy company is asked
/// for a quarter at a time.
/// </summary>
public sealed class SalesLinesService(IUnitOfWorkAccessor unitOfWork, ICurrentPrincipal principal, ICompanyDirectory companies, IClock clock) : ISalesLines
{
    public Task<Result<SalesLinesReport>> PostedAsync(Guid companyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) => ReadAsync(companyId, from, to, null, cancellationToken);

    public const int MaxRows = 20_000;

    private static readonly string[] DefaultStatuses = ["posted"];

    public async Task<Result<SalesLinesReport>> ReadAsync(Guid companyId, DateOnly? from, DateOnly? to, string? status, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        if (principal.Required.ScopesFor("sales.invoice.read")?.AllowsCompany(companyId) != true)
        {
            return Error.Forbidden("auth.forbidden", "Reading the sales lines needs sales.invoice.read in the company.").WithWhy(("permission", "sales.invoice.read"));
        }

        var today = clock.TodayIn(company.TimeZone);
        var end = to ?? today;
        var start = from ?? new DateOnly(end.Year, 1, 1);
        if (start > end)
        {
            return Error.Validation("report.range_invalid", "The range starts after it ends.").WithWhy(("from", start), ("to", end));
        }

        var statuses = string.IsNullOrWhiteSpace(status) ? DefaultStatuses : status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var uow = unitOfWork.Current;
        var rows = (await uow.Connection.QueryAsync<Row>(new CommandDefinition("""
            SELECT i.id AS InvoiceId, i.number AS InvoiceNumber, i.kind AS Kind, i.status AS Status, i.document_date AS DocumentDate, i.posting_date AS PostingDate, i.due_date AS DueDate,
                   to_char(i.posting_date, 'YYYY') AS Year, to_char(i.posting_date, 'YYYY') || '-Q' || to_char(i.posting_date, 'Q') AS Quarter, to_char(i.posting_date, 'YYYY-MM') AS Month,
                   i.partner_id AS CustomerId, p.code AS CustomerCode, p.legal_name_i18n::text AS CustomerNameJson,
                   cg.code AS CustomerGroup, r.code AS SalesRep, r.name_i18n::text AS SalesRepNameJson, b.code AS Branch, pt.code AS PaymentTerms, pl.code AS PriceList,
                   i.currency AS Currency, i.exchange_rate AS ExchangeRate,
                   l.id AS LineId, l.line_no AS LineNo, l.kind AS LineKind, l.item_id AS ItemId, it.code AS ItemCode, it.name_i18n::text AS ItemNameJson, it.type AS ItemType,
                   c.code AS Category, br.code AS Brand, u.code AS Uom, l.description AS Description,
                   s.sign * l.quantity AS Quantity, l.unit_price AS UnitPrice, l.discount_pct AS DiscountPct,
                   s.sign * l.net_amount AS NetAmount, s.sign * l.tax_amount AS TaxAmount, s.sign * (l.net_amount + l.tax_amount) AS GrossAmount, s.sign * l.net_amount_fc AS NetAmountFc,
                   tc.code AS TaxCode, l.tax_rate_pct AS TaxRatePct,
                   cc.code AS CostCentre, dp.code AS Department, pj.code AS Project
            FROM app.sls_invoice_lines l
            JOIN app.sls_invoices i ON i.tenant_id = l.tenant_id AND i.id = l.invoice_id
            CROSS JOIN LATERAL (SELECT CASE WHEN i.kind = 'credit_note' THEN -1 ELSE 1 END AS sign) s
            JOIN app.ptr_partners p ON p.tenant_id = i.tenant_id AND p.id = i.partner_id
            LEFT JOIN app.ptr_customer_accounts ca ON ca.tenant_id = i.tenant_id AND ca.partner_id = i.partner_id AND ca.company_id = i.company_id
            LEFT JOIN app.ptr_customer_groups cg ON cg.tenant_id = ca.tenant_id AND cg.id = ca.customer_group_id
            LEFT JOIN app.ptr_sales_reps r ON r.tenant_id = i.tenant_id AND r.id = i.sales_rep_id
            LEFT JOIN app.org_branches b ON b.tenant_id = i.tenant_id AND b.id = i.branch_id
            LEFT JOIN app.ptr_payment_terms pt ON pt.tenant_id = i.tenant_id AND pt.id = i.payment_terms_id
            LEFT JOIN app.prc_price_lists pl ON pl.tenant_id = i.tenant_id AND pl.id = i.price_list_id
            LEFT JOIN app.itm_items it ON it.tenant_id = l.tenant_id AND it.id = l.item_id
            LEFT JOIN app.itm_item_categories c ON c.tenant_id = it.tenant_id AND c.id = it.category_id
            LEFT JOIN app.itm_brands br ON br.tenant_id = it.tenant_id AND br.id = it.brand_id
            LEFT JOIN app.org_uoms u ON u.tenant_id = l.tenant_id AND u.id = l.uom_id
            LEFT JOIN app.tax_codes tc ON tc.tenant_id = l.tenant_id AND tc.id = l.tax_code_id
            LEFT JOIN app.org_dimension_sets ds ON ds.tenant_id = l.tenant_id AND ds.id = l.dimension_set_id
            LEFT JOIN app.org_dimension_values cc ON cc.tenant_id = ds.tenant_id AND cc.id::text = ds.values->>'COST_CENTER'
            LEFT JOIN app.org_dimension_values dp ON dp.tenant_id = ds.tenant_id AND dp.id::text = ds.values->>'DEPARTMENT'
            LEFT JOIN app.org_dimension_values pj ON pj.tenant_id = ds.tenant_id AND pj.id::text = ds.values->>'PROJECT'
            WHERE i.company_id = @company AND i.posting_date >= @from AND i.posting_date <= @to AND i.status = ANY(@statuses)
            ORDER BY i.posting_date, i.number, l.line_no
            LIMIT @limit
            """, new { company = companyId, from = start, to = end, statuses, limit = MaxRows + 1 }, uow.Transaction, cancellationToken: cancellationToken))).ToList();

        var truncated = rows.Count > MaxRows;
        var lines = rows.Take(MaxRows).Select(static r => new SalesLineRow(
            r.InvoiceId, r.InvoiceNumber, r.Kind, r.Status, DateOnly.FromDateTime(r.DocumentDate), DateOnly.FromDateTime(r.PostingDate), r.DueDate is { } due ? DateOnly.FromDateTime(due) : null,
            r.Year, r.Quarter, r.Month,
            r.CustomerId, r.CustomerCode, Names(r.CustomerNameJson) ?? new Dictionary<string, string>(StringComparer.Ordinal),
            r.CustomerGroup, r.SalesRep, Names(r.SalesRepNameJson), r.Branch, r.PaymentTerms, r.PriceList, r.Currency, r.ExchangeRate,
            r.LineId, r.LineNo, r.LineKind, r.ItemId, r.ItemCode, Names(r.ItemNameJson), r.ItemType, r.Category, r.Brand, r.Uom, r.Description,
            r.Quantity, r.UnitPrice, r.DiscountPct, r.NetAmount, r.TaxAmount, r.GrossAmount, r.NetAmountFc, r.TaxCode, r.TaxRatePct,
            r.CostCentre, r.Department, r.Project)).ToList();
        return new SalesLinesReport(companyId, company.FunctionalCurrency.Code, start, end, lines, truncated);
    }

    private static IReadOnlyDictionary<string, string>? Names(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(json);

    private sealed class Row
    {
        public Guid InvoiceId { get; init; }
        public string InvoiceNumber { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public DateTime DocumentDate { get; init; }
        public DateTime PostingDate { get; init; }
        public DateTime? DueDate { get; init; }
        public string Year { get; init; } = string.Empty;
        public string Quarter { get; init; } = string.Empty;
        public string Month { get; init; } = string.Empty;
        public Guid CustomerId { get; init; }
        public string CustomerCode { get; init; } = string.Empty;
        public string? CustomerNameJson { get; init; }
        public string? CustomerGroup { get; init; }
        public string? SalesRep { get; init; }
        public string? SalesRepNameJson { get; init; }
        public string? Branch { get; init; }
        public string? PaymentTerms { get; init; }
        public string? PriceList { get; init; }
        public string Currency { get; init; } = string.Empty;
        public decimal ExchangeRate { get; init; }
        public Guid LineId { get; init; }
        public int LineNo { get; init; }
        public string LineKind { get; init; } = string.Empty;
        public Guid? ItemId { get; init; }
        public string? ItemCode { get; init; }
        public string? ItemNameJson { get; init; }
        public string? ItemType { get; init; }
        public string? Category { get; init; }
        public string? Brand { get; init; }
        public string? Uom { get; init; }
        public string? Description { get; init; }
        public decimal Quantity { get; init; }
        public decimal UnitPrice { get; init; }
        public decimal DiscountPct { get; init; }
        public decimal NetAmount { get; init; }
        public decimal TaxAmount { get; init; }
        public decimal GrossAmount { get; init; }
        public decimal NetAmountFc { get; init; }
        public string? TaxCode { get; init; }
        public decimal TaxRatePct { get; init; }
        public string? CostCentre { get; init; }
        public string? Department { get; init; }
        public string? Project { get; init; }
    }
}
