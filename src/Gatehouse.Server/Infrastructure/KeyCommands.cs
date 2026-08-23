using Gatehouse.Configuration;
using Gatehouse.Security;
using Gatehouse.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Gatehouse.Server.Infrastructure;

/// <summary>
/// The <c>gatehouse keys</c> commands.
/// </summary>
/// <remarks>
/// <para>
/// Key issuance has to live somewhere, and until the Phase 2 admin UI exists the command line
/// is the honest place for it. A feature that can be enforced but not provisioned is not a
/// feature.
/// </para>
/// <para>
/// Arguments are parsed by hand rather than with <c>System.CommandLine</c>. Four commands do
/// not justify a dependency in a binary whose size and NativeAOT compatibility are both
/// shipping constraints.
/// </para>
/// </remarks>
internal static class KeyCommands
{
    /// <summary>Runs a <c>keys</c> subcommand.</summary>
    /// <param name="args">The full argument list, beginning with <c>keys</c>.</param>
    /// <param name="configPath">The configuration file, if one was supplied.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args, string? configPath)
    {
        string? subcommand = args.Length > 1 ? args[1] : null;

        switch (subcommand)
        {
            case "create":
                return await CreateAsync(args, configPath);

            case "list":
                return await ListAsync(args, configPath);

            case "revoke":
                return await RevokeAsync(args, configPath);

            default:
                WriteUsage();
                return subcommand is null ? 1 : 2;
        }
    }

    private static async Task<int> CreateAsync(string[] args, string? configPath)
    {
        string? name = ValueOf(args, "--name");
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("gatehouse keys create requires --name.");
            return 2;
        }

        int? expiresInDays = null;
        if (ValueOf(args, "--expires-in-days") is { } raw)
        {
            if (!int.TryParse(raw, out int days) || days <= 0)
            {
                Console.Error.WriteLine("--expires-in-days must be a positive whole number of days.");
                return 2;
            }

            expiresInDays = days;
        }

        await using StoreContext store = await StoreContext.OpenAsync(configPath);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        VirtualKeySecret.GeneratedSecret generated = VirtualKeySecret.Generate();

        var key = new VirtualKey
        {
            Id = $"vk_{Guid.NewGuid():N}"[..19],
            Name = name,
            SecretHash = generated.Hash,
            SecretPrefix = generated.DisplayPrefix,
            Organisation = ValueOf(args, "--org"),
            Team = ValueOf(args, "--team"),
            Application = ValueOf(args, "--app"),
            CreatedAt = now,
            ExpiresAt = expiresInDays is { } d ? now.AddDays(d) : null,
        };

        await store.Keys.AddAsync(key);

        // Printed once and never recoverable: only the hash is stored. Saying so plainly here
        // is the difference between an operator copying it now and opening a support issue
        // tomorrow.
        Console.WriteLine();
        Console.WriteLine($"Key created: {key.Id}  ({key.Name})");
        Console.WriteLine();
        Console.WriteLine(generated.Secret);
        Console.WriteLine();
        Console.WriteLine("This is the only time the secret is shown. Gatehouse stores only a hash");
        Console.WriteLine("of it and cannot recover it. Copy it now; if you lose it, revoke this key");
        Console.WriteLine("and create another.");

        if (key.ExpiresAt is { } expires)
        {
            Console.WriteLine();
            Console.WriteLine($"Expires: {expires:u}");
        }

