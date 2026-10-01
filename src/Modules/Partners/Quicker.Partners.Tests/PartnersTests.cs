using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quicker.Identity.TestSupport;
using Quicker.Partners.Contracts;

namespace Quicker.Partners.Tests;

/// <summary>The supplier master (roadmap 4.1, DOMAIN_MODEL §7): partners with contacts, addresses, encrypted bank accounts and tax registrations; supplier accounts with groups, terms, tolerances and holds; payment schedules; withholding codes.</summary>
[Collection(ApiCollection.Name)]
public sealed class PartnersTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId);

    private async Task<Setup> SetUpAsync(string currency = "IQD")
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "PTR", legalName = Name("Partners Co", "شركة الشركاء"), country = "IQ", functionalCurrency = currency, timeZone = "Asia/Baghdad" });
        return new Setup(ws, owner, company.GetProperty("id").GetGuid());
    }

    private async Task<HttpClient> InviteAsync(HttpClient owner, Workspace ws, string roleCode, params string[] grants)
    {
        var role = await owner.PostAsync("/api/v1/roles", new { code = roleCode, name = Name(roleCode, roleCode), description = "", grants });
        var email = $"{roleCode}-{ws.Slug}@example.test";
        await owner.PostAsync("/api/v1/users/invite", new { email, displayName = roleCode, roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, Json)).ReadJsonAsync();
        return Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);
    }

    [Fact]
    public async Task A_partner_carries_contacts_addresses_encrypted_bank_accounts_and_tax_registrations()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        var created = await owner.PostAsync("/api/v1/partners", new { code = "acme-1", legalName = Name("ACME Trading LLC", "شركة أكمي للتجارة"), tradeName = Name("ACME", "أكمي"), isSupplier = true, email = "hello@acme.example", phone = "+964 770 000 0000", website = "https://acme.example" });
        var partnerId = created.GetProperty("id").GetGuid();
        created.GetProperty("code").GetString().ShouldBe("ACME-1");
        created.GetProperty("isSupplier").GetBoolean().ShouldBeTrue();
        (await owner.PostErrorAsync("/api/v1/partners", new { code = "ACME-1", legalName = Name("Dup", "مكرر") }, HttpStatusCode.Conflict)).Code.ShouldBe("partner.code_taken");
        (await owner.PostErrorAsync("/api/v1/partners", new { code = "BAD", legalName = Name("Bad mail", "بريد"), email = "not-an-email" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("partner.email_invalid");
        (await owner.PostErrorAsync("/api/v1/partners", new { code = "BAD", legalName = new { } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("partner.name_required");
        (await owner.PostErrorAsync("/api/v1/partners", new { code = "BAD", legalName = Name("x", "x"), kind = "robot" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("partner.kind_invalid");
        (await owner.PostErrorAsync("/api/v1/partners", new { code = "BAD", legalName = Name("x", "x"), parentPartnerId = Guid.CreateVersion7() }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("partner.parent_unknown");

        // A subsidiary under ACME, an intercompany partner for the company itself.
        var subsidiary = await owner.PostAsync("/api/v1/partners", new { code = "ACME-IQ", legalName = Name("ACME Iraq", "أكمي العراق"), isSupplier = true, parentPartnerId = partnerId });
        subsidiary.GetProperty("parentPartnerCode").GetString().ShouldBe("ACME-1");
        var intercompany = await owner.PostAsync("/api/v1/partners", new { code = "PTR-SELF", legalName = Name("Partners Co", "شركة الشركاء"), intercompanyCompanyId = s.CompanyId });
        intercompany.GetProperty("intercompanyCompanyId").GetGuid().ShouldBe(s.CompanyId);

        // Contacts: one primary at a time.
        var ali = await owner.PostAsync($"/api/v1/partners/{partnerId}/contacts", new { name = Name("Ali Hassan", "علي حسن"), role = "Sales", email = "ali@acme.example", isPrimary = true });
        var sara = await owner.PostAsync($"/api/v1/partners/{partnerId}/contacts", new { name = Name("Sara Karim", "سارة كريم"), role = "Accounts", email = "sara@acme.example", isPrimary = true, receivesStatements = true });
        (await owner.PostErrorAsync($"/api/v1/partners/{partnerId}/contacts", new { name = Name("Bad", "سيء"), email = "nope" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("contact.email_invalid");

        // Addresses: one default per role, structured bilingual content kept as given.
        await owner.PostAsync($"/api/v1/partners/{partnerId}/addresses", new { role = "legal", country = "iq", region = "Baghdad", isDefault = true, address = new { line1 = Name("12 Karrada St", "شارع الكرادة ١٢"), city = Name("Baghdad", "بغداد") } });
        var shipping = await owner.PostAsync($"/api/v1/partners/{partnerId}/addresses", new { role = "shipping", country = "IQ", isDefault = true, address = new { line1 = Name("Warehouse 3, Dora", "مستودع ٣، الدورة") } });
        shipping.GetProperty("country").GetString().ShouldBe("IQ");
        (await owner.PostErrorAsync($"/api/v1/partners/{partnerId}/addresses", new { role = "home", country = "IQ" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("address.role_invalid");
        (await owner.PostErrorAsync($"/api/v1/partners/{partnerId}/addresses", new { role = "legal", country = "Iraq" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("address.country_invalid");

        // Bank accounts: the IBAN is checked (mod-97), stored only encrypted, shown masked.
        (await owner.PostErrorAsync($"/api/v1/partners/{partnerId}/bank-accounts", new { bankName = "Trade Bank of Iraq", currency = "IQD", iban = "GB82 WEST 1234 5698 7654 33" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("bank_account.iban_invalid");
        (await owner.PostErrorAsync($"/api/v1/partners/{partnerId}/bank-accounts", new { bankName = "Trade Bank of Iraq", currency = "IQD" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("bank_account.identifier_required");
        (await owner.PostErrorAsync($"/api/v1/partners/{partnerId}/bank-accounts", new { bankName = "Trade Bank of Iraq", currency = "XXX", accountNumber = "0011223344" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("bank_account.currency_unknown");
        var bank = await owner.PostAsync($"/api/v1/partners/{partnerId}/bank-accounts", new { bankName = "Trade Bank of Iraq", branch = "Karrada", swiftBic = "tbiriqba", currency = "usd", iban = "GB82 WEST 1234 5698 7654 32", accountNumber = "0011-2233-44", isDefault = true });
        var bankId = bank.GetProperty("id").GetGuid();
        bank.GetProperty("ibanMasked").GetString().ShouldBe("GB****************5432");
        bank.GetProperty("accountNumberMasked").GetString().ShouldBe("00****3344");
        bank.GetProperty("currency").GetString().ShouldBe("USD");
        bank.GetProperty("swiftBic").GetString().ShouldBe("TBIRIQBA");
        bank.TryGetProperty("iban", out _).ShouldBeFalse();
        await using (var db = new NpgsqlConnection(Api.Db.OwnerConnectionString))
        {
            await db.OpenAsync(TestContext.Current.CancellationToken);
            var row = await db.QuerySingleAsync<(string IbanEnc, string NumberEnc)>("SELECT iban_enc, account_number_enc FROM app.ptr_partner_bank_accounts WHERE tenant_id = @t AND id = @id", new { t = s.Ws.TenantId, id = bankId });
            row.IbanEnc.ShouldNotContain("WEST");
            row.IbanEnc.ShouldNotContain("5432");
            row.NumberEnc.ShouldNotContain("0011");
        }

        // Only the reveal permission sees the full identifiers, and every reveal is in the audit trail.
        var reader = await InviteAsync(owner, s.Ws, "reader", "partners.supplier.read");
        (await reader.PostErrorAsync($"/api/v1/partners/{partnerId}/bank-accounts/{bankId}/reveal", new { }, HttpStatusCode.Forbidden)).Code.ShouldBe("auth.forbidden");
        var revealed = await owner.PostAsync($"/api/v1/partners/{partnerId}/bank-accounts/{bankId}/reveal", new { }, HttpStatusCode.OK);
        revealed.GetProperty("iban").GetString().ShouldBe("GB82WEST12345698765432");
        revealed.GetProperty("accountNumber").GetString().ShouldBe("0011223344");
        var trail = await owner.GetOkAsync($"/api/v1/audit/records/partner_bank_account/{bankId}");
        trail.EnumerateArray().Select(static e => e.GetProperty("action").GetString()).ShouldContain("revealed");

        // Tax registrations: unique per country, type and number; the country is an ISO code.
        await owner.PostAsync($"/api/v1/partners/{partnerId}/tax-registrations", new { country = "IQ", registrationType = "tin", number = "1234 5678" });
        (await owner.PostErrorAsync($"/api/v1/partners/{partnerId}/tax-registrations", new { country = "iq", registrationType = "tin", number = "12345678" }, HttpStatusCode.Conflict)).Code.ShouldBe("tax_registration.duplicate");
        (await owner.PostErrorAsync($"/api/v1/partners/{partnerId}/tax-registrations", new { country = "IQ", registrationType = "vat", number = "1", validFrom = "2026-01-01", validTo = "2025-01-01" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("tax_registration.number_invalid");
        await owner.PostAsync($"/api/v1/partners/{partnerId}/tax-registrations", new { country = "AE", registrationType = "vat", number = "100123456700003", validFrom = "2024-01-01" });

        // The detail composes everything; the list finds by code, name or email and filters by role.
        var detail = await owner.GetOkAsync($"/api/v1/partners/{partnerId}");
        detail.GetProperty("contacts").GetArrayLength().ShouldBe(2);
        detail.GetProperty("contacts").EnumerateArray().Single(static c => c.GetProperty("isPrimary").GetBoolean()).GetProperty("id").GetGuid().ShouldBe(sara.GetProperty("id").GetGuid());
        detail.GetProperty("addresses").GetArrayLength().ShouldBe(2);
        detail.GetProperty("bankAccounts").Only().GetProperty("isDefault").GetBoolean().ShouldBeTrue();
        detail.GetProperty("taxRegistrations").GetArrayLength().ShouldBe(2);
        (await owner.GetOkAsync("/api/v1/partners?q=acmi")).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await owner.GetOkAsync("/api/v1/partners?q=أكمي")).GetProperty("items").GetArrayLength().ShouldBe(2);
        (await owner.GetOkAsync("/api/v1/partners?q=hello@acme")).GetProperty("items").Only().GetProperty("code").GetString().ShouldBe("ACME-1");
        (await owner.GetOkAsync("/api/v1/partners?role=supplier")).GetProperty("items").GetArrayLength().ShouldBe(2);
        (await owner.GetOkAsync("/api/v1/partners/by-code/acme-1")).GetProperty("partner").GetProperty("id").GetGuid().ShouldBe(partnerId);
        await owner.DeleteOkAsync($"/api/v1/partners/{partnerId}/contacts/{ali.GetProperty("id").GetGuid()}");
        (await owner.GetOkAsync($"/api/v1/partners/{partnerId}")).GetProperty("contacts").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Supplier_accounts_carry_terms_tolerances_and_holds_and_default_from_their_group()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var partnerId = (await owner.PostAsync("/api/v1/partners", new { code = "GULF", legalName = Name("Gulf Importers", "مستوردو الخليج") })).GetProperty("id").GetGuid();

        // Every tenant starts with the usual terms as system rows.
        var paymentTerms = await owner.GetOkAsync("/api/v1/partners/payment-terms");
        var net60 = paymentTerms.ByCode("NET60");
        net60.GetProperty("isSystem").GetBoolean().ShouldBeTrue();
        net60.GetProperty("dueDays").GetInt32().ShouldBe(60);
        var deliveryTerms = await owner.GetOkAsync("/api/v1/partners/delivery-terms");
        var cif = deliveryTerms.ByCode("CIF").GetProperty("id").GetGuid();
        var fob = deliveryTerms.ByCode("FOB").GetProperty("id").GetGuid();
        (await owner.PutErrorAsync($"/api/v1/partners/delivery-terms/{cif}", new { code = "CIF2", name = Name("x", "x") }, HttpStatusCode.Conflict)).Code.ShouldBe("delivery_terms.system_code_locked");

        // A supplier posting group from the accounting module; an item posting group is refused here.
        var supplierGroupPosting = (await owner.PostAsync("/api/v1/accounting/posting-groups", new { kind = "partner_supplier", code = "SUP-LOCAL", name = Name("Local suppliers", "موردون محليون") })).GetProperty("id").GetGuid();
        var itemPosting = (await owner.PostAsync("/api/v1/accounting/posting-groups", new { kind = "item", code = "GOODS", name = Name("Goods", "بضائع") })).GetProperty("id").GetGuid();
        (await owner.PostErrorAsync("/api/v1/partners/supplier-groups", new { code = "IMPORTERS", name = Name("Importers", "مستوردون"), postingGroupId = itemPosting }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("supplier_group.posting_group_invalid");
        var group = await owner.PostAsync("/api/v1/partners/supplier-groups", new { code = "IMPORTERS", name = Name("Importers", "مستوردون"), postingGroupId = supplierGroupPosting, paymentTermsId = net60.GetProperty("id").GetGuid(), deliveryTermsId = cif });
        var groupId = group.GetProperty("id").GetGuid();

        // The account with only the group set reads the group's terms; its own delivery terms win over the group's.
        (await owner.PutErrorAsync($"/api/v1/partners/{partnerId}/supplier-accounts/{s.CompanyId}", new { supplierGroupId = groupId, priceTolerancePct = 120 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("supplier.price_tolerance_invalid");
        (await owner.PutErrorAsync($"/api/v1/partners/{partnerId}/supplier-accounts/{s.CompanyId}", new { supplierGroupId = groupId, currency = "ZZZ" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("supplier.currency_unknown");
        var account = await owner.PutAsync($"/api/v1/partners/{partnerId}/supplier-accounts/{s.CompanyId}", new { supplierGroupId = groupId, leadTimeDays = 21, priceTolerancePct = 2.5, qtyTolerancePct = 5, requiresPo = true, currency = "usd" });
        account.GetProperty("currency").GetString().ShouldBe("USD");
        account.GetProperty("effective").GetProperty("paymentTermsCode").GetString().ShouldBe("NET60");
        account.GetProperty("effective").GetProperty("deliveryTermsCode").GetString().ShouldBe("CIF");
        account.GetProperty("effective").GetProperty("postingGroupId").GetGuid().ShouldBe(supplierGroupPosting);
        (await owner.GetOkAsync($"/api/v1/partners/{partnerId}")).GetProperty("partner").GetProperty("isSupplier").GetBoolean().ShouldBeTrue("registering an account gives the partner the supplier role");
        var overridden = await owner.PutAsync($"/api/v1/partners/{partnerId}/supplier-accounts/{s.CompanyId}", new { supplierGroupId = groupId, deliveryTermsId = fob, leadTimeDays = 21, priceTolerancePct = 2.5, qtyTolerancePct = 5, requiresPo = true, currency = "USD" });
        overridden.GetProperty("effective").GetProperty("deliveryTermsCode").GetString().ShouldBe("FOB");
        (await owner.GetOkAsync("/api/v1/partners/supplier-groups")).ByCode("IMPORTERS").GetProperty("suppliers").GetInt32().ShouldBe(1);

        // What purchasing reads: the effective terms, and the holds that refuse a purpose.
        var terms = await host.InTenantAsync(s.Ws.TenantId, (sp, ct) => sp.GetRequiredService<IPartnerDirectory>().EnsureSupplierAsync(s.CompanyId, partnerId, SupplierPurposes.Purchase, ct));
        terms.IsSuccess.ShouldBeTrue(terms.Error?.Code);
        terms.Value.PaymentTermsCode.ShouldBe("NET60");
        terms.Value.DeliveryTermsCode.ShouldBe("FOB");
        terms.Value.LeadTimeDays.ShouldBe(21);
        terms.Value.PriceTolerancePct.ShouldBe(2.5m);
        terms.Value.RequiresPo.ShouldBeTrue();
        var unknown = await host.InTenantAsync(s.Ws.TenantId, (sp, ct) => sp.GetRequiredService<IPartnerDirectory>().EnsureSupplierAsync(s.CompanyId, Guid.CreateVersion7(), SupplierPurposes.Purchase, ct));
        unknown.Error!.Code.ShouldBe("supplier.not_registered");

        (await owner.PostErrorAsync($"/api/v1/partners/{partnerId}/supplier-accounts/{s.CompanyId}/hold", new { status = "purchase", reason = "" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("supplier.hold_reason_required");
        var held = await owner.PostAsync($"/api/v1/partners/{partnerId}/supplier-accounts/{s.CompanyId}/hold", new { status = "purchase", reason = "Quality claim open" }, HttpStatusCode.OK);
        held.GetProperty("holdStatus").GetString().ShouldBe("purchase");
        held.GetProperty("heldAt").ValueKind.ShouldBe(JsonValueKind.String);
        var purchase = await host.InTenantAsync(s.Ws.TenantId, (sp, ct) => sp.GetRequiredService<IPartnerDirectory>().EnsureSupplierAsync(s.CompanyId, partnerId, SupplierPurposes.Purchase, ct));
        purchase.Error!.Code.ShouldBe("supplier.on_hold");
        purchase.Error.Why!["reason"].ShouldBe("Quality claim open");
        var payment = await host.InTenantAsync(s.Ws.TenantId, (sp, ct) => sp.GetRequiredService<IPartnerDirectory>().EnsureSupplierAsync(s.CompanyId, partnerId, SupplierPurposes.Payment, ct));
        payment.IsSuccess.ShouldBeTrue("a purchase hold does not stop paying what is owed");
        (await owner.GetOkAsync($"/api/v1/partners/suppliers?companyId={s.CompanyId}&holdStatus=held")).Only().GetProperty("partnerCode").GetString().ShouldBe("GULF");
        var released = await owner.PostAsync($"/api/v1/partners/{partnerId}/supplier-accounts/{s.CompanyId}/release", new { }, HttpStatusCode.OK);
        released.GetProperty("holdStatus").GetString().ShouldBe("none");
        released.GetProperty("holdReason").ValueKind.ShouldBe(JsonValueKind.Null);
        (await owner.GetOkAsync($"/api/v1/partners/suppliers?companyId={s.CompanyId}&holdStatus=held")).GetArrayLength().ShouldBe(0);

        // The supplier role cannot be removed while an account is active; an inactive account refuses documents.
        (await owner.PutErrorAsync($"/api/v1/partners/{partnerId}", new { code = "GULF", legalName = Name("Gulf Importers", "مستوردو الخليج"), isSupplier = false }, HttpStatusCode.Conflict)).Code.ShouldBe("partner.supplier_accounts_active");
        await owner.PutAsync($"/api/v1/partners/{partnerId}/supplier-accounts/{s.CompanyId}", new { supplierGroupId = groupId, currency = "USD", isActive = false });
        var inactive = await host.InTenantAsync(s.Ws.TenantId, (sp, ct) => sp.GetRequiredService<IPartnerDirectory>().EnsureSupplierAsync(s.CompanyId, partnerId, SupplierPurposes.Payment, ct));
        inactive.Error!.Code.ShouldBe("supplier.inactive");
    }

    [Fact]
    public async Task Payment_terms_schedule_instalments_to_the_minor_unit_on_working_days_and_withholding_codes_are_validated()
    {
        var s = await SetUpAsync("USD");
        var owner = s.Owner;

        (await owner.PostErrorAsync("/api/v1/partners/payment-terms", new { code = "BAD", name = Name("Bad", "سيء"), lines = new[] { new { sequence = 1, percentage = 40, days = 0 }, new { sequence = 2, percentage = 50, days = 30 } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("payment_terms.lines_sum");
        (await owner.PostErrorAsync("/api/v1/partners/payment-terms", new { code = "BAD", name = Name("Bad", "سيء"), dueBasis = "whenever" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("payment_terms.due_basis_invalid");
        var split = await owner.PostAsync("/api/v1/partners/payment-terms", new { code = "50-50", name = Name("Half now, half in 30 days", "النصف الآن والنصف بعد ٣٠ يومًا"), lines = new[] { new { sequence = 1, percentage = 50, days = 0 }, new { sequence = 2, percentage = 50, days = 30 } }, earlyDiscountPct = 2, earlyDiscountDays = 10 });
        var splitId = split.GetProperty("id").GetGuid();

        // 100.01 USD splits 50.01 + 50.00: the parts sum to the whole in the minor unit.
        var schedule = await owner.PostAsync($"/api/v1/partners/payment-terms/{splitId}/schedule", new { companyId = s.CompanyId, invoiceDate = "2026-09-10", amount = 100.01, currency = "USD" }, HttpStatusCode.OK);
        var instalments = schedule.GetProperty("instalments").EnumerateArray().ToList();
        instalments.Count.ShouldBe(2);
        instalments[0].GetProperty("dueOn").GetString().ShouldBe("2026-09-10");
        instalments[1].GetProperty("dueOn").GetString().ShouldBe("2026-10-10");
        instalments.Sum(static i => i.GetProperty("amount").GetDecimal()).ShouldBe(100.01m);
        instalments.Select(static i => i.GetProperty("amount").GetDecimal()).ShouldBe([50.01m, 50.00m]);
        schedule.GetProperty("earlyDiscountUntil").GetString().ShouldBe("2026-09-20");
        schedule.GetProperty("dueOn").GetString().ShouldBe("2026-10-10");

        // End of month: 30 days after the invoice month's last day.
        var eom = (await owner.GetOkAsync("/api/v1/partners/payment-terms")).ByCode("EOM30").GetProperty("id").GetGuid();
        (await owner.PostAsync($"/api/v1/partners/payment-terms/{eom}/schedule", new { companyId = s.CompanyId, invoiceDate = "2026-09-10", amount = 10, currency = "USD" }, HttpStatusCode.OK)).GetProperty("dueOn").GetString().ShouldBe("2026-10-30");

        // Business days only: Sunday 20 September + 5 days lands on Friday 25 September, the Iraqi weekend, so the due date moves to Sunday 27.
        var workingDays = await owner.PostAsync("/api/v1/partners/payment-terms", new { code = "NET5-BD", name = Name("Net 5 working", "صافي ٥ أيام عمل"), dueDays = 5, businessDaysOnly = true });
        (await owner.PostAsync($"/api/v1/partners/payment-terms/{workingDays.GetProperty("id").GetGuid()}/schedule", new { companyId = s.CompanyId, invoiceDate = "2026-09-20", amount = 10, currency = "USD" }, HttpStatusCode.OK)).GetProperty("dueOn").GetString().ShouldBe("2026-09-27");

        // Delivery basis takes the delivery date when given.
        var delivery = await owner.PostAsync("/api/v1/partners/payment-terms", new { code = "DEL15", name = Name("15 days after delivery", "١٥ يومًا بعد التسليم"), dueBasis = "delivery", dueDays = 15 });
        (await owner.PostAsync($"/api/v1/partners/payment-terms/{delivery.GetProperty("id").GetGuid()}/schedule", new { companyId = s.CompanyId, invoiceDate = "2026-09-10", deliveryDate = "2026-09-14", amount = 10, currency = "USD" }, HttpStatusCode.OK)).GetProperty("dueOn").GetString().ShouldBe("2026-09-29");

        // Withholding codes.
        (await owner.PostErrorAsync("/api/v1/partners/wht-codes", new { code = "WHT-X", name = Name("x", "x"), ratePct = 120 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("wht_code.rate_invalid");
        (await owner.PostErrorAsync("/api/v1/partners/wht-codes", new { code = "WHT-X", name = Name("x", "x"), ratePct = 3, thresholdAmount = 1000 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("wht_code.threshold_currency_unknown");
        var wht = await owner.PostAsync("/api/v1/partners/wht-codes", new { code = "contract", name = Name("Contractor withholding 3.3%", "استقطاع المقاولين ٣٫٣٪"), ratePct = 3.3, withholdAt = "payment", thresholdAmount = 1000000, thresholdCurrency = "IQD" });
        wht.GetProperty("code").GetString().ShouldBe("CONTRACT");
        var info = await host.InTenantAsync(s.Ws.TenantId, (sp, ct) => sp.GetRequiredService<IPartnerDirectory>().FindWhtCodeAsync(wht.GetProperty("id").GetGuid(), ct));
        info.ShouldNotBeNull();
        info.RatePct.ShouldBe(3.3m);
        info.WithholdAt.ShouldBe("payment");
        info.ThresholdAmount.ShouldBe(1000000m);

        // A reader sees terms but cannot change them.
        var reader = await InviteAsync(owner, s.Ws, "reader", "partners.supplier.read");
        (await reader.GetOkAsync("/api/v1/partners/wht-codes")).GetArrayLength().ShouldBe(1);
        (await reader.PostErrorAsync("/api/v1/partners/wht-codes", new { code = "NOPE", name = Name("x", "x"), ratePct = 1 }, HttpStatusCode.Forbidden)).Code.ShouldBe("auth.forbidden");
    }
}
