using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;
using UglyToad.PdfPig;

namespace Quicker.Printing.Tests;

/// <summary>
/// The print pipeline (roadmap 5.9, ADR-0022): a draft invoice refuses to print as a tax invoice and prints pro forma;
/// the posted invoice previews as bilingual HTML and renders to a PDF through headless Chromium in the worker, cached
/// by its fingerprint (the same document and template give the same render), kept as evidence under retention, and
/// re-rendered when the template changes; a template edit is a new version and a rollback restores the old one; the
/// PDF goes by email with the send logged on the invoice; a reader without the keys is refused.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PrintingTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Customer, Guid Tea, Guid StandardCode, Guid Warehouse);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = (await owner.PostAsync("/api/v1/organization/companies", new { code = "PRT", legalName = Name("Riyadh Retail", "الرياض للتجزئة"), country = "SA", functionalCurrency = "SAR", timeZone = "Asia/Riyadh", registrationNumbers = new { vat = "310123456700003" }, address = new { street = "King Fahd Rd 12", city = "Riyadh" } })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId = company });
        var regime = await owner.PostAsync("/api/v1/tax/templates/SA-VAT/install", new { });
        await owner.PostAsync("/api/v1/tax/registrations", new { companyId = company, regimeId = regime.GetProperty("regime").GetProperty("id").GetGuid(), registrationNumber = "310123456700003", registeredFrom = "2018-01-01" });
        var groups = await owner.GetOkAsync("/api/v1/tax/groups");
        Guid Group(string kind, string code) => groups.EnumerateArray().Single(g => g.GetProperty("kind").GetString() == kind && g.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Black tea", "شاي أسود"), baseUom = "PCS", type = "service", listPrice = 100m, listPriceCurrency = "SAR", itemTaxGroupId = Group("item", "STANDARD") })).GetProperty("id").GetGuid();
        var customer = (await owner.PostAsync("/api/v1/partners", new { code = "JEDDAH-MALL", legalName = Name("Jeddah Mall LLC", "شركة جدة مول"), isCustomer = true, email = "ap@jeddahmall.example" })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{customer}/customer-accounts/{company}", new { currency = "SAR", taxGroupId = Group("partner", "DOMESTIC") });
        var standardCode = regime.GetProperty("codes").EnumerateArray().Single(static c => c.GetProperty("code").GetString() == "SA-S").GetProperty("id").GetGuid();
        var warehouse = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId = company, code = "RUH", name = Name("Riyadh warehouse", "مستودع الرياض") })).GetProperty("id").GetGuid();
        return new Setup(ws, owner, company, customer, tea, standardCode, warehouse);
    }

    private static async Task<(Guid Id, string Number)> DraftInvoiceAsync(Setup s)
    {
        var draft = await s.Owner.PostAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, customerReference = "JM-PO-77", lines = new object[] { new { kind = "item", itemId = s.Tea, quantity = 12m }, new { kind = "service", quantity = 1m, unitPrice = 250m, description = "Delivery to the Corniche store", accountRole = "Revenue", taxCodeId = s.StandardCode } } });
        return (draft.GetProperty("id").GetGuid(), draft.GetProperty("number").GetString()!);
    }

    private static async Task<JsonElement> RenderedAsync(ApiHostFixture host, HttpClient owner, Guid renderId)
    {
        await host.RunWorkerAsync();
        var render = await owner.GetOkAsync($"/api/v1/print/renders/{renderId}");
        render.GetProperty("status").GetString().ShouldBe("ready", render.ToString());
        return render;
    }

    [Fact]
    public async Task A_draft_prints_pro_forma_only_and_the_posted_invoice_previews_bilingually_renders_to_pdf_once_per_fingerprint_with_evidence_and_rerenders_when_the_template_changes()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var (invoiceId, number) = await DraftInvoiceAsync(s);

        // A draft is not a tax invoice; it prints pro forma with the watermark, and nothing is stored.
        (await owner.GetErrorAsync($"/api/v1/print/preview?documentType=sales_invoice&documentId={invoiceId}", HttpStatusCode.Conflict)).Code.ShouldBe("print.draft_not_tax_invoice");
        var proforma = await owner.GetTextAsync($"/api/v1/print/preview?documentType=sales_invoice&documentId={invoiceId}&variant=proforma");
        proforma.ShouldContain("PRO FORMA");
        proforma.ShouldContain("فاتورة أولية");
        proforma.ShouldContain(number);
        (await owner.GetOkAsync($"/api/v1/print/renders?documentType=sales_invoice&documentId={invoiceId}")).GetArrayLength().ShouldBe(0);

        // Posted: the tax invoice preview carries both languages, the seller's VAT number, the customer, the lines, the tax summary and the amount in words.
        await owner.PostAsync($"/api/v1/sales/invoices/{invoiceId}/post", new { }, HttpStatusCode.OK);
        var html = await owner.GetTextAsync($"/api/v1/print/preview?documentType=sales_invoice&documentId={invoiceId}");
        html.ShouldContain("Tax invoice / فاتورة ضريبية");
        html.ShouldContain("310123456700003");
        html.ShouldContain("Jeddah Mall LLC / شركة جدة مول");
        html.ShouldContain("Black tea / شاي أسود");
        html.ShouldContain("Delivery to the Corniche store");
        html.ShouldContain("1,450.00 SAR"); // 12 × 100 + 250 net
        html.ShouldContain("217.50 SAR"); // 15% VAT
        html.ShouldContain("1,667.50 SAR");
        html.ShouldNotContain("PRO FORMA");
        html.ShouldContain("JM-PO-77");
        html.ShouldContain("ريال");

        // The render: queued, then made by the worker, stored, hashed, and kept as evidence since the invoice is posted.
        var requested = await owner.PostAsync("/api/v1/print/renders", new { documentType = "sales_invoice", documentId = invoiceId });
        (requested.GetProperty("status").GetString(), requested.GetProperty("reused").GetBoolean(), requested.GetProperty("isFinal").GetBoolean()).ShouldBe(("queued", false, true));
        var renderId = requested.GetProperty("id").GetGuid();
        (await owner.GetErrorAsync($"/api/v1/print/renders/{renderId}/file", HttpStatusCode.Conflict)).Code.ShouldBe("print.render_not_ready");
        var render = await RenderedAsync(host, owner, renderId);
        render.GetProperty("isEvidence").GetBoolean().ShouldBeTrue();
        render.GetProperty("sizeBytes").GetInt64().ShouldBeGreaterThan(5_000);
        render.GetProperty("sha256").GetString()!.Length.ShouldBe(64);
        var file = await owner.GetAsync(new Uri($"/api/v1/print/renders/{renderId}/file", UriKind.Relative));
        file.StatusCode.ShouldBe(HttpStatusCode.OK);
        file.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        file.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe($"{number}.pdf");
        var bytes = await file.Content.ReadAsByteArrayAsync();
        bytes.Take(5).ToArray().ShouldBe("%PDF-"u8.ToArray());
        using (var pdf = PdfDocument.Open(bytes))
        {
            pdf.NumberOfPages.ShouldBe(1);
            var text = string.Join(" ", pdf.GetPages().Select(static p => p.Text));
            text.ShouldContain(number);
            text.ShouldContain("Jeddah Mall LLC");
            text.ShouldContain("310123456700003");
            text.Any(static c => c >= '؀' && c <= 'ۿ').ShouldBeTrue("the Arabic half of the bilingual invoice is in the PDF");
        }

        // The same document and template again: the same render, no second file.
        var again = await owner.PostAsync("/api/v1/print/renders", new { documentType = "sales_invoice", documentId = invoiceId });
        (again.GetProperty("id").GetGuid(), again.GetProperty("reused").GetBoolean()).ShouldBe((renderId, true));
        (await owner.GetOkAsync($"/api/v1/print/renders?documentType=sales_invoice&documentId={invoiceId}")).GetArrayLength().ShouldBe(1);

        // The render is on the invoice's timeline.
        var activity = (await owner.GetOkAsync($"/api/v1/collaboration/activities?entityType=sales_invoice&entityId={invoiceId}")).GetProperty("items");
        activity.EnumerateArray().Any(static a => a.GetProperty("kind").GetString() == "print.rendered").ShouldBeTrue(activity.ToString());

        // The template edited (a new version): the invoice renders again under the new version, and the first render stays as evidence.
        var templates = await owner.GetOkAsync("/api/v1/print/templates?documentType=sales_invoice");
        var standard = templates.EnumerateArray().Single(static t => t.GetProperty("variant").GetString() == "standard");
        var templateId = standard.GetProperty("id").GetGuid();
        (standard.GetProperty("isSystem").GetBoolean(), standard.GetProperty("currentVersion").GetInt32(), standard.GetProperty("languageMode").GetString()).ShouldBe((true, 1, "bilingual"));
        var detail = await owner.GetOkAsync($"/api/v1/print/templates/{templateId}");
        var editedHtml = detail.GetProperty("html").GetString()!.Replace("{{ L.thank_you }}", "Payment within 30 days to IBAN SA00 0000 0000 0000 0000 0000.", StringComparison.Ordinal);
        var saved = await owner.PutAsync($"/api/v1/print/templates/{templateId}", new { html = editedHtml, note = "Bank details in the footer" });
        saved.GetProperty("currentVersion").GetInt32().ShouldBe(2);
        (await owner.GetTextAsync($"/api/v1/print/preview?documentType=sales_invoice&documentId={invoiceId}")).ShouldContain("IBAN SA00");
        var rerender = await owner.PostAsync("/api/v1/print/renders", new { documentType = "sales_invoice", documentId = invoiceId });
        (rerender.GetProperty("reused").GetBoolean(), rerender.GetProperty("templateVersion").GetInt32()).ShouldBe((false, 2));
        await RenderedAsync(host, owner, rerender.GetProperty("id").GetGuid());
        (await owner.GetOkAsync($"/api/v1/print/renders?documentType=sales_invoice&documentId={invoiceId}")).GetArrayLength().ShouldBe(2);

        // Versions and rollback: three versions, the third with version 1's content; a bad template is refused.
        var rolledBack = await owner.PostAsync($"/api/v1/print/templates/{templateId}/rollback/1", new { }, HttpStatusCode.OK);
        rolledBack.GetProperty("currentVersion").GetInt32().ShouldBe(3);
        (await owner.GetOkAsync($"/api/v1/print/templates/{templateId}/versions")).EnumerateArray().Select(static v => v.GetProperty("version").GetInt32()).ShouldBe([3, 2, 1]);
        (await owner.GetTextAsync($"/api/v1/print/preview?documentType=sales_invoice&documentId={invoiceId}")).ShouldNotContain("IBAN SA00");
        var invalid = await owner.PutAsJsonAsync($"/api/v1/print/templates/{templateId}", new { html = "{{ for x in }} broken" }, ApiFixture.Json);
        invalid.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await invalid.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("template.invalid");

        // The evidence copy sits in the immutable store under retention.
        var evidence = Directory.EnumerateFiles(host.StorageRoot, "*.pdf", SearchOption.AllDirectories).Select(static f => f.Replace('\\', '/')).Where(f => f.Contains("/immutable/print/", StringComparison.Ordinal) && f.Contains(invoiceId.ToString("N"), StringComparison.Ordinal)).ToList();
        evidence.Count.ShouldBe(2, string.Join("\n", evidence));
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task The_pdf_goes_by_email_to_the_customer_with_the_send_logged_and_an_order_and_a_statement_print_too_while_a_reader_is_refused()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var (invoiceId, number) = await DraftInvoiceAsync(s);
        await owner.PostAsync($"/api/v1/sales/invoices/{invoiceId}/post", new { }, HttpStatusCode.OK);

        // Email: queued against a render the worker makes first, then sent with the PDF attached to the customer's address.
        var queued = await owner.PostAsync("/api/v1/print/emails", new { documentType = "sales_invoice", documentId = invoiceId, message = "Attached is your invoice for September." });
        (queued.GetProperty("status").GetString(), queued.GetProperty("to").GetString()).ShouldBe(("queued", "ap@jeddahmall.example"));
        queued.GetProperty("subject").GetString().ShouldBe($"Tax invoice / فاتورة ضريبية {number}");
        await host.RunWorkerAsync();
        var emails = await owner.GetOkAsync($"/api/v1/print/emails?documentType=sales_invoice&documentId={invoiceId}");
        emails.EnumerateArray().Single().GetProperty("status").GetString().ShouldBe("sent");
        var mail = Api.Emails.LastTo("ap@jeddahmall.example").ShouldNotBeNull();
        mail.Subject.ShouldContain(number);
        mail.TextBody.ShouldContain("Attached is your invoice");
        var attachment = mail.Attachments.ShouldNotBeNull().ShouldHaveSingleItem();
        (attachment.FileName, attachment.ContentType).ShouldBe(($"{number}.pdf", "application/pdf"));
        attachment.Content.Span[..5].ToArray().ShouldBe("%PDF-"u8.ToArray());
        var activity = (await owner.GetOkAsync($"/api/v1/collaboration/activities?entityType=sales_invoice&entityId={invoiceId}")).GetProperty("items");
        activity.EnumerateArray().Any(static a => a.GetProperty("kind").GetString() == "print.emailed").ShouldBeTrue(activity.ToString());

        // No address anywhere: refused with the problem named.
        var nobody = (await owner.PostAsync("/api/v1/partners", new { code = "NOBODY", legalName = Name("Nobody LLC", "لا أحد"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{nobody}/customer-accounts/{s.CompanyId}", new { currency = "SAR" });
        var silent = await owner.PostAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = nobody, lines = new object[] { new { kind = "service", quantity = 1m, unitPrice = 10m, description = "Fee", accountRole = "Revenue", taxCodeId = s.StandardCode } } });
        await owner.PostAsync($"/api/v1/sales/invoices/{silent.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        (await owner.PostErrorAsync("/api/v1/print/emails", new { documentType = "sales_invoice", documentId = silent.GetProperty("id").GetGuid() }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("print.no_email");

        // An order confirmation and a customer statement print through the same pipeline.
        var order = await owner.PostAsync("/api/v1/sales/orders", new { companyId = s.CompanyId, partnerId = s.Customer, warehouseId = s.Warehouse, lines = new object[] { new { itemId = s.Tea, quantity = 3m } } });
        var orderHtml = await owner.GetTextAsync($"/api/v1/print/preview?documentType=sales_order&documentId={order.GetProperty("id").GetGuid()}");
        orderHtml.ShouldContain("Sales order / أمر بيع");
        orderHtml.ShouldContain(order.GetProperty("number").GetString()!);
        orderHtml.ShouldContain("DRAFT");
        var statementHtml = await owner.GetTextAsync($"/api/v1/print/preview?documentType=customer_statement&companyId={s.CompanyId}&partnerId={s.Customer}&from=2026-09-01&to=2026-09-30");
        statementHtml.ShouldContain("Statement of account / كشف حساب");
        statementHtml.ShouldContain(number);
        statementHtml.ShouldContain("Closing balance");
        (await owner.GetErrorAsync($"/api/v1/print/preview?documentType=customer_statement&partnerId={s.Customer}", HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("print.parameter_required");
        (await owner.GetErrorAsync($"/api/v1/print/preview?documentType=nothing&documentId={invoiceId}", HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("print.document_type_unknown");
        var statementRender = await owner.PostAsync("/api/v1/print/renders", new { documentType = "customer_statement", parameters = new { companyId = s.CompanyId.ToString(), partnerId = s.Customer.ToString(), from = "2026-09-01", to = "2026-09-30" } });
        statementRender.GetProperty("isFinal").GetBoolean().ShouldBeFalse("a statement is never evidence");
        (await RenderedAsync(host, owner, statementRender.GetProperty("id").GetGuid())).GetProperty("isEvidence").GetBoolean().ShouldBeFalse();

        // The document types the UI offers; a member who may read invoices but not print is refused, and one who may print but not read invoices too.
        (await owner.GetOkAsync("/api/v1/print/document-types")).EnumerateArray().Select(static d => d.GetProperty("documentType").GetString()).ShouldBe(["customer_statement", "financial_statement", "payslip", "pos_order", "report", "sales_invoice", "sales_order"]);
        var reader = await InviteAsync(owner, s.Ws, "invoice_reader", "sales.invoice.read");
        (await reader.GetErrorAsync($"/api/v1/print/preview?documentType=sales_invoice&documentId={invoiceId}", HttpStatusCode.Forbidden)).Code.ShouldBe("auth.forbidden");
        var printer = await InviteAsync(owner, s.Ws, "printer_only", "print.document.render");
        (await printer.GetErrorAsync($"/api/v1/print/preview?documentType=sales_invoice&documentId={invoiceId}", HttpStatusCode.Forbidden)).Code.ShouldBe("print.forbidden");
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_share_link_opens_the_pdf_without_signing_in_counts_its_openings_and_dies_when_revoked_or_expired_while_a_queued_render_and_a_bad_phone_are_refused()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var (invoiceId, number) = await DraftInvoiceAsync(s);
        await owner.PostAsync($"/api/v1/sales/invoices/{invoiceId}/post", new { }, HttpStatusCode.OK);
        var requested = await owner.PostAsync("/api/v1/print/renders", new { documentType = "sales_invoice", documentId = invoiceId });
        var renderId = requested.GetProperty("id").GetGuid();

        // Nothing to share until the worker has rendered; a phone without its country code, a life beyond the limit and an unknown channel are refused.
        (await owner.PostErrorAsync("/api/v1/print/shares", new { renderId, channel = "whatsapp" }, HttpStatusCode.Conflict)).Code.ShouldBe("print.render_not_ready");
        await RenderedAsync(host, owner, renderId);
        (await owner.PostErrorAsync("/api/v1/print/shares", new { renderId, channel = "whatsapp", recipient = "12345" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("print.share_phone_invalid");
        (await owner.PostErrorAsync("/api/v1/print/shares", new { renderId, expiresInDays = 31 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("print.share_expiry_invalid");
        (await owner.PostErrorAsync("/api/v1/print/shares", new { renderId, channel = "pigeon" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("print.share_channel_unknown");

        // The WhatsApp link: the phone kept as digits, the URL given once, seven days of life.
        var share = await owner.PostAsync("/api/v1/print/shares", new { renderId, channel = "whatsapp", recipient = "+966 50 123 4567" });
        (share.GetProperty("recipient").GetString(), share.GetProperty("channel").GetString(), share.GetProperty("isLive").GetBoolean()).ShouldBe(("966501234567", "whatsapp", true));
        share.GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(Api.Clock.UtcNow.AddDays(7));
        var url = share.GetProperty("url").GetString()!;
        url.ShouldContain("/api/v1/public/prints/");
        var token = url[(url.LastIndexOf('/') + 1)..];

        // Anyone with the link and no account: the PDF shown inline; two openings counted, and the list never repeats the URL.
        var anonymous = Api.Client;
        var opened = await anonymous.GetAsync(new Uri($"/api/v1/public/prints/{token}", UriKind.Relative));
        opened.StatusCode.ShouldBe(HttpStatusCode.OK);
        opened.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        opened.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
        (await opened.Content.ReadAsByteArrayAsync()).Take(5).ToArray().ShouldBe("%PDF-"u8.ToArray());
        (await anonymous.GetAsync(new Uri($"/api/v1/public/prints/{token}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var listed = (await owner.GetOkAsync($"/api/v1/print/shares?documentType=sales_invoice&documentId={invoiceId}")).EnumerateArray().Single();
        (listed.GetProperty("openCount").GetInt32(), listed.GetProperty("number").GetString()).ShouldBe((2, number));
        (!listed.TryGetProperty("url", out var listedUrl) || listedUrl.ValueKind == JsonValueKind.Null).ShouldBeTrue("the token is kept only as a hash");
        var activity = (await owner.GetOkAsync($"/api/v1/collaboration/activities?entityType=sales_invoice&entityId={invoiceId}")).GetProperty("items");
        activity.EnumerateArray().Any(static a => a.GetProperty("kind").GetString() == "print.shared").ShouldBeTrue(activity.ToString());

        // A tampered token and a malformed one read the same as none; revoked, the link is gone for good.
        (await anonymous.GetAsync(new Uri($"/api/v1/public/prints/{token[..^4]}AAAA", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await anonymous.GetAsync(new Uri("/api/v1/public/prints/not-a-token", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var revoked = await owner.PostAsync($"/api/v1/print/shares/{share.GetProperty("id").GetGuid()}/revoke", new { }, HttpStatusCode.OK);
        revoked.GetProperty("isLive").GetBoolean().ShouldBeFalse();
        (await anonymous.GetAsync(new Uri($"/api/v1/public/prints/{token}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A plain link of one day: past its life it answers expired, and says so.
        var brief = await owner.PostAsync("/api/v1/print/shares", new { renderId, expiresInDays = 1 });
        var briefToken = brief.GetProperty("url").GetString()!.Split('/')[^1];
        var jump = TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1);
        Api.Clock.Advance(jump);
        try
        {
            var expired = await anonymous.GetAsync(new Uri($"/api/v1/public/prints/{briefToken}", UriKind.Relative));
            expired.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await expired.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("print.share_expired");
        }
        finally
        {
            Api.Clock.Advance(-jump);
        }

        (await owner.GetOkAsync($"/api/v1/print/shares?documentType=sales_invoice&documentId={invoiceId}")).GetArrayLength().ShouldBe(2);
        await owner.AssertInvariantsAsync();
    }

    private async Task<HttpClient> InviteAsync(HttpClient owner, Workspace ws, string roleCode, params string[] grants)
    {
        var role = await owner.PostAsync("/api/v1/roles", new { code = roleCode, name = Name(roleCode, roleCode), description = "", grants });
        var email = $"{roleCode}-{ws.Slug}@example.test";
        await owner.PostAsync("/api/v1/users/invite", new { email, displayName = roleCode, roleIds = new[] { role.GetProperty("id").GetGuid() } }, HttpStatusCode.Created);
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, ApiFixture.Json)).ReadJsonAsync();
        return Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);
    }
}
