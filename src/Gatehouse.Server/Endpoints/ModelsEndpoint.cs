using System.Text.Json;
using Gatehouse.Routing;
using Gatehouse.Wire;

namespace Gatehouse.Server.Endpoints;

/// <summary>
/// The OpenAI-compatible <c>/v1/models</c> endpoint.
/// </summary>
internal static class ModelsEndpoint
{
    /// <summary>Maps the endpoint.</summary>
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/models", HandleAsync)
           .WithName("ListModels")
           .WithSummary("Lists the model aliases this gateway routes.");
    }

    private static async Task HandleAsync(HttpContext context)
    {
        var router = context.RequestServices.GetRequiredService<IModelRouter>();
        var timeProvider = context.RequestServices.GetRequiredService<TimeProvider>();

        long created = timeProvider.GetUtcNow().ToUnixTimeSeconds();

        var response = new ModelListResponse
        {
            Data = [.. router.Aliases
                .Order(StringComparer.Ordinal)
                .Select(alias => new ModelDescriptor { Id = alias, Created = created })],
        };

        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            response,
            GatehouseJsonContext.Default.ModelListResponse,
            context.RequestAborted);
    }
}
