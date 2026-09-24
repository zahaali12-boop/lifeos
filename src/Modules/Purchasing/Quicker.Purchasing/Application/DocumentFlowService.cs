using Dapper;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Results;
using Quicker.Numbering.Contracts;
using Quicker.Persistence;
using Quicker.Purchasing.Contracts;

namespace Quicker.Purchasing.Application;

/// <summary>One document of a purchasing flow, as the smart buttons and the flow list show it.</summary>
public sealed record FlowDocument(string DocumentType, Guid Id, string Number, string Status, DateOnly? Date, decimal? Amount, string? Currency, Guid CompanyId, string? Kind, bool IsCurrent);

/// <summary>
/// The purchasing documents connected to one document: where it came from (requisition, request for quotation,
/// blanket agreement, order) and what followed (receipts, returns, landed costs, invoices and debit notes), limited to
/// what the member may read. <see cref="Truncated"/> says the chain was cut at <see cref="DocumentFlowService.MaxOrders"/> orders.
/// </summary>
public sealed record DocumentFlow(string DocumentType, Guid DocumentId, IReadOnlyList<FlowDocument> Documents, bool Truncated);

/// <summary>
/// SAP's document flow and Odoo's smart buttons over the purchasing chain. Every link already lives in the documents
/// (an order names its requisition, request for quotation and agreement; a receipt its order; a return its receipt; an
/// invoice line its receipt, order, return or landed-cost line; a landed cost its receipt lines), so the flow is read,
/// not stored: the orders the document belongs to are found first, then everything around those orders.
/// </summary>
public sealed class DocumentFlowService(IUnitOfWorkAccessor unitOfWork, ICurrentPrincipal principal)
{
    /// <summary>A blanket agreement can release many orders; past this many the flow is cut and says so.</summary>
    public const int MaxOrders = 50;

    private static readonly Dictionary<string, string> ReadPermissions = new(StringComparer.Ordinal)
    {
        [PurchaseDocumentTypes.Requisition] = PurchasingPermissions.RequisitionRead,
        [PurchaseDocumentTypes.Rfq] = PurchasingPermissions.RfqRead,
        [PurchaseDocumentTypes.BlanketAgreement] = PurchasingPermissions.AgreementRead,
        [PurchaseDocumentTypes.Order] = PurchasingPermissions.OrderRead,
        [PurchaseDocumentTypes.Receipt] = PurchasingPermissions.ReceiptRead,
        [PurchaseDocumentTypes.Invoice] = PurchasingPermissions.InvoiceRead,
        [PurchaseDocumentTypes.LandedCost] = PurchasingPermissions.LandedCostRead,
        [PurchaseDocumentTypes.Return] = PurchasingPermissions.ReturnRead,
    };

