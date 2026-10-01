using Quicker.Kernel.Amounts;
using Quicker.Tax.Contracts;
using Quicker.Tax.Engine;

namespace Quicker.Tax.Tests;

/// <summary>
/// The calculator on worked examples (ADR-0005, ADR-0018): both price bases, line and document rounding, zero and
/// reverse-charge codes; then its guarantees over thousands of generated documents: every line adds up, document
/// rounding lands each code's tax on the rounded total of its lines, and the order of the lines changes nothing.
/// </summary>
public sealed class TaxCalculatorTests
{
    private static readonly Currency Sar = Currency.Of("SAR", 2);
    private static readonly Currency Kwd = Currency.Of("KWD", 3);
    private static readonly Currency Iqd = Currency.Of("IQD", 0);

    private static readonly TaxCodeInfo Standard15 = Code("SA-S", 15m);
    private static readonly TaxCodeInfo Standard5 = Code("AE-S", 5m);
    private static readonly TaxCodeInfo Zero = Code("SA-Z", 0m, TaxTreatments.ZeroRated);
    private static readonly TaxCodeInfo ReverseCharge = Code("SA-RC", 15m, reverseCharge: true);

    private static TaxCodeInfo Code(string code, decimal rate, string treatment = TaxTreatments.Standard, bool reverseCharge = false) =>
        new(Guid.CreateVersion7(), Guid.Empty, "T", code, TaxKinds.Vat, treatment, rate, true, reverseCharge, null, "OutputTax", "InputTax", TaxRoundingLevels.Line);

    private static TaxedDocument Calculate(IReadOnlyList<TaxLineInput> lines, bool inclusive = false, string level = TaxRoundingLevels.Line, Currency? currency = null) =>
        TaxCalculator.Calculate(lines, inclusive, level, currency ?? Sar, RoundingPolicy.Default);

    [Fact]
    public void On_prices_that_exclude_tax_the_tax_is_the_net_times_the_rate()
    {
        var doc = Calculate([new("1", 100m, Standard15), new("2", 40m, Zero)]);

        var first = doc.Lines[0];
        (first.Net, first.Tax, first.Gross).ShouldBe((100m, 15m, 115m));
        (doc.Lines[1].Net, doc.Lines[1].Tax, doc.Lines[1].Gross).ShouldBe((40m, 0m, 40m));
        (doc.Net, doc.Tax, doc.Gross).ShouldBe((140m, 15m, 155m));
        doc.ByCode.Select(static s => (s.TaxCode, s.Net, s.Tax)).ShouldBe([("SA-S", 100m, 15m), ("SA-Z", 40m, 0m)]);
    }

    [Fact]
    public void On_prices_that_include_tax_the_gross_stays_what_was_asked_and_the_tax_is_carved_out_of_it()
    {
        var doc = Calculate([new("1", 115m, Standard15), new("2", 99.99m, Standard15)], inclusive: true);

        (doc.Lines[0].Net, doc.Lines[0].Tax, doc.Lines[0].Gross).ShouldBe((100m, 15m, 115m));

        // 99.99 × 15 / 115 = 13.0421… → 13.04, so the net is 86.95 and the customer still pays 99.99.
        (doc.Lines[1].Net, doc.Lines[1].Tax, doc.Lines[1].Gross).ShouldBe((86.95m, 13.04m, 99.99m));
        doc.Gross.ShouldBe(214.99m);
    }

    [Fact]
    public void Document_rounding_taxes_the_sum_of_the_lines_and_puts_the_cent_on_the_largest_line()
    {
        // Three lines of 0.10 at 5%: 0.005 each. Per line each rounds up to 0.01 (0.03 in all); on the document the
        // tax is 5% of 0.30 = 0.015 → 0.02, and the cent of difference comes off the first of the equal lines.
        var lines = new List<TaxLineInput> { new("a", 0.10m, Standard5), new("b", 0.10m, Standard5), new("c", 0.10m, Standard5) };

        Calculate(lines).Tax.ShouldBe(0.03m);
        var document = Calculate(lines, level: TaxRoundingLevels.Document);
        document.Tax.ShouldBe(0.02m);
        document.Lines.Select(static l => l.Tax).ShouldBe([0.00m, 0.01m, 0.01m]);
        document.RoundingLevel.ShouldBe(TaxRoundingLevels.Document);
    }

    [Fact]
    public void Reverse_charge_carries_the_self_assessed_tax_but_adds_nothing_to_what_the_supplier_is_paid()
    {
        var doc = Calculate([new("svc", 1_000m, ReverseCharge), new("goods", 200m, Standard15)]);

        var svc = doc.Lines[0];
        (svc.Net, svc.Tax, svc.Gross, svc.IsReverseCharge).ShouldBe((1_000m, 150m, 1_000m, true));
        doc.Tax.ShouldBe(30m, "only the tax the supplier charges is on the invoice");
        doc.Gross.ShouldBe(1_230m);
        doc.ByCode.Single(static s => s.IsReverseCharge).Tax.ShouldBe(150m);
    }