        return 0;
    }

    private static async Task<int> ListAsync(string[] args, string? configPath)
    {
        bool includeRevoked = args.Contains("--include-revoked", StringComparer.Ordinal);

        await using StoreContext store = await StoreContext.OpenAsync(configPath);
        IReadOnlyList<VirtualKey> keys = await store.Keys.ListAsync(includeRevoked);

        if (keys.Count == 0)
        {
            Console.WriteLine("No keys. Create one with: gatehouse keys create --name my-app");
            return 0;
        }

        Console.WriteLine($"{"ID",-20} {"NAME",-24} {"PREFIX",-10} {"OWNER",-28} STATUS");

        foreach (VirtualKey key in keys)
        {
            string owner = string.Join(
                '/',
                new[] { key.Organisation, key.Team, key.Application }.Where(p => !string.IsNullOrEmpty(p)));

            string status = key.RevokedAt is { } revoked
                ? $"revoked {revoked:yyyy-MM-dd}"
                : key.ExpiresAt is { } expires
                    ? expires <= DateTimeOffset.UtcNow ? $"expired {expires:yyyy-MM-dd}" : $"expires {expires:yyyy-MM-dd}"
                    : "active";

            Console.WriteLine(
                $"{Truncate(key.Id, 20),-20} {Truncate(key.Name, 24),-24} "
                + $"{Truncate(key.SecretPrefix, 10),-10} {Truncate(owner, 28),-28} {status}");
        }

        return 0;
    }

    private static async Task<int> RevokeAsync(string[] args, string? configPath)
    {
        string? id = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : null;

        if (string.IsNullOrWhiteSpace(id))
        {
            Console.Error.WriteLine("gatehouse keys revoke requires a key ID. List them with: gatehouse keys list");
            return 2;
        }

        await using StoreContext store = await StoreContext.OpenAsync(configPath);

        if (await store.Keys.RevokeAsync(id, DateTimeOffset.UtcNow))
        {
            Console.WriteLine($"Revoked {id}. It stops working immediately; the record is kept for audit.");
            return 0;
        }

        // Distinguished from success so a typo does not read as a completed revocation — the
        // one mistake here that leaves an operator believing a live key is dead.
        Console.Error.WriteLine($"No live key with ID '{id}'. It may not exist, or may already be revoked.");
        return 1;
    }

    private static void WriteUsage() =>
        Console.WriteLine(
            """
            gatehouse keys — manage virtual keys

            Usage:
              gatehouse keys create --name <name> [options]
              gatehouse keys list [--include-revoked]
              gatehouse keys revoke <key-id>

            Create options:
              --name <name>             Required. A recognisable name, e.g. checkout-service-prod.
              --org <organisation>      Chargeback hierarchy: organisation.
              --team <team>             Chargeback hierarchy: team.
              --app <application>       Chargeback hierarchy: application.
              --expires-in-days <n>     Expire the key after n days. Omit for no expiry.

            Common options:
              --config <path>           The same configuration file the server uses, so the key
                                        lands in the store the server reads.

            The secret is shown once, at creation. Only its hash is stored.
            """);

    private static string? ValueOf(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static string Truncate(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";

    /// <summary>
    /// Opens the key store the way the server would, including migrating the schema.
    /// </summary>
    /// <remarks>
    /// The command line has to migrate too. Otherwise <c>keys create</c> on a fresh deployment
    /// fails against a database with no tables, and the operator is told to start the server
    /// first — which will not start, because it has no keys.
    /// </remarks>
    private sealed class StoreContext : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private StoreContext(SqliteConnection connection, IVirtualKeyStore keys)
        {
            _connection = connection;
            Keys = keys;
        }

        public IVirtualKeyStore Keys { get; }

        public static async Task<StoreContext> OpenAsync(string? configPath)
        {
            GatehouseOptions options = LoadOptions(configPath);

            var connection = new SqliteConnection(options.Store.ConnectionString);
            await connection.OpenAsync();
            await SqliteSchema.ApplyPragmasAsync(connection);
            await SqliteSchema.MigrateAsync(connection);

            return new StoreContext(connection, new SqliteVirtualKeyStore(Options.Create(options)));
        }

        private static GatehouseOptions LoadOptions(string? configPath)
        {
            var configuration = new ConfigurationBuilder();

            if (!string.IsNullOrWhiteSpace(configPath))
            {
                configuration.AddJsonFile(configPath, optional: false);
            }

            // Environment variables as well, so a containerised deployment that configures the
            // store that way does not need a config file just to issue a key.
            configuration.AddEnvironmentVariables();

            return configuration.Build()
                       .GetSection(GatehouseOptions.SectionName)
                       .Get<GatehouseOptions>()
                   ?? new GatehouseOptions();
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }
}
