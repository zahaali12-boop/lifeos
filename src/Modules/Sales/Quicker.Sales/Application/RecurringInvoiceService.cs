using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Messaging.Jobs;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Persistence;
using Quicker.Sales.Domain;
using Quicker.Sales.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Sales.Application;

/// <summary>
/// Recurring invoices (roadmap 5.6b, A-160): a customer's direct invoice (items and service lines) issued on a cron
/// schedule in the company's time zone, as the recurring journals of 2.7 are: each run creates the invoice for the run
/// date through <see cref="InvoiceService"/> (priced and taxed on that date), posts it when the template says so or
/// leaves it as a draft to review, and advances the next run; the daily job generates every run due, catching up at most
/// a year, and a run that cannot be made (a blocked customer, a missing rate) waits with its reason.
/// </summary>
public sealed class RecurringInvoiceService(SalesDbContext db, InvoiceService invoices, ICompanyDirectory companies, ICustomerDirectory customers, ICurrentPrincipal principal, IAuditSink audit, IClock clock)
{
    public const string EntityType = "sales_recurring_invoice";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<RecurringInvoiceSummary>> ListAsync(Guid? companyId, Guid? partnerId, bool? active, CancellationToken cancellationToken)
    {
        var query = db.RecurringInvoices.AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(r => r.CompanyId == c);
        }

        if (partnerId is { } p)
        {
            query = query.Where(r => r.PartnerId == p);
        }

        if (active is { } a)
        {
            query = query.Where(r => r.IsActive == a);
        }

        var templates = await query.OrderBy(static r => r.Code).Take(500).ToListAsync(cancellationToken);
        var result = new List<RecurringInvoiceSummary>(templates.Count);
        foreach (var template in templates)
        {
            result.Add(await MapAsync(template, cancellationToken));
        }