    [Fact]
    public void Reverse_charge_on_a_tax_inclusive_document_is_still_on_the_net_the_supplier_asked()
    {
        var doc = Calculate([new("svc", 1_000m, ReverseCharge)], inclusive: true);

        (doc.Lines[0].Net, doc.Lines[0].Tax, doc.Lines[0].Gross).ShouldBe((1_000m, 150m, 1_000m));
    }

    [Fact]
    public void A_line_without_a_code_carries_no_tax_and_currencies_round_to_their_own_minor_units()
    {
        Calculate([new("1", 100m, null)]).Tax.ShouldBe(0m);
        Calculate([new("1", 10.005m, Standard5)], currency: Kwd).Lines[0].Tax.ShouldBe(0.500m);
        Calculate([new("1", 12_345m, Standard15)], currency: Iqd).Lines[0].Tax.ShouldBe(1_852m);
    }

    [Fact]
    public void A_credit_note_is_the_mirror_of_its_invoice()
    {
        var invoice = Calculate([new("1", 99.99m, Standard15), new("2", 0.33m, Standard15)], inclusive: true, level: TaxRoundingLevels.Document);
        var credit = Calculate([new("1", -99.99m, Standard15), new("2", -0.33m, Standard15)], inclusive: true, level: TaxRoundingLevels.Document);

        credit.Lines.Select(static l => (-l.Net, -l.Tax, -l.Gross)).ShouldBe(invoice.Lines.Select(static l => (l.Net, l.Tax, l.Gross)));
    }

    [Fact]
    public void Generated_documents_add_up_on_either_basis_and_level_whatever_the_order_of_their_lines()
    {
        var random = new Random(20260925);
        TaxCodeInfo?[] codes = [Standard15, Standard5, Zero, ReverseCharge, Code("X-7", 7.5m), null];
        Currency[] currencies = [Sar, Kwd, Iqd];
        for (var run = 0; run < 3_000; run++)
        {
            var currency = currencies[random.Next(currencies.Length)];
            var inclusive = random.Next(2) == 0;
            var level = random.Next(2) == 0 ? TaxRoundingLevels.Line : TaxRoundingLevels.Document;
            var lines = Enumerable.Range(0, random.Next(1, 12))
                .Select(i => new TaxLineInput($"L{i:00}", RoundingPolicy.Default.Round((decimal)random.Next(-2_000, 200_000) / 100m * (random.Next(4) == 0 ? 0.001m : 1m), currency.MinorUnits), codes[random.Next(codes.Length)]))
                .ToList();

            var doc = TaxCalculator.Calculate(lines, inclusive, level, currency, RoundingPolicy.Default);
            var context = $"run {run} ({currency}, {(inclusive ? "inclusive" : "exclusive")}, {level})";

            foreach (var line in doc.Lines)
            {
                var input = lines.Single(l => l.Key == line.Key);
                line.Tax.ShouldBe(RoundingPolicy.Default.Round(line.Tax, currency.MinorUnits), context);
                line.Gross.ShouldBe(line.Net + (line.IsReverseCharge ? 0m : line.Tax), context);
                if (inclusive && !line.IsReverseCharge)
                {
                    line.Gross.ShouldBe(input.Amount, context + ": the customer pays the price asked");
                }
                else
                {
                    line.Net.ShouldBe(input.Amount, context);
                }

                if (level == TaxRoundingLevels.Line && input.Code is { } code)
                {
                    var raw = inclusive && !code.IsReverseCharge ? input.Amount * code.RatePct / (100m + code.RatePct) : input.Amount * code.RatePct / 100m;
                    line.Tax.ShouldBe(RoundingPolicy.Default.Round(raw, currency.MinorUnits), context);
                }
            }

            foreach (var summary in doc.ByCode)
            {
                var ofCode = lines.Where(l => l.Code?.Id == summary.TaxCodeId).ToList();
                if (level == TaxRoundingLevels.Document)
                {
                    var code = ofCode[0].Code!;
                    var raw = ofCode.Sum(l => inclusive && !code.IsReverseCharge ? l.Amount * code.RatePct / (100m + code.RatePct) : l.Amount * code.RatePct / 100m);
                    summary.Tax.ShouldBe(RoundingPolicy.Default.Round(raw, currency.MinorUnits), context + ": a code's tax is its lines' tax rounded once");
                }

                summary.Tax.ShouldBe(doc.Lines.Where(l => l.TaxCodeId == summary.TaxCodeId).Sum(static l => l.Tax), context);
            }

            (doc.Net + doc.Tax).ShouldBe(doc.Gross, context);

            var shuffled = lines.OrderBy(_ => random.Next()).ToList();
            var again = TaxCalculator.Calculate(shuffled, inclusive, level, currency, RoundingPolicy.Default);
            again.Lines.OrderBy(static l => l.Key, StringComparer.Ordinal).ShouldBe(doc.Lines.OrderBy(static l => l.Key, StringComparer.Ordinal), context + ": the lines read in another order");
            again.ByCode.ShouldBe(doc.ByCode, context);
        }
    }
}
