using Microsoft.EntityFrameworkCore;
using Quicker.Payroll.Domain;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Payroll.Persistence;

public sealed class PayrollDbContext(DbContextOptions<PayrollDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<PayComponent> Components => Set<PayComponent>();

    public DbSet<Employee> Employees => Set<Employee>();

    public DbSet<EmployeeComponent> EmployeeComponents => Set<EmployeeComponent>();

    public DbSet<Leave> Leaves => Set<Leave>();

    public DbSet<PayrollRun> Runs => Set<PayrollRun>();

    public DbSet<PayrollRunLine> RunLines => Set<PayrollRunLine>();

    public DbSet<PayrollRunItem> RunItems => Set<PayrollRunItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<PayComponent>(b =>
        {
            b.ToTable("pay_components", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("pay_component", static x => x.Code);
        });

        modelBuilder.Entity<Employee>(b =>
        {
            b.ToTable("pay_employees", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.JobTitle).HasColumnName("job_title_i18n");
            b.HasMany(static x => x.Components).WithOne().HasForeignKey(static c => new { c.TenantId, c.EmployeeId });
            b.HasAuditTrail("employee", static x => x.Number);
        });

        modelBuilder.Entity<EmployeeComponent>(b =>
        {
            b.ToTable("pay_employee_components", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.HasOne<PayComponent>().WithMany().HasForeignKey(static x => new { x.TenantId, x.ComponentId });
        });

        modelBuilder.Entity<Leave>(b =>
        {
            b.ToTable("pay_leaves", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.HasOne<Employee>().WithMany().HasForeignKey(static x => new { x.TenantId, x.EmployeeId });
            b.HasAuditTrail("leave", static x => x.Kind + " " + x.FromDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        });

        modelBuilder.Entity<PayrollRun>(b =>
        {
            b.ToTable("pay_runs", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.RunId });
            b.HasAuditTrail("payroll_run", static x => x.Number);
        });

        modelBuilder.Entity<PayrollRunLine>(b =>
        {
            b.ToTable("pay_run_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.EmployeeName).HasColumnName("employee_name_i18n");
            b.HasOne<Employee>().WithMany().HasForeignKey(static x => new { x.TenantId, x.EmployeeId });
            b.HasMany(static x => x.Items).WithOne().HasForeignKey(static i => new { i.TenantId, i.LineId });
        });

        modelBuilder.Entity<PayrollRunItem>(b =>
        {
            b.ToTable("pay_run_items", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.ComponentName).HasColumnName("component_name_i18n");
            b.HasOne<PayComponent>().WithMany().HasForeignKey(static x => new { x.TenantId, x.ComponentId });
        });

        base.OnModelCreating(modelBuilder);
    }
}