        return result;
    }

    public async Task<Result<RecurringInvoiceSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var template = await db.RecurringInvoices.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        return template is null ? Error.NotFound(EntityType, id) : await MapAsync(template, cancellationToken);
    }

    public async Task<Result<RecurringInvoiceSummary>> CreateAsync(SaveRecurringInvoiceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var template = new SalesRecurringInvoice { Id = Guid.CreateVersion7(), CreatedBy = principal.Principal?.MembershipId.Value, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        var applied = await ApplyAsync(template, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        db.RecurringInvoices.Add(template);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(EntityType, template.Id, template.Code, AuditActions.Created, After: new { template.PartnerId, template.Cron, template.NextRunOn, template.AutoPost }, CompanyId: template.CompanyId), cancellationToken);
        return await MapAsync(template, cancellationToken);
    }

    public async Task<Result<RecurringInvoiceSummary>> UpdateAsync(Guid id, SaveRecurringInvoiceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var template = await db.RecurringInvoices.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (template is null)
        {
            return Error.NotFound(EntityType, id);
        }

        if (request.CompanyId != template.CompanyId)
        {
            return Error.Validation("recurring_invoice.company_fixed", "A recurring invoice keeps its company.").WithWhy(("companyId", template.CompanyId));
        }

        var applied = await ApplyAsync(template, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        template.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(EntityType, template.Id, template.Code, AuditActions.Updated, After: new { template.Cron, template.NextRunOn, template.AutoPost, template.IsActive }, CompanyId: template.CompanyId), cancellationToken);
        return await MapAsync(template, cancellationToken);
    }

    /// <summary>Issues the invoice for one run (the next due date unless another is given) and advances the schedule.</summary>
    public async Task<Result<SalesInvoiceSummary>> GenerateAsync(Guid id, GenerateRecurringInvoiceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var template = await db.RecurringInvoices.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (template is null)
        {
            return Error.NotFound(EntityType, id);
        }

        if (!template.IsActive)
        {
            return Error.Conflict("recurring_invoice.inactive", $"Recurring invoice '{template.Code}' is inactive.");
        }

        if (request.RunDate is null && template.NextRunOn is null)
        {
            return Error.Conflict("recurring_invoice.ended", $"Recurring invoice '{template.Code}' has no run due; give a date to issue one out of schedule.").WithWhy(("endsOn", template.EndsOn), ("lastGeneratedOn", template.LastGeneratedOn));
        }

        var company = (await companies.FindAsync(new CompanyId(template.CompanyId), cancellationToken))!;
        var runDate = request.RunDate ?? template.NextRunOn!.Value;
        if (template.EndsOn is { } ends && runDate > ends)
        {
            return Error.Conflict("recurring_invoice.ended", $"Recurring invoice '{template.Code}' ended on {ends:yyyy-MM-dd}.").WithWhy(("endsOn", ends), ("runDate", runDate));
        }

        var lines = JsonSerializer.Deserialize<List<SaveSalesInvoiceLineRequest>>(template.Lines, Json) ?? [];
        var created = await invoices.CreateAsync(new SaveSalesInvoiceRequest(template.CompanyId, template.PartnerId, lines, "invoice", template.CustomerReference, null, runDate, runDate, template.Currency, template.PaymentTermsId, null, null, null, template.Notes), template.Id, cancellationToken);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        var invoice = created.Value;
        if (template.AutoPost)
        {
            var posted = await invoices.PostAsync(invoice.Id, cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error!;
            }

            invoice = posted.Value;
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(template.TimeZone);
        template.LastGeneratedOn = runDate;
        var next = NextRun(template.Cron, runDate, timeZone);
        template.NextRunOn = next is { } n && (template.EndsOn is null || n <= template.EndsOn) ? n : null;
        template.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(EntityType, template.Id, template.Code, "generated", After: new { runDate, invoiceId = invoice.Id, invoice.Number, invoice.Status, nextRunOn = template.NextRunOn }, CompanyId: template.CompanyId), cancellationToken);
        return invoice;
    }

    /// <summary>Every active recurring invoice due on or before the date, one run per due date (catching up at most a year); a run that fails stops that template and reports why.</summary>
    public async Task<IReadOnlyList<RecurringInvoiceRun>> GenerateDueAsync(Guid? companyId, DateOnly asOf, CancellationToken cancellationToken)
    {
        var due = await db.RecurringInvoices.AsNoTracking()
            .Where(r => r.IsActive && r.NextRunOn != null && r.NextRunOn <= asOf && (companyId == null || r.CompanyId == companyId))
            .OrderBy(static r => r.Code)
            .Select(static r => new { r.Id, r.Code })
            .ToListAsync(cancellationToken);
        var runs = new List<RecurringInvoiceRun>();
        foreach (var template in due)
        {
            for (var guard = 0; guard < 366; guard++)
            {
                var next = await db.RecurringInvoices.AsNoTracking().Where(r => r.Id == template.Id).Select(static r => r.NextRunOn).SingleAsync(cancellationToken);
                if (next is not { } run || run > asOf)
                {
                    break;
                }

                var generated = await GenerateAsync(template.Id, new GenerateRecurringInvoiceRequest(run), cancellationToken);
                if (generated.IsFailure)
                {
                    runs.Add(new RecurringInvoiceRun(template.Id, template.Code, run, "waiting", null, null, generated.Error!.Code));
                    break;
                }

                runs.Add(new RecurringInvoiceRun(template.Id, template.Code, run, "generated", generated.Value.Id, generated.Value.Number, null));
            }
        }

        return runs;
    }

    private async Task<Result> ApplyAsync(SalesRecurringInvoice template, SaveRecurringInvoiceRequest request, CancellationToken cancellationToken)
    {
        var code = Shared.Trim(request.Code);
        if (code is null)
        {
            return Error.Validation("recurring_invoice.code_required", "A recurring invoice has a code.");
        }

        if (request.Name is null || string.IsNullOrWhiteSpace(request.Name.GetValueOrDefault("en")) || string.IsNullOrWhiteSpace(request.Name.GetValueOrDefault("ar")))
        {
            return Error.Validation("recurring_invoice.name_required", "A recurring invoice is named in English and Arabic.");
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.Validation("recurring_invoice.company_unknown", "The company does not exist.").WithWhy(("companyId", request.CompanyId));
        }

        var customer = await customers.EnsureCustomerAsync(request.CompanyId, request.PartnerId, CustomerPurposes.Invoice, cancellationToken);
        if (customer.IsFailure)
        {
            return customer.Error!;
        }

        var cron = CronExpression.Parse(request.Cron);
        if (cron.IsFailure)
        {
            return cron.Error!;
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("recurring_invoice.lines_required", "A recurring invoice has at least one line.");
        }

        if (request.Lines.Any(static l => l.Kind is not ("item" or "service")))
        {
            return Error.Validation("recurring_invoice.line_kind_invalid", "A recurring invoice bills items and service lines; shipments and orders are invoiced as they happen.");
        }

        var currency = await Shared.CurrencyAsync(companies, request.Currency, customer.Value.Currency, "recurring_invoice", cancellationToken);
        if (currency.IsFailure)
        {
            return currency.Error!;
        }

        if (await db.RecurringInvoices.AsNoTracking().AnyAsync(r => r.CompanyId == request.CompanyId && r.Code == code && r.Id != template.Id, cancellationToken))
        {
            return Error.Conflict("recurring_invoice.code_taken", "Another recurring invoice of the company has this code.").WithWhy(("code", code));
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(company.TimeZone);
        var startsOn = request.StartsOn ?? clock.TodayIn(company.TimeZone);
        if (request.EndsOn is { } endsOn && endsOn < startsOn)
        {
            return Error.Validation("recurring_invoice.ends_before_start", "The schedule ends on or after it starts.").WithWhy(("startsOn", startsOn), ("endsOn", endsOn));
        }

        template.CompanyId = request.CompanyId;
        template.PartnerId = request.PartnerId;
        template.Code = code;
        template.Name = new LocalizedText(request.Name);
        template.Cron = cron.Value.Text;
        template.TimeZone = company.TimeZone;
        template.EndsOn = request.EndsOn;
        template.Currency = currency.Value.Code;
        template.Lines = JsonSerializer.Serialize(request.Lines, Json);
        template.CustomerReference = Shared.Trim(request.CustomerReference);
        template.PaymentTermsId = request.PaymentTermsId;
        template.AutoPost = request.AutoPost;
        template.IsActive = request.IsActive;
        template.Notes = Shared.Trim(request.Notes);
        // The first run is the first cron date on or after the start; a template that already ran keeps its place.
        if (template.LastGeneratedOn is null || request.StartsOn is not null)
        {
            var first = NextRun(template.Cron, startsOn.AddDays(-1), timeZone);
            template.NextRunOn = first is { } f && (template.EndsOn is null || f <= template.EndsOn) ? f : null;
        }

        return Result.Success();
    }

    private static DateOnly? NextRun(string cronText, DateOnly after, TimeZoneInfo timeZone)
    {
        var cron = CronExpression.Parse(cronText);
        if (cron.IsFailure)
        {
            return null;
        }

        var from = new DateTimeOffset(after.ToDateTime(TimeOnly.MaxValue), timeZone.GetUtcOffset(after.ToDateTime(TimeOnly.MinValue)));
        var next = cron.Value.Next(from, timeZone);
        return next is { } n ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(n, timeZone).Date) : null;
    }

    private async Task<RecurringInvoiceSummary> MapAsync(SalesRecurringInvoice t, CancellationToken cancellationToken)
    {
        var partner = await customers.FindCustomerAsync(t.CompanyId, t.PartnerId, cancellationToken);
        var generated = await db.Invoices.AsNoTracking().CountAsync(i => i.RecurringInvoiceId == t.Id, cancellationToken);
        return new RecurringInvoiceSummary(t.Id, t.CompanyId, t.PartnerId, partner?.PartnerCode ?? string.Empty, partner?.PartnerName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), t.Code, t.Name.Values, t.Cron, t.TimeZone, t.NextRunOn, t.EndsOn, t.Currency,
            JsonSerializer.Deserialize<List<SaveSalesInvoiceLineRequest>>(t.Lines, Json) ?? [], t.CustomerReference, t.PaymentTermsId, t.AutoPost, t.IsActive, t.LastGeneratedOn, t.Notes, generated, t.UpdatedAt);
    }
}