    /// <summary>The orders a document belongs to.</summary>
    private static readonly Dictionary<string, string> AnchorSql = new(StringComparer.Ordinal)
    {
        [PurchaseDocumentTypes.Order] = "SELECT id FROM app.pur_orders WHERE id = @id",
        [PurchaseDocumentTypes.Receipt] = "SELECT order_id FROM app.pur_receipts WHERE id = @id",
        [PurchaseDocumentTypes.Return] = "SELECT r.order_id FROM app.pur_returns t JOIN app.pur_receipts r ON r.id = t.receipt_id WHERE t.id = @id",
        [PurchaseDocumentTypes.Requisition] = """
            SELECT id FROM app.pur_orders WHERE requisition_id = @id
            UNION SELECT ol.order_id FROM app.pur_order_lines ol JOIN app.pur_requisition_lines rl ON rl.id = ol.requisition_line_id WHERE rl.requisition_id = @id
            """,
        [PurchaseDocumentTypes.Rfq] = "SELECT id FROM app.pur_orders WHERE rfq_id = @id",
        [PurchaseDocumentTypes.BlanketAgreement] = """
            SELECT id FROM app.pur_orders WHERE agreement_id = @id
            UNION SELECT ol.order_id FROM app.pur_order_lines ol JOIN app.pur_blanket_lines bl ON bl.id = ol.blanket_line_id WHERE bl.agreement_id = @id
            """,
        [PurchaseDocumentTypes.Invoice] = """
            SELECT ol.order_id FROM app.pur_invoice_lines il JOIN app.pur_order_lines ol ON ol.id = il.order_line_id WHERE il.invoice_id = @id
            UNION SELECT r.order_id FROM app.pur_invoice_lines il JOIN app.pur_receipt_lines rl ON rl.id = il.receipt_line_id JOIN app.pur_receipts r ON r.id = rl.receipt_id WHERE il.invoice_id = @id
            UNION SELECT r.order_id FROM app.pur_invoice_lines il JOIN app.pur_return_lines tl ON tl.id = il.return_line_id JOIN app.pur_returns t ON t.id = tl.return_id JOIN app.pur_receipts r ON r.id = t.receipt_id WHERE il.invoice_id = @id
            UNION SELECT r.order_id FROM app.pur_invoice_lines il JOIN app.pur_landed_cost_allocations a ON a.charge_id = il.landed_cost_charge_id JOIN app.pur_receipt_lines rl ON rl.id = a.receipt_line_id JOIN app.pur_receipts r ON r.id = rl.receipt_id WHERE il.invoice_id = @id
            """,
        [PurchaseDocumentTypes.LandedCost] = """
            SELECT DISTINCT r.order_id FROM app.pur_landed_cost_allocations a JOIN app.pur_receipt_lines rl ON rl.id = a.receipt_line_id JOIN app.pur_receipts r ON r.id = rl.receipt_id WHERE a.landed_cost_id = @id
            """,
    };

    /// <summary>The document itself, found whatever its links (an expense invoice or an RFQ not yet awarded has no order).</summary>
    private static readonly Dictionary<string, string> SelfSql = new(StringComparer.Ordinal)
    {
        [PurchaseDocumentTypes.Requisition] = Select(PurchaseDocumentTypes.Requisition) + " WHERE d.id = @id",
        [PurchaseDocumentTypes.Rfq] = Select(PurchaseDocumentTypes.Rfq) + " WHERE d.id = @id",
        [PurchaseDocumentTypes.BlanketAgreement] = Select(PurchaseDocumentTypes.BlanketAgreement) + " WHERE d.id = @id",
        [PurchaseDocumentTypes.Order] = Select(PurchaseDocumentTypes.Order) + " WHERE d.id = @id",
        [PurchaseDocumentTypes.Receipt] = Select(PurchaseDocumentTypes.Receipt) + " WHERE d.id = @id",
        [PurchaseDocumentTypes.Invoice] = Select(PurchaseDocumentTypes.Invoice) + " WHERE d.id = @id",
        [PurchaseDocumentTypes.LandedCost] = Select(PurchaseDocumentTypes.LandedCost) + " WHERE d.id = @id",
        [PurchaseDocumentTypes.Return] = Select(PurchaseDocumentTypes.Return) + " WHERE d.id = @id",
    };

