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

            if (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out Uri? baseUri))
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
