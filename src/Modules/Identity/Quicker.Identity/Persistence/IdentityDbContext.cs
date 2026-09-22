using Microsoft.EntityFrameworkCore;
using Quicker.Identity.Domain;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Identity.Persistence;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<MfaMethod> MfaMethods => Set<MfaMethod>();

    public DbSet<TenantMembership> Memberships => Set<TenantMembership>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<OneTimeToken> OneTimeTokens => Set<OneTimeToken>();

    public DbSet<SsoConnection> SsoConnections => Set<SsoConnection>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<RoleAssignment> Assignments => Set<RoleAssignment>();

    public DbSet<SodRule> SodRules => Set<SodRule>();

    public DbSet<SodException> SodExceptions => Set<SodException>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("users", "control");
            b.HasKey(static u => u.Id);
            b.Property(static u => u.Email).IsRequired();
            b.HasMany(static u => u.MfaMethods).WithOne().HasForeignKey(static m => m.UserId);
            b.HasMany(static u => u.Memberships).WithOne(static m => m.User).HasForeignKey(static m => m.UserId);
        });

        modelBuilder.Entity<MfaMethod>(b =>
        {
            b.ToTable("mfa_methods", "control");
            b.HasKey(static m => m.Id);
        });

        modelBuilder.Entity<TenantMembership>(b =>
        {
            b.ToTable("tenant_memberships", "control");
            b.HasKey(static m => m.Id);
            b.HasIndex(static m => new { m.TenantId, m.UserId }).IsUnique();
        });

        modelBuilder.Entity<Session>(b =>
        {
            b.ToTable("sessions", "control");
            b.HasKey(static s => s.Id);
        });

        modelBuilder.Entity<OneTimeToken>(b =>
        {
            b.ToTable("one_time_tokens", "control");
            b.HasKey(static t => t.Id);
            b.Property(static t => t.Payload).HasColumnType("jsonb");
        });

        modelBuilder.Entity<SsoConnection>(b =>
        {
            b.ToTable("sso_connections", "control");
            b.HasKey(static c => c.Id);
            b.Property(static c => c.GroupRoleMap).HasColumnType("jsonb");
        });

        modelBuilder.Entity<Role>(b =>
        {
            b.ToTable("idn_roles", "app");
            b.HasKey(static r => new { r.TenantId, r.Id });
            b.Property(static r => r.Name).HasColumnName("name_i18n");
            b.HasMany(static r => r.Permissions).WithOne().HasForeignKey(static p => new { p.TenantId, p.RoleId });
            b.HasMany(static r => r.FieldRules).WithOne().HasForeignKey(static f => new { f.TenantId, f.RoleId });
            b.HasMany(static r => r.DocumentTypeRules).WithOne().HasForeignKey(static d => new { d.TenantId, d.RoleId });
        });

        modelBuilder.Entity<RolePermission>(b =>
        {
            b.ToTable("idn_role_permissions", "app");
            b.HasKey(static p => new { p.TenantId, p.RoleId, p.PermissionKey });
        });

        modelBuilder.Entity<RoleAssignment>(b =>
        {
            b.ToTable("idn_role_assignments", "app");
            b.HasKey(static a => new { a.TenantId, a.Id });
            b.HasOne(static a => a.Role).WithMany().HasForeignKey(static a => new { a.TenantId, a.RoleId });
            b.HasMany(static a => a.Scopes).WithOne().HasForeignKey(static s => new { s.TenantId, s.AssignmentId });
        });

        modelBuilder.Entity<AssignmentScope>(b =>
        {
            b.ToTable("idn_assignment_scopes", "app");
            b.HasKey(static s => new { s.TenantId, s.AssignmentId, s.ScopeType, s.ScopeId });
        });

        modelBuilder.Entity<FieldRuleEntity>(b =>
        {
            b.ToTable("idn_field_rules", "app");
            b.HasKey(static f => new { f.TenantId, f.Id });
        });

        modelBuilder.Entity<DocumentTypeRuleEntity>(b =>
        {
            b.ToTable("idn_document_type_rules", "app");
            b.HasKey(static d => new { d.TenantId, d.Id });
        });

        modelBuilder.Entity<SodRule>(b =>
        {
            b.ToTable("idn_sod_rules", "app");
            b.HasKey(static r => new { r.TenantId, r.Id });
            b.Property(static r => r.Rationale).HasColumnName("rationale_i18n");
        });

        modelBuilder.Entity<SodException>(b =>
        {
            b.ToTable("idn_sod_exceptions", "app");
            b.HasKey(static e => new { e.TenantId, e.Id });
        });

        modelBuilder.Entity<ApiKey>(b =>
        {
            b.ToTable("idn_api_keys", "app");
            b.HasKey(static k => new { k.TenantId, k.Id });
        });

        base.OnModelCreating(modelBuilder);
    }
}
