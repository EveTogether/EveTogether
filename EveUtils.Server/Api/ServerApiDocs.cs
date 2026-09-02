using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace EveUtils.Server.Api;

/// <summary>
/// The self-documenting half of the REST API: the OpenAPI document behind <c>/openapi/v1.json</c> and the Scalar
/// reference at <c>/scalar</c>, both public and keyless (ratified decision 4). The transformers below put the
/// API-key scheme in the document, so the reference shows a consumer how to authenticate and lets them try it.
/// </summary>
public static class ServerApiDocs
{
    public const string SecuritySchemeId = "ApiKey";
    public const string QuerySchemeId = "ApiKeyQuery";

    /// <summary>The header form, and the reason it is the documented default.</summary>
    private const string HeaderDescription =
        "Your API key, as issued in the admin panel. The preferred way to send it.";

    /// <summary>Ratified decision 8: the query form stays, and it is documented with the leak it carries.</summary>
    private const string QueryDescription =
        "Your API key as a query parameter, for browsers and embeds that cannot set a header. "
        + "Proxies, CDNs and web servers routinely log query strings, so a key sent this way can end up in "
        + "logs outside your control. Prefer the X-API-KEY header wherever you can set one.";

    public static IServiceCollection AddServerApiDocs(this IServiceCollection services) =>
        services.AddOpenApi(options =>
        {
            options.ShouldInclude = description => DescribesTheV1Api(description.RelativePath);
            options.AddDocumentTransformer((document, _, _) =>
            {
                DescribeApiKeyAuth(document);
                return Task.CompletedTask;
            });
            options.AddOperationTransformer((operation, context, _) =>
            {
                RequireApiKey(operation, context.Description.ActionDescriptor.EndpointMetadata);
                return Task.CompletedTask;
            });
        });

    /// <summary>
    /// The document describes the v1 REST contract and nothing else. The host maps plenty of other routes — the
    /// panel's login post, the backup download, the pairing scope list — and this document is public, so leaving
    /// them in would hand a keyless caller a map of the rest of the server.
    /// </summary>
    internal static bool DescribesTheV1Api(string? relativePath) =>
        relativePath?.TrimStart('/').StartsWith("api/v1/", StringComparison.Ordinal) == true;

    /// <summary>Puts both ways of presenting a key in the document's security schemes.</summary>
    internal static void DescribeApiKeyAuth(OpenApiDocument document)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes[SecuritySchemeId] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApiKeyAuthentication.HeaderName,
            Description = HeaderDescription
        };
        document.Components.SecuritySchemes[QuerySchemeId] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Query,
            Name = ApiKeyAuthentication.QueryName,
            Description = QueryDescription
        };
    }

    /// <summary>
    /// Marks an operation as needing a key — unless its endpoint opted out of the group's gate, which is how
    /// <c>/health</c> stays keyless. Reading the same <c>AllowAnonymous</c> the pipeline reads keeps the document
    /// honest: a route that changes sides in code changes sides in the contract too.
    /// </summary>
    internal static void RequireApiKey(OpenApiOperation operation, IEnumerable<object> endpointMetadata)
    {
        if (endpointMetadata.OfType<IAllowAnonymous>().Any()) return;

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(SecuritySchemeId)] = []
        });
    }
}
