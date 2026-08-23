using System.Net.Http.Headers;
using Azure.Core;
using Azure.Identity;
using Gatehouse.Configuration;
using Gatehouse.Providers;
using Gatehouse.Providers.Anthropic;
using Gatehouse.Providers.AzureOpenAI;
using Gatehouse.Providers.Google;
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
            // An explicit switch rather than a plugin registry, deliberately. Adding a provider
            // is a governance event under GOVERNANCE.md — it needs an RFC and an owner — so a
            // compile-time list of the supported kinds is an accurate reflection of the
            // project's constraints rather than an obstacle to them. It also keeps every
            // registration path visible to the trimmer.
            switch (provider.Kind.ToLowerInvariant())
            {
                case OpenAiCompatibleProvider.Kind:
                    builder.AddOpenAiCompatible(name, provider);
                    break;

                case AzureOpenAiProviderKind:
                    builder.AddAzureOpenAi(name, provider);
                    break;

                case AnthropicProvider.Kind:
                    builder.AddAnthropic(name, provider);
                    break;

                case GeminiProvider.Kind:
                    builder.AddGemini(name, provider);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Provider '{name}' has kind '{provider.Kind}', which this build does not "
                        + $"support. Supported kinds are: {string.Join(", ", SupportedKinds)}.");
            }
        }
    }

    /// <summary>The configuration <c>Kind</c> that selects Azure OpenAI.</summary>
    /// <remarks>
    /// Distinct from <see cref="OpenAiCompatibleProvider.Kind"/> even though the payload is
    /// identical, because addressing and authentication differ: Azure names the deployment in
    /// the path and can authenticate with a managed identity.
    /// </remarks>
    public const string AzureOpenAiProviderKind = "azure-openai";

    private static readonly string[] SupportedKinds =
    [
        OpenAiCompatibleProvider.Kind,
        AzureOpenAiProviderKind,
        AnthropicProvider.Kind,
        GeminiProvider.Kind,
    ];

    /// <summary>
    /// Registers an OpenAI-compatible upstream — OpenAI itself, Ollama, vLLM, Foundry Local.
    /// </summary>
    private static void AddOpenAiCompatible(
        this IHostApplicationBuilder builder,
        string name,
        ProviderOptions provider)
    {
        string clientName = HttpClientNameFor(name);
        TimeSpan timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds);
        string? apiKey = ResolveApiKey(provider);

        builder.Services
            .AddHttpClient(clientName, client => ConfigureClient(client, provider))
            .ConfigureHttpClient(client =>
            {
                if (!string.IsNullOrEmpty(apiKey))
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }
            });

        builder.Services.AddSingleton<IChatProvider>(sp => new OpenAiCompatibleProvider(
            name,
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName),
            timeout,
            sp.GetRequiredService<ILogger<OpenAiCompatibleProvider>>(),
            OpenAiAddressing.Instance));
    }

    /// <summary>
    /// Registers an Azure OpenAI upstream: deployment-in-path addressing, with either an
    /// API key or a Microsoft Entra managed identity.
    /// </summary>
    private static void AddAzureOpenAi(
        this IHostApplicationBuilder builder,
        string name,
        ProviderOptions provider)
    {
        string clientName = HttpClientNameFor(name);
        TimeSpan timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds);
        string? apiKey = ResolveApiKey(provider);
        bool useManagedIdentity = provider.UseManagedIdentity;
        string? clientId = provider.ManagedIdentityClientId;
        string? apiVersion = provider.ApiVersion;

        IHttpClientBuilder clientBuilder = builder.Services
            .AddHttpClient(clientName, client => ConfigureClient(client, provider));

        if (useManagedIdentity)
        {
            // The recommended path: no credential is stored anywhere. The token is attached by
            // a delegating handler so authentication stays orthogonal to the wire format.
            clientBuilder.AddHttpMessageHandler(sp => new EntraTokenHandler(
                CreateCredential(clientId),
                sp.GetRequiredService<ILogger<EntraTokenHandler>>(),
                sp.GetRequiredService<TimeProvider>()));
        }
        else
        {
            clientBuilder.ConfigureHttpClient(client =>
            {
                // Azure uses an api-key header rather than a bearer token for key-based auth.
                if (!string.IsNullOrEmpty(apiKey))
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("api-key", apiKey);
                }
            });
        }

        builder.Services.AddSingleton<IChatProvider>(sp => new OpenAiCompatibleProvider(
            name,
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName),
            timeout,
            sp.GetRequiredService<ILogger<OpenAiCompatibleProvider>>(),
            new AzureOpenAiAddressing(apiVersion)));
    }

    /// <summary>Registers an Anthropic upstream.</summary>
    private static void AddAnthropic(
        this IHostApplicationBuilder builder,
        string name,
        ProviderOptions provider)
    {
        string clientName = HttpClientNameFor(name);
        TimeSpan timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds);
        string? apiKey = ResolveApiKey(provider);

        builder.Services
            .AddHttpClient(clientName, client => ConfigureClient(client, provider))
            .ConfigureHttpClient(client =>
            {
                // Anthropic uses x-api-key, not Authorization: Bearer. Sending a bearer token
                // produces a 401 whose message does not mention the wrong header.
                if (!string.IsNullOrEmpty(apiKey))
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey);
                }

                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "anthropic-version",
                    AnthropicProvider.AnthropicVersion);
            });

        builder.Services.AddSingleton<IChatProvider>(sp => new AnthropicProvider(
            name,
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName),
            timeout,
            sp.GetRequiredService<ILogger<AnthropicProvider>>(),
            sp.GetRequiredService<TimeProvider>()));
    }

    /// <summary>Registers a Google Gemini upstream.</summary>
    private static void AddGemini(
        this IHostApplicationBuilder builder,
        string name,
        ProviderOptions provider)
    {
        string clientName = HttpClientNameFor(name);
        TimeSpan timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds);
        string? apiKey = ResolveApiKey(provider);

        builder.Services
            .AddHttpClient(clientName, client => ConfigureClient(client, provider))
            .ConfigureHttpClient(client =>
            {
                // The header form, not the ?key= query parameter the API also accepts: a
                // credential in a query string ends up in access logs and error reports.
                if (!string.IsNullOrEmpty(apiKey))
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("x-goog-api-key", apiKey);
                }
            });

        builder.Services.AddSingleton<IChatProvider>(sp => new GeminiProvider(
            name,
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName),
            timeout,
            sp.GetRequiredService<ILogger<GeminiProvider>>(),
            sp.GetRequiredService<TimeProvider>()));
    }

    /// <summary>Applies the settings every provider's client needs.</summary>
    private static void ConfigureClient(HttpClient client, ProviderOptions provider)
    {
        // A trailing slash matters: without it, Uri resolution against a relative path
        // silently discards the last path segment of the base address, so
        // "https://host/openai/v1" + "chat/completions" would lose "/v1".
        client.BaseAddress = new Uri(provider.BaseUrl.TrimEnd('/') + "/");

        // Timeouts are enforced per call by the provider, not here. See the
        // OpenAiCompatibleProvider constructor for why the built-in one is unusable for
        // streaming.
        client.Timeout = Timeout.InfiniteTimeSpan;

        foreach ((string header, string value) in provider.Headers)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(header, value);
        }
    }

    /// <summary>
    /// Builds the credential used for Entra authentication.
    /// </summary>
    /// <remarks>
    /// <see cref="DefaultAzureCredential"/> rather than <c>ManagedIdentityCredential</c>, so
    /// the same configuration works on a developer machine signed in to the Azure CLI as it
    /// does on a managed-identity host. The alternative is configuration that only works in
    /// production, which is configuration nobody tests.
    /// </remarks>
    private static DefaultAzureCredential CreateCredential(string? managedIdentityClientId) =>
        new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = string.IsNullOrWhiteSpace(managedIdentityClientId)
                ? null
                : managedIdentityClientId,
        });

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
