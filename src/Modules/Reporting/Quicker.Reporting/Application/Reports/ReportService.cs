using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Reporting.Application.Semantic;
using Quicker.Reporting.Domain;
using Quicker.Reporting.Persistence;

namespace Quicker.Reporting.Application.Reports;

/// <summary>How a saved report draws: the chart type, whether series stack, which dimension gives the categories and which the series, and which measures plot.</summary>
public sealed record ChartOptions(string Type = "bar", bool Stacked = false, bool Horizontal = false, string? CategoryField = null, string? SeriesField = null, IReadOnlyList<string>? Measures = null);

public sealed record ShareRequest(string ShareWith, Guid? TargetId = null, string Access = ShareAccess.View);

public sealed record SaveReportRequest(
    IReadOnlyDictionary<string, string> Name,
    QueryDefinition Definition,
    string Kind = ReportKinds.Pivot,
    ChartOptions? Chart = null,
    IReadOnlyDictionary<string, string>? Description = null,
    Guid? CompanyId = null,
    IReadOnlyList<ShareRequest>? Shares = null);

public sealed record ShareSummary(Guid Id, string ShareWith, Guid? TargetId, string Access);

public sealed record ReportSummary(
    Guid Id,
    Guid? CompanyId,
    string Model,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyDictionary<string, string> Description,
    string Kind,
    ChartOptions Chart,
    QueryDefinition Definition,
    Guid OwnerMembershipId,
    bool IsOwner,
    bool CanEdit,
    bool IsShared,
    IReadOnlyList<ShareSummary> Shares,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Overrides when running a saved report: another company, another range; everything else is the saved definition.</summary>
public sealed record RunReportRequest(Guid? CompanyId = null, DateOnly? From = null, DateOnly? To = null);

/// <summary>
/// Saved reports (ADR-0034 §4): a member keeps a query under a name, alone or shared with everyone, a role or a
/// member, to view or to edit. Whoever runs it runs it as themselves: the compiler applies the runner's scopes, so a
/// share never widens access. Saving validates the definition by compiling it for the author.
/// </summary>
public sealed class ReportService(ReportingDbContext db, QueryService queries, ICurrentPrincipal principal, IAuditSink audit, IClock clock)
{
    public const string AuditEntity = "saved_report";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ReportSummary>> ListAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var who = principal.Required;
        var query = db.Reports.AsNoTracking().Include(static r => r.Shares).AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(r => r.CompanyId == null || r.CompanyId == c);
        }

        var reports = await query.OrderBy(static r => r.UpdatedAt).ToListAsync(cancellationToken);
        return reports.Where(r => CanSee(r, who)).Select(r => Map(r, who)).OrderBy(static r => r.Name.GetValueOrDefault("en") ?? string.Empty, StringComparer.Ordinal).ToList();
    }

    public async Task<Result<ReportSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var who = principal.Required;
        var report = await db.Reports.AsNoTracking().Include(static r => r.Shares).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        return report is null || !CanSee(report, who) ? Error.NotFound(AuditEntity, id) : Map(report, who);
    }

    public async Task<Result<ReportSummary>> SaveAsync(Guid? id, SaveReportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var who = principal.Required;
        if (request.Name is null || request.Name.Count == 0 || request.Name.Values.All(string.IsNullOrWhiteSpace))
        {
            return Error.Validation("report.name_required", "A name in at least one language is required.");
        }

        if (!ReportKinds.All.Contains(request.Kind, StringComparer.Ordinal))
        {
            return Error.Validation("report.kind_invalid", "A report is a table, a pivot or a chart.").WithWhy(("kind", request.Kind), ("allowed", ReportKinds.All));
        }

        if (request.Definition is null)
        {
            return Error.Validation("report.definition_required", "A report keeps a query definition.");
        }

        // Compiling for the author checks the model, the fields, the filters and the author's reach in one go.
        var checkedDefinition = await queries.ExplainAsync(request.Definition, cancellationToken);
        if (checkedDefinition.IsFailure)
        {
            return checkedDefinition.Error!;
        }

        var shares = ValidateShares(request.Shares);
        if (shares.IsFailure)
        {
            return shares.Error!;
        }

        Report? report = null;
        if (id is { } existing)
        {
            report = await db.Reports.Include(static r => r.Shares).SingleOrDefaultAsync(r => r.Id == existing, cancellationToken);
            if (report is null || !CanSee(report, who))
            {
                return Error.NotFound(AuditEntity, existing);
            }

            if (!CanEdit(report, who))
            {
                return Error.Forbidden("report.not_editable", "The report is shared with you to view, not to change; save a copy instead.");
            }
        }

        var before = report is null ? null : Snapshot(report);
        var now = clock.UtcNow;
        report ??= new Report { Id = Guid.CreateVersion7(), OwnerMembershipId = who.MembershipId.Value, CreatedAt = now };
        report.CompanyId = request.CompanyId ?? request.Definition.CompanyId;
        report.Model = request.Definition.Model;
        report.Name = Text(request.Name);
        report.Description = Text(request.Description);
        report.Definition = JsonSerializer.Serialize(request.Definition, Json);
        report.Kind = request.Kind;
        report.Chart = JsonSerializer.Serialize(request.Chart ?? new ChartOptions(), Json);
        report.UpdatedAt = now;
        if (request.Shares is not null && report.OwnerMembershipId == who.MembershipId.Value)
        {
            // Only the owner decides who else sees the report; an editor changes the query, not the audience.
            ReplaceShares(report, shares.Value, now);
        }

        if (id is null)
        {
            db.Reports.Add(report);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, report.Id, report.Name.Resolve("en", "ar"), id is null ? AuditActions.Created : AuditActions.Updated, Before: before, After: Snapshot(report), CompanyId: report.CompanyId), cancellationToken);
        return Map(report, who);
    }

    public async Task<Result<ReportSummary>> ShareAsync(Guid id, IReadOnlyList<ShareRequest> requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var who = principal.Required;
        var report = await db.Reports.Include(static r => r.Shares).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (report is null || !CanSee(report, who))
        {
            return Error.NotFound(AuditEntity, id);
        }

        if (report.OwnerMembershipId != who.MembershipId.Value)
        {
            return Error.Forbidden("report.not_owner", "Only the owner of a report decides who it is shared with.");
        }

        var shares = ValidateShares(requests);
        if (shares.IsFailure)
        {
            return shares.Error!;
        }

        var before = Snapshot(report);
        ReplaceShares(report, shares.Value, clock.UtcNow);
        report.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, report.Id, report.Name.Resolve("en", "ar"), AuditActions.Updated, Before: before, After: Snapshot(report), CompanyId: report.CompanyId), cancellationToken);
        return Map(report, who);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var who = principal.Required;
        var report = await db.Reports.Include(static r => r.Shares).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (report is null || !CanSee(report, who))
        {
            return Error.NotFound(AuditEntity, id);
        }

        if (report.OwnerMembershipId != who.MembershipId.Value)
        {
            return Error.Forbidden("report.not_owner", "Only the owner deletes a report.");
        }

        db.Shares.RemoveRange(report.Shares);
        db.Reports.Remove(report);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, report.Id, report.Name.Resolve("en", "ar"), AuditActions.Deleted, Before: Snapshot(report), CompanyId: report.CompanyId), cancellationToken);
        return Result.Success();
    }

    /// <summary>Runs the saved definition as the caller, with the company or range changed when asked.</summary>
    public async Task<Result<QueryResult>> RunAsync(Guid id, RunReportRequest? request, CancellationToken cancellationToken)
    {
        var who = principal.Required;
        var report = await db.Reports.AsNoTracking().Include(static r => r.Shares).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (report is null || !CanSee(report, who))
        {
            return Error.NotFound(AuditEntity, id);
        }

        var definition = Definition(report);
        if (request is not null)
        {
            definition = definition with { CompanyId = request.CompanyId ?? definition.CompanyId, From = request.From ?? definition.From, To = request.To ?? definition.To };
        }

        return await queries.RunAsync(definition, cancellationToken);
    }

    private static Result<List<ShareRequest>> ValidateShares(IReadOnlyList<ShareRequest>? requests)
    {
        var result = new List<ShareRequest>();
        foreach (var share in requests ?? [])
        {
            if (!ShareTargets.All.Contains(share.ShareWith, StringComparer.Ordinal))
            {
                return Error.Validation("report.share_target_invalid", "A report is shared with the tenant, a role or a member.").WithWhy(("shareWith", share.ShareWith));
            }

            if (!ShareAccess.All.Contains(share.Access, StringComparer.Ordinal))
            {
                return Error.Validation("report.share_access_invalid", "A share is to view or to edit.").WithWhy(("access", share.Access));
            }

            if (share.ShareWith != ShareTargets.Tenant && share.TargetId is null)
            {
                return Error.Validation("report.share_target_required", "A share with a role or a member names it.").WithWhy(("shareWith", share.ShareWith));
            }

            result.Add(share with { TargetId = share.ShareWith == ShareTargets.Tenant ? null : share.TargetId });
        }

        return result;
    }

    private void ReplaceShares(Report report, List<ShareRequest> shares, DateTimeOffset now)
    {
        db.Shares.RemoveRange(report.Shares);
        report.Shares.Clear();
        foreach (var share in shares.DistinctBy(static s => (s.ShareWith, s.TargetId)))
        {
            report.Shares.Add(new ReportShare { Id = Guid.CreateVersion7(), ReportId = report.Id, ShareWith = share.ShareWith, TargetId = share.TargetId, Access = share.Access, CreatedAt = now });
        }
    }

    private static bool CanSee(Report report, Principal who) =>
        report.OwnerMembershipId == who.MembershipId.Value || report.Shares.Any(s => Reaches(s, who));

    private static bool CanEdit(Report report, Principal who) =>
        report.OwnerMembershipId == who.MembershipId.Value || report.Shares.Any(s => s.Access == ShareAccess.Edit && Reaches(s, who));

    private static bool Reaches(ReportShare share, Principal who) => share.ShareWith switch
    {
        ShareTargets.Tenant => true,
        ShareTargets.Membership => share.TargetId == who.MembershipId.Value,
        ShareTargets.Role => share.TargetId is { } role && who.Roles.Contains(role),
        _ => false,
    };

    private static QueryDefinition Definition(Report report) =>
        JsonSerializer.Deserialize<QueryDefinition>(report.Definition, Json) ?? new QueryDefinition(report.Model, report.CompanyId ?? Guid.Empty);

    private static ReportSummary Map(Report r, Principal who) =>
        new(r.Id, r.CompanyId, r.Model, r.Name.Values, r.Description.Values, r.Kind, JsonSerializer.Deserialize<ChartOptions>(r.Chart, Json) ?? new ChartOptions(), Definition(r), r.OwnerMembershipId,
            r.OwnerMembershipId == who.MembershipId.Value, CanEdit(r, who), r.Shares.Count > 0, r.Shares.Select(static s => new ShareSummary(s.Id, s.ShareWith, s.TargetId, s.Access)).ToList(), r.CreatedAt, r.UpdatedAt);

    private static object Snapshot(Report r) => new { name = r.Name, r.Model, r.Kind, r.CompanyId, shares = r.Shares.Select(static s => new { s.ShareWith, s.TargetId, s.Access }).ToList() };

    private static LocalizedText Text(IReadOnlyDictionary<string, string>? values)
    {
        var text = new LocalizedText();
        foreach (var (language, value) in values ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                text = text.Set(language, value.Trim());
            }
        }

        return text;
    }
}
