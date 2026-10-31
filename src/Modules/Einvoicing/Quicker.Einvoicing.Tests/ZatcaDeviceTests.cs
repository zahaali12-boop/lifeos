using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.TestSupport;

namespace Quicker.Einvoicing.Tests;

/// <summary>
/// ZATCA phase 2 end to end against the simulator (A-179): a Saudi company registered for VAT onboards a device (key
/// and request, the compliance certificate with an OTP, the compliance checks with one sample of each kind, the
/// production certificate); a standard invoice to a VAT-registered customer is cleared and comes back with the
/// scheme's QR, a simplified invoice to a cash customer is signed here and reported with its own QR, a credit note
/// references its invoice, the counter and the previous hash chain from one submission to the next, and the
/// invoice records what the scheme answered. A device with no certificate, and a company with no device, refuse.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ZatcaDeviceTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Cleaning, Guid Business, Guid Cash, Guid StandardCode);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = (await owner.PostAsync("/api/v1/organization/companies", new { code = "RYD", legalName = Name("Riyadh Services", "الرياض للخدمات"), country = "SA", functionalCurrency = "SAR", timeZone = "Asia/Riyadh", costingMethod = "average", registrationNumbers = new { crn = "1010010000" }, address = new { street = "King Fahd Road", buildingNumber = "1234", district = "Al Olaya", city = "Riyadh", postalCode = "12211" } })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId = company });
        var regime = await owner.PostAsync("/api/v1/tax/templates/SA-VAT/install", new { });
        var regimeId = regime.GetProperty("regime").GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/tax/registrations", new { companyId = company, regimeId, registrationNumber = "310123456700003", registeredFrom = "2018-01-01" });
        var groups = await owner.GetOkAsync("/api/v1/tax/groups");
        Guid Group(string kind, string code) => groups.EnumerateArray().Single(g => g.GetProperty("kind").GetString() == kind && g.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();
        var cleaning = (await owner.PostAsync("/api/v1/items", new { code = "CLEAN", name = Name("Office cleaning", "تنظيف المكتب"), baseUom = "HR", type = "service", listPrice = 50m, listPriceCurrency = "SAR", itemTaxGroupId = Group("item", "SERVICES") })).GetProperty("id").GetGuid();
        var business = (await owner.PostAsync("/api/v1/partners", new { code = "DAMMAM-CO", legalName = Name("Dammam Co", "شركة الدمام"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{business}/customer-accounts/{company}", new { currency = "SAR", taxGroupId = Group("partner", "DOMESTIC") });
        await owner.PostAsync($"/api/v1/partners/{business}/tax-registrations", new { country = "SA", registrationType = "vat", number = "399999999900003" });
        await owner.PostAsync($"/api/v1/partners/{business}/addresses", new { role = "billing", country = "SA", isDefault = true, address = new { line1 = Name("Prince Sultan Street", "شارع الأمير سلطان"), city = Name("Dammam", "الدمام"), buildingNumber = "4321", district = "Al Faisaliyah", postalCode = "32241" } });
        var cash = (await owner.PostAsync("/api/v1/partners", new { code = "WALKIN", legalName = Name("Walk-in customer", "عميل نقدي"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{cash}/customer-accounts/{company}", new { currency = "SAR", taxGroupId = Group("partner", "DOMESTIC_UNREG") });
        var standardCode = regime.GetProperty("codes").EnumerateArray().Single(static c => c.GetProperty("code").GetString() == "SA-S").GetProperty("id").GetGuid();
        return new Setup(ws, owner, company, cleaning, business, cash, standardCode);
    }

    private static async Task<JsonElement> PostedInvoiceAsync(Setup s, Guid customer, string documentDate, string kind = "invoice", Guid? creditsInvoiceId = null, decimal amount = 500m)
    {
        var draft = await s.Owner.PostAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = customer, documentDate, kind, creditsInvoiceId, notes = kind == "credit_note" ? "Service not rendered" : null, lines = new[] { new { kind = "service", quantity = 1m, unitPrice = amount, description = "Office cleaning", taxCodeId = s.StandardCode } } });
        return await s.Owner.PostAsync($"/api/v1/sales/invoices/{draft.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_device_is_onboarded_through_compliance_to_production_and_invoices_are_cleared_reported_and_chained()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        // No device yet: a posted invoice waits, and submitting it by hand says why.
        var early = await PostedInvoiceAsync(s, s.Business, "2026-10-01");
        early.GetProperty("einvoiceStatus").GetString().ShouldBe("pending");
        (await owner.PostErrorAsync($"/api/v1/sales/invoices/{early.GetProperty("id").GetGuid()}/einvoice/submit", new { }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("einvoice.no_device");

        // The device: a key and a certificate request named after the company's VAT registration; the seller's address comes from the company.
        (await owner.PostErrorAsync("/api/v1/einvoicing/devices", new { companyId = s.CompanyId, name = "Riyadh POS 1", environment = "mars" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("einvoice.environment_invalid");
        var device = await owner.PostAsync("/api/v1/einvoicing/devices", new { companyId = s.CompanyId, name = "Riyadh POS 1", environment = "simulator", industry = "Cleaning services", invoiceTypes = "1100" });
        var deviceId = device.GetProperty("id").GetGuid();
        (device.GetProperty("status").GetString(), device.GetProperty("vatNumber").GetString(), device.GetProperty("invoiceCounter").GetInt64()).ShouldBe(("csr", "310123456700003", 0L));
        device.GetProperty("csr").GetString()!.ShouldStartWith("-----BEGIN CERTIFICATE REQUEST-----");
        device.GetProperty("serialNumber").GetString()!.ShouldStartWith("1-Quicker|2-ERP|3-");
        device.GetProperty("seller").GetProperty("registrationNumber").GetString().ShouldBe("1010010000");
        device.GetProperty("seller").GetProperty("city").GetString().ShouldBe("Riyadh");
        (await owner.PostErrorAsync($"/api/v1/sales/invoices/{early.GetProperty("id").GetGuid()}/einvoice/submit", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("einvoice.device_not_active");

        // The compliance certificate with the OTP; a wrong OTP is refused by the scheme.
        (await owner.PostErrorAsync($"/api/v1/einvoicing/devices/{deviceId}/compliance-csid", new { otp = "12" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("einvoice.csid_refused");
        var compliance = await owner.PostAsync($"/api/v1/einvoicing/devices/{deviceId}/compliance-csid", new { otp = "123456" }, HttpStatusCode.OK);
        compliance.GetProperty("status").GetString().ShouldBe("compliance");
        compliance.GetProperty("certificateSubject").GetString()!.ShouldContain("CN=RYD-Riyadh-POS-1");
        compliance.GetProperty("certificateIssuer").GetString()!.ShouldContain("QUICKER-SIMULATOR-CA");
        (await owner.PostErrorAsync($"/api/v1/einvoicing/devices/{deviceId}/production-csid", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("einvoice.device_state");

        // The compliance checks: six samples (standard and simplified invoice, credit note and debit note), each accepted; the counter moved on for each.
        var checked_ = await owner.PostAsync($"/api/v1/einvoicing/devices/{deviceId}/compliance-checks", new { }, HttpStatusCode.OK);
        checked_.GetProperty("status").GetString().ShouldBe("checked");
        var checks = checked_.GetProperty("complianceChecks").EnumerateArray().ToList();
        checks.Count.ShouldBe(6);
        checks.Select(static c => (c.GetProperty("kind").GetString(), c.GetProperty("typeCode").GetString(), c.GetProperty("status").GetString())).ShouldBe([("standard", "388", "cleared"), ("standard", "381", "cleared"), ("standard", "383", "cleared"), ("simplified", "388", "reported"), ("simplified", "381", "reported"), ("simplified", "383", "reported")]);
        checks.ShouldAllBe(static c => c.GetProperty("errors").GetArrayLength() == 0);
        checked_.GetProperty("invoiceCounter").GetInt64().ShouldBe(6L);

        // The production certificate: the device is live.
        var live = await owner.PostAsync($"/api/v1/einvoicing/devices/{deviceId}/production-csid", new { }, HttpStatusCode.OK);
        live.GetProperty("status").GetString().ShouldBe("active");
        live.GetProperty("certificateExpiresAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddYears(2));

        // The standard invoice from before is cleared: the scheme signs it and returns its QR; the invoice records the outcome.
        var cleared = await owner.PostAsync($"/api/v1/sales/invoices/{early.GetProperty("id").GetGuid()}/einvoice/submit", new { }, HttpStatusCode.OK);
        cleared.GetProperty("einvoiceStatus").GetString().ShouldBe("cleared");
        cleared.GetProperty("einvoiceQr").GetString().ShouldNotBeNullOrEmpty();
        var submissionId = cleared.GetProperty("einvoiceSubmissionId").GetGuid();
        var submission = await owner.GetOkAsync($"/api/v1/einvoicing/submissions/{submissionId}");
        (submission.GetProperty("kind").GetString(), submission.GetProperty("typeCode").GetString(), submission.GetProperty("status").GetString(), submission.GetProperty("invoiceCounter").GetInt64(), submission.GetProperty("hasClearedXml").GetBoolean()).ShouldBe(("standard", "388", "cleared", 7L, true));
        submission.GetProperty("documentNumber").GetString().ShouldBe(early.GetProperty("number").GetString());
        using (var xml = await owner.GetAsync($"/api/v1/einvoicing/submissions/{submissionId}/xml?cleared=true", TestContext.Current.CancellationToken))
        {
            xml.StatusCode.ShouldBe(HttpStatusCode.OK);
            var text = await xml.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            text.ShouldContain("<cbc:ID>QR</cbc:ID>");
            text.ShouldContain("<cbc:CompanyID>399999999900003</cbc:CompanyID>");
            text.ShouldContain("<cbc:StreetName>Prince Sultan Street</cbc:StreetName>");
        }

        using (var svg = await owner.GetAsync($"/api/v1/einvoicing/submissions/{submissionId}/qr.svg", TestContext.Current.CancellationToken))
        {
            svg.StatusCode.ShouldBe(HttpStatusCode.OK);
            svg.Content.Headers.ContentType!.MediaType.ShouldBe("image/svg+xml");
        }

        // A simplified invoice to a cash customer: signed here, reported, with the QR built here; it follows in the chain.
        var simplified = await PostedInvoiceAsync(s, s.Cash, "2026-10-02", amount: 200m);
        var reported = await owner.PostAsync($"/api/v1/sales/invoices/{simplified.GetProperty("id").GetGuid()}/einvoice/submit", new { }, HttpStatusCode.OK);
        reported.GetProperty("einvoiceStatus").GetString().ShouldBe("reported");
        var reportedSubmission = await owner.GetOkAsync($"/api/v1/einvoicing/submissions/{reported.GetProperty("einvoiceSubmissionId").GetGuid()}");
        (reportedSubmission.GetProperty("kind").GetString(), reportedSubmission.GetProperty("invoiceCounter").GetInt64()).ShouldBe(("simplified", 8L));
        reportedSubmission.GetProperty("previousHash").GetString().ShouldBe(submission.GetProperty("invoiceHash").GetString(), "the chain links each submission to the one before");
        var qr = Zatca.ZatcaCrypto.ReadQr(reported.GetProperty("einvoiceQr").GetString()!);
        (qr[1], qr[2], qr[4], qr[5]).ShouldBe(("Riyadh Services", "310123456700003", "230.00", "30.00"));
        qr[6].ShouldBe(reportedSubmission.GetProperty("invoiceHash").GetString());

        // A credit note on the standard invoice: 381 with the billing reference, cleared.
        var credit = await PostedInvoiceAsync(s, s.Business, "2026-10-03", kind: "credit_note", creditsInvoiceId: early.GetProperty("id").GetGuid(), amount: 100m);
        var creditCleared = await owner.PostAsync($"/api/v1/sales/invoices/{credit.GetProperty("id").GetGuid()}/einvoice/submit", new { }, HttpStatusCode.OK);
        creditCleared.GetProperty("einvoiceStatus").GetString().ShouldBe("cleared");
        var creditSubmission = await owner.GetOkAsync($"/api/v1/einvoicing/submissions/{creditCleared.GetProperty("einvoiceSubmissionId").GetGuid()}");
        (creditSubmission.GetProperty("typeCode").GetString(), creditSubmission.GetProperty("invoiceCounter").GetInt64()).ShouldBe(("381", 9L));
        using (var xml = await owner.GetAsync($"/api/v1/einvoicing/submissions/{creditSubmission.GetProperty("id").GetGuid()}/xml", TestContext.Current.CancellationToken))
        {
            var text = await xml.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            text.ShouldContain("<cac:BillingReference>");
            text.ShouldContain("<cbc:InstructionNote>Service not rendered</cbc:InstructionNote>");
        }

        // The preview of a document and the list of submissions.
        var preview = await owner.GetOkAsync($"/api/v1/einvoicing/documents/sales_invoice/{simplified.GetProperty("id").GetGuid()}/preview");
        (preview.GetProperty("kind").GetString(), preview.GetProperty("typeCode").GetString()).ShouldBe(("simplified", "388"));
        preview.GetProperty("xml").GetString()!.ShouldContain("<cbc:UUID>10</cbc:UUID>");
        var all = await owner.GetOkAsync($"/api/v1/einvoicing/submissions?companyId={s.CompanyId}");
        all.GetArrayLength().ShouldBe(9);
        (await owner.GetOkAsync($"/api/v1/einvoicing/submissions?companyId={s.CompanyId}&documentType=sales_invoice")).GetArrayLength().ShouldBe(3);
        var refreshed = await owner.GetOkAsync($"/api/v1/einvoicing/devices/{deviceId}");
        (refreshed.GetProperty("invoiceCounter").GetInt64(), refreshed.GetProperty("lastInvoiceHash").GetString()).ShouldBe((9L, creditSubmission.GetProperty("invoiceHash").GetString()));

        // Renewed with a fresh OTP, then revoked: a revoked device no longer submits.
        var renewed = await owner.PostAsync($"/api/v1/einvoicing/devices/{deviceId}/renew", new { otp = "654321" }, HttpStatusCode.OK);
        renewed.GetProperty("status").GetString().ShouldBe("active");
        (await owner.PostAsync($"/api/v1/einvoicing/devices/{deviceId}/revoke", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("revoked");
        var late = await PostedInvoiceAsync(s, s.Cash, "2026-10-04", amount: 50m);
        (await owner.PostErrorAsync($"/api/v1/sales/invoices/{late.GetProperty("id").GetGuid()}/einvoice/submit", new { }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("einvoice.no_device");
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_device_for_standard_invoices_only_refuses_a_cash_sale_and_a_manual_device_leaves_automatic_submissions_to_a_person()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var device = await owner.PostAsync("/api/v1/einvoicing/devices", new { companyId = s.CompanyId, name = "Head office", environment = "simulator", invoiceTypes = "1000", autoSubmit = false, seller = new { registrationNumber = "1010099999", street = "Olaya Street", buildingNumber = "7777", district = "Al Olaya", city = "Riyadh", postalCode = "12333" } });
        var deviceId = device.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/einvoicing/devices/{deviceId}/compliance-csid", new { otp = "111111" }, HttpStatusCode.OK);
        var checked_ = await owner.PostAsync($"/api/v1/einvoicing/devices/{deviceId}/compliance-checks", new { }, HttpStatusCode.OK);
        checked_.GetProperty("complianceChecks").GetArrayLength().ShouldBe(3, "standard invoices only");
        await owner.PostAsync($"/api/v1/einvoicing/devices/{deviceId}/production-csid", new { }, HttpStatusCode.OK);

        var cash = await PostedInvoiceAsync(s, s.Cash, "2026-10-05", amount: 80m);
        (await owner.PostErrorAsync($"/api/v1/sales/invoices/{cash.GetProperty("id").GetGuid()}/einvoice/submit", new { }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("einvoice.simplified_not_enabled");

        // The automatic submission a posting enqueues is refused by a manual device; the person submits.
        var invoiceId = (await PostedInvoiceAsync(s, s.Business, "2026-10-06")).GetProperty("id").GetGuid();
        var automatic = await host.InTenantAsync(s.Ws.TenantId, async (sp, ct) =>
        {
            var clearance = sp.GetRequiredService<Quicker.Tax.Contracts.ITaxClearance>();
            var result = await clearance.SubmitAsync("sales_invoice", invoiceId, automatic: true, ct);
            return result.IsFailure ? result.Error!.Code : "submitted";
        });
        automatic.ShouldBe("einvoice.manual_only");
        (await owner.GetOkAsync($"/api/v1/sales/invoices/{invoiceId}")).GetProperty("einvoiceStatus").GetString().ShouldBe("pending");
        var byHand = await owner.PostAsync($"/api/v1/sales/invoices/{invoiceId}/einvoice/submit", new { }, HttpStatusCode.OK);
        byHand.GetProperty("einvoiceStatus").GetString().ShouldBe("cleared");
        using (var xml = await owner.GetAsync($"/api/v1/einvoicing/submissions/{byHand.GetProperty("einvoiceSubmissionId").GetGuid()}/xml", TestContext.Current.CancellationToken))
        {
            var text = await xml.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            text.ShouldContain("<cbc:ID schemeID=\"CRN\">1010099999</cbc:ID>");
            text.ShouldContain("<cbc:StreetName>Olaya Street</cbc:StreetName>");
        }

        // A device that is updated keeps its chain; its seller details change for the next invoice.
        var updated = await owner.PutAsync($"/api/v1/einvoicing/devices/{deviceId}", new { companyId = s.CompanyId, name = "Head office (renamed)", environment = "simulator", invoiceTypes = "1000", autoSubmit = true, seller = new { registrationNumber = "1010099999", street = "Tahlia Street", buildingNumber = "7777", district = "Al Olaya", city = "Riyadh", postalCode = "12333" } });
        (updated.GetProperty("name").GetString(), updated.GetProperty("autoSubmit").GetBoolean(), updated.GetProperty("invoiceCounter").GetInt64()).ShouldBe(("Head office (renamed)", true, 4L));
        await owner.AssertInvariantsAsync();
    }
}
