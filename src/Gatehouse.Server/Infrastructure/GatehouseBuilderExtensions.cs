using Gatehouse.Configuration;
using Gatehouse.Providers;
using Gatehouse.Providers.OpenAI;
using Gatehouse.Routing;
using Gatehouse.Storage;
using Gatehouse.Storage.Sqlite;
using Microsoft.Extensions.Options;

namespace Gatehouse.Server.Infrastructure;

/// <summary>
/// Registers everything Gatehouse needs in the host container.
/// </summary>
internal static class GatehouseBuilderExtensions
{
    /// <summary>
    /// Binds configuration, validates it, and registers the routing, provider and storage
    /// services.
    /// </summary>
    public static IHostApplicationBuilder AddGatehouse(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services
            .AddOptions<GatehouseOptions>()
            .Bind(builder.Configuration.GetSection(GatehouseOptions.SectionName))

            // No ValidateDataAnnotations here. It reflects over the options type at runtime,
            // which the trimmer cannot see through — in the NativeAOT build the validation
            // would quietly do nothing, which is worse than not having it. GatehouseOptionsValidator
            // performs the same checks in ordinary code that the linker can follow.
            //
            // Validate at startup rather than on first use: a gateway that starts happily and
            // then rejects every request is worse than one that refuses to start, because the
            // first looks healthy to an orchestrator and gets rolled out everywhere.
            .ValidateOnStart();

        builder.Services.AddSingleton<IValidateOptions<GatehouseOptions>, GatehouseOptionsValidator>();

        builder.Services.TryAddSingletonTimeProvider();

        builder.Services.AddSingleton<IModelRouter, ModelRouter>();
        builder.Services.AddSingleton<IProviderRegistry, ProviderRegistry>();

        builder.Services.AddSingleton<SqliteRequestLogStore>();
        builder.Services.AddSingleton<IRequestLogStore>(sp => sp.GetRequiredService<SqliteRequestLogStore>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SqliteRequestLogStore>());

        builder.AddConfiguredProviders();

        return builder;
    }

    /// <summary>
    /// Registers one <see cref="IChatProvider"/> per configured provider entry.
    /// </summary>
    /// <remarks>
    /// Configuration is read here at registration time rather than resolved lazily, because
    /// each provider needs its own named <see cref="HttpClient"/> and those have to exist
    /// before the container is built. The trade is that provider topology is fixed at
    /// startup; changing it requires a restart. That is the right trade for Phase 0 and the
    /// admin UI in Phase 2 changes it by rewriting the config file, not by mutating a live
    /// container.
    /// </remarks>
    private static void AddConfiguredProviders(this IHostApplicationBuilder builder)
    {
        GatehouseOptions options = builder.Configuration
            .GetSection(GatehouseOptions.SectionName)
            .Get<GatehouseOptions>() ?? new GatehouseOptions();

        foreach ((string name, ProviderOptions provider) in options.Providers)
        {
            if (!string.Equals(provider.Kind, OpenAiCompatibleProvider.Kind, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Provider '{name}' has kind '{provider.Kind}', which this build does not "
                    + $"support. Phase 0 ships only '{OpenAiCompatibleProvider.Kind}'; the "
                    + "remaining six providers arrive in Phase 1.");
            }

            string clientName = HttpClientNameFor(name);
            string? apiKey = ResolveApiKey(provider);
            TimeSpan timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds);

            builder.Services.AddHttpClient(clientName, client =>
            {
                // A trailing slash matters: without it, Uri resolution against a relative
                // path silently discards the last path segment of the base address, so
                // "https://host/openai/v1" + "chat/completions" would lose "/v1".
                client.BaseAddress = new Uri(provider.BaseUrl.TrimEnd('/') + "/");

                // Timeouts are enforced per call by the provider, not here. See the
                // OpenAiCompatibleProvider constructor for why the built-in one is unusable
                // for streaming.
                client.Timeout = Timeout.InfiniteTimeSpan;

                if (!string.IsNullOrEmpty(apiKey))
                {
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                }

                foreach ((string header, string value) in provider.Headers)
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation(header, value);
                }
            });

            builder.Services.AddSingleton<IChatProvider>(sp => new OpenAiCompatibleProvider(
                name,
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName),
                timeout,
                sp.GetRequiredService<ILogger<OpenAiCompatibleProvider>>()));
        }
    }

    /// <summary>The named-client key for a provider.</summary>
    public static string HttpClientNameFor(string providerName) => $"gatehouse.provider.{providerName}";

    /// <summary>
    /// Resolves the API key, preferring the environment variable over the literal.
    /// </summary>
    /// <remarks>
    /// A literal key in a configuration file ends up in git history, in container images, and
    /// in every backup of both. The environment variable form is preferred, and Phase 1 adds
    /// Entra managed identity so that the best option needs no stored credential at all.
    /// </remarks>
    private static string? ResolveApiKey(ProviderOptions provider)
    {
        if (!string.IsNullOrWhiteSpace(provider.ApiKeyEnvironmentVariable))
        {
            return Environment.GetEnvironmentVariable(provider.ApiKeyEnvironmentVariable);
        }

        return provider.ApiKey;
    }

    private static IServiceCollection TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        // Registered rather than used statically so that tests can substitute a fake clock
        // without any production code taking a dependency on a testing package.
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
