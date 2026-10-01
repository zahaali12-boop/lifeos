using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Accounting.Application;
using Quicker.Kernel.Results;
using Quicker.Organization.Application;
using Quicker.Partners.Application;
using Quicker.Partners.Contracts;
using Quicker.Persistence;

namespace Quicker.Migrator.Demo;

/// <summary>What the customer and CRM seed produced.</summary>
public sealed record DemoCrmOutcome(int Customers, int Prospects, int Opportunities, int Activities);

/// <summary>
/// Demo seed for roadmap 5.1: the six customers the books already sell to become real customers, with contacts,
/// billing and shipping addresses and an account in each company they buy from (groups, terms, the rep, the default
/// warehouse, a credit limit; Al-Noor on credit hold after two returned cheques); four sales reps (Noor Abbas, the
/// demo's sales user, among them) paid under a commission plan per company; six prospects; and a pipeline of two dozen
/// opportunities across the three companies and every stage, with won and lost deals and the calls, meetings, tasks
/// and notes around them. Everything goes through the modules' services; afterwards the pipeline's history is dated
/// over the past four months, as a CRM in use would have it, because the services stamp a move with the time it is
/// made.
/// </summary>
internal static class DemoCrm
{
    private sealed record Contact(string En, string Ar, string Role, string Email);

    private sealed record Account(string Company, string Group, string Rep, decimal CreditLimit, string? Hold = null);

    private sealed record CustomerPlan(string Key, string Country, string CityEn, string CityAr, Contact Buyer, Contact Finance, Account[] Accounts, string? Vat = null);

    private sealed record Prospect(string Code, string En, string Ar, string Country, Contact Contact);

    private sealed record Deal(string Partner, string Company, string Title, decimal Amount, string Rep, string[] Path, int CreatedDaysAgo, int LastMoveDaysAgo, int CloseInDays, string? LostReason = null, string? Source = null);

    private static readonly (string Code, string En, string Ar, string Terms, string Delivery)[] Groups =
    [
        ("RETAIL", "Retail chains", "سلاسل التجزئة", "NET30", "DAP"),
        ("WHOLESALE", "Wholesale and distribution", "الجملة والتوزيع", "NET60", "EXW"),
        ("CORPORATE", "Corporate and energy", "الشركات والطاقة", "EOM30", "DAP"),
        ("HORECA", "Hotels and restaurants", "الفنادق والمطاعم", "NET15", "DAP"),
    ];

    private static readonly (string Code, string En, string Ar, string? Member, string Company, string Plan, string Email)[] Reps =
    [
        ("NOOR", "Noor Abbas", "نور عباس", "sales", "IQT", "IQT-STD", "noor.abbas@demo.quicker.example"),
        ("HASSAN", "Hassan Kadhim", "حسن كاظم", null, "IQT", "IQT-STD", "hassan.kadhim@demo.quicker.example"),
        ("RANA", "Rana Adel", "رنا عادل", null, "USI", "USI-STD", "rana.adel@demo.quicker.example"),
        ("FAISAL", "Faisal Al-Mansoori", "فيصل المنصوري", null, "AEG", "AEG-STD", "faisal.mansoori@demo.quicker.example"),
    ];

