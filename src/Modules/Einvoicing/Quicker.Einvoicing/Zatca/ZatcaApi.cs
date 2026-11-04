using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quicker.Einvoicing.Zatca;

/// <summary>Where each environment's API lives; the simulator needs no address.</summary>
public sealed class ZatcaOptions
{
    public const string Section = "Quicker:Einvoicing:Zatca";

    public Dictionary<string, string> BaseUrls { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sandbox"] = "https://gw-fatoora.zatca.gov.sa/e-invoicing/developer-portal",
        ["simulation"] = "https://gw-fatoora.zatca.gov.sa/e-invoicing/simulation",
        ["production"] = "https://gw-fatoora.zatca.gov.sa/e-invoicing/core",
    };

    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>A certificate the scheme issued: its request id, the security token (the certificate, base64) and the secret that go with every later call.</summary>
public sealed record ZatcaCsid(string RequestId, string Token, string Secret, string CertificateBase64);

/// <summary>What the scheme answered for a document: the outcome, the messages, and for a cleared invoice the XML as the scheme signed it.</summary>
public sealed record ZatcaDocumentResult(int StatusCode, string Outcome, IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors, string? ClearedXml, string? RequestId)
{
    public bool Accepted => Outcome is "CLEARED" or "REPORTED" or "PASS" or "WARNING";
}

/// <summary>The scheme could not be reached, or answered outside its contract: the caller retries later rather than records an answer.</summary>
public sealed class ZatcaUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The ZATCA e-invoicing API (compliance and production CSIDs, compliance checks, clearance, reporting) over any
/// <see cref="HttpMessageInvoker"/>: the real gateway for the sandbox, simulation and production environments, or the
/// in-process simulator. Requests and answers follow the scheme's published contract; an answer the contract does not
/// cover is an unavailability, never a verdict.
/// </summary>
public sealed class ZatcaApi(HttpMessageInvoker http, string baseUrl)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<ZatcaCsid> ComplianceCsidAsync(string csrPem, string otp, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/compliance");
        request.Headers.TryAddWithoutValidation("OTP", otp);
        request.Content = JsonContent(new { csr = Convert.ToBase64String(Encoding.UTF8.GetBytes(csrPem)) });
        return await CsidAsync(request, cancellationToken);
    }

    public async Task<ZatcaCsid> ProductionCsidAsync(string token, string secret, string complianceRequestId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/production/csids");
        Authenticate(request, token, secret);
        request.Content = JsonContent(new { compliance_request_id = complianceRequestId });
        return await CsidAsync(request, cancellationToken);
    }

    public async Task<ZatcaCsid> RenewProductionCsidAsync(string token, string secret, string otp, string csrPem, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/production/csids");
        Authenticate(request, token, secret);
        request.Headers.TryAddWithoutValidation("OTP", otp);
        request.Content = JsonContent(new { csr = Convert.ToBase64String(Encoding.UTF8.GetBytes(csrPem)) });
        return await CsidAsync(request, cancellationToken);
    }

    public Task<ZatcaDocumentResult> ComplianceCheckAsync(string token, string secret, string invoiceHash, Guid uuid, string xml, CancellationToken cancellationToken) =>
        DocumentAsync($"{baseUrl}/compliance/invoices", token, secret, invoiceHash, uuid, xml, clearance: null, cancellationToken);

    public Task<ZatcaDocumentResult> ReportAsync(string token, string secret, string invoiceHash, Guid uuid, string xml, CancellationToken cancellationToken) =>
        DocumentAsync($"{baseUrl}/invoices/reporting/single", token, secret, invoiceHash, uuid, xml, clearance: false, cancellationToken);

    public Task<ZatcaDocumentResult> ClearAsync(string token, string secret, string invoiceHash, Guid uuid, string xml, CancellationToken cancellationToken) =>
        DocumentAsync($"{baseUrl}/invoices/clearance/single", token, secret, invoiceHash, uuid, xml, clearance: true, cancellationToken);

