using Microsoft.EntityFrameworkCore;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;
using Quicker.Printing.Domain;

namespace Quicker.Printing.Persistence;

public sealed class PrintingDbContext(DbContextOptions<PrintingDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<PrintTemplate> Templates => Set<PrintTemplate>();

    public DbSet<PrintTemplateVersion> Versions => Set<PrintTemplateVersion>();

    public DbSet<PrintRender> Renders => Set<PrintRender>();

    public DbSet<PrintEmail> Emails => Set<PrintEmail>();

    public DbSet<PrintShareLink> Shares => Set<PrintShareLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PrintTemplate>(b =>
        {
            b.ToTable("prt_templates", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("print_template", static x => x.Code);
        });

        modelBuilder.Entity<PrintTemplateVersion>(b =>
        {
            b.ToTable("prt_template_versions", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.SampleData).HasColumnType("jsonb");
            b.HasOne<PrintTemplate>().WithMany().HasForeignKey(static v => new { v.TenantId, v.TemplateId });
        });

        modelBuilder.Entity<PrintRender>(b =>
        {
            b.ToTable("prt_renders", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Parameters).HasColumnType("jsonb");
        });

        modelBuilder.Entity<PrintEmail>(b =>
        {
            b.ToTable("prt_emails", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
        });

        modelBuilder.Entity<PrintShareLink>(b =>
        {
            b.ToTable("prt_share_links", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
        });

        base.OnModelCreating(modelBuilder);
    }
}