public sealed record RecurringInvoicePayload(DateOnly? AsOf = null);

/// <summary>Platform schedule <c>sales.invoices.recur</c>: the recurring invoices due in every tenant, each tenant in its own unit of work, as the accounting routines run.</summary>
public sealed class RecurringInvoiceJob(IUnitOfWorkFactory unitOfWorkFactory, IServiceScopeFactory scopeFactory, ITenantContextAccessor tenantContext, RecurringInvoiceService recurring, IClock clock) : IJobHandler<RecurringInvoicePayload>
{
    public static string JobType => "sales.invoices.recur";

    public async Task<object?> ExecuteAsync(RecurringInvoicePayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var asOf = payload?.AsOf ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        if (context.TenantId is not null)
        {
            var runs = await recurring.GenerateDueAsync(null, asOf, cancellationToken);
            return new { generated = runs.Count(static r => r.Outcome == "generated"), waiting = runs.Count(static r => r.Outcome == "waiting") };
        }

        var generated = 0;
        var waiting = 0;
        var tenants = await InTenantAsync(null, static (sp, ct) => sp.GetRequiredService<ITenantDirectory>().ListAsync(ct), cancellationToken);
        foreach (var tenant in tenants.Where(static t => t.Status == "active"))
        {
            var runs = await InTenantAsync(tenant.Id.Value, (sp, ct) => sp.GetRequiredService<RecurringInvoiceService>().GenerateDueAsync(null, asOf, ct), cancellationToken);
            generated += runs.Count(static r => r.Outcome == "generated");
            waiting += runs.Count(static r => r.Outcome == "waiting");
        }

        return new { tenants = tenants.Count, generated, waiting };
    }

    private async Task<T> InTenantAsync<T>(Guid? tenantId, Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var requestId = "job-" + Guid.CreateVersion7().ToString("N")[^12..];
        var context = tenantId is { } id ? TenantContext.System(new TenantId(id), requestId) : TenantContext.Anonymous(requestId);
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(context, cancellationToken: cancellationToken);
        scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(unitOfWork);
        using var ambient = tenantContext.Use(context);
        var result = await work(scope.ServiceProvider, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return result;
    }
}