    private static readonly CustomerPlan[] Customers =
    [
        new("cust:baghdad-mall", "IQ", "Baghdad", "بغداد", new("Ahmed Jassim", "أحمد جاسم", "Purchasing manager", "ahmed.jassim@baghdad-mall.example"), new("Rasha Kamil", "رشا كامل", "Accounts payable", "ap@baghdad-mall.example"),
            [new("IQT", "RETAIL", "NOOR", 250_000_000m)]),
        new("cust:al-noor", "IQ", "Baghdad", "بغداد", new("Mustafa Adnan", "مصطفى عدنان", "Category buyer", "m.adnan@al-noor.example"), new("Huda Salman", "هدى سلمان", "Finance", "finance@al-noor.example"),
            [new("IQT", "RETAIL", "NOOR", 120_000_000m, Hold: "Two cheques returned in August; new orders wait for credit control")]),
        new("cust:kurdistan-dist", "IQ", "Erbil", "أربيل", new("Azad Kareem", "آزاد كريم", "Operations director", "azad@kurdistan-dist.example"), new("Shilan Omer", "شيلان عمر", "Accounts", "accounts@kurdistan-dist.example"),
            [new("IQT", "WHOLESALE", "HASSAN", 400_000_000m)]),
        new("cust:basra-oil", "IQ", "Basra", "البصرة", new("Haider Najm", "حيدر نجم", "Procurement lead", "h.najm@basra-oil.example"), new("Zahraa Ali", "زهراء علي", "Accounts payable", "ap@basra-oil.example"),
            [new("IQT", "CORPORATE", "NOOR", 600_000_000m), new("USI", "CORPORATE", "RANA", 250_000m)]),
        new("cust:gulf-retail", "AE", "Dubai", "دبي", new("Khalid Al-Suwaidi", "خالد السويدي", "Head of buying", "khalid@gulf-retail.example"), new("Mariam Nasser", "مريم ناصر", "Finance", "finance@gulf-retail.example"),
            [new("AEG", "RETAIL", "FAISAL", 500_000m), new("USI", "RETAIL", "RANA", 150_000m)], Vat: "100234567800003"),
        new("cust:dubai-hotels", "AE", "Dubai", "دبي", new("Ravi Menon", "رافي مينون", "Purchasing manager", "ravi@dubai-hotels.example"), new("Aisha Rahman", "عائشة رحمن", "Accounts", "accounts@dubai-hotels.example"),
            [new("AEG", "HORECA", "FAISAL", 300_000m)], Vat: "100345678900003"),
    ];

    private static readonly Prospect[] Prospects =
    [
        new("PRS-ERBIL-GRAND", "Erbil Grand Hotel", "فندق أربيل الكبير", "IQ", new("Dilshad Aziz", "دلشاد عزيز", "General manager", "gm@erbil-grand.example")),
        new("PRS-NAJAF-SERV", "Najaf Pilgrim Services", "خدمات زوار النجف", "IQ", new("Sayed Hashim", "سيد هاشم", "Logistics", "logistics@najaf-services.example")),
        new("PRS-MOSUL-CEMENT", "Mosul Cement Company", "شركة سمنت الموصل", "IQ", new("Yousif Qasim", "يوسف قاسم", "Site administrator", "admin@mosul-cement.example")),
        new("PRS-BAGHDAD-UNI", "University of Baghdad Procurement", "مشتريات جامعة بغداد", "IQ", new("Dr. Samir Hadi", "د. سمير هادي", "Procurement committee", "procurement@uob.example")),
        new("PRS-SHARJAH-FOODS", "Sharjah Fine Foods", "الشارقة للأغذية الفاخرة", "AE", new("Omar Farouk", "عمر فاروق", "Supply chain", "supply@sharjah-foods.example")),
        new("PRS-ABUDHABI-CATER", "Abu Dhabi Catering Co", "أبوظبي للتموين", "AE", new("Lina Haddad", "لينا حداد", "Procurement", "lina@ad-catering.example")),
    ];

    private static readonly string[] ToNegotiation = ["QUALIFIED", "PROPOSAL", "NEGOTIATION"];

