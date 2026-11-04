using Quicker.Kernel.Results;

namespace Quicker.Sales.Application;

/// <summary>Shared checks of sales documents: quantities, dates and free text.</summary>
internal static class Validation
{
    public static Result<decimal> Positive(decimal value, string field) =>
        value > 0m ? value : Error.Validation($"{field}_invalid", "Must be greater than zero.").WithWhy(("value", value));

    public static Result Period(DateOnly quoteDate, DateOnly? validUntil) =>
        validUntil is { } v && v < quoteDate
            ? Error.Validation("quotation.validity_invalid", "The valid-until date is on or after the quote date.").WithWhy(("quoteDate", quoteDate), ("validUntil", validUntil))
            : Result.Success();

    public static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
