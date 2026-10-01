using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Accounting.Application;
using Quicker.Accounting.Persistence;
using Quicker.Integrity.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Organization.Application;
using Quicker.Organization.Contracts;
using Quicker.Payables.Contracts;
using Quicker.Persistence;

namespace Quicker.Migrator.Demo;

/// <summary>What the books seed produced: journals posted through the services, entries in the ledger, periods closed.</summary>
public sealed record DemoBooksOutcome(int Journals, int Entries, int PeriodsClosed, int DimensionValues);

/// <summary>
/// Demo seed v2 (roadmap 2.7): a chart per company from the template its country uses, cost centres, departments
/// and projects, and twelve months of books ending today: opening balances, monthly sales and collections,
/// purchases and payments, salaries split by cost centre, utility accruals reversed by the daily routine, bank
/// charges, rent from a recurring template, an insurance prepayment and a service contract amortised by deferral
/// schedules, the months before last hard-closed with one correction posted into the open period. Everything goes
/// through the same services an accountant uses, and the invariant harness must pass before the seed commits.
/// Amounts derive from a fixed seed per calendar month, so the dataset is identical on every run for the same day.
/// </summary>
internal static class DemoBooks
{
    internal sealed record Party(string Key, string En, string Ar)
    {
        public Guid Ref => DemoIds.For("party:" + Key);
    }

    private sealed record Centre(string Code, string En, string Ar, int SalaryShare);

    private static readonly IReadOnlyDictionary<string, string> Templates = new Dictionary<string, string>(StringComparer.Ordinal) { ["IQT"] = "IRAQ_UAS", ["USI"] = "IFRS_SME", ["AEG"] = "GCC" };

    private static readonly Centre[] Centres =
    [
        new("CC-ADM", "Administration", "الإدارة", 30),
        new("CC-SLS", "Sales", "المبيعات", 30),
        new("CC-OPS", "Operations", "العمليات", 25),
        new("CC-LOG", "Logistics", "اللوجستيات", 15),
    ];

    private static readonly (string Code, string En, string Ar)[] Departments = [("FIN", "Finance", "المالية"), ("HR", "Human resources", "الموارد البشرية"), ("IT", "Information technology", "تقنية المعلومات")];

    private static readonly (string Code, string En, string Ar)[] Projects = [("PRJ-ERP", "ERP rollout", "تطبيق نظام تخطيط الموارد"), ("PRJ-BSR", "Basra expansion", "توسعة البصرة")];

    internal static readonly Party[] Customers =
    [
        new("cust:baghdad-mall", "Baghdad Mall LLC", "شركة بغداد مول"), new("cust:al-noor", "Al-Noor Supermarkets", "أسواق النور"), new("cust:kurdistan-dist", "Kurdistan Distribution", "توزيع كردستان"),
        new("cust:basra-oil", "Basra Oil Services", "خدمات نفط البصرة"), new("cust:gulf-retail", "Gulf Retail Group", "مجموعة الخليج للتجزئة"), new("cust:dubai-hotels", "Dubai Hotels Supply", "تجهيزات فنادق دبي"),
    ];

    private static readonly Party[] Suppliers =
    [
        new("supp:turkish-foods", "Turkish Foods Export", "تركيا للأغذية"), new("supp:jebel-ali", "Jebel Ali Trading", "جبل علي للتجارة"), new("supp:al-furat", "Al-Furat Packaging", "الفرات للتغليف"),
        new("supp:iraqi-power", "Iraqi Power Distribution", "توزيع الكهرباء العراقية"), new("supp:etisalat", "Etisalat", "اتصالات"),
    ];

    private static readonly RoundingPolicy Rounding = RoundingPolicy.Default;

