namespace Gatehouse.Resilience;

/// <summary>
/// The state of one upstream's circuit.
/// </summary>
public enum CircuitState
{
    /// <summary>Calls pass through. The normal state.</summary>
    Closed = 0,

    /// <summary>
    /// Calls are rejected without being attempted, because the upstream has been failing.
    /// </summary>
    Open = 1,

    /// <summary>
    /// One probe call is allowed through to find out whether the upstream has recovered.
    /// </summary>
    /// <remarks>
    /// Exactly one, and not one per caller. A gateway that lets every waiting request probe
    /// simultaneously reproduces the thundering herd that broke the upstream in the first
    /// place, at the precise moment it is least able to absorb it.
    /// </remarks>
    HalfOpen = 2,
}