    /// <summary>Everything around a set of orders (@orders).</summary>
    private static readonly string AroundOrdersSql = string.Join("\nUNION ALL\n",
        Select(PurchaseDocumentTypes.Order) + " WHERE d.id = ANY(@orders)",
        Select(PurchaseDocumentTypes.Requisition) + """
             WHERE d.id IN (SELECT requisition_id FROM app.pur_orders WHERE id = ANY(@orders))
                OR d.id IN (SELECT rl.requisition_id FROM app.pur_requisition_lines rl JOIN app.pur_order_lines ol ON ol.requisition_line_id = rl.id WHERE ol.order_id = ANY(@orders))
            """,
        Select(PurchaseDocumentTypes.Rfq) + " WHERE d.id IN (SELECT rfq_id FROM app.pur_orders WHERE id = ANY(@orders))",
        Select(PurchaseDocumentTypes.BlanketAgreement) + """
             WHERE d.id IN (SELECT agreement_id FROM app.pur_orders WHERE id = ANY(@orders))
                OR d.id IN (SELECT bl.agreement_id FROM app.pur_blanket_lines bl JOIN app.pur_order_lines ol ON ol.blanket_line_id = bl.id WHERE ol.order_id = ANY(@orders))
            """,
        Select(PurchaseDocumentTypes.Receipt) + " WHERE d.order_id = ANY(@orders)",
        Select(PurchaseDocumentTypes.Return) + " WHERE d.receipt_id IN (SELECT id FROM app.pur_receipts WHERE order_id = ANY(@orders))",
        Select(PurchaseDocumentTypes.LandedCost) + """
             WHERE d.id IN (SELECT a.landed_cost_id FROM app.pur_landed_cost_allocations a JOIN app.pur_receipt_lines rl ON rl.id = a.receipt_line_id
                            JOIN app.pur_receipts r ON r.id = rl.receipt_id WHERE r.order_id = ANY(@orders))
            """,
        Select(PurchaseDocumentTypes.Invoice) + """
             WHERE d.id IN (
                SELECT il.invoice_id FROM app.pur_invoice_lines il JOIN app.pur_order_lines ol ON ol.id = il.order_line_id WHERE ol.order_id = ANY(@orders)
                UNION SELECT il.invoice_id FROM app.pur_invoice_lines il JOIN app.pur_receipt_lines rl ON rl.id = il.receipt_line_id JOIN app.pur_receipts r ON r.id = rl.receipt_id WHERE r.order_id = ANY(@orders)
                UNION SELECT il.invoice_id FROM app.pur_invoice_lines il JOIN app.pur_return_lines tl ON tl.id = il.return_line_id JOIN app.pur_returns t ON t.id = tl.return_id
                      JOIN app.pur_receipts r ON r.id = t.receipt_id WHERE r.order_id = ANY(@orders)
                UNION SELECT il.invoice_id FROM app.pur_invoice_lines il JOIN app.pur_landed_cost_allocations a ON a.charge_id = il.landed_cost_charge_id
                      JOIN app.pur_receipt_lines rl ON rl.id = a.receipt_line_id JOIN app.pur_receipts r ON r.id = rl.receipt_id WHERE r.order_id = ANY(@orders))
            """);

    public static IReadOnlyCollection<string> DocumentTypes => ReadPermissions.Keys;

    public async Task<Result<DocumentFlow>> FlowAsync(string documentType, Guid documentId, CancellationToken cancellationToken)
    {
        if (!AnchorSql.TryGetValue(documentType ?? string.Empty, out var anchorSql))
        {
            return Error.Validation("document_flow.type_invalid", "The document type is not a purchasing document.").WithWhy(("documentType", documentType), ("types", DocumentTypes.Order(StringComparer.Ordinal).ToList()));
        }

        var uow = unitOfWork.Current;
        var self = await uow.Connection.QuerySingleOrDefaultAsync<Row?>(new CommandDefinition(SelfSql[documentType!], new { id = documentId }, uow.Transaction, cancellationToken: cancellationToken));
        if (self is null || !MayRead(self))
        {
            return Error.NotFound(documentType!, documentId);
        }

        var orders = (await uow.Connection.QueryAsync<Guid>(new CommandDefinition(anchorSql, new { id = documentId }, uow.Transaction, cancellationToken: cancellationToken))).Distinct().ToList();
        var truncated = orders.Count > MaxOrders;
        var rows = orders.Count == 0 ? [] : (await uow.Connection.QueryAsync<Row>(new CommandDefinition(AroundOrdersSql, new { orders = orders.Take(MaxOrders).ToArray() }, uow.Transaction, cancellationToken: cancellationToken))).ToList();

        var documents = rows.Append(self)
            .DistinctBy(static r => (r.DocumentType, r.Id))
            .Where(MayRead)
            .Select(r => new FlowDocument(r.DocumentType, r.Id, r.Number ?? DraftIdentifiers.For(r.Id), r.Status, r.Date is { } d ? DateOnly.FromDateTime(d) : null, r.Amount, r.Currency, r.CompanyId, r.Kind,
                r.DocumentType == documentType && r.Id == documentId))
            .OrderBy(static d => Array.IndexOf(FlowOrder, d.DocumentType)).ThenBy(static d => d.Date).ThenBy(static d => d.Number, StringComparer.Ordinal)
            .ToList();
        return new DocumentFlow(documentType!, documentId, documents, truncated);
    }

