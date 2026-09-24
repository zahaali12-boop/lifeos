using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.TestSupport;
using Quicker.Partners.Contracts;

namespace Quicker.Partners.Tests;

/// <summary>
/// The customer side of the partner master and its CRM (roadmap 5.1, DOMAIN_MODEL §7): customer accounts with terms
/// defaulting from their group and credit settings only the credit permission changes; credit holds and blocks as
/// sales will read them; each side of the business giving only its own role; sales reps, tiered commission plans,
/// the pipeline, opportunities and activities, all within the companies a member's permission reaches.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CustomersTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid OtherCompanyId);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "SLS", legalName = Name("Sales Co", "شركة المبيعات"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad" });
        var other = await owner.PostAsync("/api/v1/organization/companies", new { code = "EXP", legalName = Name("Export Co", "شركة التصدير"), country = "AE", functionalCurrency = "AED", timeZone = "Asia/Dubai" });
        return new Setup(ws, owner, company.GetProperty("id").GetGuid(), other.GetProperty("id").GetGuid());
    }

    /// <summary>A member holding the grants, everywhere or only in the given company.</summary>
    private async Task<(HttpClient Client, Guid MembershipId)> MemberAsync(Setup s, string roleCode, Guid? onlyCompany, params string[] grants)
    {
        var role = await s.Owner.PostAsync("/api/v1/roles", new { code = roleCode, name = Name(roleCode, roleCode), description = "", grants });
        var roleId = role.GetProperty("id").GetGuid();
        var email = $"{roleCode}-{s.Ws.Slug}@example.test";
        var invited = await s.Owner.PostAsync("/api/v1/users/invite", new { email, displayName = roleCode, roleIds = onlyCompany is null ? new[] { roleId } : [] }, HttpStatusCode.Created);
        var membershipId = invited.GetProperty("membershipId").GetGuid();
        if (onlyCompany is { } company)
        {
            (await s.Owner.PostAsJsonAsync($"/api/v1/users/{membershipId}/assignments", new { roleId, scopes = new[] { new { scopeType = "company", scopeId = company } } }, Json)).EnsureSuccessStatusCode();
        }

        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, Json)).ReadJsonAsync();
        return (Api.ClientFor(accepted.GetProperty("accessToken").GetString()!), membershipId);
    }

    private static async Task<Guid> PartnerAsync(HttpClient client, string code, bool isCustomer = true, bool isSupplier = false) =>
        (await client.PostAsync("/api/v1/partners", new { code, legalName = Name(code + " Trading", "تجارة " + code), isCustomer, isSupplier })).GetProperty("id").GetGuid();

    [Fact]
    public async Task Customer_accounts_default_from_their_group_and_only_the_credit_permission_changes_credit()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var partnerId = await PartnerAsync(owner, "BASRA-RETAIL");

        var terms = await owner.GetOkAsync("/api/v1/partners/payment-terms");
        var net30 = terms.ByCode("NET30").GetProperty("id").GetGuid();
        var dap = (await owner.GetOkAsync("/api/v1/partners/delivery-terms")).ByCode("DAP").GetProperty("id").GetGuid();
        var customerPosting = (await owner.PostAsync("/api/v1/accounting/posting-groups", new { kind = "partner_customer", code = "CUS-LOCAL", name = Name("Local customers", "عملاء محليون") })).GetProperty("id").GetGuid();
        var supplierPosting = (await owner.PostAsync("/api/v1/accounting/posting-groups", new { kind = "partner_supplier", code = "SUP-LOCAL", name = Name("Local suppliers", "موردون محليون") })).GetProperty("id").GetGuid();
        (await owner.PostErrorAsync("/api/v1/partners/customer-groups", new { code = "RETAIL", name = Name("Retail", "تجزئة"), postingGroupId = supplierPosting }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("customer_group.posting_group_invalid");
        var group = await owner.PostAsync("/api/v1/partners/customer-groups", new { code = "retail", name = Name("Retail", "تجزئة"), postingGroupId = customerPosting, paymentTermsId = net30, deliveryTermsId = dap });
        var groupId = group.GetProperty("id").GetGuid();
        group.GetProperty("code").GetString().ShouldBe("RETAIL");

        // A warehouse of the other company, and a rep who sells only for the other company, are refused.
        var otherWarehouse = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId = s.OtherCompanyId, code = "DXB", name = Name("Dubai", "دبي") })).GetProperty("id").GetGuid();
        (await owner.PutErrorAsync($"/api/v1/partners/{partnerId}/customer-accounts/{s.CompanyId}", new { customerGroupId = groupId, defaultWarehouseId = otherWarehouse }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("customer.warehouse_invalid");
        var exportRep = (await owner.PostAsync("/api/v1/partners/sales-reps", new { code = "EXP-1", name = Name("Export rep", "مندوب التصدير"), companyId = s.OtherCompanyId })).GetProperty("id").GetGuid();
        (await owner.PutErrorAsync($"/api/v1/partners/{partnerId}/customer-accounts/{s.CompanyId}", new { customerGroupId = groupId, salesRepId = exportRep }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("customer.sales_rep_other_company");
        (await owner.PutErrorAsync($"/api/v1/partners/{partnerId}/customer-accounts/{s.CompanyId}", new { customerGroupId = groupId, creditLimit = 1000.5555 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("customer.credit_limit_precision");
        (await owner.PutErrorAsync($"/api/v1/partners/{partnerId}/customer-accounts/{s.CompanyId}", new { customerGroupId = groupId, statementFrequency = "daily" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("customer.statement_frequency_invalid");

        var rep = (await owner.PostAsync("/api/v1/partners/sales-reps", new { code = "SLS-1", name = Name("Huda Salim", "هدى سالم"), companyId = s.CompanyId })).GetProperty("id").GetGuid();
        var warehouse = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId = s.CompanyId, code = "BSR", name = Name("Basra", "البصرة") })).GetProperty("id").GetGuid();
        var account = await owner.PutAsync($"/api/v1/partners/{partnerId}/customer-accounts/{s.CompanyId}", new { customerGroupId = groupId, salesRepId = rep, defaultWarehouseId = warehouse, creditLimit = 25_000_000, overdueBlockDays = 30 });
        account.GetProperty("currency").GetString().ShouldBe("IQD", "the currency defaults to the company's");
        account.GetProperty("functionalCurrency").GetString().ShouldBe("IQD");
        account.GetProperty("effective").GetProperty("paymentTermsCode").GetString().ShouldBe("NET30");
        account.GetProperty("effective").GetProperty("deliveryTermsCode").GetString().ShouldBe("DAP");
        account.GetProperty("effective").GetProperty("postingGroupId").GetGuid().ShouldBe(customerPosting);
        account.GetProperty("salesRepCode").GetString().ShouldBe("SLS-1");
        account.GetProperty("creditLimit").GetDecimal().ShouldBe(25_000_000m);
        account.GetProperty("creditExposureBasis").GetString().ShouldBe(CreditExposureBases.OpenArPlusOrders);
        account.GetProperty("creditStatus").GetString().ShouldBe(CreditStatuses.Ok);
        (await owner.GetOkAsync("/api/v1/partners/customer-groups")).ByCode("RETAIL").GetProperty("customers").GetInt32().ShouldBe(1);
        (await owner.GetOkAsync("/api/v1/partners/sales-reps")).ByCode("SLS-1").GetProperty("customers").GetInt32().ShouldBe(1);

        // A sales rep keeps the account but cannot touch its credit; sent as it stands, the save passes.
        var (salesRep, _) = await MemberAsync(s, "rep", null, "partners.customer.*");
        (await salesRep.PutErrorAsync($"/api/v1/partners/{partnerId}/customer-accounts/{s.CompanyId}", new { customerGroupId = groupId, salesRepId = rep, defaultWarehouseId = warehouse, creditLimit = 90_000_000, overdueBlockDays = 30 }, HttpStatusCode.Forbidden)).Code.ShouldBe("customer.credit_forbidden");
        var kept = await salesRep.PutAsync($"/api/v1/partners/{partnerId}/customer-accounts/{s.CompanyId}", new { customerGroupId = groupId, salesRepId = rep, defaultWarehouseId = warehouse, creditLimit = 25_000_000, overdueBlockDays = 30, statementFrequency = "weekly" });
        kept.GetProperty("statementFrequency").GetString().ShouldBe("weekly");
        (await salesRep.PostErrorAsync($"/api/v1/partners/{partnerId}/customer-accounts/{s.CompanyId}/credit-status", new { status = "blocked", reason = "x" }, HttpStatusCode.Forbidden)).Code.ShouldBe("auth.forbidden");

        // Credit control holds, blocks and releases; sales reads the result through the directory.
        var (credit, _) = await MemberAsync(s, "credit", null, "partners.customer.read", "partners.credit.manage");
        (await credit.PostErrorAsync($"/api/v1/partners/{partnerId}/customer-accounts/{s.CompanyId}/credit-status", new { status = "on_hold" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("customer.credit_reason_required");
        var onHold = await credit.PostAsync($"/api/v1/partners/{partnerId}/customer-accounts/{s.CompanyId}/credit-status", new { status = "on_hold", reason = "Cheque returned" }, HttpStatusCode.OK);
        onHold.GetProperty("creditStatus").GetString().ShouldBe("on_hold");
        onHold.GetProperty("creditStatusReason").GetString().ShouldBe("Cheque returned");
        var held = await host.InTenantAsync(s.Ws.TenantId, (sp, ct) => sp.GetRequiredService<ICustomerDirectory>().EnsureCustomerAsync(s.CompanyId, partnerId, CustomerPurposes.Order, ct));
        held.IsSuccess.ShouldBeTrue("a hold is the credit check's to apply, not a refusal");
        held.Value.CreditStatus.ShouldBe(CreditStatuses.OnHold);
        held.Value.PaymentTermsCode.ShouldBe("NET30");
        held.Value.CreditLimit.ShouldBe(25_000_000m);
        held.Value.OverdueBlockDays.ShouldBe(30);
        held.Value.SalesRepId.ShouldBe(rep);
        held.Value.DefaultWarehouseId.ShouldBe(warehouse);

        await credit.PostAsync($"/api/v1/partners/{partnerId}/customer-accounts/{s.CompanyId}/credit-status", new { status = "blocked", reason = "Legal case" }, HttpStatusCode.OK);
        foreach (var purpose in new[] { CustomerPurposes.Quote, CustomerPurposes.Order, CustomerPurposes.Shipment, CustomerPurposes.Invoice })
        {
            var refused = await host.InTenantAsync(s.Ws.TenantId, (sp, ct) => sp.GetRequiredService<ICustomerDirectory>().EnsureCustomerAsync(s.CompanyId, partnerId, purpose, ct));
            refused.Error!.Code.ShouldBe("customer.credit_blocked", purpose);
            refused.Error.Why!["reason"].ShouldBe("Legal case");
        }

        var receipt = await host.InTenantAsync(s.Ws.TenantId, (sp, ct) => sp.GetRequiredService<ICustomerDirectory>().EnsureCustomerAsync(s.CompanyId, partnerId, CustomerPurposes.Receipt, ct));
        receipt.IsSuccess.ShouldBeTrue("money owed is always taken in");
        (await owner.GetOkAsync($"/api/v1/partners/customers?creditStatus=held")).Only().GetProperty("partnerCode").GetString().ShouldBe("BASRA-RETAIL");
        var released = await credit.PostAsync($"/api/v1/partners/{partnerId}/customer-accounts/{s.CompanyId}/credit-status", new { status = "ok" }, HttpStatusCode.OK);
        released.GetProperty("creditStatus").GetString().ShouldBe("ok");
        released.GetProperty("creditStatusReason").ValueKind.ShouldBe(JsonValueKind.Null);
        (await credit.PutErrorAsync($"/api/v1/partners/{partnerId}/customer-accounts/{s.CompanyId}", new { customerGroupId = groupId, salesRepId = rep, defaultWarehouseId = warehouse, creditLimit = 25_000_000, overdueBlockDays = 30, statementFrequency = "none" }, HttpStatusCode.Forbidden)).Code.ShouldBe("customer.manage_forbidden");
        var raised = await credit.PutAsync($"/api/v1/partners/{partnerId}/customer-accounts/{s.CompanyId}", new { customerGroupId = groupId, salesRepId = rep, defaultWarehouseId = warehouse, creditLimit = 40_000_000, creditExposureBasis = "open_ar", overdueBlockDays = 45, statementFrequency = "weekly" });
        raised.GetProperty("creditLimit").GetDecimal().ShouldBe(40_000_000m);
        raised.GetProperty("creditExposureBasis").GetString().ShouldBe("open_ar");

        var unknown = await host.InTenantAsync(s.Ws.TenantId, (sp, ct) => sp.GetRequiredService<ICustomerDirectory>().EnsureCustomerAsync(s.OtherCompanyId, partnerId, CustomerPurposes.Order, ct));
        unknown.Error!.Code.ShouldBe("customer.not_registered");

        // The customer role cannot be taken while an account is active; an inactive account refuses documents.
        (await owner.PutErrorAsync($"/api/v1/partners/{partnerId}", new { code = "BASRA-RETAIL", legalName = Name("x", "x"), isCustomer = false }, HttpStatusCode.Conflict)).Code.ShouldBe("partner.customer_accounts_active");
        await owner.PutAsync($"/api/v1/partners/{partnerId}/customer-accounts/{s.CompanyId}", new { customerGroupId = groupId, creditLimit = 40_000_000, creditExposureBasis = "open_ar", overdueBlockDays = 45, isActive = false });
        var inactive = await host.InTenantAsync(s.Ws.TenantId, (sp, ct) => sp.GetRequiredService<ICustomerDirectory>().EnsureCustomerAsync(s.CompanyId, partnerId, CustomerPurposes.Receipt, ct));
        inactive.Error!.Code.ShouldBe("customer.inactive");
    }

    [Fact]
    public async Task Each_side_gives_only_its_own_role_and_sees_only_its_own_accounts_within_its_companies()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var both = await PartnerAsync(owner, "TWOWAY", isCustomer: true, isSupplier: true);
        await owner.PutAsync($"/api/v1/partners/{both}/supplier-accounts/{s.CompanyId}", new { currency = "IQD" });
        await owner.PutAsync($"/api/v1/partners/{both}/customer-accounts/{s.CompanyId}", new { });
        await owner.PutAsync($"/api/v1/partners/{both}/customer-accounts/{s.OtherCompanyId}", new { });

        // A sales rep limited to the first company.
        var (rep, _) = await MemberAsync(s, "rep_sls", s.CompanyId, "partners.customer.read", "partners.customer.manage", "collaboration.comment.read", "collaboration.comment.write");
        var detail = await rep.GetOkAsync($"/api/v1/partners/{both}");
        detail.GetProperty("supplierAccounts").GetArrayLength().ShouldBe(0, "supplier terms and holds are purchasing's");
        detail.GetProperty("customerAccounts").Only().GetProperty("companyId").GetGuid().ShouldBe(s.CompanyId);
        (await rep.GetOkAsync("/api/v1/partners/customers")).Only().GetProperty("companyId").GetGuid().ShouldBe(s.CompanyId);
        (await rep.PutErrorAsync($"/api/v1/partners/{both}/customer-accounts/{s.OtherCompanyId}", new { }, HttpStatusCode.NotFound)).Code.ShouldBe("company.not_found");

        // A new prospect is fine; making a partner payable is not the rep's to decide, nor are a supplier's bank accounts.
        (await rep.PostAsync("/api/v1/partners", new { code = "PROSPECT", legalName = Name("Prospect", "عميل محتمل"), isCustomer = true })).GetProperty("isCustomer").GetBoolean().ShouldBeTrue();
        (await rep.PostErrorAsync("/api/v1/partners", new { code = "SNEAKY", legalName = Name("Sneaky", "متسلل"), isSupplier = true }, HttpStatusCode.Forbidden)).Code.ShouldBe("partner.role_forbidden");
        (await rep.PostErrorAsync($"/api/v1/partners/{both}/bank-accounts", new { bankName = "Rafidain", currency = "IQD", accountNumber = "0099887766" }, HttpStatusCode.Forbidden)).Code.ShouldBe("bank_account.supplier_permission_required");
        var customerOnly = await PartnerAsync(owner, "REFUNDS");
        (await rep.PostAsync($"/api/v1/partners/{customerOnly}/bank-accounts", new { bankName = "Rafidain", currency = "IQD", accountNumber = "0099887766" })).GetProperty("accountNumberMasked").GetString().ShouldBe("00****7766");
        await rep.PostAsync($"/api/v1/partners/{both}/contacts", new { name = Name("Omar", "عمر"), email = "omar@twoway.example" });

        // A purchaser sees the supplier side only, and cannot make a partner a customer.
        var (buyer, _) = await MemberAsync(s, "buyer", null, "partners.supplier.read", "partners.supplier.manage", "collaboration.comment.read");
        var buyerView = await buyer.GetOkAsync($"/api/v1/partners/{both}");
        buyerView.GetProperty("supplierAccounts").GetArrayLength().ShouldBe(1);
        buyerView.GetProperty("customerAccounts").GetArrayLength().ShouldBe(0);
        (await buyer.PutErrorAsync($"/api/v1/partners/{customerOnly}", new { code = "REFUNDS", legalName = Name("x", "x"), isCustomer = false, isSupplier = true }, HttpStatusCode.Forbidden)).Code.ShouldBe("partner.role_forbidden");
        (await buyer.GetAsync(new Uri("/api/v1/partners/customers", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Comments on a partner are open to either side.
        (await rep.PostAsJsonAsync("/api/v1/collaboration/comments", new { entityType = "partner", entityId = both, body = "Visited their Basra branch." }, Json)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await (await buyer.GetAsync(new Uri($"/api/v1/collaboration/comments?entityType=partner&entityId={both}", UriKind.Relative))).ReadJsonAsync()).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Commission_plans_pay_marginal_bands_for_the_most_specific_scope_and_credit_notes_give_back_what_was_earned()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var electronics = (await owner.PostAsync("/api/v1/items/categories", new { code = "ELEC", name = Name("Electronics", "إلكترونيات") })).GetProperty("id").GetGuid();
        var laptops = (await owner.PostAsync("/api/v1/items/categories", new { code = "LAPTOP", name = Name("Laptops", "حواسيب محمولة"), parentId = electronics })).GetProperty("id").GetGuid();
        var furniture = (await owner.PostAsync("/api/v1/items/categories", new { code = "FURN", name = Name("Furniture", "أثاث") })).GetProperty("id").GetGuid();
        var wholesale = (await owner.PostAsync("/api/v1/partners/customer-groups", new { code = "WHOLESALE", name = Name("Wholesale", "جملة") })).GetProperty("id").GetGuid();

        (await owner.PostErrorAsync("/api/v1/partners/commission-plans", new { code = "BAD", name = Name("Bad", "سيء"), currency = "IQD", basis = "collected", accrualPoint = "invoice", rules = new[] { new { ratePct = 1 } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("commission_plan.collected_accrues_at_payment");
        (await owner.PostErrorAsync("/api/v1/partners/commission-plans", new { code = "BAD", name = Name("Bad", "سيء"), currency = "IQD" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("commission_plan.rules_required");
        (await owner.PostErrorAsync("/api/v1/partners/commission-plans", new { code = "BAD", name = Name("Bad", "سيء"), currency = "IQD", rules = new[] { new { ratePct = 1, fromAmount = 0 }, new { ratePct = 2, fromAmount = 0 } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("commission_plan.rule_duplicate");

        // Plan-wide 2% up to 10 million a month and 3% above; electronics 5%; electronics sold to wholesale 1%.
        var plan = await owner.PostAsync("/api/v1/partners/commission-plans", new
        {
            code = "STD-2026",
            name = Name("Standard 2026", "القياسية ٢٠٢٦"),
            currency = "iqd",
            tierPeriod = "month",
            rules = new object[]
            {
                new { ratePct = 2, fromAmount = 0 },
                new { ratePct = 3, fromAmount = 10_000_000 },
                new { ratePct = 5, itemCategoryId = electronics },
                new { ratePct = 1, itemCategoryId = electronics, customerGroupId = wholesale },
            },
        });
        var planId = plan.GetProperty("id").GetGuid();
        plan.GetProperty("currency").GetString().ShouldBe("IQD");
        plan.GetProperty("rules").EnumerateArray().Select(static r => r.GetProperty("itemCategoryCode").GetString()).ShouldBe([null, null, "ELEC", "ELEC"]);

        async Task<JsonElement> QuoteAsync(object body) => await owner.PostAsync($"/api/v1/partners/commission-plans/{planId}/quote", body, HttpStatusCode.OK);

        // 4 million on top of 8 million this month: 2 million at 2%, 2 million at 3%.
        var crossing = await QuoteAsync(new { amount = 4_000_000, periodToDate = 8_000_000, itemCategoryId = furniture });
        crossing.GetProperty("commission").GetDecimal().ShouldBe(100_000m);
        crossing.GetProperty("bands").EnumerateArray().Select(static b => (b.GetProperty("basis").GetDecimal(), b.GetProperty("commission").GetDecimal())).ShouldBe([(2_000_000m, 40_000m), (2_000_000m, 60_000m)]);
        crossing.GetProperty("matchedCategoryId").ValueKind.ShouldBe(JsonValueKind.Null);

        // A laptop is electronics (its parent); sold to wholesale the rule naming both wins.
        var laptop = await QuoteAsync(new { amount = 1_000_000, itemCategoryId = laptops });
        laptop.GetProperty("matchedCategoryId").GetGuid().ShouldBe(electronics);
        laptop.GetProperty("commission").GetDecimal().ShouldBe(50_000m);
        var laptopWholesale = await QuoteAsync(new { amount = 1_000_000, itemCategoryId = laptops, customerGroupId = wholesale });
        laptopWholesale.GetProperty("matchedCustomerGroupId").GetGuid().ShouldBe(wholesale);
        laptopWholesale.GetProperty("commission").GetDecimal().ShouldBe(10_000m);
        (await QuoteAsync(new { amount = 1_000_000, itemCategoryId = furniture, customerGroupId = wholesale })).GetProperty("commission").GetDecimal().ShouldBe(20_000m, "no wholesale-only rule: the plan-wide rates apply");

        // A credit note of the whole sale gives back exactly what it earned, band by band.
        var back = await QuoteAsync(new { amount = -4_000_000, periodToDate = 12_000_000, itemCategoryId = furniture });
        back.GetProperty("commission").GetDecimal().ShouldBe(-100_000m);

        // Reps are paid under a plan; a plan in use stays active; a member is one rep at most.
        var (member, membershipId) = await MemberAsync(s, "seller", null, "partners.customer.read");
        var rep = await owner.PostAsync("/api/v1/partners/sales-reps", new { code = "hs", name = Name("Huda Salim", "هدى سالم"), membershipId, commissionPlanId = planId, email = "huda@example.test" });
        rep.GetProperty("code").GetString().ShouldBe("HS");
        rep.GetProperty("memberName").GetString().ShouldBe("seller");
        rep.GetProperty("commissionPlanCode").GetString().ShouldBe("STD-2026");
        (await owner.PostErrorAsync("/api/v1/partners/sales-reps", new { code = "HS2", name = Name("Twin", "توأم"), membershipId }, HttpStatusCode.Conflict)).Code.ShouldBe("sales_rep.member_taken");
        (await owner.PostErrorAsync("/api/v1/partners/sales-reps", new { code = "HS3", name = Name("Ghost", "شبح"), membershipId = Guid.CreateVersion7() }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("sales_rep.member_unknown");
        (await owner.PutErrorAsync($"/api/v1/partners/commission-plans/{planId}", new { code = "STD-2026", name = Name("Standard 2026", "القياسية ٢٠٢٦"), currency = "IQD", rules = new[] { new { ratePct = 2 } }, isActive = false }, HttpStatusCode.Conflict)).Code.ShouldBe("commission_plan.in_use");
        var edited = await owner.PutAsync($"/api/v1/partners/commission-plans/{planId}", new { code = "STD-2026", name = Name("Standard 2026", "القياسية ٢٠٢٦"), currency = "IQD", rules = new[] { new { ratePct = 2.5, fromAmount = 0 } } });
        edited.GetProperty("rules").Only().GetProperty("ratePct").GetDecimal().ShouldBe(2.5m);
        edited.GetProperty("salesReps").GetInt32().ShouldBe(1);
        (await member.PostErrorAsync("/api/v1/partners/commission-plans", new { code = "MINE", name = Name("Mine", "لي"), currency = "IQD", rules = new[] { new { ratePct = 50 } } }, HttpStatusCode.Forbidden)).Code.ShouldBe("auth.forbidden");

        var quote = await host.InTenantAsync(s.Ws.TenantId, (sp, ct) => sp.GetRequiredService<ICustomerDirectory>().QuoteCommissionAsync(planId, null, null, 0m, 1_000_001m, ct));
        quote.Value.Commission.ShouldBe(25_000.025m, "2.5% of 1,000,001 IQD, to the dinar's three decimals");
        var fils = await host.InTenantAsync(s.Ws.TenantId, (sp, ct) => sp.GetRequiredService<ICustomerDirectory>().QuoteCommissionAsync(planId, null, null, 0m, 0.3m, ct));
        fils.Value.Commission.ShouldBe(0.008m, "2.5% of 0.300 is 0.0075, rounded half away from zero");
    }

    [Fact]
    public async Task Opportunities_move_through_the_pipeline_with_their_history_and_the_board_weights_them()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var stages = await owner.GetOkAsync("/api/v1/partners/pipeline-stages");
        stages.EnumerateArray().Select(static st => st.GetProperty("code").GetString()).ShouldBe(["LEAD", "QUALIFIED", "PROPOSAL", "NEGOTIATION", "WON", "LOST"]);
        Guid Stage(string code) => stages.ByCode(code).GetProperty("id").GetGuid();

        var partnerId = await PartnerAsync(owner, "NAJAF-MALL");
        var contact = (await owner.PostAsync($"/api/v1/partners/{partnerId}/contacts", new { name = Name("Zainab", "زينب"), isPrimary = true })).GetProperty("id").GetGuid();
        var (seller, sellerMembership) = await MemberAsync(s, "seller", null, "partners.customer.*");
        var rep = (await owner.PostAsync("/api/v1/partners/sales-reps", new { code = "ZA", name = Name("Zaid", "زيد"), membershipId = sellerMembership, companyId = s.CompanyId })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{partnerId}/customer-accounts/{s.CompanyId}", new { salesRepId = rep, currency = "USD" });

        // Numbered in the company's series, in the first open stage, the rep and currency from the customer account.
        (await seller.PostErrorAsync("/api/v1/partners/opportunities", new { companyId = s.CompanyId, partnerId, title = "", expectedAmount = 10 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("opportunity.title_invalid");
        (await seller.PostErrorAsync("/api/v1/partners/opportunities", new { companyId = s.CompanyId, partnerId, title = "Kiosks", expectedAmount = 10.001 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("opportunity.amount_precision");
        (await seller.PostErrorAsync("/api/v1/partners/opportunities", new { companyId = s.CompanyId, partnerId, title = "Kiosks", stageId = Stage("WON") }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("opportunity.stage_not_open");
        var created = await seller.PostAsync("/api/v1/partners/opportunities", new { companyId = s.CompanyId, partnerId, title = "Point-of-sale kiosks for 12 stores", expectedAmount = 120_000, contactId = contact, expectedClose = "2026-11-30", source = "Referral" });
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("number").GetString()!.ShouldMatch(@"^OPP-\d{4}-00001$");
        created.GetProperty("stageCode").GetString().ShouldBe("LEAD");
        created.GetProperty("probabilityPct").GetInt32().ShouldBe(10);
        created.GetProperty("currency").GetString().ShouldBe("USD");
        created.GetProperty("salesRepCode").GetString().ShouldBe("ZA");
        created.GetProperty("weightedAmount").GetDecimal().ShouldBe(12_000m);
        var second = await seller.PostAsync("/api/v1/partners/opportunities", new { companyId = s.CompanyId, partnerId, title = "Service contract", expectedAmount = 30_000, stageId = Stage("QUALIFIED") });
        second.GetProperty("number").GetString()!.ShouldMatch(@"^OPP-\d{4}-00002$");
        second.GetProperty("probabilityPct").GetInt32().ShouldBe(25);

        // Moves: to a proposal (the stage's probability), lost only with a reason, reopened, then won.
        var proposal = await seller.PostAsync($"/api/v1/partners/opportunities/{id}/move", new { stageId = Stage("PROPOSAL") }, HttpStatusCode.OK);
        proposal.GetProperty("probabilityPct").GetInt32().ShouldBe(50);
        proposal.GetProperty("weightedAmount").GetDecimal().ShouldBe(60_000m);
        (await seller.PostErrorAsync($"/api/v1/partners/opportunities/{id}/move", new { stageId = Stage("LOST") }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("opportunity.lost_reason_required");
        var lost = await seller.PostAsync($"/api/v1/partners/opportunities/{id}/move", new { stageId = Stage("LOST"), lostReason = "Chose a cheaper vendor" }, HttpStatusCode.OK);
        lost.GetProperty("status").GetString().ShouldBe("lost");
        lost.GetProperty("probabilityPct").GetInt32().ShouldBe(0);
        lost.GetProperty("closedOn").ValueKind.ShouldBe(JsonValueKind.String);
        (await seller.PutErrorAsync($"/api/v1/partners/opportunities/{id}", new { title = "x", expectedAmount = 1, currency = "USD", probabilityPct = 10 }, HttpStatusCode.Conflict)).Code.ShouldBe("opportunity.closed");
        var reopened = await seller.PostAsync($"/api/v1/partners/opportunities/{id}/move", new { stageId = Stage("NEGOTIATION"), probabilityPct = 80 }, HttpStatusCode.OK);
        reopened.GetProperty("status").GetString().ShouldBe("open");
        reopened.GetProperty("lostReason").ValueKind.ShouldBe(JsonValueKind.Null);
        reopened.GetProperty("closedOn").ValueKind.ShouldBe(JsonValueKind.Null);
        reopened.GetProperty("probabilityPct").GetInt32().ShouldBe(80);
        var updated = await seller.PutAsync($"/api/v1/partners/opportunities/{id}", new { title = "Point-of-sale kiosks for 14 stores", expectedAmount = 140_000, currency = "USD", probabilityPct = 80, contactId = contact, salesRepId = rep, expectedClose = "2026-12-15" });
        updated.GetProperty("weightedAmount").GetDecimal().ShouldBe(112_000m);
        var won = await seller.PostAsync($"/api/v1/partners/opportunities/{id}/move", new { stageId = Stage("WON"), probabilityPct = 40 }, HttpStatusCode.OK);
        won.GetProperty("status").GetString().ShouldBe("won");
        won.GetProperty("probabilityPct").GetInt32().ShouldBe(100, "a won deal is certain whatever was sent");

        var detail = await seller.GetOkAsync($"/api/v1/partners/opportunities/{id}");
        detail.GetProperty("stageHistory").EnumerateArray().Select(static h => h.GetProperty("toStageCode").GetString()).ShouldBe(["LEAD", "PROPOSAL", "LOST", "NEGOTIATION", "WON"]);
        detail.GetProperty("stageHistory").EnumerateArray().Select(static h => h.GetProperty("fromStageCode").GetString()).ShouldBe([null, "LEAD", "PROPOSAL", "LOST", "NEGOTIATION"]);

        // The board: open deals in their columns, the won one in its column, totals weighted per currency.
        await seller.PostAsync("/api/v1/partners/opportunities", new { companyId = s.CompanyId, partnerId, title = "Dinar deal", expectedAmount = 5_000_000, currency = "IQD", stageId = Stage("PROPOSAL") });
        var board = await seller.GetOkAsync($"/api/v1/partners/pipeline?companyId={s.CompanyId}&mine=true");
        var columns = board.GetProperty("columns").EnumerateArray().ToList();
        columns.Select(static c => c.GetProperty("stage").GetProperty("code").GetString()).ShouldBe(["LEAD", "QUALIFIED", "PROPOSAL", "NEGOTIATION", "WON", "LOST"]);
        columns[1].GetProperty("opportunities").Only().GetProperty("title").GetString().ShouldBe("Service contract");
        columns[4].GetProperty("opportunities").Only().GetProperty("id").GetGuid().ShouldBe(id);
        var open = board.GetProperty("openTotals").EnumerateArray().ToDictionary(static t => t.GetProperty("currency").GetString()!);
        open["USD"].GetProperty("count").GetInt32().ShouldBe(1);
        open["USD"].GetProperty("weighted").GetDecimal().ShouldBe(7_500m);
        open["IQD"].GetProperty("weighted").GetDecimal().ShouldBe(2_500_000m);

        // Stages: a system stage keeps its code and outcome, a stage holding open deals stays active, and the pipeline
        // always keeps a way to win.
        (await owner.PutErrorAsync($"/api/v1/partners/pipeline-stages/{Stage("WON")}", new { code = "CLOSED", name = Name("Closed", "مغلق"), defaultProbability = 100, outcome = "won" }, HttpStatusCode.Conflict)).Code.ShouldBe("pipeline_stage.system_locked");
        (await owner.PutErrorAsync($"/api/v1/partners/pipeline-stages/{Stage("QUALIFIED")}", new { code = "QUALIFIED", name = Name("Qualified", "مؤهل"), defaultProbability = 25, isActive = false }, HttpStatusCode.Conflict)).Code.ShouldBe("pipeline_stage.in_use");
        (await owner.PutErrorAsync($"/api/v1/partners/pipeline-stages/{Stage("WON")}", new { code = "WON", name = Name("Won", "مكسوبة"), defaultProbability = 100, outcome = "won", isActive = false }, HttpStatusCode.Conflict)).Code.ShouldBe("pipeline_stage.outcome_required");
        (await owner.PostErrorAsync("/api/v1/partners/pipeline-stages", new { code = "DEMO", name = Name("Demo", "عرض"), defaultProbability = 90, outcome = "lost" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("pipeline_stage.probability_outcome");
        var demo = await owner.PostAsync("/api/v1/partners/pipeline-stages", new { code = "DEMO", name = Name("Product demo", "عرض المنتج"), defaultProbability = 40 });
        var order = stages.EnumerateArray().Select(static st => st.GetProperty("id").GetGuid()).ToList();
        order.Insert(2, demo.GetProperty("id").GetGuid());
        (await owner.PutErrorAsync("/api/v1/partners/pipeline-stages/order", new { stageIds = order.Take(3) }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("pipeline_stage.order_incomplete");
        var reordered = await owner.PutAsync("/api/v1/partners/pipeline-stages/order", new { stageIds = order });
        reordered.EnumerateArray().Select(static st => st.GetProperty("code").GetString()).ShouldBe(["LEAD", "QUALIFIED", "DEMO", "PROPOSAL", "NEGOTIATION", "WON", "LOST"]);
        (await seller.PostErrorAsync("/api/v1/partners/pipeline-stages", new { code = "MINE", name = Name("Mine", "لي"), defaultProbability = 5 }, HttpStatusCode.Forbidden)).Code.ShouldBe("auth.forbidden");

        // Found by number or title; another company's reader sees none of it.
        (await seller.GetOkAsync($"/api/v1/partners/opportunities?q=kiosks")).Only().GetProperty("id").GetGuid().ShouldBe(id);
        var (exportReader, _) = await MemberAsync(s, "export_reader", s.OtherCompanyId, "partners.customer.read", "collaboration.comment.read");
        (await exportReader.GetOkAsync("/api/v1/partners/opportunities")).GetArrayLength().ShouldBe(0);
        (await exportReader.GetAsync(new Uri($"/api/v1/partners/opportunities/{id}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await exportReader.GetAsync(new Uri($"/api/v1/collaboration/comments?entityType=opportunity&entityId={id}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Activities_are_planned_assigned_with_a_notice_completed_and_notes_are_logged_done()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var partnerId = await PartnerAsync(owner, "ERBIL-HOTELS");
        var otherPartner = await PartnerAsync(owner, "OTHER");
        var (seller, sellerMembership) = await MemberAsync(s, "seller", null, "partners.customer.*");
        var opportunity = await owner.PostAsync("/api/v1/partners/opportunities", new { companyId = s.CompanyId, partnerId, title = "Linen supply", expectedAmount = 50_000_000 });
        var opportunityId = opportunity.GetProperty("id").GetGuid();

        (await owner.PostErrorAsync("/api/v1/partners/crm-activities", new { partnerId, kind = "fax", subject = "x" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("crm_activity.kind_invalid");
        (await owner.PostErrorAsync("/api/v1/partners/crm-activities", new { partnerId = otherPartner, kind = "call", subject = "x", opportunityId }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("crm_activity.opportunity_unknown");
        (await owner.PostErrorAsync("/api/v1/partners/crm-activities", new { partnerId, kind = "call", subject = "x", opportunityId, companyId = s.OtherCompanyId }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("crm_activity.company_mismatch");

        // The owner plans a call for the seller, due yesterday: the seller is told, and it shows overdue.
        var call = await owner.PostAsync("/api/v1/partners/crm-activities", new { partnerId, kind = "call", subject = "Confirm room count", dueAt = Api.Clock.UtcNow.AddDays(-1), assignedMembershipId = sellerMembership, opportunityId });
        var callId = call.GetProperty("id").GetGuid();
        call.GetProperty("companyId").GetGuid().ShouldBe(s.CompanyId, "an activity about an opportunity belongs to its company");
        call.GetProperty("opportunityNumber").GetString().ShouldBe(opportunity.GetProperty("number").GetString());
        call.GetProperty("isOverdue").GetBoolean().ShouldBeTrue();
        call.GetProperty("assignedName").GetString().ShouldBe("seller");
        var inbox = await seller.GetOkAsync("/api/v1/collaboration/notifications");
        inbox.GetProperty("items").EnumerateArray().ShouldContain(static n => n.GetProperty("kind").GetString() == "collaboration.assignment");

        var note = await seller.PostAsync("/api/v1/partners/crm-activities", new { partnerId, kind = "note", subject = "Prefers Egyptian cotton", dueAt = Api.Clock.UtcNow.AddDays(3) });
        note.GetProperty("status").GetString().ShouldBe("done");
        note.GetProperty("dueAt").ValueKind.ShouldBe(JsonValueKind.Null);
        (await seller.PutErrorAsync($"/api/v1/partners/crm-activities/{note.GetProperty("id").GetGuid()}", new { partnerId, kind = "note", subject = "edited" }, HttpStatusCode.Conflict)).Code.ShouldBe("crm_activity.closed");

        (await seller.GetOkAsync("/api/v1/partners/crm-activities?mine=true&due=overdue")).Only().GetProperty("id").GetGuid().ShouldBe(callId);
        var done = await seller.PostAsync($"/api/v1/partners/crm-activities/{callId}/complete", new { outcome = "140 rooms, wants samples" }, HttpStatusCode.OK);
        done.GetProperty("status").GetString().ShouldBe("done");
        done.GetProperty("isOverdue").GetBoolean().ShouldBeFalse();
        done.GetProperty("outcome").GetString().ShouldBe("140 rooms, wants samples");
        (await seller.PostErrorAsync($"/api/v1/partners/crm-activities/{callId}/complete", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("crm_activity.closed");
        var task = await seller.PostAsync("/api/v1/partners/crm-activities", new { partnerId, kind = "task", subject = "Send samples" });
        (await seller.PostAsync($"/api/v1/partners/crm-activities/{task.GetProperty("id").GetGuid()}/cancel", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("cancelled");

        var list = await seller.GetOkAsync($"/api/v1/partners/crm-activities?partnerId={partnerId}");
        list.GetArrayLength().ShouldBe(3);
        (await seller.GetOkAsync($"/api/v1/partners/opportunities/{opportunityId}")).GetProperty("activities").Only().GetProperty("id").GetGuid().ShouldBe(callId);

        // Another company's reader sees the partner's company-free notes, not the opportunity's call.
        var (exportReader, _) = await MemberAsync(s, "export_reader", s.OtherCompanyId, "partners.customer.read");
        (await exportReader.GetOkAsync($"/api/v1/partners/crm-activities?partnerId={partnerId}")).EnumerateArray().Select(static a => a.GetProperty("kind").GetString()).Order(StringComparer.Ordinal).ShouldBe(["note", "task"]);
    }
}
