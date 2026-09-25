using System.Globalization;
using System.Text;
using Quicker.Kernel.Results;

namespace Quicker.Numbering.Application;

/// <summary>Everything a template can reference besides the sequence; the sequence is rendered by the database at allocation.</summary>
public sealed record TemplateContext(string CompanyCode, string? BranchCode, DateOnly Date, string? FiscalYearCode);

/// <summary>
/// Number templates: literal text with tokens <c>{company}</c>, <c>{branch}</c>, <c>{yy}</c>, <c>{yyyy}</c>, <c>{mm}</c>,
/// <c>{fy}</c> (fiscal year code without the FY prefix), <c>{seq}</c> and <c>{seq:N}</c> (zero-padded to N digits).
/// </summary>
public static class Templates
{
    public static readonly IReadOnlySet<string> Tokens = new HashSet<string>(StringComparer.Ordinal) { "company", "branch", "yy", "yyyy", "mm", "fy", "seq" };

    /// <summary>Checks syntax and token names and requires exactly one sequence token.</summary>
    public static Result Validate(string? template)
    {
        var parsed = Parse(template);
        if (parsed.IsFailure)
        {
            return parsed.Error!;
        }

        var sequences = parsed.Value.Count(static p => p.Token == "seq");
        return sequences == 1
            ? Result.Success()
            : Error.Validation("series.template_sequence_required", "A template contains exactly one {seq} or {seq:N} token.");
    }

    public static bool Needs(string template, string token) => Parse(template).Value.Any(p => p.Token == token);

    /// <summary>Renders every token except the sequence, leaving <c>{seq}</c>/<c>{seq:N}</c> for the allocator.</summary>
    public static Result<string> Render(string template, TemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var parsed = Parse(template);
        if (parsed.IsFailure)
        {
            return parsed.Error!;
        }

        var builder = new StringBuilder(template.Length + 16);
        foreach (var part in parsed.Value)
        {
            switch (part.Token)
            {
                case null:
                    builder.Append(part.Text);
                    break;
                case "seq":
                    builder.Append(part.Text);
                    break;
                case "company":
                    builder.Append(context.CompanyCode);
                    break;
                case "branch":
                    if (context.BranchCode is null)
                    {
                        return Error.Validation("series.branch_required", "The template uses {branch} but the document has no branch.");
                    }

                    builder.Append(context.BranchCode);
                    break;
                case "yy":
                    builder.Append((context.Date.Year % 100).ToString("00", CultureInfo.InvariantCulture));
                    break;
                case "yyyy":
                    builder.Append(context.Date.Year.ToString("0000", CultureInfo.InvariantCulture));
                    break;
                case "mm":
                    builder.Append(context.Date.Month.ToString("00", CultureInfo.InvariantCulture));
                    break;
                case "fy":
                    if (context.FiscalYearCode is null)
                    {
                        return Error.Conflict("series.fiscal_year_required", "The template uses {fy} but no fiscal year covers the document date.");
                    }

                    builder.Append(context.FiscalYearCode.StartsWith("FY", StringComparison.Ordinal) ? context.FiscalYearCode[2..] : context.FiscalYearCode);
                    break;
                default:
                    return Error.Validation("series.template_token_unknown", $"Unknown template token '{part.Token}'.");
            }
        }

        return builder.ToString();
    }

    /// <summary>Renders the sequence the same way the database does: zero-padded to the requested width, never truncated.</summary>
    public static string RenderSequence(string renderedTemplate, long number)
    {
        var parsed = Parse(renderedTemplate);
        var builder = new StringBuilder(renderedTemplate.Length + 8);
        foreach (var part in parsed.Value)
        {
            if (part.Token == "seq")
            {
                builder.Append(number.ToString(CultureInfo.InvariantCulture).PadLeft(part.Width, '0'));
            }
            else
            {
                builder.Append(part.Text);
            }
        }

        return builder.ToString();
    }

    private static Result<List<(string Text, string? Token, int Width)>> Parse(string? template)
    {
        var parts = new List<(string Text, string? Token, int Width)>();
        if (string.IsNullOrWhiteSpace(template))
        {
            return Error.Validation("series.template_required", "A template is required.");
        }

        var literal = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c != '{')
            {
                if (c == '}')
                {
                    return Error.Validation("series.template_invalid", "Unbalanced '}' in the template.");
                }

                literal.Append(c);
                continue;
            }

            var end = template.IndexOf('}', i + 1);
            if (end < 0)
            {
                return Error.Validation("series.template_invalid", "Unbalanced '{' in the template.");
            }

            if (literal.Length > 0)
            {
                parts.Add((literal.ToString(), null, 0));
                literal.Clear();
            }

            var body = template[(i + 1)..end];
            var colon = body.IndexOf(':', StringComparison.Ordinal);
            var token = (colon < 0 ? body : body[..colon]).Trim().ToLowerInvariant();
            var width = 0;
            if (colon >= 0)
            {
                if (token != "seq" || !int.TryParse(body[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out width) || width is < 1 or > 18)
                {
                    return Error.Validation("series.template_invalid", "Only {seq:N} takes a width, between 1 and 18.");
                }
            }

            if (!Tokens.Contains(token))
            {
                return Error.Validation("series.template_token_unknown", $"Unknown template token '{token}'.").WithWhy(("allowed", Tokens.OrderBy(static t => t, StringComparer.Ordinal).ToList()));
            }

            parts.Add((template[i..(end + 1)], token, width));
            i = end;
        }

        if (literal.Length > 0)
        {
            parts.Add((literal.ToString(), null, 0));
        }

        return parts;
    }
}