    private static readonly Deal[] Deals =
    [
        new("cust:baghdad-mall", "IQT", "Ramadan beverage promotion, 14 stores", 85_000_000m, "NOOR", ToNegotiation, 40, 6, 10, Source: "Existing customer"),
        new("cust:baghdad-mall", "IQT", "Snack aisle refit", 32_000_000m, "NOOR", ["QUALIFIED"], 20, 9, 35),
        new("cust:baghdad-mall", "IQT", "Winter dairy contract", 120_000_000m, "NOOR", ["QUALIFIED", "PROPOSAL", "WON"], 95, 60, 0),
        new("cust:al-noor", "IQT", "Opening stock for the new Karrada branch", 64_000_000m, "NOOR", ["QUALIFIED", "PROPOSAL"], 25, 4, 21),
        new("cust:al-noor", "IQT", "Private-label bottled water", 45_000_000m, "NOOR", ["QUALIFIED", "LOST"], 80, 30, 0, LostReason: "Chose a local bottler on price"),
        new("cust:kurdistan-dist", "IQT", "Erbil distribution agreement 2027", 310_000_000m, "HASSAN", ToNegotiation, 70, 12, 45, Source: "Trade fair"),
        new("cust:kurdistan-dist", "IQT", "Duhok warehouse restock", 58_000_000m, "HASSAN", ["PROPOSAL", "WON"], 50, 18, 0),
        new("cust:basra-oil", "IQT", "Camp catering supplies, Rumaila", 240_000_000m, "NOOR", ["QUALIFIED", "PROPOSAL"], 33, 8, 30),
        new("cust:basra-oil", "IQT", "PPE and cleaning materials", 27_500_000m, "NOOR", ["PROPOSAL", "WON"], 120, 90, 0),
        new("PRS-ERBIL-GRAND", "IQT", "Minibar and pantry supply", 18_000_000m, "HASSAN", [], 5, 5, 60, Source: "Website"),
        new("PRS-NAJAF-SERV", "IQT", "Arbaeen water and snacks", 150_000_000m, "NOOR", ["QUALIFIED"], 15, 3, 40, Source: "Referral"),
        new("PRS-MOSUL-CEMENT", "IQT", "Site canteen supplies", 22_000_000m, "HASSAN", ["QUALIFIED", "LOST"], 60, 25, 0, LostReason: "No budget this year"),
        new("cust:basra-oil", "USI", "Networking equipment for field offices", 185_000m, "RANA", ToNegotiation, 45, 7, 14),
        new("cust:basra-oil", "USI", "Tablets for inspectors", 64_000m, "RANA", ["PROPOSAL", "WON"], 75, 40, 0),
        new("cust:gulf-retail", "USI", "Accessories range for the Basra stores", 42_000m, "RANA", ["QUALIFIED"], 18, 10, 50),
        new("PRS-BAGHDAD-UNI", "USI", "Computer lab refresh", 230_000m, "RANA", ["QUALIFIED", "PROPOSAL"], 28, 11, 25, Source: "Tender"),
        new("PRS-BAGHDAD-UNI", "USI", "Library printers", 18_500m, "RANA", ["QUALIFIED", "LOST"], 55, 35, 0, LostReason: "Tender cancelled"),
        new("cust:gulf-retail", "AEG", "Back-to-school stationery", 380_000m, "FAISAL", ToNegotiation, 30, 2, 7),
        new("cust:gulf-retail", "AEG", "Office supplies framework", 150_000m, "FAISAL", ["PROPOSAL", "WON"], 110, 70, 0),
        new("cust:dubai-hotels", "AEG", "Guest stationery for six hotels", 96_000m, "FAISAL", ["QUALIFIED", "PROPOSAL"], 21, 6, 20),
        new("cust:dubai-hotels", "AEG", "Printed menus and folders", 44_000m, "FAISAL", ["PROPOSAL", "LOST"], 65, 45, 0, LostReason: "Stayed with the incumbent printer"),
        new("PRS-SHARJAH-FOODS", "AEG", "Food packaging materials", 210_000m, "FAISAL", ["QUALIFIED"], 12, 5, 45, Source: "Trade fair"),
        new("PRS-ABUDHABI-CATER", "AEG", "Disposable tableware", 125_000m, "FAISAL", [], 3, 3, 75, Source: "Website"),
    ];

    public static async Task<DemoCrmOutcome> SeedAsync(IServiceProvider services, IReadOnlyList<(DemoCompany Definition, CompanySummary Company)> companies, DateTimeOffset now, DateOnly today, CancellationToken cancellationToken)
    {
        var unitOfWork = services.GetRequiredService<IUnitOfWorkAccessor>().Current;
        var tenant = unitOfWork.Context.TenantId.Value;
        var partnerService = services.GetRequiredService<PartnerService>();
        var customerService = services.GetRequiredService<CustomerService>();
        var salesSetup = services.GetRequiredService<SalesSetupService>();
        var opportunityService = services.GetRequiredService<OpportunityService>();
        var activityService = services.GetRequiredService<CrmActivityService>();
        var profiles = services.GetRequiredService<ProfileService>();
        Guid Company(string code) => companies.Single(c => c.Definition.Code == code).Company.Id;
        string Currency(string code) => companies.Single(c => c.Definition.Code == code).Definition.FunctionalCurrency;

        // Terms, groups and the posting group trade customers share.
        var paymentTerms = await TermsAsync(services, cancellationToken);
        var posting = Require(await profiles.SaveGroupAsync(null, new SavePostingGroupRequest(CustomerService.CustomerPostingGroupKind, "CUS-TRADE", Text("Trade customers", "عملاء تجاريون")), cancellationToken)).Id;
        var groups = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (code, en, ar, terms, delivery) in Groups)
        {
            groups[code] = Require(await customerService.SaveGroupAsync(null, new SaveCustomerGroupRequest(code, Text(en, ar), posting, paymentTerms.Payment[terms], paymentTerms.Delivery[delivery]), cancellationToken)).Id;
        }

