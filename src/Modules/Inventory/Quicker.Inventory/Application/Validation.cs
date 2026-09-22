using System.Text.Json;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;

namespace Quicker.Inventory.Application;

/// <summary>Input rules of the inventory services: codes, bilingual names, enumerations, scopes.</summary>
internal static class Validation
{
    public static readonly IReadOnlyList<string> WarehouseKinds = ["standard", "in_transit", "consignment", "quarantine", "virtual"];
    public static readonly IReadOnlyList<string> BinKinds = ["storage", "receiving", "shipping", "quarantine", "returns"];

    public static Result<string> UpperCode(string? value, string field, int maxLength = 32)
    {
        var code = value?.Trim().ToUpperInvariant() ?? string.Empty;
        var valid = code.Length >= 1 && code.Length <= maxLength
            && char.IsAsciiLetterOrDigit(code[0])
            && code.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '/');
        return valid ? code : Error.Validation($"{field}.code_invalid", $"Codes are 1–{maxLength} letters, digits, '_', '.', '-' or '/'.");
    }

    public static Result<LocalizedText> Name(IReadOnlyDictionary<string, string>? values, string field)
    {
        if (values is null || values.Count == 0 || values.Values.All(string.IsNullOrWhiteSpace))
        {
            return Error.Validation($"{field}.name_required", "A name in at least one language is required.");
        }

        var text = new LocalizedText();
        foreach (var (language, value) in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                text = text.Set(language, value.Trim());
            }
        }

        return text;
    }

    public static Result<string> OneOf(string? value, string field, IReadOnlyList<string> allowed)
    {
        var v = value?.Trim() ?? string.Empty;
        return allowed.Contains(v, StringComparer.Ordinal)
            ? v
            : Error.Validation($"{field}.invalid", $"Expected one of: {string.Join(", ", allowed)}.").WithWhy(("value", v), ("allowed", allowed));
    }

    public static string JsonObject(JsonElement? element)
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return "{}";
        }

        return element.Value.ValueKind == JsonValueKind.Object ? element.Value.GetRawText() : "{}";
    }

    /// <summary>
    /// Record scopes of a role assignment (ADR-0014): a person whose grant is limited to companies or warehouses may
    /// only move stock there; system actors and unscoped grants pass.
    /// </summary>
    public static Result Scope(ICurrentPrincipal principal, string permission, Guid companyId, params Guid[] warehouseIds)
    {
        if (principal.Principal is not { } actor)
        {
            return Result.Success();
        }

        var scopes = actor.ScopesFor(permission) ?? RecordScopes.All;
        if (!scopes.AllowsCompany(companyId))
        {
            return Error.Forbidden("stock.company_scope", "Your role does not cover this company.").WithWhy(("companyId", companyId), ("permission", permission));
        }

        foreach (var warehouseId in warehouseIds)
        {
            if (!scopes.AllowsWarehouse(warehouseId))
            {
                return Error.Forbidden("stock.warehouse_scope", "Your role does not cover this warehouse.").WithWhy(("warehouseId", warehouseId), ("permission", permission));
            }
        }

        return Result.Success();
    }
}
