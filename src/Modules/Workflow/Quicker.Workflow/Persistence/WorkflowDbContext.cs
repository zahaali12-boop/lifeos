using Microsoft.EntityFrameworkCore;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;
using Quicker.Workflow.Domain;

namespace Quicker.Workflow.Persistence;

public sealed class WorkflowDbContext(DbContextOptions<WorkflowDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<Definition> Definitions => Set<Definition>();

    public DbSet<Rule> Rules => Set<Rule>();

    public DbSet<Step> Steps => Set<Step>();

    public DbSet<Request> Requests => Set<Request>();

    public DbSet<RequestStep> RequestSteps => Set<RequestStep>();

    public DbSet<RequestAction> Actions => Set<RequestAction>();

    public DbSet<Block> Blocks => Set<Block>();

    public DbSet<Override> Overrides => Set<Override>();

    public DbSet<Delegation> Delegations => Set<Delegation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Definition>(b =>
        {
            b.ToTable("wf_definitions", "app");
            b.HasKey(static d => new { d.TenantId, d.Id });
            b.Property(static d => d.Name).HasColumnName("name_i18n");
            b.Property(static d => d.Description).HasColumnName("description_i18n");
            b.HasMany(static d => d.Rules).WithOne().HasForeignKey(static r => new { r.TenantId, r.DefinitionId });
            b.HasAuditTrail("workflow_definition", static d => d.EntityType + " v" + d.Version);
        });

        modelBuilder.Entity<Rule>(b =>
        {
            b.ToTable("wf_rules", "app");
            b.HasKey(static r => new { r.TenantId, r.Id });
            b.Property(static r => r.Name).HasColumnName("name_i18n");
            b.HasMany(static r => r.Steps).WithOne().HasForeignKey(static s => new { s.TenantId, s.RuleId });
        });

        modelBuilder.Entity<Step>(b =>
        {
            b.ToTable("wf_steps", "app");
            b.HasKey(static s => new { s.TenantId, s.Id });
            b.Property(static s => s.Name).HasColumnName("name_i18n");
            b.Property(static s => s.ApproverSpec).HasColumnType("jsonb");
            b.Property(static s => s.Escalation).HasColumnType("jsonb");
        });

        modelBuilder.Entity<Block>(b =>
        {
            b.ToTable("wf_blocks", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Why).HasColumnType("jsonb");
            b.HasAuditTrail("workflow_block", static x => x.Kind + " " + x.Display);
        });

        modelBuilder.Entity<Request>(b =>
        {
            b.ToTable("wf_requests", "app");
            b.HasKey(static r => new { r.TenantId, r.Id });
            b.Property(static r => r.RuleName).HasColumnName("rule_name_i18n");
            b.Property(static r => r.Evaluation).HasColumnType("jsonb");
            b.Property(static r => r.Subject).HasColumnType("jsonb");
            b.HasOne<Definition>().WithMany().HasForeignKey(static r => new { r.TenantId, r.DefinitionId });
            b.HasOne<Block>().WithMany().HasForeignKey(static r => new { r.TenantId, r.BlockId });
            b.HasMany(static r => r.Steps).WithOne().HasForeignKey(static s => new { s.TenantId, s.RequestId });
            b.HasAuditTrail("workflow_request", static r => r.Display);
        });

        modelBuilder.Entity<RequestStep>(b =>
        {
            b.ToTable("wf_request_steps", "app");
            b.HasKey(static s => new { s.TenantId, s.Id });
            b.Property(static s => s.Name).HasColumnName("name_i18n");
            b.Property(static s => s.Approvers).HasColumnType("jsonb");
            b.Property(static s => s.ApprovedBy).HasColumnType("jsonb");
            b.Property(static s => s.Escalation).HasColumnType("jsonb");
        });

        modelBuilder.Entity<RequestAction>(b =>
        {
            b.ToTable("wf_actions", "app");
            b.HasKey(static a => new { a.TenantId, a.Id });
            b.HasOne<Request>().WithMany().HasForeignKey(static a => new { a.TenantId, a.RequestId });
        });

        modelBuilder.Entity<Override>(b =>
        {
            b.ToTable("wf_overrides", "app");
            b.HasKey(static o => new { o.TenantId, o.Id });
            b.HasOne<Block>().WithMany().HasForeignKey(static o => new { o.TenantId, o.BlockId });
            b.HasOne<Request>().WithMany().HasForeignKey(static o => new { o.TenantId, o.RequestId });
            b.HasAuditTrail("workflow_override", static o => o.Reason);
        });

        modelBuilder.Entity<Delegation>(b =>
        {
            b.ToTable("wf_delegations", "app");
            b.HasKey(static d => new { d.TenantId, d.Id });
            b.HasOne<Definition>().WithMany().HasForeignKey(static d => new { d.TenantId, d.DefinitionId });
            b.HasAuditTrail("workflow_delegation", static d => d.FromMembershipId + " → " + d.ToMembershipId);
        });

        base.OnModelCreating(modelBuilder);
    }
}
