using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Kernel.Results;
using Quicker.Organization.Application;
using Quicker.Organization.Contracts;
using Quicker.Web;

namespace Quicker.Organization.Api;

/// <summary>The organization surface of /api/v1: companies, branches, calendars, currencies and rates, dimensions, units, settings.</summary>
public static class OrganizationEndpoints
{
    public static RouteGroupBuilder MapOrganizationEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var org = api.MapGroup("/organization").WithTags("Organization").RequireAuthorization();
        MapCompanies(org);
        MapFiscalCalendars(org);
        MapCurrencies(org);
        MapDimensions(org);
        MapUnits(org);
        MapBusinessCalendars(org);
        return api;
    }

    private static void MapCompanies(RouteGroupBuilder org)
    {
        var companies = org.MapGroup("/companies");
        companies.MapGet("/", async (CompanyService service, CancellationToken ct) => Results.Ok(await service.ListCompaniesAsync(ct)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
        companies.MapGet("/{companyId:guid}", async (Guid companyId, CompanyService service, CancellationToken ct) =>
            await service.GetCompanyAsync(companyId, ct) is { } company ? Results.Ok(company) : ApiProblems.From(Error.NotFound("company", companyId)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
        companies.MapPost("/", async (SaveCompanyRequest request, CompanyService service, CancellationToken ct) =>
            ApiProblems.From(await service.CreateCompanyAsync(request, ct), static c => Results.Created($"/api/v1/organization/companies/{c.Id}", c)))
            .RequirePermission(OrganizationPermissions.CompanyManage)
            .WithSummary("Create a company; its functional currency is enabled and the fiscal year containing today is opened");
        companies.MapPut("/{companyId:guid}", async (Guid companyId, SaveCompanyRequest request, CompanyService service, CancellationToken ct) =>
            ApiProblems.From(await service.UpdateCompanyAsync(companyId, request, ct), static c => Results.Ok(c)))
            .RequirePermission(OrganizationPermissions.CompanyManage);

        companies.MapGet("/{companyId:guid}/branches", async (Guid companyId, CompanyService service, CancellationToken ct) => Results.Ok(await service.ListBranchesAsync(companyId, ct)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
        companies.MapPost("/{companyId:guid}/branches", async (Guid companyId, SaveBranchRequest request, CompanyService service, CancellationToken ct) =>
            ApiProblems.From(await service.CreateBranchAsync(companyId, request, ct), b => Results.Created($"/api/v1/organization/companies/{companyId}/branches/{b.Id}", b)))
            .RequirePermission(OrganizationPermissions.CompanyManage)
            .WithSummary("Create a branch and its BRANCH dimension value");
        companies.MapPut("/{companyId:guid}/branches/{branchId:guid}", async (Guid companyId, Guid branchId, SaveBranchRequest request, CompanyService service, CancellationToken ct) =>
            ApiProblems.From(await service.UpdateBranchAsync(companyId, branchId, request, ct), static b => Results.Ok(b)))
            .RequirePermission(OrganizationPermissions.CompanyManage);

        companies.MapGet("/{companyId:guid}/currencies", async (Guid companyId, CompanyService service, CancellationToken ct) =>
            ApiProblems.From(await service.ListCurrenciesAsync(companyId, ct), static c => Results.Ok(c)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
        companies.MapPut("/{companyId:guid}/currencies", async (Guid companyId, CompanyCurrencyRequest request, CompanyService service, CancellationToken ct) =>
            ApiProblems.From(await service.SetCurrencyAsync(companyId, request, ct), static c => Results.Ok(c)))
            .RequirePermission(OrganizationPermissions.CompanyManage)
            .WithSummary("Enable a currency for the company with its display precision and cash rounding increment");

        companies.MapGet("/{companyId:guid}/settings", async (Guid companyId, CompanyService service, CancellationToken ct) =>
            ApiProblems.From(await service.ListSettingsAsync(companyId, ct), static s => Results.Ok(s)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
        companies.MapPut("/{companyId:guid}/settings/{key}", async (Guid companyId, string key, SettingRequest request, CompanyService service, CancellationToken ct) =>
            ApiProblems.From(await service.SetSettingAsync(companyId, key, request, ct), static s => Results.Ok(s)))
            .RequirePermission(OrganizationPermissions.SettingsManage);

        companies.MapGet("/{companyId:guid}/periods/resolve", async (Guid companyId, DateOnly date, string? module, FiscalCalendarService service, CancellationToken ct) =>
            ApiProblems.From(await service.ResolveAsync(new Kernel.Ids.CompanyId(companyId), date, module ?? PostingModules.GeneralLedger, ct), static p =>
                Results.Ok(new PeriodResolution(p.Period.PeriodId, p.Period.FiscalYearId, p.Period.FiscalYearCode, p.Period.Number, p.Period.StartsOn, p.Period.EndsOn, p.Period.YearStatus, p.Module, p.State))))
            .RequirePermission(OrganizationPermissions.CompanyRead)
            .WithSummary("The fiscal period a posting date falls in and the module's state in it");

        companies.MapGet("/{companyId:guid}/working-days", async (Guid companyId, DateOnly from, int days, string? mode, BusinessCalendarService service, CancellationToken ct) =>
            ApiProblems.From(await service.ComputeForCompanyAsync(companyId, from, days, mode ?? "calendar", ct), static r => Results.Ok(r)))
            .RequirePermission(OrganizationPermissions.CompanyRead)
            .WithSummary("Due-date arithmetic on the company's business calendar: mode=calendar (days then next working day) or mode=working");

        var settings = org.MapGroup("/settings");
        settings.MapGet("/", async (CompanyService service, CancellationToken ct) => ApiProblems.From(await service.ListSettingsAsync(null, ct), static s => Results.Ok(s)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
        settings.MapPut("/{key}", async (string key, SettingRequest request, CompanyService service, CancellationToken ct) =>
            ApiProblems.From(await service.SetSettingAsync(null, key, request, ct), static s => Results.Ok(s)))
            .RequirePermission(OrganizationPermissions.SettingsManage);
    }

    private static void MapFiscalCalendars(RouteGroupBuilder org)
    {
        var calendars = org.MapGroup("/fiscal-calendars");
        calendars.MapGet("/", async (FiscalCalendarService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
        calendars.MapGet("/{calendarId:guid}", async (Guid calendarId, FiscalCalendarService service, CancellationToken ct) =>
            await service.GetAsync(calendarId, ct) is { } calendar ? Results.Ok(calendar) : ApiProblems.From(Error.NotFound("fiscal_calendar", calendarId)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
        calendars.MapPost("/", async (SaveFiscalCalendarRequest request, FiscalCalendarService service, CancellationToken ct) =>
            ApiProblems.From(await service.CreateAsync(request, ct), static c => Results.Created($"/api/v1/organization/fiscal-calendars/{c.Id}", c)))
            .RequirePermission(OrganizationPermissions.CalendarManage);
        calendars.MapPut("/{calendarId:guid}", async (Guid calendarId, SaveFiscalCalendarRequest request, FiscalCalendarService service, CancellationToken ct) =>
            ApiProblems.From(await service.UpdateAsync(calendarId, request, ct), static c => Results.Ok(c)))
            .RequirePermission(OrganizationPermissions.CalendarManage);
        calendars.MapPost("/{calendarId:guid}/years", async (Guid calendarId, OpenFiscalYearRequest request, FiscalCalendarService service, CancellationToken ct) =>
            ApiProblems.From(await service.OpenYearAsync(calendarId, request, ct), static y => Results.Created($"/api/v1/organization/fiscal-calendars/{y.CalendarId}", y)))
            .RequirePermission(OrganizationPermissions.CalendarManage)
            .WithSummary("Open the fiscal year starting in a given calendar year, with its 12 periods (plus the adjustment period on 13-period calendars)");

        var periods = org.MapGroup("/periods");
        periods.MapGet("/{periodId:guid}/states", async (Guid periodId, Guid companyId, FiscalCalendarService service, CancellationToken ct) =>
            ApiProblems.From(await service.ListStatesAsync(periodId, companyId, ct), static s => Results.Ok(s)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
        periods.MapPut("/{periodId:guid}/states", async (Guid periodId, SetPeriodStateRequest request, FiscalCalendarService service, CancellationToken ct) =>
            ApiProblems.From(await service.SetStateAsync(periodId, request, ct), static s => Results.Ok(s)))
            .RequirePermission(OrganizationPermissions.PeriodManage)
            .WithSummary("Open, soft-close or hard-close a period for a company and modules; hard-closed periods need the reopen endpoint");
        periods.MapPost("/{periodId:guid}/reopen", async (Guid periodId, ReopenPeriodRequest request, FiscalCalendarService service, CancellationToken ct) =>
            ApiProblems.From(await service.ReopenAsync(periodId, request, ct), static s => Results.Ok(s)))
            .RequirePermission(OrganizationPermissions.PeriodReopen)
            .RequireRecentAuth()
            .WithSummary("Reopen a hard-closed period: reason required, recent authentication required, audited as an override");
    }

    private static void MapCurrencies(RouteGroupBuilder org)
    {
        org.MapGet("/currencies", async (CurrencyService service, CancellationToken ct) => Results.Ok(await service.ListCurrenciesAsync(ct)))
            .RequirePermission(OrganizationPermissions.CompanyRead)
            .WithSummary("ISO 4217 reference list");

        var rateTypes = org.MapGroup("/rate-types");
        rateTypes.MapGet("/", async (CurrencyService service, CancellationToken ct) => Results.Ok(await service.ListRateTypesAsync(ct)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
        rateTypes.MapPost("/", async (SaveRateTypeRequest request, CurrencyService service, CancellationToken ct) =>
            ApiProblems.From(await service.CreateRateTypeAsync(request, ct), static t => Results.Created($"/api/v1/organization/rate-types/{t.Id}", t)))
            .RequirePermission(OrganizationPermissions.CurrencyManage);

        var rates = org.MapGroup("/rates");
        rates.MapGet("/", async (string? rateType, string? from, string? to, DateOnly? fromDate, DateOnly? toDate, int? limit, CurrencyService service, CancellationToken ct) =>
            ApiProblems.From(await service.ListRatesAsync(rateType, from, to, fromDate, toDate, limit ?? 200, ct), static r => Results.Ok(r)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
        rates.MapPost("/", async (SaveRateRequest request, CurrencyService service, CancellationToken ct) =>
            ApiProblems.From(await service.SaveRateAsync(request, ct), static r => Results.Created($"/api/v1/organization/rates/{r.Id}", r)))
            .RequirePermission(OrganizationPermissions.RateManage)
            .WithSummary("Enter a rate (1 from = rate to) valid from a date; correcting an existing one needs a reason");
        rates.MapDelete("/{rateId:guid}", async (Guid rateId, string? reason, CurrencyService service, CancellationToken ct) =>
            ApiProblems.From(await service.DeleteRateAsync(rateId, reason, ct), static () => Results.NoContent()))
            .RequirePermission(OrganizationPermissions.RateManage);
        rates.MapGet("/resolve", async (Guid companyId, string from, string to, DateOnly date, string? rateType, CurrencyService service, CancellationToken ct) =>
            ApiProblems.From(await service.ResolveForApiAsync(companyId, from, to, date, rateType ?? RateTypes.Spot, ct), static r => Results.Ok(r)))
            .RequirePermission(OrganizationPermissions.CompanyRead)
            .WithSummary("Resolve a rate for a date: direct, inverse, or cross through the company's functional currency");
        rates.MapGet("/providers", (CurrencyService service) => Results.Ok(service.ProviderCodes))
            .RequirePermission(OrganizationPermissions.CompanyRead);
        rates.MapPost("/import", async (ImportRatesRequest request, CurrencyService service, CancellationToken ct) =>
            ApiProblems.From(await service.ImportAsync(request, ct), static r => Results.Ok(r)))
            .RequirePermission(OrganizationPermissions.RateImport)
            .WithSummary("Import a provider's rates into a rate type; manual rates on the same date are never overwritten");
    }

    private static void MapDimensions(RouteGroupBuilder org)
    {
        var dimensions = org.MapGroup("/dimensions");
        dimensions.MapGet("/", async (DimensionService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
        dimensions.MapPost("/", async (SaveDimensionRequest request, DimensionService service, CancellationToken ct) =>
            ApiProblems.From(await service.CreateAsync(request, ct), static d => Results.Created($"/api/v1/organization/dimensions/{d.Id}", d)))
            .RequirePermission(OrganizationPermissions.DimensionManage);
        dimensions.MapPut("/{dimensionId:guid}", async (Guid dimensionId, SaveDimensionRequest request, DimensionService service, CancellationToken ct) =>
            ApiProblems.From(await service.UpdateAsync(dimensionId, request, ct), static d => Results.Ok(d)))
            .RequirePermission(OrganizationPermissions.DimensionManage);
        dimensions.MapGet("/{dimensionId:guid}/values", async (Guid dimensionId, DimensionService service, CancellationToken ct) =>
            ApiProblems.From(await service.ListValuesAsync(dimensionId, ct), static v => Results.Ok(v)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
        dimensions.MapPost("/{dimensionId:guid}/values", async (Guid dimensionId, SaveDimensionValueRequest request, DimensionService service, CancellationToken ct) =>
            ApiProblems.From(await service.CreateValueAsync(dimensionId, request, ct), v => Results.Created($"/api/v1/organization/dimensions/{dimensionId}/values/{v.Id}", v)))
            .RequirePermission(OrganizationPermissions.DimensionManage);
        dimensions.MapPut("/{dimensionId:guid}/values/{valueId:guid}", async (Guid dimensionId, Guid valueId, SaveDimensionValueRequest request, DimensionService service, CancellationToken ct) =>
            ApiProblems.From(await service.UpdateValueAsync(dimensionId, valueId, request, ct), static v => Results.Ok(v)))
            .RequirePermission(OrganizationPermissions.DimensionManage);

        var sets = org.MapGroup("/dimension-sets");
        sets.MapPost("/", async (DimensionSetRequest request, DimensionService service, CancellationToken ct) =>
            ApiProblems.From(await service.GetOrCreateSetAsync(request, ct), static s => Results.Ok(s)))
            .RequirePermission(OrganizationPermissions.CompanyRead)
            .WithSummary("Get or create the set for a combination of dimension values (one id per combination)");
        sets.MapGet("/{setId:guid}", async (Guid setId, DimensionService service, CancellationToken ct) =>
            await service.GetSetAsync(setId, ct) is { } set ? Results.Ok(set) : ApiProblems.From(Error.NotFound("dimension_set", setId)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
    }

    private static void MapUnits(RouteGroupBuilder org)
    {
        var uoms = org.MapGroup("/uoms");
        uoms.MapGet("/", async (UomService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
        uoms.MapPost("/", async (SaveUomRequest request, UomService service, CancellationToken ct) =>
            ApiProblems.From(await service.CreateAsync(request, ct), static u => Results.Created($"/api/v1/organization/uoms/{u.Id}", u)))
            .RequirePermission(OrganizationPermissions.UomManage);
        uoms.MapPut("/{uomId:guid}", async (Guid uomId, SaveUomRequest request, UomService service, CancellationToken ct) =>
            ApiProblems.From(await service.UpdateAsync(uomId, request, ct), static u => Results.Ok(u)))
            .RequirePermission(OrganizationPermissions.UomManage);

        var conversions = org.MapGroup("/uom-conversions");
        conversions.MapGet("/", async (UomService service, CancellationToken ct) => Results.Ok(await service.ListConversionsAsync(ct)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
        conversions.MapPost("/", async (SaveUomConversionRequest request, UomService service, CancellationToken ct) =>
            ApiProblems.From(await service.SaveConversionAsync(request, ct), static c => Results.Ok(c)))
            .RequirePermission(OrganizationPermissions.UomManage)
            .WithSummary("Define from × numerator / denominator = to within one family; the reverse direction is derived");
        conversions.MapDelete("/{conversionId:guid}", async (Guid conversionId, UomService service, CancellationToken ct) =>
            ApiProblems.From(await service.DeleteConversionAsync(conversionId, ct), static () => Results.NoContent()))
            .RequirePermission(OrganizationPermissions.UomManage);
        conversions.MapGet("/convert", async (Guid from, Guid to, decimal value, UomService service, CancellationToken ct) =>
            ApiProblems.From(await service.ConvertAsync(from, to, value, ct), static c => Results.Ok(c)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
    }

    private static void MapBusinessCalendars(RouteGroupBuilder org)
    {
        var calendars = org.MapGroup("/business-calendars");
        calendars.MapGet("/", async (BusinessCalendarService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
        calendars.MapGet("/{calendarId:guid}", async (Guid calendarId, BusinessCalendarService service, CancellationToken ct) =>
            await service.GetAsync(calendarId, ct) is { } calendar ? Results.Ok(calendar) : ApiProblems.From(Error.NotFound("business_calendar", calendarId)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
        calendars.MapPost("/", async (SaveBusinessCalendarRequest request, BusinessCalendarService service, CancellationToken ct) =>
            ApiProblems.From(await service.CreateAsync(request, ct), static c => Results.Created($"/api/v1/organization/business-calendars/{c.Id}", c)))
            .RequirePermission(OrganizationPermissions.CalendarManage);
        calendars.MapPut("/{calendarId:guid}", async (Guid calendarId, SaveBusinessCalendarRequest request, BusinessCalendarService service, CancellationToken ct) =>
            ApiProblems.From(await service.UpdateAsync(calendarId, request, ct), static c => Results.Ok(c)))
            .RequirePermission(OrganizationPermissions.CalendarManage);
        calendars.MapPost("/{calendarId:guid}/holidays", async (Guid calendarId, SaveHolidayRequest request, BusinessCalendarService service, CancellationToken ct) =>
            ApiProblems.From(await service.AddHolidayAsync(calendarId, request, ct), static h => Results.Ok(h)))
            .RequirePermission(OrganizationPermissions.CalendarManage);
        calendars.MapDelete("/{calendarId:guid}/holidays/{holidayId:guid}", async (Guid calendarId, Guid holidayId, BusinessCalendarService service, CancellationToken ct) =>
            ApiProblems.From(await service.RemoveHolidayAsync(calendarId, holidayId, ct), static () => Results.NoContent()))
            .RequirePermission(OrganizationPermissions.CalendarManage);
        calendars.MapGet("/{calendarId:guid}/working-days", async (Guid calendarId, DateOnly from, int days, string? mode, BusinessCalendarService service, CancellationToken ct) =>
            ApiProblems.From(await service.ComputeAsync(calendarId, from, days, mode ?? "calendar", ct), static r => Results.Ok(r)))
            .RequirePermission(OrganizationPermissions.CompanyRead);
    }
}
