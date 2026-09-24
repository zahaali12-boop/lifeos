using Microsoft.EntityFrameworkCore;
using Quicker.Partners.Domain;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Partners.Persistence;

public sealed class PartnersDbContext(DbContextOptions<PartnersDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<Partner> Partners => Set<Partner>();

    public DbSet<Contact> Contacts => Set<Contact>();

    public DbSet<PartnerAddress> Addresses => Set<PartnerAddress>();

    public DbSet<PartnerBankAccount> BankAccounts => Set<PartnerBankAccount>();

    public DbSet<PartnerTaxRegistration> TaxRegistrations => Set<PartnerTaxRegistration>();

    public DbSet<PaymentTerms> PaymentTerms => Set<PaymentTerms>();

    public DbSet<PaymentTermLine> PaymentTermLines => Set<PaymentTermLine>();

    public DbSet<DeliveryTerms> DeliveryTerms => Set<DeliveryTerms>();

    public DbSet<WhtCode> WhtCodes => Set<WhtCode>();

    public DbSet<SupplierGroup> SupplierGroups => Set<SupplierGroup>();

    public DbSet<SupplierAccount> SupplierAccounts => Set<SupplierAccount>();

    public DbSet<CustomerGroup> CustomerGroups => Set<CustomerGroup>();

    public DbSet<CustomerAccount> CustomerAccounts => Set<CustomerAccount>();

    public DbSet<CommissionPlan> CommissionPlans => Set<CommissionPlan>();

    public DbSet<SalesRep> SalesReps => Set<SalesRep>();

    public DbSet<PipelineStage> PipelineStages => Set<PipelineStage>();

    public DbSet<Opportunity> Opportunities => Set<Opportunity>();

    public DbSet<OpportunityStageChange> OpportunityStageChanges => Set<OpportunityStageChange>();

    public DbSet<CrmActivity> CrmActivities => Set<CrmActivity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Partner>(b =>
        {
            b.ToTable("ptr_partners", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.LegalName).HasColumnName("legal_name_i18n");
            b.Property(static x => x.TradeName).HasColumnName("trade_name_i18n");
            b.Property(static x => x.CustomFields).HasColumnType("jsonb");
            b.HasOne<Partner>().WithMany().HasForeignKey(static x => new { x.TenantId, x.ParentPartnerId });
            b.HasAuditTrail("partner", static x => x.Code);
        });

        modelBuilder.Entity<Contact>(b =>
        {
            b.ToTable("ptr_contacts", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.HasOne<Partner>().WithMany().HasForeignKey(static x => new { x.TenantId, x.PartnerId });
        });

        modelBuilder.Entity<PartnerAddress>(b =>
        {
            b.ToTable("ptr_partner_addresses", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Address).HasColumnType("jsonb");
            b.HasOne<Partner>().WithMany().HasForeignKey(static x => new { x.TenantId, x.PartnerId });
        });

        modelBuilder.Entity<PartnerBankAccount>(b =>
        {
            b.ToTable("ptr_partner_bank_accounts", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.HasOne<Partner>().WithMany().HasForeignKey(static x => new { x.TenantId, x.PartnerId });
            b.HasAuditTrail("partner_bank_account", static x => x.BankName + " " + (x.IbanMasked ?? x.AccountNumberMasked ?? string.Empty));
        });

        modelBuilder.Entity<PartnerTaxRegistration>(b =>
        {
            b.ToTable("ptr_partner_tax_registrations", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.HasOne<Partner>().WithMany().HasForeignKey(static x => new { x.TenantId, x.PartnerId });
        });

        modelBuilder.Entity<PaymentTerms>(b =>
        {
            b.ToTable("ptr_payment_terms", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.Property(static x => x.EarlyDiscountPct).HasPrecision(9, 6);
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.TermsId });
            b.HasAuditTrail("payment_terms", static x => x.Code);
        });

        modelBuilder.Entity<PaymentTermLine>(b =>
        {
            b.ToTable("ptr_payment_term_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.TermsId, x.Sequence });
            b.Property(static x => x.Percentage).HasPrecision(9, 6);
        });

        modelBuilder.Entity<DeliveryTerms>(b =>
        {
            b.ToTable("ptr_delivery_terms", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("delivery_terms", static x => x.Code);
        });

        modelBuilder.Entity<WhtCode>(b =>
        {
            b.ToTable("ptr_wht_codes", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.Property(static x => x.RatePct).HasPrecision(9, 6);
            b.Property(static x => x.ThresholdAmount).HasPrecision(24, 6);
            b.HasAuditTrail("wht_code", static x => x.Code);
        });

        modelBuilder.Entity<SupplierGroup>(b =>
        {
            b.ToTable("ptr_supplier_groups", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("supplier_group", static x => x.Code);
        });

        modelBuilder.Entity<SupplierAccount>(b =>
        {
            b.ToTable("ptr_supplier_accounts", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.PriceTolerancePct).HasPrecision(9, 6);
            b.Property(static x => x.QtyTolerancePct).HasPrecision(9, 6);
            b.HasOne<Partner>().WithMany().HasForeignKey(static x => new { x.TenantId, x.PartnerId });
            b.HasAuditTrail("supplier_account", static x => x.PartnerId.ToString("N") + "@" + x.CompanyId.ToString("N"));
        });

        modelBuilder.Entity<CustomerGroup>(b =>
        {
            b.ToTable("ptr_customer_groups", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("customer_group", static x => x.Code);
        });

        modelBuilder.Entity<CustomerAccount>(b =>
        {
            b.ToTable("ptr_customer_accounts", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.CreditLimit).HasPrecision(24, 6);
            b.HasOne<Partner>().WithMany().HasForeignKey(static x => new { x.TenantId, x.PartnerId });
            b.HasAuditTrail("customer_account", static x => x.PartnerId.ToString("N") + "@" + x.CompanyId.ToString("N"));
        });

        modelBuilder.Entity<CommissionPlan>(b =>
        {
            b.ToTable("ptr_commission_plans", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.HasMany(static x => x.Rules).WithOne().HasForeignKey(static r => new { r.TenantId, r.PlanId });
            b.HasAuditTrail("commission_plan", static x => x.Code);
        });

        modelBuilder.Entity<CommissionRule>(b =>
        {
            b.ToTable("ptr_commission_rules", "app");
            b.HasKey(static x => new { x.TenantId, x.PlanId, x.Sequence });
            b.Property(static x => x.FromAmount).HasPrecision(24, 6);
            b.Property(static x => x.RatePct).HasPrecision(9, 6);
        });

        modelBuilder.Entity<SalesRep>(b =>
        {
            b.ToTable("ptr_sales_reps", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("sales_rep", static x => x.Code);
        });

        modelBuilder.Entity<PipelineStage>(b =>
        {
            b.ToTable("ptr_pipeline_stages", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("pipeline_stage", static x => x.Code);
        });

        modelBuilder.Entity<Opportunity>(b =>
        {
            b.ToTable("ptr_opportunities", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.ExpectedAmount).HasPrecision(24, 6);
            b.Property(static x => x.CustomFields).HasColumnType("jsonb");
            b.HasAuditTrail("opportunity", static x => x.Number);
        });

        modelBuilder.Entity<OpportunityStageChange>(b =>
        {
            b.ToTable("ptr_opportunity_stage_changes", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.ExpectedAmount).HasPrecision(24, 6);
        });

        modelBuilder.Entity<CrmActivity>(b =>
        {
            b.ToTable("ptr_crm_activities", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
        });

        base.OnModelCreating(modelBuilder);
    }
}