        // Commission plans per company currency: monthly or quarterly bands, beverages at a thinner rate, hotels a little more.
        var beverages = await unitOfWork.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition("SELECT id FROM app.itm_item_categories WHERE tenant_id = @tenant AND code = 'BEV'", new { tenant }, unitOfWork.Transaction, cancellationToken: cancellationToken));
        var plans = new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            ["IQT-STD"] = Require(await salesSetup.SavePlanAsync(null, new SaveCommissionPlanRequest("IQT-STD", Text("Iraq trading sales 2026", "مبيعات العراق للتجارة ٢٠٢٦"), "IQD", TierPeriod: CommissionTierPeriods.Month,
                Rules: [new(1.5m), new(2.5m, 100_000_000m), new(1m, ItemCategoryId: beverages)]), cancellationToken)).Id,
            ["USI-STD"] = Require(await salesSetup.SavePlanAsync(null, new SaveCommissionPlanRequest("USI-STD", Text("Imports sales 2026", "مبيعات الاستيراد ٢٠٢٦"), "USD", TierPeriod: CommissionTierPeriods.Quarter,
                Rules: [new(2m), new(3m, 150_000m)]), cancellationToken)).Id,
            ["AEG-STD"] = Require(await salesSetup.SavePlanAsync(null, new SaveCommissionPlanRequest("AEG-STD", Text("Gulf Gate sales 2026", "مبيعات بوابة الخليج ٢٠٢٦"), "AED", TierPeriod: CommissionTierPeriods.Month,
                Rules: [new(2m), new(3m, 250_000m), new(2.5m, CustomerGroupId: groups["HORECA"])]), cancellationToken)).Id,
        };

        var reps = new Dictionary<string, (Guid Id, Guid? Membership)>(StringComparer.Ordinal);
        foreach (var (code, en, ar, member, company, plan, email) in Reps)
        {
            var membership = member is null ? (Guid?)null : DemoData.MembershipId(DemoData.Users.Single(u => u.Local == member));
            var rep = Require(await salesSetup.SaveRepAsync(null, new SaveSalesRepRequest(code, Text(en, ar), membership, CompanyId: Company(company), CommissionPlanId: plans[plan], Email: email), cancellationToken));
            reps[code] = (rep.Id, membership);
        }

        var warehouses = (await unitOfWork.Connection.QueryAsync<(string Code, Guid Id)>(new CommandDefinition(
            "SELECT code, id FROM app.inv_warehouses WHERE tenant_id = @tenant AND code IN ('BSR-WH', 'BGD-DIST', 'DXB-SHOP')", new { tenant }, unitOfWork.Transaction, cancellationToken: cancellationToken))).ToDictionary(static w => w.Code, static w => w.Id, StringComparer.Ordinal);
        var defaultWarehouse = new Dictionary<string, Guid>(StringComparer.Ordinal) { ["IQT"] = warehouses["BSR-WH"], ["USI"] = warehouses["BGD-DIST"], ["AEG"] = warehouses["DXB-SHOP"] };

        // The customers the books already name by fixed ids become partner records with those ids, as the suppliers did.
        var partners = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var customer in Customers)
        {
            var party = DemoBooks.Customers.Single(c => c.Key == customer.Key);
            await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO app.ptr_partners (tenant_id, id, code, legal_name_i18n, is_customer, email) VALUES (@tenant, @id, @code, @name::jsonb, true, @email) ON CONFLICT (tenant_id, id) DO NOTHING",
                new { tenant, id = party.Ref, code = "CUS-" + customer.Key[5..].ToUpperInvariant(), name = JsonSerializer.Serialize(Text(party.En, party.Ar)), email = customer.Finance.Email },
                unitOfWork.Transaction, cancellationToken: cancellationToken));
            partners[customer.Key] = party.Ref;
            await ContactsAndAddressesAsync(partnerService, party.Ref, customer.Country, customer.CityEn, customer.CityAr, [customer.Buyer, customer.Finance], cancellationToken);
            if (customer.Vat is { } vat)
            {
                Require(await partnerService.SaveTaxRegistrationAsync(party.Ref, null, new SaveTaxRegistrationRequest(customer.Country, "vat", vat), cancellationToken));
            }

            foreach (var account in customer.Accounts)
            {
                Require(await customerService.SaveAccountAsync(party.Ref, Company(account.Company), new SaveCustomerAccountRequest(
                    CustomerGroupId: groups[account.Group], SalesRepId: reps[account.Rep].Id, DefaultWarehouseId: defaultWarehouse[account.Company], Currency: Currency(account.Company),
                    CreditLimit: account.CreditLimit, OverdueBlockDays: 60), cancellationToken));
                if (account.Hold is { } reason)
                {
                    Require(await customerService.SetCreditStatusAsync(party.Ref, Company(account.Company), new CreditStatusRequest(CreditStatuses.OnHold, reason), cancellationToken));
                }
            }
        }

        foreach (var prospect in Prospects)
        {
            var created = Require(await partnerService.CreateAsync(new SavePartnerRequest(prospect.Code, Text(prospect.En, prospect.Ar), Email: prospect.Contact.Email), cancellationToken));
            partners[prospect.Code] = created.Id;
            await ContactsAndAddressesAsync(partnerService, created.Id, prospect.Country, null, null, [prospect.Contact], cancellationToken);
        }

        // The pipeline: every deal through the stages it reached, then dated as it happened.
        var stages = (await salesSetup.ListStagesAsync(cancellationToken)).ToDictionary(static s => s.Code, static s => s.Id, StringComparer.Ordinal);
        var ownerMembership = DemoData.MembershipId(DemoData.Owner);
        var activities = 0;
        var ordinal = 0;
        foreach (var deal in Deals)
        {
            ordinal++;
            var partner = partners[deal.Partner];
            var opportunity = Require(await opportunityService.CreateAsync(new CreateOpportunityRequest(Company(deal.Company), partner, deal.Title, deal.Amount, Currency(deal.Company),
                SalesRepId: reps[deal.Rep].Id, ExpectedClose: deal.CloseInDays > 0 ? today.AddDays(deal.CloseInDays) : null, Source: deal.Source), cancellationToken));
            foreach (var stage in deal.Path)
            {
                Require(await opportunityService.MoveAsync(opportunity.Id, new MoveOpportunityRequest(stages[stage], LostReason: stage == "LOST" ? deal.LostReason : null), cancellationToken));
            }

            await DateHistoryAsync(unitOfWork, tenant, opportunity.Id, now, today, deal, cancellationToken);

            // Around each deal: what was done, and for an open one the next step, assigned to its rep or, for reps who are
            // not members of the workspace, to the owner.
            var assignee = reps[deal.Rep].Membership ?? ownerMembership;
            var done = Require(await activityService.SaveAsync(null, new SaveCrmActivityRequest(partner, CrmActivityKinds.Meeting, $"Discovery meeting: {deal.Title}", AssignedMembershipId: assignee, OpportunityId: opportunity.Id), cancellationToken));
            Require(await activityService.CompleteAsync(done.Id, new CompleteCrmActivityRequest("Needs, volumes and decision makers agreed"), cancellationToken));
            await DateActivityAsync(unitOfWork, tenant, done.Id, now.AddDays(-deal.CreatedDaysAgo + 1), now.AddDays(-deal.CreatedDaysAgo + 2), cancellationToken);
            activities++;
            if (deal.Path.Length == 0 || deal.Path[^1] is not ("WON" or "LOST"))
            {
                var (kind, subject) = (ordinal % 3) switch
                {
                    0 => (CrmActivityKinds.Call, "Call to confirm quantities and delivery dates"),
                    1 => (CrmActivityKinds.Task, "Send the revised proposal"),
                    _ => (CrmActivityKinds.Meeting, "Meet the buyer on site"),
                };

                // Every fourth next step is already late, so the overdue list has something to show.
                Require(await activityService.SaveAsync(null, new SaveCrmActivityRequest(partner, kind, subject, DueAt: now.AddDays(ordinal % 4 == 0 ? -2 : 1 + (ordinal % 6)), AssignedMembershipId: assignee, OpportunityId: opportunity.Id), cancellationToken));
                activities++;
            }
        }

        // Notes on the accounts the credit controller and the reps keep an eye on.
        foreach (var (key, subject) in new[]
        {
            ("cust:al-noor", "Credit hold: two cheques returned in August, finance to agree a payment plan before new orders"),
            ("cust:basra-oil", "Invoices must quote the field PO number and go to ap@basra-oil.example"),
            ("cust:dubai-hotels", "Prefers deliveries before 10:00, loading bay at the back"),
        })
        {
            var note = Require(await activityService.SaveAsync(null, new SaveCrmActivityRequest(partners[key], CrmActivityKinds.Note, subject), cancellationToken));
            await DateActivityAsync(unitOfWork, tenant, note.Id, now.AddDays(-20), now.AddDays(-20), cancellationToken);
            activities++;
        }

        return new DemoCrmOutcome(Customers.Length, Prospects.Length, Deals.Length, activities);
    }

    private sealed record Terms(IReadOnlyDictionary<string, Guid> Payment, IReadOnlyDictionary<string, Guid> Delivery);

    private static async Task<Terms> TermsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var suppliers = services.GetRequiredService<SupplierService>();
        var payment = (await suppliers.ListPaymentTermsAsync(cancellationToken)).ToDictionary(static t => t.Code, static t => t.Id, StringComparer.Ordinal);
        var delivery = (await suppliers.ListDeliveryTermsAsync(cancellationToken)).ToDictionary(static t => t.Code, static t => t.Id, StringComparer.Ordinal);
        return new Terms(payment, delivery);
    }

    private static async Task ContactsAndAddressesAsync(PartnerService partners, Guid partnerId, string country, string? cityEn, string? cityAr, IReadOnlyList<Contact> contacts, CancellationToken cancellationToken)
    {
        for (var i = 0; i < contacts.Count; i++)
        {
            var c = contacts[i];
            Require(await partners.SaveContactAsync(partnerId, null, new SaveContactRequest(Text(c.En, c.Ar), c.Role, c.Email, IsPrimary: i == 0, ReceivesStatements: i == contacts.Count - 1 && contacts.Count > 1), cancellationToken));
        }

        if (cityEn is null || cityAr is null)
        {
            return;
        }

        foreach (var (role, lineEn, lineAr) in new[] { ("billing", "Head office", "المكتب الرئيسي"), ("shipping", "Central warehouse", "المستودع المركزي") })
        {
            var address = JsonSerializer.SerializeToElement(new { line1 = Text(lineEn, lineAr), city = Text(cityEn, cityAr) });
            Require(await partners.SaveAddressAsync(partnerId, null, new SaveAddressRequest(role, country, address, cityEn, IsDefault: true), cancellationToken));
        }
    }

    /// <summary>Spreads a deal's moves between the day it was created and its last move, and closes a won or lost one that day.</summary>
    private static Task DateHistoryAsync(IUnitOfWork unitOfWork, Guid tenant, Guid opportunityId, DateTimeOffset now, DateOnly today, Deal deal, CancellationToken cancellationToken)
    {
        var created = now.AddDays(-deal.CreatedDaysAgo);
        var lastMove = now.AddDays(-deal.LastMoveDaysAgo);
        return unitOfWork.Connection.ExecuteAsync(new CommandDefinition("""
            UPDATE app.ptr_opportunity_stage_changes c
               SET changed_at = @created + (@lastMove - @created) * ((c.sequence - 1)::float8 / greatest(n.total - 1, 1))
              FROM (SELECT count(*) AS total FROM app.ptr_opportunity_stage_changes WHERE tenant_id = @tenant AND opportunity_id = @opportunityId) n
             WHERE c.tenant_id = @tenant AND c.opportunity_id = @opportunityId;
            UPDATE app.ptr_opportunities
               SET created_at = @created, updated_at = @lastMove, closed_on = CASE WHEN status = 'open' THEN NULL ELSE @closedOn END
             WHERE tenant_id = @tenant AND id = @opportunityId;
            """, new { tenant, opportunityId, created, lastMove, closedOn = today.AddDays(-deal.LastMoveDaysAgo) }, unitOfWork.Transaction, cancellationToken: cancellationToken));
    }

    private static Task DateActivityAsync(IUnitOfWork unitOfWork, Guid tenant, Guid activityId, DateTimeOffset created, DateTimeOffset completed, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app.ptr_crm_activities SET created_at = @created, updated_at = @completed, completed_at = CASE WHEN status = 'open' THEN NULL ELSE @completed END WHERE tenant_id = @tenant AND id = @activityId",
            new { tenant, activityId, created, completed }, unitOfWork.Transaction, cancellationToken: cancellationToken));

    private static Dictionary<string, string> Text(string en, string ar) => new(StringComparer.Ordinal) { ["en"] = en, ["ar"] = ar };

    private static T Require<T>(Result<T> result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Demo seed failed: {result.Error!.Code} — {result.Error.Message}");
        }

        return result.Value;
    }
}
