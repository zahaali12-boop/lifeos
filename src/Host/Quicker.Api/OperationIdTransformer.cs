using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Quicker.Api;

/// <summary>
/// Stable operation ids for the generated clients: the HTTP method plus the route segments after the version,
/// camel-cased, with route parameters folded in as "By…" (GET /api/v1/organization/companies/{companyId} →
/// getOrganizationCompaniesByCompanyId). An explicit name set with WithName() wins.
/// </summary>
public sealed class OperationIdTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        if (!string.IsNullOrEmpty(operation.OperationId))
        {
            return Task.CompletedTask;
        }

        var template = context.Description.RelativePath ?? string.Empty;
        var method = (context.Description.HttpMethod ?? "get").ToLowerInvariant();
        var name = new StringBuilder(method);
        foreach (var raw in template.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw;
            if (segment is "api" or "v1")
            {
                continue;
            }

            var isParameter = segment.StartsWith('{');
            segment = segment.Trim('{', '}');
            var colon = segment.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                segment = segment[..colon];
            }

            if (isParameter)
            {
                name.Append("By");
            }

            foreach (var part in segment.Split('-', '_', '.'))
            {
                if (part.Length > 0)
                {
                    name.Append(char.ToUpper(part[0], CultureInfo.InvariantCulture)).Append(part.AsSpan(1));
                }
            }
        }

        operation.OperationId = name.ToString();
        return Task.CompletedTask;
    }
}
