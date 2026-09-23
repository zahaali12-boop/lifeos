using Microsoft.EntityFrameworkCore;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;
using Quicker.Purchasing.Domain;

namespace Quicker.Purchasing.Persistence;

public sealed class PurchasingDbContext(DbContextOptions<PurchasingDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<Requisition> Requisitions => Set<Requisition>();

    public DbSet<RequisitionLine> RequisitionLines => Set<RequisitionLine>();

    public DbSet<Rfq> Rfqs => Set<Rfq>();

    public DbSet<RfqLine> RfqLines => Set<RfqLine>();

    public DbSet<RfqSupplier> RfqSuppliers => Set<RfqSupplier>();

    public DbSet<SupplierQuote> Quotes => Set<SupplierQuote>();

    public DbSet<SupplierQuoteLine> QuoteLines => Set<SupplierQuoteLine>();

    public DbSet<BlanketAgreement> Agreements => Set<BlanketAgreement>();

    public DbSet<BlanketLine> AgreementLines => Set<BlanketLine>();

    public DbSet<PurchaseOrder> Orders => Set<PurchaseOrder>();

    public DbSet<PurchaseOrderLine> OrderLines => Set<PurchaseOrderLine>();

    public DbSet<PurchaseOrderRevision> Revisions => Set<PurchaseOrderRevision>();

    public DbSet<Commitment> Commitments => Set<Commitment>();

    public DbSet<Receipt> Receipts => Set<Receipt>();

    public DbSet<ReceiptLine> ReceiptLines => Set<ReceiptLine>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();

    public DbSet<MatchResult> MatchResults => Set<MatchResult>();

    public DbSet<ChargeType> ChargeTypes => Set<ChargeType>();

    public DbSet<LandedCostDocument> LandedCosts => Set<LandedCostDocument>();

    public DbSet<LandedCostCharge> LandedCostCharges => Set<LandedCostCharge>();

    public DbSet<LandedCostAllocation> LandedCostAllocations => Set<LandedCostAllocation>();

    public DbSet<SupplierReturn> Returns => Set<SupplierReturn>();

    public DbSet<SupplierReturnLine> ReturnLines => Set<SupplierReturnLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Requisition>(b =>
        {
            b.ToTable("pur_requisitions", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.TotalEstimated).HasPrecision(24, 6);
            b.Property(static x => x.CustomFields).HasColumnType("jsonb");
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.RequisitionId });
            b.HasAuditTrail("purchase_requisition", static x => x.Number);
        });

        modelBuilder.Entity<RequisitionLine>(b =>
        {
            b.ToTable("pur_requisition_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Quantity).HasPrecision(24, 9);
            b.Property(static x => x.QuantityBase).HasPrecision(24, 9);
            b.Property(static x => x.QtyOrdered).HasPrecision(24, 9);
            b.Property(static x => x.EstimatedPrice).HasPrecision(24, 6);
        });

        modelBuilder.Entity<Rfq>(b =>
        {
            b.ToTable("pur_rfqs", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.RfqId });
            b.HasMany(static x => x.Suppliers).WithOne().HasForeignKey(static s => new { s.TenantId, s.RfqId });
            b.HasAuditTrail("purchase_rfq", static x => x.Number);
        });

        modelBuilder.Entity<RfqLine>(b =>
        {
            b.ToTable("pur_rfq_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Quantity).HasPrecision(24, 9);
            b.Property(static x => x.QuantityBase).HasPrecision(24, 9);
        });

        modelBuilder.Entity<RfqSupplier>(b =>
        {
            b.ToTable("pur_rfq_suppliers", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
        });

        modelBuilder.Entity<SupplierQuote>(b =>
        {
            b.ToTable("pur_supplier_quotes", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.FreightAmount).HasPrecision(24, 6);
            b.Property(static x => x.OtherCharges).HasPrecision(24, 6);
            b.Property(static x => x.ComparisonScore).HasColumnType("jsonb");
            b.HasOne<RfqSupplier>().WithMany().HasForeignKey(static x => new { x.TenantId, x.RfqSupplierId });
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.QuoteId });
            b.HasAuditTrail("supplier_quote", static x => x.SupplierReference ?? x.Id.ToString("N"));
        });

        modelBuilder.Entity<SupplierQuoteLine>(b =>
        {
            b.ToTable("pur_supplier_quote_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.UnitPrice).HasPrecision(24, 6);
            b.Property(static x => x.Quantity).HasPrecision(24, 9);
        });

        modelBuilder.Entity<BlanketAgreement>(b =>
        {
            b.ToTable("pur_blanket_agreements", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.CommittedAmount).HasPrecision(24, 6);
            b.Property(static x => x.ReleasedAmount).HasPrecision(24, 6);
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.AgreementId });
            b.HasAuditTrail("purchase_agreement", static x => x.Number);
        });

        modelBuilder.Entity<BlanketLine>(b =>
        {
            b.ToTable("pur_blanket_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.AgreedQty).HasPrecision(24, 9);
            b.Property(static x => x.AgreedPrice).HasPrecision(24, 6);
            b.Property(static x => x.ReleasedQty).HasPrecision(24, 9);
        });

        modelBuilder.Entity<PurchaseOrder>(b =>
        {
            b.ToTable("pur_orders", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.ExchangeRate).HasPrecision(24, 12);
            b.Property(static x => x.TotalNet).HasPrecision(24, 6);
            b.Property(static x => x.TotalTax).HasPrecision(24, 6);
            b.Property(static x => x.TotalGross).HasPrecision(24, 6);
            b.Property(static x => x.SupplierSnapshot).HasColumnType("jsonb");
            b.Property(static x => x.CustomFields).HasColumnType("jsonb");
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.OrderId });
            b.HasAuditTrail("purchase_order", static x => x.Number);
        });

        modelBuilder.Entity<PurchaseOrderLine>(b =>
        {
            b.ToTable("pur_order_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Quantity).HasPrecision(24, 9);
            b.Property(static x => x.QuantityBase).HasPrecision(24, 9);
            b.Property(static x => x.UnitPrice).HasPrecision(24, 6);
            b.Property(static x => x.DiscountPct).HasPrecision(9, 6);
            b.Property(static x => x.NetAmount).HasPrecision(24, 6);
            b.Property(static x => x.TaxAmount).HasPrecision(24, 6);
            b.Property(static x => x.QtyReceived).HasPrecision(24, 9);
            b.Property(static x => x.QtyInvoiced).HasPrecision(24, 9);
            b.Property(static x => x.QtyCancelled).HasPrecision(24, 9);
        });

        modelBuilder.Entity<PurchaseOrderRevision>(b =>
        {
            b.ToTable("pur_order_revisions", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Snapshot).HasColumnType("jsonb");
            b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(static x => new { x.TenantId, x.OrderId });
        });

        modelBuilder.Entity<Commitment>(b =>
        {
            b.ToTable("pur_commitments", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.AmountFc).HasPrecision(24, 6);
            b.Property(static x => x.AmountRc).HasPrecision(24, 6);
            b.Property(static x => x.ConsumedRc).HasPrecision(24, 6);
            b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(static x => new { x.TenantId, x.OrderId });
        });

        modelBuilder.Entity<Receipt>(b =>
        {
            b.ToTable("pur_receipts", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.ExchangeRate).HasPrecision(24, 12);
            b.Property(static x => x.TotalExpectedCost).HasPrecision(24, 6);
            b.Property(static x => x.CustomFields).HasColumnType("jsonb");
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.ReceiptId });
            b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(static x => new { x.TenantId, x.OrderId });
            b.HasAuditTrail("purchase_receipt", static x => x.Number);
        });

        modelBuilder.Entity<ReceiptLine>(b =>
        {
            b.ToTable("pur_receipt_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Quantity).HasPrecision(24, 9);
            b.Property(static x => x.QuantityBase).HasPrecision(24, 9);
            b.Property(static x => x.QtyInOrderUom).HasPrecision(24, 9);
            b.Property(static x => x.SerialNumbers).HasColumnType("jsonb");
            b.Property(static x => x.UnitPrice).HasPrecision(24, 6);
            b.Property(static x => x.ExpectedUnitCost).HasPrecision(24, 9);
            b.Property(static x => x.ExpectedCostAmount).HasPrecision(24, 6);
            b.Property(static x => x.InvoicedCostAmount).HasPrecision(24, 6);
            b.Property(static x => x.ReturnedCostAmount).HasPrecision(24, 6);
            b.Property(static x => x.QtyInvoiced).HasPrecision(24, 9);
            b.Property(static x => x.QtyReturned).HasPrecision(24, 9);
            b.Property(static x => x.SleIds).HasColumnType("jsonb");
            b.HasOne<PurchaseOrderLine>().WithMany().HasForeignKey(static x => new { x.TenantId, x.OrderLineId });
        });

        modelBuilder.Entity<Invoice>(b =>
        {
            b.ToTable("pur_invoices", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.ExchangeRate).HasPrecision(24, 12);
            b.Property(static x => x.TotalNet).HasPrecision(24, 6);
            b.Property(static x => x.TotalTax).HasPrecision(24, 6);
            b.Property(static x => x.TotalWht).HasPrecision(24, 6);
            b.Property(static x => x.TotalGross).HasPrecision(24, 6);
            b.Property(static x => x.TotalPayable).HasPrecision(24, 6);
            b.Property(static x => x.CustomFields).HasColumnType("jsonb");
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.InvoiceId });
            b.HasAuditTrail("purchase_invoice", static x => x.Number);
        });

        modelBuilder.Entity<InvoiceLine>(b =>
        {
            b.ToTable("pur_invoice_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Quantity).HasPrecision(24, 9);
            b.Property(static x => x.UnitPrice).HasPrecision(24, 6);
            b.Property(static x => x.DiscountPct).HasPrecision(9, 6);
            b.Property(static x => x.NetAmount).HasPrecision(24, 6);
            b.Property(static x => x.TaxAmount).HasPrecision(24, 6);
            b.Property(static x => x.WhtAmount).HasPrecision(24, 6);
            b.Property(static x => x.NetAmountFc).HasPrecision(24, 6);
            b.Property(static x => x.ExpectedUnitPrice).HasPrecision(24, 6);
            b.Property(static x => x.PriceVariancePct).HasPrecision(12, 6);
            b.Property(static x => x.QtyVariance).HasPrecision(24, 9);
        });

        modelBuilder.Entity<MatchResult>(b =>
        {
            b.ToTable("pur_match_results", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.PriceTolerancePct).HasPrecision(9, 6);
            b.Property(static x => x.QtyTolerancePct).HasPrecision(9, 6);
            b.Property(static x => x.PriceVarianceAmount).HasPrecision(24, 6);
            b.Property(static x => x.PriceVariancePct).HasPrecision(12, 6);
            b.Property(static x => x.QtyVariance).HasPrecision(24, 9);
            b.Property(static x => x.Details).HasColumnType("jsonb");
            b.HasOne<Invoice>().WithMany().HasForeignKey(static x => new { x.TenantId, x.InvoiceId });
        });

        modelBuilder.Entity<ChargeType>(b =>
        {
            b.ToTable("pur_charge_types", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("charge_type", static x => x.Code);
        });

        modelBuilder.Entity<LandedCostDocument>(b =>
        {
            b.ToTable("pur_landed_cost_docs", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.ExchangeRate).HasPrecision(24, 12);
            b.Property(static x => x.TotalAmount).HasPrecision(24, 6);
            b.Property(static x => x.TotalAmountFc).HasPrecision(24, 6);
            b.Property(static x => x.OnHandPortionFc).HasPrecision(24, 6);
            b.Property(static x => x.SoldPortionFc).HasPrecision(24, 6);
            b.Property(static x => x.CustomFields).HasColumnType("jsonb");
            b.HasMany(static x => x.Charges).WithOne().HasForeignKey(static c => new { c.TenantId, c.LandedCostId });
            b.HasMany(static x => x.Allocations).WithOne().HasForeignKey(static a => new { a.TenantId, a.LandedCostId });
            b.HasAuditTrail("landed_cost_document", static x => x.Number);
        });

        modelBuilder.Entity<LandedCostCharge>(b =>
        {
            b.ToTable("pur_landed_cost_charges", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Amount).HasPrecision(24, 6);
            b.Property(static x => x.AmountFc).HasPrecision(24, 6);
            b.Property(static x => x.InvoicedAmountFc).HasPrecision(24, 6);
            b.HasOne<ChargeType>().WithMany().HasForeignKey(static x => new { x.TenantId, x.ChargeTypeId });
        });

        modelBuilder.Entity<LandedCostAllocation>(b =>
        {
            b.ToTable("pur_landed_cost_allocations", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.BasisValue).HasPrecision(24, 9);
            b.Property(static x => x.AllocatedAmountFc).HasPrecision(24, 6);
            b.Property(static x => x.OnHandPortionFc).HasPrecision(24, 6);
            b.Property(static x => x.SoldPortionFc).HasPrecision(24, 6);
            b.HasOne<ReceiptLine>().WithMany().HasForeignKey(static x => new { x.TenantId, x.ReceiptLineId });
            b.HasOne<LandedCostCharge>().WithMany().HasForeignKey(static x => new { x.TenantId, x.ChargeId });
        });

        modelBuilder.Entity<SupplierReturn>(b =>
        {
            b.ToTable("pur_returns", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.TotalCostFc).HasPrecision(24, 6);
            b.Property(static x => x.CustomFields).HasColumnType("jsonb");
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.ReturnId });
            b.HasOne<Receipt>().WithMany().HasForeignKey(static x => new { x.TenantId, x.ReceiptId });
            b.HasAuditTrail("purchase_return", static x => x.Number);
        });

        modelBuilder.Entity<SupplierReturnLine>(b =>
        {
            b.ToTable("pur_return_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Quantity).HasPrecision(24, 9);
            b.Property(static x => x.QuantityBase).HasPrecision(24, 9);
            b.Property(static x => x.SerialNumbers).HasColumnType("jsonb");
            b.Property(static x => x.SleIds).HasColumnType("jsonb");
            b.Property(static x => x.CostAmountFc).HasPrecision(24, 6);
            b.Property(static x => x.CreditedAmountFc).HasPrecision(24, 6);
            b.Property(static x => x.QtyCredited).HasPrecision(24, 9);
            b.HasOne<ReceiptLine>().WithMany().HasForeignKey(static x => new { x.TenantId, x.ReceiptLineId });
        });

        base.OnModelCreating(modelBuilder);
    }
}
