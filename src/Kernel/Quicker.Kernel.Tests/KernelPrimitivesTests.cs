using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;

namespace Quicker.Kernel.Tests;

public class KernelPrimitivesTests
{
    [Fact]
    public void Uuid7_values_are_time_ordered()
    {
        var earlier = Uuid7.NewAt(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var later = Uuid7.NewAt(new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero));
        earlier.Version.ShouldBe(7);
        string.CompareOrdinal(earlier.ToString(), later.ToString()).ShouldBeLessThan(0);
    }

    [Fact]
    public void Typed_ids_do_not_mix()
    {
        var tenant = TenantId.New();
        var company = new CompanyId(tenant.Value);
        tenant.Value.ShouldBe(company.Value);
        tenant.Equals(company).ShouldBeFalse();
    }

    [Fact]
    public void Fake_clock_resolves_today_in_a_time_zone()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 3, 31, 22, 30, 0, TimeSpan.Zero));
        clock.TodayIn("Asia/Baghdad").ShouldBe(new DateOnly(2026, 4, 1));
        clock.TodayIn("America/New_York").ShouldBe(new DateOnly(2026, 3, 31));
        clock.Advance(TimeSpan.FromHours(6));
        clock.TodayIn("America/New_York").ShouldBe(new DateOnly(2026, 4, 1));
    }

    [Fact]
    public void Results_carry_structured_why()
    {
        Result<int> failure = Error.Forbidden("credit.limit_exceeded", "Credit limit exceeded")
            .WithWhy(("limit", 5000m), ("exposure", 6200m));
        failure.IsFailure.ShouldBeTrue();
        failure.Error!.Why!["exposure"].ShouldBe(6200m);
        Should.Throw<InvalidOperationException>(() => failure.Value);

        Result<int> ok = 42;
        ok.Map(static v => v * 2).Value.ShouldBe(84);
    }

    [Fact]
    public void Localized_text_falls_back_predictably()
    {
        var text = LocalizedText.Bilingual("Cost centre", "مركز التكلفة");
        text.Resolve("ar-IQ").ShouldBe("مركز التكلفة");
        text.Resolve("fr", "en").ShouldBe("Cost centre");
        LocalizedText.Of("ar", "فقط").Resolve("en").ShouldBe("فقط");
        new LocalizedText().Resolve("en").ShouldBe(string.Empty);
    }

    [Fact]
    public void Tenant_context_is_scoped_to_the_async_flow()
    {
        var accessor = new TenantContextAccessor();
        accessor.Current.ShouldBeNull();
        var context = TenantContext.System(TenantId.New(), "req-1");
        using (accessor.Use(context))
        {
            accessor.Required.ShouldBe(context);
        }

        accessor.Current.ShouldBeNull();
    }
}
