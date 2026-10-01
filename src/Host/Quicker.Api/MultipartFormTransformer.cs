using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Quicker.Web;

namespace Quicker.Api;

/// <summary>Writes the request body of endpoints marked with <see cref="MultipartFormMetadata"/> as multipart/form-data with the form's schema.</summary>
public sealed class MultipartFormTransformer : IOpenApiOperationTransformer
{
    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        var form = context.Description.ActionDescriptor.EndpointMetadata.OfType<MultipartFormMetadata>().FirstOrDefault();
        if (form is null)
        {
            return;
        }

        var schema = await context.GetOrCreateSchemaAsync(form.FormType, null, cancellationToken);
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal) { ["multipart/form-data"] = new OpenApiMediaType { Schema = schema } },
        };
    }
}
