using Microsoft.Extensions.Options;

namespace Gatehouse.Configuration;

/// <summary>
/// Validates that the configuration is internally consistent before the gateway starts
/// accepting traffic.
/// </summary>
/// <remarks>
/// <para>
/// A route pointing at a provider that does not exist is a configuration typo. Left
/// unvalidated it surfaces as a 500 on the first request that happens to use that alias,
/// possibly days after the deployment, and the operator has no reason to connect the two.
/// Failing at startup instead turns a latent production incident into a failed rollout,
/// which is the whole point of having a validation step.
/// </para>
/// <para>
/// Every problem found is reported, not just the first. An operator fixing a config file
/// should need one iteration, not one per mistake.
/// </para>
/// <para>
/// The range and presence checks here would conventionally be DataAnnotations attributes.
/// They are hand-written because <c>ValidateDataAnnotations</c> reflects over the options
/// type, which the trimmer cannot follow: in the NativeAOT build those attributes would be
/// silently unenforced. Validation that only works in debug builds is worse than none,
/// because it is trusted.
/// </para>
/// </remarks>
public sealed class GatehouseOptionsValidator : IValidateOptions<GatehouseOptions>
{
    /// <summary>
    /// The provider kind that is addressed by region rather than by URL.
    /// </summary>
    /// <remarks>
    /// Duplicated as a literal rather than referenced from the Bedrock provider, because Core
    /// must not depend on a provider assembly — the dependency runs the other way. Kept in step
    /// by a test that asserts the two agree.
    /// </remarks>
    public const string BedrockProviderKind = "amazon-bedrock";

    /// <summary>The shortest upstream timeout that is not simply a misconfiguration.</summary>
    public const int MinTimeoutSeconds = 1;

    /// <summary>
    /// The longest upstream timeout accepted. An hour is generous even for a reasoning model;
    /// beyond it, the value is far more likely to be a units mistake than an intention.
    /// </summary>
    public const int MaxTimeoutSeconds = 3600;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, GatehouseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        if (options.Models.Count == 0)
        {
            failures.Add(
                "No models are configured. Gatehouse would accept connections and reject "
                + "every request, so this is treated as a configuration error rather than "
                + "an empty but valid deployment.");
        }

        foreach ((string alias, ModelRouteOptions route) in options.Models)
        {
            if (string.IsNullOrWhiteSpace(route.Provider))
            {
                failures.Add($"Model '{alias}' does not name a provider.");
                continue;
            }

            if (!options.Providers.ContainsKey(route.Provider))
            {
                string known = options.Providers.Count == 0
                    ? "none are configured"
                    : string.Join(", ", options.Providers.Keys.Order(StringComparer.Ordinal));

                failures.Add(
                    $"Model '{alias}' names provider '{route.Provider}', which is not "
                    + $"configured (known providers: {known}).");
            }

            foreach (string fallback in route.Fallbacks)
            {
                if (!options.Models.ContainsKey(fallback))
                {
                    failures.Add(
                        $"Model '{alias}' falls back to '{fallback}', which is not a "
                        + "configured model.");
                }
                else if (string.Equals(fallback, alias, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"Model '{alias}' lists itself as a fallback.");
                }
            }
        }

        foreach ((string providerName, ProviderOptions provider) in options.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Kind))
            {
                failures.Add($"Provider '{providerName}' does not specify a kind.");
            }

            // Bedrock is addressed by region rather than by URL: the AWS SDK derives the endpoint
            // itself, and that is the point of using it. Demanding a BaseUrl here would force
            // operators to invent one, and whatever they invented would be ignored.
            bool addressedByRegion = string.Equals(provider.Kind, BedrockProviderKind, StringComparison.OrdinalIgnoreCase);

            if (addressedByRegion)
            {
                if (string.IsNullOrWhiteSpace(provider.Region))
                {
                    failures.Add(
                        $"Provider '{providerName}' is Amazon Bedrock and does not specify a "
                        + "Region. Model availability and price both vary by region, so there is "
                        + "no safe default to fall back on.");
                }

                if (!string.IsNullOrWhiteSpace(provider.BaseUrl))
                {
                    failures.Add(
                        $"Provider '{providerName}' is Amazon Bedrock and specifies a BaseUrl, "
                        + "which is ignored — the AWS SDK derives the endpoint from Region. "
                        + "Remove it rather than leave it looking effective.");
                }

                // One environment variable without the other resolves to no credential at all
                // and silently falls through to the IAM role, which is a confusing way to find
                // out that half the configuration was missed.
                bool hasAccessKey = !string.IsNullOrWhiteSpace(provider.AccessKeyIdEnvironmentVariable);
                bool hasSecret = !string.IsNullOrWhiteSpace(provider.SecretAccessKeyEnvironmentVariable);

                if (hasAccessKey != hasSecret)
                {
                    failures.Add(
                        $"Provider '{providerName}' sets only one of "
                        + "AccessKeyIdEnvironmentVariable and SecretAccessKeyEnvironmentVariable. "
                        + "Set both to use static credentials, or neither to use the AWS "
                        + "credential chain (an IAM role, which stores no credential at all).");
                }
            }
            else if (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out Uri? baseUri))
            {
                failures.Add(
                    $"Provider '{providerName}' has a base URL that is not an absolute "
                    + $"URI: '{provider.BaseUrl}'.");
            }
            else if (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            {
                failures.Add(
                    $"Provider '{providerName}' has base URL scheme '{baseUri.Scheme}'; "
                    + "only http and https are supported.");
            }

            if (provider.TimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
            {
                failures.Add(
                    $"Provider '{providerName}' has TimeoutSeconds {provider.TimeoutSeconds}, "
                    + $"which is outside the supported range {MinTimeoutSeconds}–{MaxTimeoutSeconds}.");
            }
        }

        if (string.IsNullOrWhiteSpace(options.Store.ConnectionString))
        {
            failures.Add("Store.ConnectionString is empty. Gatehouse would have nowhere to record usage.");
        }

        if (string.IsNullOrWhiteSpace(options.Telemetry.ServiceName))
        {
            failures.Add("Telemetry.ServiceName is empty; it is required as the OpenTelemetry resource name.");
        }

        if (!string.IsNullOrWhiteSpace(options.Telemetry.OtlpEndpoint)
            && !Uri.TryCreate(options.Telemetry.OtlpEndpoint, UriKind.Absolute, out _))
        {
            failures.Add(
                $"Telemetry.OtlpEndpoint is not an absolute URI: '{options.Telemetry.OtlpEndpoint}'.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
