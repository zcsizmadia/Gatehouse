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

    private GatehouseHost(WebApplication app, string configPath, string databasePath, HttpClient client)
    {
        _app = app;
        _configPath = configPath;
        _databasePath = databasePath;
        Client = client;
    }

    /// <summary>An HTTP client pointed at the gateway.</summary>
    public HttpClient Client { get; }

    /// <summary>The gateway's request log, for asserting on what was recorded.</summary>
    public IRequestLogStore RequestLog => _app.Services.GetRequiredService<IRequestLogStore>();

    /// <summary>Starts a gateway routing two aliases at the given upstream.</summary>
    /// <param name="upstreamBaseUrl">The fake upstream address.</param>
    /// <param name="apiKey">The credential the gateway should present upstream.</param>
    /// <param name="allowPassthrough">Whether to enable the YARP passthrough route.</param>
    public static async Task<GatehouseHost> StartAsync(
        string upstreamBaseUrl,
        string apiKey = "test-upstream-key",
        bool allowPassthrough = false)
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"gatehouse-it-{Guid.NewGuid():N}.db");
        string configPath = Path.Combine(Path.GetTempPath(), $"gatehouse-it-{Guid.NewGuid():N}.json");

        // Written as a file rather than injected in-process, because config binding and
        // startup validation are part of what these tests are covering.
        var config = new
        {
            Gatehouse = new
            {
                Store = new { ConnectionString = $"Data Source={databasePath}", AutoMigrate = true },
                Telemetry = new { ServiceName = "gatehouse-tests" },
                Providers = new Dictionary<string, object>
                {
                    ["fake"] = new
                    {
                        Kind = "openai-compatible",
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

        WebApplication app = Program.BuildApplication(
            ["--config", configPath, "--urls", "http://127.0.0.1:0"]);

        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        return new GatehouseHost(app, configPath, databasePath, client);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

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
