using System.Net.Http.Headers;
using Gatehouse.Configuration;
using Gatehouse.Security;
using Gatehouse.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Gatehouse.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Gatehouse.Server.Tests.Fakes;

/// <summary>
/// A real Gatehouse process, configured from a temporary file and listening on loopback.
/// </summary>
/// <remarks>
/// Built through <see cref="Program.BuildApplication"/> — the same code path the shipped
/// binary takes. A test that assembles its own service collection proves that the test's
/// wiring works, which is not the question anyone is asking.
/// </remarks>
internal sealed class GatehouseHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions ConfigJsonOptions = new() { WriteIndented = true };

    private readonly WebApplication _app;
    private readonly string _configPath;
    private readonly string _databasePath;

    private GatehouseHost(
        WebApplication app,
        string configPath,
        string databasePath,
        HttpClient client,
        string? virtualKeySecret,
        string? virtualKeyId)
    {
        _app = app;
        _configPath = configPath;
        _databasePath = databasePath;
        Client = client;
        VirtualKeySecret = virtualKeySecret;
        VirtualKeyId = virtualKeyId;
    }

    /// <summary>
    /// An HTTP client pointed at the gateway, already presenting a valid virtual key.
    /// </summary>
    /// <remarks>
    /// Pre-authenticated on purpose. Every test that is not about authentication should exercise
    /// the authenticated path, because that is the path production runs — and it means an
    /// authentication regression fails the whole suite rather than only the tests that thought
    /// to check.
    /// </remarks>
    public HttpClient Client { get; }

    /// <summary>The secret the client presents, or null when authentication is disabled.</summary>
    public string? VirtualKeySecret { get; }

    /// <summary>The identifier of the provisioned key, for asserting on attribution.</summary>
    public string? VirtualKeyId { get; }

    /// <summary>The gateway's request log, for asserting on what was recorded.</summary>
    public IRequestLogStore RequestLog => _app.Services.GetRequiredService<IRequestLogStore>();

    /// <summary>Starts a gateway routing two aliases at the given upstream.</summary>
    /// <param name="upstreamBaseUrl">The fake upstream address.</param>
    /// <param name="apiKey">The credential the gateway should present upstream.</param>
    /// <param name="allowPassthrough">Whether to enable the YARP passthrough route.</param>
    /// <param name="kind">
    /// The provider kind to configure — <c>openai-compatible</c>, <c>anthropic</c>,
    /// <c>google-gemini</c> or <c>azure-openai</c>. The model aliases stay the same across all
    /// of them so the same tests can be pointed at any provider.
    /// </param>
    /// <param name="authenticationMode">
    /// <c>Required</c> by default, matching production. The host provisions a key before the
    /// server starts and presents it on <see cref="Client"/>, which is the same order an
    /// operator follows: issue a key, then start the gateway.
    /// </param>
    public static async Task<GatehouseHost> StartAsync(
        string upstreamBaseUrl,
        string apiKey = "test-upstream-key",
        bool allowPassthrough = false,
        string kind = "openai-compatible",
        string authenticationMode = "Required")
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"gatehouse-it-{Guid.NewGuid():N}.db");
        string configPath = Path.Combine(Path.GetTempPath(), $"gatehouse-it-{Guid.NewGuid():N}.json");

        // Written as a file rather than injected in-process, because config binding and
        // startup validation are part of what these tests are covering.
        var config = new
        {
            Gatehouse = new
            {
                // Pooling=False so the file handle closes deterministically on dispose. The
                // alternative, SqliteConnection.ClearAllPools(), is process-global and would
                // disrupt the other integration tests running in parallel.
                Store = new { ConnectionString = $"Data Source={databasePath};Pooling=False", AutoMigrate = true },
                Telemetry = new { ServiceName = "gatehouse-tests" },
                Authentication = new { Mode = authenticationMode },
                Providers = new Dictionary<string, object>
                {
                    ["fake"] = new
                    {
                        Kind = kind,
                        BaseUrl = upstreamBaseUrl,
                        ApiKey = apiKey,
                        TimeoutSeconds = 30,
                        AllowPassthrough = allowPassthrough,
                    },
                },
                Models = new Dictionary<string, object>
                {
                    // Alias whose upstream name differs, so translation is observable.
                    ["fast"] = new { Provider = "fake", UpstreamModel = "upstream-model-name" },

                    // Alias that matches its upstream model, the common case.
                    ["gpt-4o-mini"] = new { Provider = "fake" },
                },
            },
        };

        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(config, ConfigJsonOptions));

        // Provisioned before the server starts, because requiring authentication with no keys
        // is a startup failure by design. This is the same order an operator follows:
        // `gatehouse keys create`, then start the gateway.
        string? secret = null;
        string? keyId = null;

        if (!string.Equals(authenticationMode, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            (keyId, secret) = await ProvisionKeyAsync($"Data Source={databasePath};Pooling=False");
        }

        WebApplication app = Program.BuildApplication(
            ["--config", configPath, "--urls", "http://127.0.0.1:0"]);

        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        if (secret is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }

        return new GatehouseHost(app, configPath, databasePath, client, secret, keyId);
    }

    /// <summary>
    /// Inserts a usable virtual key straight into the store and returns its secret.
    /// </summary>
    /// <remarks>
    /// Uses the production store and secret generator rather than hand-written SQL, so a change
    /// to the hashing scheme or the schema breaks these tests instead of silently letting them
    /// authenticate against a format the server no longer accepts.
    /// </remarks>
    private static async Task<(string KeyId, string Secret)> ProvisionKeyAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await SqliteSchema.ApplyPragmasAsync(connection);
        await SqliteSchema.MigrateAsync(connection);

        var options = new GatehouseOptions
        {
            Store = new StoreOptions { ConnectionString = connectionString },
        };

        var store = new SqliteVirtualKeyStore(Options.Create(options));
        // Fully qualified: the property of the same name on this type shadows the static class.
        Security.VirtualKeySecret.GeneratedSecret generated = Security.VirtualKeySecret.Generate();

        var key = new VirtualKey
        {
            Id = "vk_test_" + Guid.NewGuid().ToString("N")[..8],
            Name = "integration-tests",
            SecretHash = generated.Hash,
            SecretPrefix = generated.DisplayPrefix,
            Organisation = "acme",
            Team = "platform",
            Application = "integration-tests",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await store.AddAsync(key);

        return (key.Id, generated.Secret);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();

        foreach (string path in new[] { _configPath, _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Cleanup failure must not fail an otherwise passing run.
            }
        }
    }
}