    private async Task<ZatcaDocumentResult> DocumentAsync(string url, string token, string secret, string invoiceHash, Guid uuid, string xml, bool? clearance, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        Authenticate(request, token, secret);
        if (clearance is { } c)
        {
            request.Headers.TryAddWithoutValidation("Clearance-Status", c ? "1" : "0");
        }

        request.Content = JsonContent(new { invoiceHash, uuid = uuid.ToString(), invoice = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml)) });
        var (status, body) = await SendAsync(request, cancellationToken);
        if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
        {
            throw new ZatcaUnavailableException($"The scheme refused the device's credentials (HTTP {(int)status}).");
        }

        if ((int)status >= 500)
        {
            throw new ZatcaUnavailableException($"The scheme answered HTTP {(int)status}.");
        }

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = document.RootElement;
            var warnings = new List<string>();
            var errors = new List<string>();
            var outcome = "FAILED";
            if (root.TryGetProperty("validationResults", out var validation) && validation.ValueKind == JsonValueKind.Object)
            {
                warnings.AddRange(Messages(validation, "warningMessages"));
                errors.AddRange(Messages(validation, "errorMessages"));
                if (validation.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String)
                {
                    outcome = s.GetString() ?? outcome;
                }
            }

            if (root.TryGetProperty("clearanceStatus", out var cleared) && cleared.ValueKind == JsonValueKind.String)
            {
                outcome = cleared.GetString() ?? outcome;
            }
            else if (root.TryGetProperty("reportingStatus", out var reported) && reported.ValueKind == JsonValueKind.String)
            {
                outcome = reported.GetString() ?? outcome;
            }

            if (root.TryGetProperty("errors", out var topErrors) && topErrors.ValueKind == JsonValueKind.Array)
            {
                errors.AddRange(topErrors.EnumerateArray().Select(static e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : TopError(e)));
            }

            if (errors.Count == 0 && (int)status >= 400)
            {
                errors.Add($"HTTP {(int)status}: {(body.Length > 300 ? body[..300] : body)}");
            }

            string? clearedXml = null;
            if (root.TryGetProperty("clearedInvoice", out var ci) && ci.ValueKind == JsonValueKind.String && ci.GetString() is { Length: > 0 } encoded)
            {
                clearedXml = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }

            var requestId = root.TryGetProperty("requestID", out var rid) ? rid.ToString() : null;
            return new ZatcaDocumentResult((int)status, outcome, warnings, errors, clearedXml, requestId);
        }
        catch (JsonException e)
        {
            throw new ZatcaUnavailableException("The scheme's answer could not be read.", e);
        }
    }

    private async Task<ZatcaCsid> CsidAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var (status, body) = await SendAsync(request, cancellationToken);
        if ((int)status >= 500)
        {
            throw new ZatcaUnavailableException($"The scheme answered HTTP {(int)status}.");
        }

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = document.RootElement;
            if ((int)status >= 400 || !root.TryGetProperty("binarySecurityToken", out var tokenElement))
            {
                var detail = root.TryGetProperty("errors", out var errors) ? errors.ToString() : root.TryGetProperty("message", out var m) ? m.ToString() : body;
                throw new ZatcaRefusedException($"HTTP {(int)status}", detail.Length > 500 ? detail[..500] : detail);
            }

            var token = tokenElement.GetString() ?? string.Empty;
            var secret = root.TryGetProperty("secret", out var s) ? s.GetString() ?? string.Empty : string.Empty;
            var requestId = root.TryGetProperty("requestID", out var rid) ? rid.ToString() : string.Empty;
            return new ZatcaCsid(requestId, token, secret, Encoding.UTF8.GetString(Convert.FromBase64String(token)).Trim());
        }
        catch (JsonException e)
        {
            throw new ZatcaUnavailableException("The scheme's answer could not be read.", e);
        }
        catch (FormatException e)
        {
            throw new ZatcaUnavailableException("The scheme's security token could not be read.", e);
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Accept-Version", "V2");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en");
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return (response.StatusCode, body);
        }
        catch (HttpRequestException e)
        {
            throw new ZatcaUnavailableException("The scheme could not be reached.", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ZatcaUnavailableException("The scheme did not answer in time.", e);
        }
    }

    private static void Authenticate(HttpRequestMessage request, string token, string secret) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{token}:{secret}")));

    private static StringContent JsonContent(object body) => new(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

    private static string TopError(JsonElement e)
    {
        var code = e.TryGetProperty("code", out var c) ? c.GetString() : null;
        var message = e.TryGetProperty("message", out var m) ? m.GetString() : e.ToString();
        return $"{code ?? "error"}: {message}";
    }

    private static IEnumerable<string> Messages(JsonElement validation, string property)
    {
        if (!validation.TryGetProperty(property, out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind == JsonValueKind.String)
            {
                yield return message.GetString() ?? string.Empty;
            }
            else
            {
                var code = message.TryGetProperty("code", out var c) ? c.GetString() : null;
                var text = message.TryGetProperty("message", out var m) ? m.GetString() : message.ToString();
                yield return string.IsNullOrEmpty(code) ? text ?? string.Empty : $"{code}: {text}";
            }
        }
    }
}

/// <summary>The scheme answered a certificate request with a refusal (a wrong OTP, a request it does not accept): what it said.</summary>
public sealed class ZatcaRefusedException(string message, string detail) : Exception(message)
{
    public string Detail { get; } = detail;
}
