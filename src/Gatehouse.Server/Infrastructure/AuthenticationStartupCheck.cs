using Gatehouse.Configuration;
using Gatehouse.Security;
using Microsoft.Extensions.Options;

namespace Gatehouse.Server.Infrastructure;

/// <summary>
/// Refuses to start a gateway that requires authentication but has no key that could satisfy it.
/// </summary>
/// <remarks>
/// <para>
/// The alternative is a gateway that starts cleanly, reports healthy, and returns 401 to every
/// request. An orchestrator sees a healthy instance and completes the rollout; the failure is
/// discovered by users, everywhere at once. Phase 0 established that a configuration mistake
/// should fail the rollout rather than the audit, and this is the same rule applied to
/// credentials.
/// </para>
/// <para>
/// Registered after the store's own hosted service so the schema exists by the time this runs.
/// Hosted services start in registration order, which is the only ordering guarantee available
/// and is sufficient here.
/// </para>
/// </remarks>
internal sealed class AuthenticationStartupCheck : IHostedService
{
    private readonly IVirtualKeyStore _store;
    private readonly AuthenticationMode _mode;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuthenticationStartupCheck> _logger;

    /// <summary>Creates the check.</summary>
    public AuthenticationStartupCheck(
        IVirtualKeyStore store,
        IOptions<GatehouseOptions> options,
        TimeProvider timeProvider,
        ILogger<AuthenticationStartupCheck> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _mode = options.Value.Authentication.Mode;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_mode == AuthenticationMode.Disabled)
        {
            // Warned on every startup rather than once. An unauthenticated gateway holding
            // provider credentials is worth being reminded about, and a one-off warning is a
            // warning nobody sees.
            _logger.AuthenticationDisabled();
            return;
        }

        int usable = await _store.CountUsableAsync(_timeProvider.GetUtcNow(), cancellationToken);

        if (usable == 0)
        {
            throw new InvalidOperationException(
                "Authentication is required but no usable virtual key exists, so every request "
                + "would be rejected. Issue one with:"
                + Environment.NewLine
                + Environment.NewLine
                + "    gatehouse keys create --name my-app"
                + Environment.NewLine
                + Environment.NewLine
                + "For local development without authentication, set "
                + "Gatehouse:Authentication:Mode to Disabled — but note that the gateway holds "
                + "provider credentials, so do not do that where it is reachable by anything "
                + "you do not trust.");
        }

        _logger.AuthenticationEnabled(usable);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
