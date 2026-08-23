using Gatehouse.Configuration;
using Microsoft.Extensions.Options;

namespace Gatehouse.Tests.Configuration;

/// <summary>
/// Tests for startup configuration validation.
/// </summary>
/// <remarks>
/// These matter more than their simplicity suggests. Each one represents a configuration
/// mistake that, without validation, produces a gateway that starts successfully and then
/// fails requests — the failure mode that gets rolled out to every environment before anyone
/// notices.
/// </remarks>
public class GatehouseOptionsValidatorTests
{
    private readonly GatehouseOptionsValidator _validator = new();

    [Test]
    public async Task Accepts_a_valid_configuration()
    {
        GatehouseOptions options = Valid();

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Succeeded).IsTrue();
    }

    [Test]
    public async Task Rejects_a_configuration_with_no_models()
    {
        var options = new GatehouseOptions();
        options.Providers["openai"] = ValidProvider();

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage!).Contains("No models are configured");
    }

    [Test]
    public async Task Rejects_a_model_naming_an_unknown_provider()
    {
        GatehouseOptions options = Valid();
        options.Models["orphan"] = new ModelRouteOptions { Provider = "does-not-exist" };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage!).Contains("does-not-exist");

        // The message must name the providers that do exist. Being told what is wrong without
        // being told what is available means a second round trip through the docs.
        await Assert.That(result.FailureMessage!).Contains("known providers");
    }

    [Test]
    public async Task Rejects_a_model_with_no_provider()
    {
        GatehouseOptions options = Valid();
        options.Models["blank"] = new ModelRouteOptions { Provider = "" };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage!).Contains("does not name a provider");
    }

    [Test]
    public async Task Rejects_a_fallback_to_an_unconfigured_model()
    {
        GatehouseOptions options = Valid();
        options.Models["chained"] = new ModelRouteOptions
        {
            Provider = "openai",
            Fallbacks = { "nowhere" },
        };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage!).Contains("nowhere");
    }

    [Test]
    public async Task Rejects_a_model_that_falls_back_to_itself()
    {
        // A self-referential fallback is either a typo or an infinite loop waiting for an
        // outage to trigger it.
        GatehouseOptions options = Valid();
        options.Models["loop"] = new ModelRouteOptions
        {
            Provider = "openai",
            Fallbacks = { "loop" },
        };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage!).Contains("lists itself as a fallback");
    }

    [Test]
    [Arguments("not-a-url")]
    [Arguments("/relative/path")]
    [Arguments("")]
    public async Task Rejects_a_base_url_that_is_not_absolute(string baseUrl)
    {
        GatehouseOptions options = Valid();
        options.Providers["broken"] = new ProviderOptions { Kind = "openai-compatible", BaseUrl = baseUrl };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
    }

    [Test]
    public async Task Rejects_a_non_http_base_url()
    {
        // ftp:// parses as an absolute URI, so the scheme needs its own check.
        GatehouseOptions options = Valid();
        options.Providers["broken"] = new ProviderOptions
        {
            Kind = "openai-compatible",
            BaseUrl = "ftp://models.example.com/",
        };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage!).Contains("only http and https");
    }

    [Test]
    public async Task Rejects_a_provider_with_no_kind()
    {
        GatehouseOptions options = Valid();
        options.Providers["broken"] = new ProviderOptions { Kind = "", BaseUrl = "https://x.example.com/" };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage!).Contains("does not specify a kind");
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(3601)]
    public async Task Rejects_an_out_of_range_timeout(int timeoutSeconds)
    {
        GatehouseOptions options = Valid();
        options.Providers["openai"] = new ProviderOptions
        {
            Kind = "openai-compatible",
            BaseUrl = "https://api.openai.com/v1/",
            TimeoutSeconds = timeoutSeconds,
        };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage!).Contains("TimeoutSeconds");
    }

    [Test]
    public async Task Rejects_an_empty_store_connection_string()
    {
        var withEmptyStore = new GatehouseOptions
        {
            Store = new StoreOptions { ConnectionString = "" },
        };
        withEmptyStore.Providers["openai"] = ValidProvider();
        withEmptyStore.Models["gpt-4o"] = new ModelRouteOptions { Provider = "openai" };

        ValidateOptionsResult result = _validator.Validate(null, withEmptyStore);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage!).Contains("ConnectionString");
    }

    [Test]
    public async Task Rejects_a_malformed_otlp_endpoint()
    {
        var options = new GatehouseOptions
        {
            Telemetry = new TelemetryOptions { OtlpEndpoint = "not a uri" },
        };
        options.Providers["openai"] = ValidProvider();
        options.Models["gpt-4o"] = new ModelRouteOptions { Provider = "openai" };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage!).Contains("OtlpEndpoint");
    }

    [Test]
    public async Task Reports_every_problem_rather_than_only_the_first()
    {
        // An operator fixing a config file should need one iteration, not one per mistake.
        var options = new GatehouseOptions();
        options.Providers["broken"] = new ProviderOptions { Kind = "", BaseUrl = "nope" };
        options.Models["a"] = new ModelRouteOptions { Provider = "missing" };
        options.Models["b"] = new ModelRouteOptions { Provider = "" };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Failures!.Count()).IsGreaterThanOrEqualTo(4);
    }

    private static GatehouseOptions Valid()
    {
        var options = new GatehouseOptions();
        options.Providers["openai"] = ValidProvider();
        options.Models["gpt-4o"] = new ModelRouteOptions { Provider = "openai" };
        return options;
    }

    private static ProviderOptions ValidProvider() => new()
    {
        Kind = "openai-compatible",
        BaseUrl = "https://api.openai.com/v1/",
    };
}