    /// <summary>The order in which a purchase moves from need to payment.</summary>
    private static readonly string[] FlowOrder =
    [
        PurchaseDocumentTypes.Requisition, PurchaseDocumentTypes.Rfq, PurchaseDocumentTypes.BlanketAgreement, PurchaseDocumentTypes.Order,
        PurchaseDocumentTypes.Receipt, PurchaseDocumentTypes.Return, PurchaseDocumentTypes.LandedCost, PurchaseDocumentTypes.Invoice,
    ];

    /// <summary>Only what the member's grant for the type's read permission covers, company by company.</summary>
    private bool MayRead(Row row)
    {
        var scopes = principal.Required.ScopesFor(ReadPermissions[row.DocumentType]);
        return scopes is not null && scopes.AllowsCompany(row.CompanyId);
    }

    /// <summary>The same columns from every document table: type, id, number, status, date, amount, currency, company, kind.</summary>
    private static string Select(string type) => type switch
    {
        PurchaseDocumentTypes.Requisition => $"SELECT '{type}' AS document_type, d.id, d.number, d.status, d.created_at::date::timestamp AS date, d.total_estimated AS amount, d.currency, d.company_id, NULL::text AS kind FROM app.pur_requisitions d",
        PurchaseDocumentTypes.Rfq => $"SELECT '{type}' AS document_type, d.id, d.number, d.status, d.due_on::timestamp AS date, NULL::numeric AS amount, NULL::text AS currency, d.company_id, NULL::text AS kind FROM app.pur_rfqs d",
        PurchaseDocumentTypes.BlanketAgreement => $"SELECT '{type}' AS document_type, d.id, d.number, d.status, d.valid_from::timestamp AS date, d.committed_amount AS amount, d.currency, d.company_id, NULL::text AS kind FROM app.pur_blanket_agreements d",
        PurchaseDocumentTypes.Order => $"SELECT '{type}' AS document_type, d.id, d.number, d.status, d.order_date::timestamp AS date, d.total_gross AS amount, d.currency, d.company_id, NULL::text AS kind FROM app.pur_orders d",
        PurchaseDocumentTypes.Receipt => $"SELECT '{type}' AS document_type, d.id, d.number, d.status, d.posting_date::timestamp AS date, NULL::numeric AS amount, NULL::text AS currency, d.company_id, NULL::text AS kind FROM app.pur_receipts d",
        PurchaseDocumentTypes.Return => $"SELECT '{type}' AS document_type, d.id, d.number, d.status, d.posting_date::timestamp AS date, NULL::numeric AS amount, NULL::text AS currency, d.company_id, NULL::text AS kind FROM app.pur_returns d",
        PurchaseDocumentTypes.LandedCost => $"SELECT '{type}' AS document_type, d.id, d.number, d.status, d.posting_date::timestamp AS date, d.total_amount AS amount, d.currency, d.company_id, NULL::text AS kind FROM app.pur_landed_cost_docs d",
        PurchaseDocumentTypes.Invoice => $"SELECT '{type}' AS document_type, d.id, d.number, d.status, d.document_date::timestamp AS date, d.total_gross AS amount, d.currency, d.company_id, d.kind FROM app.pur_invoices d",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a purchasing document type."),
    };

    private sealed record Row(string DocumentType, Guid Id, string? Number, string Status, DateTime? Date, decimal? Amount, string? Currency, Guid CompanyId, string? Kind);
}