    public static async Task<DemoBooksOutcome> SeedAsync(IServiceProvider services, IReadOnlyList<(DemoCompany Definition, CompanySummary Company)> companies, DateOnly today, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(companies);
        var charts = services.GetRequiredService<ChartService>();
        var profiles = services.GetRequiredService<ProfileService>();
        var fiscal = services.GetRequiredService<FiscalCalendarService>();
        var dimensions = services.GetRequiredService<DimensionService>();
        var journals = services.GetRequiredService<ManualJournalService>();
        var recurring = services.GetRequiredService<RecurringService>();
        var deferrals = services.GetRequiredService<DeferralService>();
        var routines = services.GetRequiredService<AccountingRoutines>();
        var harness = services.GetRequiredService<IInvariantHarness>();
        var accounting = services.GetRequiredService<AccountingDbContext>();
        var payables = services.GetRequiredService<IPayables>();
        // The v2 books name their suppliers by fixed ids; the payables subledger (4.7) keeps open items per partner, so
        // those suppliers become real partner records with the same ids (idempotent, like the rest of the seed).
        var unitOfWork = services.GetRequiredService<IUnitOfWorkAccessor>().Current;
        foreach (var supplier in Suppliers)
        {
            await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO app.ptr_partners (tenant_id, id, code, legal_name_i18n, is_supplier) VALUES (@tenant, @id, @code, @name::jsonb, true) ON CONFLICT (tenant_id, id) DO NOTHING",
                new { tenant = unitOfWork.Context.TenantId.Value, id = supplier.Ref, code = "SUP-" + supplier.Key[5..].ToUpperInvariant(), name = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = supplier.En, ["ar"] = supplier.Ar }) },
                unitOfWork.Transaction, cancellationToken: cancellationToken));
        }

        var windowStart = new DateOnly(today.Year, today.Month, 1).AddMonths(-11);
        var previousMonthStart = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);

        // Dimension values shared by every company: cost centres carry the salaries and the expenses, projects mark a few journals.
        var dimensionIds = (await dimensions.ListAsync(cancellationToken)).ToDictionary(static d => d.Code, static d => d.Id, StringComparer.Ordinal);
        var centres = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var centre in Centres)
        {
            centres[centre.Code] = Require(await dimensions.CreateValueAsync(dimensionIds["COST_CENTER"], new SaveDimensionValueRequest(centre.Code, Bilingual(centre.En, centre.Ar)), cancellationToken)).Id;
        }

        var departments = 0;
        foreach (var (code, en, ar) in Departments)
        {
            Require(await dimensions.CreateValueAsync(dimensionIds["DEPARTMENT"], new SaveDimensionValueRequest(code, Bilingual(en, ar)), cancellationToken));
            departments++;
        }

        var projects = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (code, en, ar) in Projects)
        {
            projects[code] = Require(await dimensions.CreateValueAsync(dimensionIds["PROJECT"], new SaveDimensionValueRequest(code, Bilingual(en, ar)), cancellationToken)).Id;
        }

        var posted = 0;
        var closed = 0;
        Guid? correctionCandidate = null;
        decimal correctionAmount = 0m;
        Guid correctionSupplier = Guid.Empty;
        string correctionCurrency = string.Empty;

        foreach (var (definition, company) in companies)
        {
            var code = definition.Code;
            var currency = definition.FunctionalCurrency;
            var companyId = company.Id;
            Require(await charts.CreateFromTemplateAsync(new FromTemplateRequest(Templates[code], "CH-" + code, Bilingual($"{definition.TradeEn} chart of accounts", $"دليل حسابات {definition.TradeAr}"), companyId, Shared: false), cancellationToken));
            Require(await profiles.CreateFromChartAsync(companyId, null, cancellationToken));
            await fiscal.EnsureYearCoversAsync(company.FiscalCalendarId, windowStart, cancellationToken);

            var bank = DemoIds.For("bank:" + code);
            var cashBox = DemoIds.For("cash:" + code);
            var vehicle = DemoIds.For("asset:" + code + ":vehicles");
            decimal M(decimal usd) => Amount(usd, currency);

            // The open items a posted journal's payables lines opened, through the entry the journal carries.
            async Task<IReadOnlyList<OpenItemInfo>> ItemsOfAsync(Guid journalId)
            {
                var entryId = (await journals.GetAsync(journalId, cancellationToken))!.JournalEntryId!.Value;
                return await payables.ItemsOfAsync("journal_entry", entryId, cancellationToken);
            }

            async Task<Guid> PostAsync(DateOnly date, string kind, string en, string ar, IReadOnlyList<JournalLineRequest> lines, string? reference = null, DateOnly? autoReverseOn = null)
            {
                var draft = Require(await journals.CreateAsync(companyId, new SaveJournalRequest(date, currency, lines, kind, Description: Bilingual(en, ar), Reference: reference, AutoReverse: autoReverseOn is not null, AutoReverseOn: autoReverseOn), null, cancellationToken));
                Require(await journals.PostAsync(draft.Id, cancellationToken));
                posted++;
                // Every posted journal is saved; dropping it from the change tracker keeps the next save from re-scanning a year of books.
                accounting.ChangeTracker.Clear();
                return draft.Id;
            }

            // Opening balances on the first day of the window: what the company brought into Quicker.
            await PostAsync(windowStart, "opening", "Opening balances at go-live", "الأرصدة الافتتاحية عند الانطلاق",
            [
                Line("1121", debit: M(120000m), subledger: ("BANK", bank)),
                Line("1111", debit: M(5000m), subledger: ("BANK", cashBox)),
                Line("1510", debit: M(80000m), subledger: ("FA", vehicle)),
                Line("3100", credit: M(150000m)),
                Line("2510", credit: M(55000m)),
            ], reference: "GO-LIVE");

            // The insurance year paid up front (amortised by a deferral schedule) and, in Dubai, a service contract billed for the year ahead.
            await PostAsync(windowStart.AddDays(2), "manual", "Annual insurance premium paid", "قسط التأمين السنوي المدفوع", [Line("1410", debit: M(2400m)), Line("1121", credit: M(2400m), subledger: ("BANK", bank))], reference: "INS-" + windowStart.Year);
            Require(await deferrals.CreateAsync(companyId, new SaveDeferralRequest("prepayment", "1410", "6170", windowStart, 12, M(2400m), currency, Description: Bilingual("Insurance premium " + windowStart.Year, "قسط التأمين " + windowStart.Year)), cancellationToken));
            if (code == "AEG")
            {
                await PostAsync(windowStart.AddDays(4), "manual", "Annual maintenance contract invoiced in advance", "عقد الصيانة السنوي المفوتر مقدماً", [Line("1121", debit: M(6000m), subledger: ("BANK", bank)), Line("2180", credit: M(6000m))], reference: "AMC-" + windowStart.Year);
                Require(await deferrals.CreateAsync(companyId, new SaveDeferralRequest("deferred_revenue", "2180", "4200", windowStart, 12, M(6000m), currency, Description: Bilingual("Maintenance contract " + windowStart.Year, "عقد الصيانة " + windowStart.Year)), cancellationToken));
            }

            // Rent comes from a recurring template the daily routine generates and posts.
            Require(await recurring.CreateAsync(companyId, new SaveRecurringTemplateRequest("RENT", Bilingual("Office rent", "إيجار المكتب"), "0 0 1 * *", currency,
                [new RecurringLineRequest("6110", Debit: M(3000m), Dimensions: Dimension("COST_CENTER", centres["CC-ADM"])), new RecurringLineRequest("2170", Credit: M(3000m))],
                StartsOn: windowStart, Description: Bilingual("Monthly office rent", "إيجار المكتب الشهري"), RequiresReview: false), cancellationToken));

            for (var m = 0; m < 12; m++)
            {
                var monthStart = windowStart.AddMonths(m);
                var monthEnd = monthStart.AddMonths(1).AddDays(-1);
                var series = code + ":" + monthStart.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
                DateOnly Day(int d) => monthStart.AddDays(d - 1);
                bool Due(DateOnly date) => date <= today;

                var customer = Customers[(m + Array.IndexOf(Templates.Keys.ToArray(), code)) % Customers.Length];
                var supplier = Suppliers[m % 3];
                var sale = M(8000m + DemoIds.Draw(series + ":sale", 1, 17000));
                var purchase = M(6000m + DemoIds.Draw(series + ":purchase", 1, 9000));
                var utilityEstimate = M(800m + DemoIds.Draw(series + ":utilities", 1, 400));
                var salaries = M(12000m);
                var monthName = monthStart.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

                if (Due(Day(5)))
                {
                    await PostAsync(Day(5), "manual", $"Sales invoice {customer.En}", $"فاتورة مبيعات {customer.Ar}",
                        [Line("1210", debit: sale, subledger: ("AR", customer.Ref)), Line("4100", credit: sale, dimensions: Dimension("COST_CENTER", centres["CC-SLS"]))], reference: $"INV-{monthStart:yyyyMM}-{m + 1:00}");
                }

                Guid? purchaseItem = null;
                if (Due(Day(8)))
                {
                    var purchaseId = await PostAsync(Day(8), "manual", $"Goods purchased from {supplier.En}", $"مشتريات بضاعة من {supplier.Ar}",
                        [Line("5100", debit: purchase, dimensions: Dimension("COST_CENTER", centres["CC-OPS"])), Line("2110", credit: purchase, subledger: ("AP", supplier.Ref), dueDate: Day(8).AddDays(30))], reference: $"PINV-{monthStart:yyyyMM}");
                    // The journal's payables line opened the supplier's open item (A-140); the payment settles it.
                    purchaseItem = (await ItemsOfAsync(purchaseId)).Single().Id;
                    if (code == "IQT" && m == 8)
                    {
                        // Scenario 7's candidate stays unpaid: a settled item is not reversed by a correction.
                        (correctionCandidate, correctionAmount, correctionSupplier, correctionCurrency) = (purchaseId, purchase, supplier.Ref, currency);
                        purchaseItem = null;
                    }
                }

                if (Due(Day(10)) && m > 0)
                {
                    // Last month's actual bill, near the estimate the accrual reversed on the first.
                    var actual = M(760m + DemoIds.Draw(code + ":" + windowStart.AddMonths(m - 1).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture) + ":utilities", 2, 480));
                    var utilityId = await PostAsync(Day(10), "manual", "Electricity and water invoice", "فاتورة الكهرباء والماء",
                        [Line("6120", debit: actual, dimensions: Dimension("COST_CENTER", centres["CC-ADM"])), Line("2110", credit: actual, subledger: ("AP", Suppliers[3].Ref), dueDate: Day(10).AddDays(14))], reference: $"UTIL-{monthStart:yyyyMM}");
                }

                if (Due(Day(22)))
                {
                    await PostAsync(Day(22), "manual", $"Collection from {customer.En}", $"تحصيل من {customer.Ar}",
                        [Line("1121", debit: sale, subledger: ("BANK", bank)), Line("1210", credit: sale, subledger: ("AR", customer.Ref))], reference: $"RCPT-{monthStart:yyyyMM}");
                }

                if (Due(Day(25)) && purchaseItem is { } invoiceItem)
                {
                    var paymentId = await PostAsync(Day(25), "manual", $"Payment to {supplier.En}", $"دفعة إلى {supplier.Ar}",
                        [Line("2110", debit: purchase, subledger: ("AP", supplier.Ref)), Line("1121", credit: purchase, subledger: ("BANK", bank))], reference: $"PAY-{monthStart:yyyyMM}");
                    var paymentItem = (await ItemsOfAsync(paymentId)).Single();
                    Require(await payables.RecordAsync(new RecordSettlementRequest(paymentItem.Id, invoiceItem, Day(25), SettlementKinds.Payment, purchase, purchase, purchase, 1m, 0m, 0m, paymentItem.JournalEntryId!.Value), cancellationToken));
                }

                if (Due(Day(28)))
                {
                    var lines = new List<JournalLineRequest>();
                    var allocated = 0m;
                    for (var c = 0; c < Centres.Length; c++)
                    {
                        var share = c == Centres.Length - 1 ? salaries - allocated : Rounding.Round(salaries * Centres[c].SalaryShare / 100m, Decimals(currency));
                        allocated += share;
                        lines.Add(Line("6100", debit: share, dimensions: Dimension("COST_CENTER", centres[Centres[c].Code])));
                    }

                    lines.Add(Line("1121", credit: salaries, subledger: ("BANK", bank)));
                    await PostAsync(Day(28), "manual", $"Salaries {monthName}", $"رواتب {monthName}", lines, reference: $"PAY-{monthStart:yyyyMM}-SAL");
                }

                if (Due(monthEnd))
                {
                    await PostAsync(monthEnd, "accrual", $"Utilities accrual {monthName}", $"استحقاق المرافق {monthName}",
                        [Line("6120", debit: utilityEstimate, dimensions: Dimension("COST_CENTER", centres["CC-ADM"])), Line("2170", credit: utilityEstimate)], reference: $"ACC-{monthStart:yyyyMM}", autoReverseOn: monthEnd.AddDays(1));
                    await PostAsync(monthEnd, "manual", "Bank charges", "عمولات مصرفية", [Line("6510", debit: M(45m)), Line("1121", credit: M(45m), subledger: ("BANK", bank))], reference: $"BNK-{monthStart:yyyyMM}");
                }

                if (m % 3 == 2 && Due(Day(15)))
                {
                    var quarter = M(9000m);
                    await PostAsync(Day(15), "manual", "Quarterly rent paid to the landlord", "دفع إيجار الربع للمالك", [Line("2170", debit: quarter), Line("1121", credit: quarter, subledger: ("BANK", bank))], reference: $"RENT-{monthStart:yyyyMM}");
                }

                if (m == 6 && Due(Day(12)))
                {
                    await PostAsync(Day(12), "manual", "Consultants for the ERP rollout", "استشاريو تطبيق النظام",
                        [Line("6160", debit: M(4500m), dimensions: Dimensions(("COST_CENTER", centres["CC-ADM"]), ("PROJECT", projects["PRJ-ERP"]))), Line("2110", credit: M(4500m), subledger: ("AP", Suppliers[1].Ref), dueDate: Day(12).AddDays(30))], reference: "PRJ-ERP-01");
                }
            }
        }

        // The daily routine as of today: every rent due, every insurance and contract line due, every accrual whose reversal date has come.
        var run = await routines.RunAsync(null, today, RoutineRunner.Schedule, cancellationToken);
        posted += run.RecurringJournals.Count(static r => r.Outcome != "waiting");
        var waiting = run.AutoReversals.Concat(run.RecurringJournals).Concat(run.DeferralPostings).Where(static r => r.Outcome == "waiting").ToList();
        if (waiting.Count > 0)
        {
            throw new InvalidOperationException("Demo seed: the routines left work waiting: " + string.Join(", ", waiting.Select(static w => $"{w.TargetId}: {w.Problem}")));
        }

        // Month-end discipline: every month before last is hard-closed for the ledger, last month soft-closed.
        foreach (var (_, company) in companies)
        {
            for (var m = 0; m < 12; m++)
            {
                var monthStart = windowStart.AddMonths(m);
                if (monthStart >= new DateOnly(today.Year, today.Month, 1))
                {
                    break;
                }

                var state = monthStart < previousMonthStart ? PeriodStates.HardClosed : PeriodStates.SoftClosed;
                var period = Require(await fiscal.ResolveAsync(new CompanyId(company.Id), monthStart, PostingModules.GeneralLedger, cancellationToken));
                Require(await fiscal.SetStateAsync(period.Period.PeriodId, new SetPeriodStateRequest(company.Id, [PostingModules.GeneralLedger], state, state == PeriodStates.HardClosed ? "Month closed" : "Month-end in progress"), cancellationToken));
                closed++;
            }
        }

        // Scenario 7 on the demo books: a purchase booked in a closed month is corrected into the open period, both journals linked.
        if (correctionCandidate is { } original)
        {
            var draft = Require(await journals.CorrectAsync(original, "Goods were consumables, not stock for resale", cancellationToken));
            Require(await journals.UpdateAsync(draft.Id, new SaveJournalRequest(draft.PostingDate, correctionCurrency,
                [Line("6150", debit: correctionAmount, dimensions: Dimension("COST_CENTER", centres["CC-OPS"])), Line("2110", credit: correctionAmount, subledger: ("AP", correctionSupplier))],
                Description: Bilingual("Correction: consumables booked as cost of goods sold", "تصحيح: مستهلكات سُجّلت كتكلفة بضاعة مباعة"), Reference: "CORR-01"), cancellationToken));
            Require(await journals.PostAsync(draft.Id, cancellationToken));
            posted++;
        }

        var report = await harness.RunAsync(null, cancellationToken);
        if (!report.Passed)
        {
            throw new InvalidOperationException("Demo seed: the invariant harness failed: " + string.Join(" | ", report.Checks.Where(static c => !c.Passed).Select(static c => c.Code + ": " + string.Join("; ", c.Problems))));
        }

        var entries = (int)report.Checks.Single(static c => c.Code == InvariantCodes.EntriesBalanced).Checked;
        return new DemoBooksOutcome(posted, entries, closed, centres.Count + departments + projects.Count);
    }

    /// <summary>Scales a USD-sized figure into the company's currency (dinars in thousands, dirhams pegged) rounded to its minor unit.</summary>
    private static decimal Amount(decimal usd, string currency) => currency switch
    {
        "IQD" => Rounding.Round(usd * 1310m, 0),
        "AED" => Rounding.Round(usd * 3.67m, 2),
        _ => Rounding.Round(usd, 2),
    };

    private static int Decimals(string currency) => currency == "IQD" ? 0 : 2;

    private static JournalLineRequest Line(string account, decimal debit = 0m, decimal credit = 0m, IReadOnlyDictionary<string, Guid>? dimensions = null, (string Type, Guid Ref)? subledger = null, DateOnly? dueDate = null) =>
        new(debit, credit, account, Dimensions: dimensions, SubledgerType: subledger?.Type, SubledgerRef: subledger?.Ref, DueDate: dueDate);

    private static Dictionary<string, Guid> Dimension(string code, Guid valueId) => new(StringComparer.Ordinal) { [code] = valueId };

    private static Dictionary<string, Guid> Dimensions(params (string Code, Guid ValueId)[] values) => values.ToDictionary(static v => v.Code, static v => v.ValueId, StringComparer.Ordinal);

    private static Dictionary<string, string> Bilingual(string en, string ar) => new(StringComparer.Ordinal) { ["en"] = en, ["ar"] = ar };

    private static T Require<T>(Result<T> result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Demo seed failed: {result.Error!.Code} — {result.Error.Message}");
        }

        return result.Value;
    }
}
