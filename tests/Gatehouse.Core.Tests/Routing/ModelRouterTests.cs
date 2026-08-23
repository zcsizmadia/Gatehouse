using Gatehouse.Configuration;
using Gatehouse.Routing;
using Microsoft.Extensions.Options;

namespace Gatehouse.Tests.Routing;

/// <summary>
/// Tests for configuration-driven model routing.
/// </summary>
public class ModelRouterTests
{
    [Test]
    public async Task Resolves_a_configured_alias()
    {
        ModelRouter router = Build(("gpt-4o-mini", "openai", "gpt-4o-mini-2024-07-18"));

        bool resolved = router.TryResolve("gpt-4o-mini", out ModelRoute? route);

        await Assert.That(resolved).IsTrue();
        await Assert.That(route!.Provider).IsEqualTo("openai");
        await Assert.That(route.UpstreamModel).IsEqualTo("gpt-4o-mini-2024-07-18");
    }

    [Test]
    public async Task Defaults_the_upstream_model_to_the_alias()
    {
        // Omitting UpstreamModel is the common case: the caller already named a real model.
        ModelRouter router = Build(("gpt-4o", "openai", null));

        router.TryResolve("gpt-4o", out ModelRoute? route);

        await Assert.That(route!.UpstreamModel).IsEqualTo("gpt-4o");
    }

    [Test]
    [Arguments("GPT-4O-MINI")]
    [Arguments("gpt-4o-mini")]
    [Arguments("Gpt-4o-Mini")]
    public async Task Matches_aliases_case_insensitively(string requested)
    {
        // Clients disagree about casing and it is never what the caller meant to vary.
        ModelRouter router = Build(("gpt-4o-mini", "openai", null));

        await Assert.That(router.TryResolve(requested, out _)).IsTrue();
    }

    [Test]
    public async Task Reports_the_alias_the_caller_used()
    {
        ModelRouter router = Build(("fast", "openai", "gpt-4o-mini"));

        router.TryResolve("fast", out ModelRoute? route);

        await Assert.That(route!.Alias).IsEqualTo("fast");
    }

    [Test]
    public async Task Does_not_resolve_an_unknown_alias()
    {
        ModelRouter router = Build(("gpt-4o", "openai", null));

        bool resolved = router.TryResolve("claude-sonnet-5", out ModelRoute? route);

        await Assert.That(resolved).IsFalse();
        await Assert.That(route).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Does_not_resolve_a_blank_alias(string requested)
    {
        ModelRouter router = Build(("gpt-4o", "openai", null));

        await Assert.That(router.TryResolve(requested, out _)).IsFalse();
    }

    [Test]
    public async Task Exposes_every_alias_for_the_models_listing()
    {
        ModelRouter router = Build(
            ("gpt-4o", "openai", null),
            ("gpt-4o-mini", "openai", null),
            ("local", "ollama", "llama3.2"));

        await Assert.That(router.Aliases).Count().IsEqualTo(3);
        await Assert.That(router.Aliases).Contains("local");
    }

    [Test]
    public async Task Carries_the_configured_fallback_chain()
    {
        var options = new GatehouseOptions();
        options.Models["primary"] = new ModelRouteOptions
        {
            Provider = "openai",
            Fallbacks = { "secondary" },
        };
        options.Models["secondary"] = new ModelRouteOptions { Provider = "ollama" };

        var router = new ModelRouter(Options.Create(options));
        router.TryResolve("primary", out ModelRoute? route);

        await Assert.That(route!.Fallbacks).IsEquivalentTo(new[] { "secondary" });
    }

    [Test]
    public async Task Has_no_aliases_when_nothing_is_configured()
    {
        var router = new ModelRouter(Options.Create(new GatehouseOptions()));

        await Assert.That(router.Aliases).IsEmpty();
    }

    private static ModelRouter Build(params (string Alias, string Provider, string? UpstreamModel)[] routes)
    {
        var options = new GatehouseOptions();

        foreach ((string alias, string provider, string? upstream) in routes)
        {
            options.Models[alias] = new ModelRouteOptions
            {
                Provider = provider,
                UpstreamModel = upstream,
            };
        }

        return new ModelRouter(Options.Create(options));
    }
}
