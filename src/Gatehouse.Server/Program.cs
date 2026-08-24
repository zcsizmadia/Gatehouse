using System.Collections.Frozen;
using Gatehouse.Configuration;
using Gatehouse.Server.Endpoints;
using Gatehouse.Server.Infrastructure;
using Gatehouse.Storage;
using Yarp.ReverseProxy.Configuration;

namespace Gatehouse.Server;

/// <summary>
/// The Gatehouse entry point.
/// </summary>
/// <remarks>
/// One binary, three hosts. The same executable runs as a console process, a Windows Service
/// and a systemd unit, because <c>AddWindowsService</c> and <c>AddSystemd</c> detect their
/// environment and do nothing when it is absent. Proving that from a single build — rather
/// than shipping three packaging variants that drift apart — is the Phase 0 gate.
/// </remarks>
public static class Program
{
    /// <summary>Runs the gateway.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Contains("--version", StringComparer.Ordinal))
        {
            Console.WriteLine($"gatehouse {GatehouseVersion.Informational}");
            return 0;
        }

        if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            WriteHelp();
            return 0;
        }

        // Subcommands are handled before any host is built. `keys create` has to work on a
        // deployment that cannot start yet, because requiring authentication with no keys is
        // precisely the state it exists to resolve.
        if (args.Length > 0 && string.Equals(args[0], "keys", StringComparison.Ordinal))
        {
            TryGetConfigPath(args, out string keysConfigPath);
            return await KeyCommands.RunAsync(args, string.IsNullOrEmpty(keysConfigPath) ? null : keysConfigPath);
        }

        // Reporting, not serving: it reads the store and exits, so it must not build a host.
        if (args.Length > 0 && string.Equals(args[0], "usage", StringComparison.Ordinal))
        {
            TryGetConfigPath(args, out string usageConfigPath);
            return await UsageCommands.RunAsync(args, string.IsNullOrEmpty(usageConfigPath) ? null : usageConfigPath);
        }

        WebApplication app = BuildApplication(args);
        await app.RunAsync();
        return 0;
    }

    /// <summary>
    /// Builds the configured application.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="Main"/> so that integration tests can host the real pipeline
    /// rather than a re-implementation of it. A test that exercises a parallel wiring path is
    /// a test that passes while production is broken.
    /// </remarks>
    internal static WebApplication BuildApplication(string[] args)
    {
        // CreateSlimBuilder rather than CreateBuilder: it omits the hosting features a
        // gateway does not use (IIS integration, EventLog, regex route constraints) and is
        // the configuration the NativeAOT publish is actually tested against.
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

        if (TryGetConfigPath(args, out string? configPath))
        {
            // optional: false — an operator who passed --config and mistyped the path should
            // be told, not silently given defaults that happen to start.
            builder.Configuration.AddJsonFile(configPath, optional: false, reloadOnChange: true);
        }

        // Both are no-ops outside their respective host, so the same build serves all three
        // deployment targets.
        builder.Services.AddWindowsService(options => options.ServiceName = "Gatehouse");
        builder.Services.AddSystemd();

        builder.AddGatehouse();
        builder.AddGatehouseTelemetry();

        builder.Services.AddHealthChecks();

        GatehouseOptions options = builder.Configuration
            .GetSection(GatehouseOptions.SectionName)
            .Get<GatehouseOptions>() ?? new GatehouseOptions();

        (IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters) =
            PassthroughProxy.Build(options.Providers);

        bool passthroughEnabled = routes.Count > 0;
        if (passthroughEnabled)
        {
            builder.Services.AddReverseProxy().LoadFromMemory([.. routes], [.. clusters]);
        }

        WebApplication app = builder.Build();

        WarnAboutInsecureConfiguration(app, options);

        // Before the endpoints, and before the passthrough proxy. Health checks are reached
        // first and stay open deliberately: a liveness probe that needs a credential is a
        // liveness probe that fails during credential rotation.
        app.MapHealthChecks("/health/live");
        app.MapHealthChecks("/health/ready");

        app.UseMiddleware<VirtualKeyAuthenticationMiddleware>();

        ChatCompletionsEndpoint.Map(app);
        ModelsEndpoint.Map(app);

        if (passthroughEnabled)
        {
            FrozenDictionary<string, string?> credentials = ResolvePassthroughCredentials(options);

            app.UseMiddleware<PassthroughMiddleware>(
                credentials,
                app.Services.GetRequiredService<IRequestLogStore>(),
                app.Services.GetRequiredService<TimeProvider>());

            app.MapReverseProxy();
        }

        return app;
    }

    /// <summary>
    /// Emits startup warnings for configurations that are legal but risky.
    /// </summary>
    /// <remarks>
    /// These are warnings rather than errors because every one of them is a legitimate choice
    /// in some deployment — a literal key is fine on a laptop, passthrough is fine when you
    /// have accepted the metering gap. What is not fine is arriving at either by accident and
    /// finding out during an audit.
    /// </remarks>
    private static void WarnAboutInsecureConfiguration(WebApplication app, GatehouseOptions options)
    {
        ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Gatehouse.Startup");

        foreach ((string name, ProviderOptions provider) in options.Providers)
        {
            if (!string.IsNullOrWhiteSpace(provider.ApiKey)
                && string.IsNullOrWhiteSpace(provider.ApiKeyEnvironmentVariable))
            {
                logger.LogWarning(
                    "Provider {ProviderName} has a literal ApiKey in configuration. Use "
                    + "ApiKeyEnvironmentVariable, or managed identity, so the credential does "
                    + "not end up in source control or a container image.",
                    name);
            }

            if (provider.AllowPassthrough)
            {
                logger.LogWarning(
                    "Provider {ProviderName} has passthrough enabled at {Prefix}/{ProviderName}/. "
                    + "Requests on that path are forwarded verbatim and cannot be metered; they "
                    + "are recorded as unmetered in the request log.",
                    name,
                    PassthroughProxy.PathPrefix,
                    name);
            }
        }
    }

    private static FrozenDictionary<string, string?> ResolvePassthroughCredentials(GatehouseOptions options)
    {
        Dictionary<string, string?> credentials = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string name, ProviderOptions provider) in options.Providers)
        {
            if (!provider.AllowPassthrough)
            {
                continue;
            }

            credentials[name] = string.IsNullOrWhiteSpace(provider.ApiKeyEnvironmentVariable)
                ? provider.ApiKey
                : Environment.GetEnvironmentVariable(provider.ApiKeyEnvironmentVariable);
        }

        return credentials.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryGetConfigPath(string[] args, out string configPath)
    {
        configPath = string.Empty;

        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--config", StringComparison.Ordinal))
            {
                continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new InvalidOperationException("--config requires a path to a JSON configuration file.");
            }

            configPath = Path.GetFullPath(args[i + 1]);
            return true;
        }

        return false;
    }

    private static void WriteHelp()
    {
        Console.WriteLine(
            """
            gatehouse — the open AI control plane for the enterprise

            Usage:
              gatehouse [options]
              gatehouse keys <create|list|revoke> [options]
              gatehouse usage <summary|reconcile> [options]

            Commands:
              keys                 Manage virtual keys. Run 'gatehouse keys' for details.
              usage                Report recorded usage, and reconcile it against a
                                   provider statement. Run 'gatehouse usage' for details.

            Options:
              --config <path>    Load configuration from a JSON file, in addition to
                                 appsettings.json and environment variables.
              --urls <urls>      Addresses to listen on (default http://localhost:8080).
              --version          Print the version and exit.
              --help, -h         Print this help and exit.

            Configuration may also come from environment variables prefixed with
            Gatehouse__ — for example Gatehouse__Store__ConnectionString.

            Documentation: https://github.com/zcsizmadia/Gatehouse
            """);
    }
}
