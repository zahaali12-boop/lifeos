using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;
using Quicker.Organization.Domain;

namespace Quicker.Organization.Tests;

/// <summary>A-005: a due date skips Friday and Saturday in Iraq and public holidays; working-day arithmetic per company.</summary>
[Collection(ApiCollection.Name)]
public sealed class BusinessCalendarTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    [Fact]
    public async Task A_due_date_skips_Friday_and_Saturday_in_Iraq_and_public_holidays()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.CreateCompanyAsync("DUECO");
        var companyId = company.GetProperty("id").GetGuid();
        var calendarId = company.GetProperty("businessCalendarId").GetGuid();
        (await owner.GetOkAsync($"/api/v1/organization/business-calendars/{calendarId}")).GetProperty("code").GetString().ShouldBe("sun_thu");

        // 2026-09-24 is a Thursday: net 1 day lands on Friday, so the due date moves to Sunday the 27th.
        var due = await owner.GetOkAsync($"/api/v1/organization/companies/{companyId}/working-days?from=2026-09-24&days=1");
        due.GetProperty("mode").GetString().ShouldBe("calendar");
        due.GetProperty("fromIsWorkingDay").GetBoolean().ShouldBeTrue();
        due.GetProperty("result").GetString().ShouldBe("2026-09-27");
        (await owner.GetOkAsync($"/api/v1/organization/companies/{companyId}/working-days?from=2026-09-24&days=30")).GetProperty("result").GetString().ShouldBe("2026-10-25"); // Oct 24 is a Saturday
        (await owner.GetOkAsync($"/api/v1/organization/companies/{companyId}/working-days?from=2026-09-24&days=0")).GetProperty("result").GetString().ShouldBe("2026-09-24");
        (await owner.GetOkAsync($"/api/v1/organization/companies/{companyId}/working-days?from=2026-09-25&days=0")).GetProperty("result").GetString().ShouldBe("2026-09-27");

        // Working-day mode counts only working days: Thursday + 2 working days = Monday.
        (await owner.GetOkAsync($"/api/v1/organization/companies/{companyId}/working-days?from=2026-09-24&days=2&mode=working")).GetProperty("result").GetString().ShouldBe("2026-09-28");

        // A holiday on the Sunday pushes both computations to Monday.
        var holiday = await owner.PostAsync($"/api/v1/organization/business-calendars/{calendarId}/holidays", new { onDate = "2026-09-27", name = new { en = "Founding day", ar = "يوم التأسيس" } }, HttpStatusCode.OK);
        (await owner.GetOkAsync($"/api/v1/organization/companies/{companyId}/working-days?from=2026-09-24&days=1")).GetProperty("result").GetString().ShouldBe("2026-09-28");
        (await owner.GetOkAsync($"/api/v1/organization/business-calendars/{calendarId}/working-days?from=2026-09-24&days=1&mode=working")).GetProperty("result").GetString().ShouldBe("2026-09-28");
        (await owner.DeleteAsync($"/api/v1/organization/business-calendars/{calendarId}/holidays/{holiday.GetProperty("id").GetGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetOkAsync($"/api/v1/organization/companies/{companyId}/working-days?from=2026-09-24&days=1")).GetProperty("result").GetString().ShouldBe("2026-09-27");

        // A company on the Monday–Friday week is due on the Friday.
        var monFri = (await owner.GetOkAsync("/api/v1/organization/business-calendars")).EnumerateArray().Single(static c => c.Str("code") == "mon_fri").GetProperty("id").GetGuid();
        var euro = await owner.PostAsync("/api/v1/organization/companies", new { code = "EUCO", legalName = new { en = "EU Co" }, country = "DE", functionalCurrency = "EUR", timeZone = "Europe/Berlin", businessCalendarId = monFri });
        (await owner.GetOkAsync($"/api/v1/organization/companies/{euro.GetProperty("id").GetGuid()}/working-days?from=2026-09-24&days=1")).GetProperty("result").GetString().ShouldBe("2026-09-25");

        (await owner.GetErrorAsync($"/api/v1/organization/companies/{companyId}/working-days?from=2026-09-24&days=-1", HttpStatusCode.UnprocessableEntity)).ShouldBe("working_days.negative");
        (await owner.GetErrorAsync($"/api/v1/organization/companies/{companyId}/working-days?from=2026-09-24&days=1&mode=lunar", HttpStatusCode.UnprocessableEntity)).ShouldBe("working_days.mode.invalid");
    }

    [Fact]
    public async Task Calendars_are_admin_data_with_validated_working_weeks()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var created = await owner.PostAsync("/api/v1/organization/business-calendars", new { code = "six_day", name = new { en = "Saturday to Thursday", ar = "السبت إلى الخميس" }, workingDays = new[] { 6, 0, 1, 2, 3, 4, 4 } });
        created.GetProperty("workingDays").EnumerateArray().Select(static d => d.GetInt32()).ShouldBe([0, 1, 2, 3, 4, 6]);
        (await owner.GetOkAsync($"/api/v1/organization/business-calendars/{created.GetProperty("id").GetGuid()}/working-days?from=2026-09-24&days=2")).GetProperty("result").GetString().ShouldBe("2026-09-26");

        (await (await owner.PostAsJsonAsync("/api/v1/organization/business-calendars", new { code = "bad", name = new { en = "x" }, workingDays = new[] { 7 } }, Json)).ErrorCodeAsync()).ShouldBe("business_calendar.working_days_invalid");
        (await (await owner.PostAsJsonAsync("/api/v1/organization/business-calendars", new { code = "sun_thu", name = new { en = "x" }, workingDays = new[] { 1 } }, Json)).ErrorCodeAsync()).ShouldBe("business_calendar.code_taken");
        var system = (await owner.GetOkAsync("/api/v1/organization/business-calendars")).EnumerateArray().Single(static c => c.Str("code") == "sun_thu").GetProperty("id").GetGuid();
        (await (await owner.PutAsJsonAsync($"/api/v1/organization/business-calendars/{system}", new { code = "renamed", name = new { en = "x" }, workingDays = new[] { 1 } }, Json)).ErrorCodeAsync()).ShouldBe("business_calendar.system_locked");
    }

    [Fact]
    public void Working_day_arithmetic_on_the_calendar_entity()
    {
        var calendar = new BusinessCalendar { WorkingDays = [0, 1, 2, 3, 4], Holidays = [new Holiday { OnDate = new DateOnly(2026, 9, 28) }] };
        var thursday = new DateOnly(2026, 9, 24);
        calendar.IsWorkingDay(thursday).ShouldBeTrue();
        calendar.IsWorkingDay(thursday.AddDays(1)).ShouldBeFalse();
        calendar.IsWorkingDay(new DateOnly(2026, 9, 28)).ShouldBeFalse();
        calendar.NextWorkingDay(thursday.AddDays(1)).ShouldBe(new DateOnly(2026, 9, 27));
        calendar.AddWorkingDays(thursday, 0).ShouldBe(thursday);
        calendar.AddWorkingDays(thursday, 1).ShouldBe(new DateOnly(2026, 9, 27));
        calendar.AddWorkingDays(thursday, 2).ShouldBe(new DateOnly(2026, 9, 29)); // Monday is a holiday
        calendar.AddWorkingDays(thursday.AddDays(2), 1).ShouldBe(new DateOnly(2026, 9, 29)); // from a Saturday: Sunday is day 0
        Should.Throw<ArgumentOutOfRangeException>(() => calendar.AddWorkingDays(thursday, -1));
    }
}
