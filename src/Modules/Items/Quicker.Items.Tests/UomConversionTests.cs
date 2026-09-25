using System.Net;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.TestSupport;
using Quicker.Items.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Persistence;

namespace Quicker.Items.Tests;

/// <summary>
/// Hard scenario 9: an item bought in cartons of 24, stocked in pieces and sold by the dozen; exact conversions with
/// no rounding drift over thousands of transactions, checked against exact rational arithmetic.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class UomConversionTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    [Fact]
    public async Task Ten_thousand_random_carton_piece_and_dozen_transactions_leave_zero_drift()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var item = await owner.PostAsync("/api/v1/items", new
        {
            code = "WATER-500",
            name = new { en = "Water 500 ml", ar = "ماء ٥٠٠ مل" },
            baseUom = "PCS",
            purchaseUom = "CTN",
            salesUom = "DZ",
            uoms = new object[] { new { uom = "CTN", numerator = 24, denominator = 1, isPurchaseDefault = true }, new { uom = "DZ", numerator = 12, denominator = 1, isSalesDefault = true } },
        });
        var itemId = item.GetProperty("id").GetGuid();
        var uoms = item.GetProperty("uoms").EnumerateArray().ToDictionary(u => u.GetProperty("uomCode").GetString()!, u => u.GetProperty("uomId").GetGuid());
        uoms.Keys.ShouldBe(["PCS", "CTN", "DZ"], ignoreOrder: true);
        item.GetProperty("purchaseUom").GetString().ShouldBe("CTN");
        item.GetProperty("salesUom").GetString().ShouldBe("DZ");

        // The directory in process, as a stock document would use it: ten thousand conversions through one unit of work.
        await using var scope = Api.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = TenantContext.System(new TenantId(ws.TenantId), "scenario-9-" + Guid.CreateVersion7().ToString("N")[^12..]);
        await using var unitOfWork = await services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync(context, cancellationToken: TestContext.Current.CancellationToken);
        services.GetRequiredService<IUnitOfWorkAccessor>().Set(unitOfWork);
        using var ambient = services.GetRequiredService<ITenantContextAccessor>().Use(context);
        var directory = services.GetRequiredService<IItemDirectory>();

        var random = new Random(20260922);
        var codes = new[] { "CTN", "DZ", "PCS" };
        var factors = new Dictionary<string, (BigInteger Numerator, BigInteger Denominator)>(StringComparer.Ordinal) { ["CTN"] = (24, 1), ["DZ"] = (12, 1), ["PCS"] = (1, 1) };
        // Entered quantities are decimals with up to 2 places (half and quarter cartons, a third of a dozen never
        // appears because it is not exact in pieces and the engine must refuse it, tested below).
        decimal onHand = 0m;
        var exact = BigInteger.Zero; // in hundredths of a piece, so every accepted quantity is an integer here
        var accepted = 0;
        for (var i = 0; i < 10_000; i++)
        {
            var unit = codes[random.Next(codes.Length)];
            var sign = random.Next(3) == 0 ? -1 : 1;
            var whole = random.Next(1, 500);
            var fraction = unit switch { "CTN" => new[] { 0m, 0.5m, 0.25m, 0.75m }[random.Next(4)], "DZ" => new[] { 0m, 0.5m, 0.25m }[random.Next(3)], _ => 0m };
            var quantity = sign * (whole + fraction);
            var converted = await directory.ToBaseAsync(itemId, uoms[unit], quantity, TestContext.Current.CancellationToken);
            converted.IsSuccess.ShouldBeTrue(converted.Error?.Code + " for " + quantity + " " + unit);
            onHand += converted.Value.Quantity;
            var (n, d) = factors[unit];
            exact += new BigInteger(quantity * 100m) * n / d;
            accepted++;
            (new BigInteger(onHand * 100m) == exact).ShouldBeTrue($"drift after {i + 1} transactions: on hand {onHand}, exact {exact}/100");
            decimal.Remainder(onHand, 1m).ShouldBe(0m, "pieces are whole");
        }

        accepted.ShouldBe(10_000);
        // Whatever the total, it converts back to cartons and dozens without loss when divisible, exactly otherwise refused.
        var inCartons = await directory.FromBaseAsync(itemId, uoms["CTN"], onHand, TestContext.Current.CancellationToken);
        if (decimal.Remainder(onHand, 24m) == 0m)
        {
            inCartons.IsSuccess.ShouldBeTrue();
            (inCartons.Value * 24m).ShouldBe(onHand);
        }
        else
        {
            inCartons.IsFailure.ShouldBeTrue();
            inCartons.Error!.Code.ShouldBe("quantity.not_exact_in_uom");
        }

        var third = await directory.ToBaseAsync(itemId, uoms["CTN"], 0.333333333m, TestContext.Current.CancellationToken);
        third.IsFailure.ShouldBeTrue();
        third.Error!.Code.ShouldBe("quantity.not_exact_in_base");
        (await directory.ToBaseAsync(itemId, uoms["CTN"], 0.125m, TestContext.Current.CancellationToken)).Value.Quantity.ShouldBe(3m, "an eighth of a carton is three pieces");
        (await directory.ToBaseAsync(itemId, uoms["DZ"], 0.25m, TestContext.Current.CancellationToken)).Value.Quantity.ShouldBe(3m);
        (await directory.ToBaseAsync(itemId, Guid.CreateVersion7(), 1m, TestContext.Current.CancellationToken)).Error!.Code.ShouldBe("quantity.uom_not_item_uom");
        await unitOfWork.RollbackAsync(TestContext.Current.CancellationToken);

        // The same arithmetic over the API, with the base unit as the pivot.
        var convert = await owner.GetOkAsync($"/api/v1/items/{itemId}/convert?quantity=2&from=CTN&to=DZ");
        convert.Dec("result").ShouldBe(4m);
        convert.Dec("baseQuantity").ShouldBe(48m);
        (await owner.GetOkAsync($"/api/v1/items/{itemId}/convert?quantity=0.5&from=CTN&to=PCS")).Dec("result").ShouldBe(12m);
        (await owner.GetOkAsync($"/api/v1/items/{itemId}/convert?quantity=36&from=PCS&to=DZ")).Dec("result").ShouldBe(3m);
        var (code, problem) = await owner.GetErrorAsync($"/api/v1/items/{itemId}/convert?quantity=30&from=PCS&to=CTN", HttpStatusCode.UnprocessableEntity);
        code.ShouldBe("quantity.not_exact_in_uom");
        problem.GetProperty("why").GetProperty("result").GetDecimal().ShouldBe(1.25m);
    }

    [Fact]
    public void Rational_factors_compose_exactly_in_both_directions()
    {
        var random = new Random(9);
        for (var i = 0; i < 10_000; i++)
        {
            var numerator = random.Next(1, 1000);
            var denominator = random.Next(1, 50);
            var precision = random.Next(0, 4);
            var scale = 1m;
            for (var p = 0; p < precision; p++)
            {
                scale *= 10m;
            }

            // A base quantity that is a whole number of the unit converts to the unit and back to the same number.
            var units = random.Next(1, 10_000);
            var baseQuantity = units * numerator / (decimal)denominator;
            if (!ItemUomMath.IsExact(baseQuantity, precision))
            {
                continue;
            }

            var back = ItemUomMath.FromBase(baseQuantity, numerator, denominator, 9);
            back.IsSuccess.ShouldBeTrue();
            ItemUomMath.Normalize(back.Value).ShouldBe(units);
            ItemUomMath.ToBase(back.Value, numerator, denominator, precision).Value.ShouldBe(baseQuantity);
            ItemUomMath.IsExact(baseQuantity + 1m / scale / 10m, precision).ShouldBeFalse();
        }

        ItemUomMath.ToBase(1m, 1m, 3m, 0).Error!.Code.ShouldBe("quantity.not_exact_in_base");
        ItemUomMath.ToBase(3m, 1m, 3m, 0).Value.ShouldBe(1m);
        ItemUomMath.ToBase(1m, 1m, 3m, 9).Error!.Code.ShouldBe("quantity.not_exact_in_base", "a third of a piece is not exact at nine places either");
        ItemUomMath.ToBase(0.5m, 24m, 1m, 0).Value.ShouldBe(12m);
        ItemUomMath.FromBase(12m, 24m, 1m, 2).Value.ShouldBe(0.5m);
        ItemUomMath.FromBase(12m, 24m, 1m, 0).Error!.Code.ShouldBe("quantity.not_exact_in_uom");
    }
}
