using System.Text.Json.Serialization;

namespace Gatehouse.Wire;

/// <summary>
/// An OpenAI-compatible response for <c>/v1/models</c>.
/// </summary>
/// <remarks>
/// Worth implementing early despite looking trivial: it is how most client tooling discovers
/// what it is allowed to call, and how an operator confirms that a configuration change
/// actually took effect. In Phase 2 the same endpoint becomes the enforcement point for
/// model allowlists, returning only the models the calling key is permitted to use rather
/// than everything the gateway knows about.
/// </remarks>
public sealed class ModelListResponse
{
    /// <summary>Always <c>list</c>, for wire compatibility.</summary>
    [JsonPropertyName("object")]
    public string ObjectType { get; init; } = "list";

    /// <summary>The available models.</summary>
    [JsonPropertyName("data")]
    public required IReadOnlyList<ModelDescriptor> Data { get; init; }
}

/// <summary>One model entry in a <see cref="ModelListResponse"/>.</summary>
public sealed class ModelDescriptor
{
    /// <summary>The alias callers put in the <c>model</c> request field.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Always <c>model</c>, for wire compatibility.</summary>
    [JsonPropertyName("object")]
    public string ObjectType { get; init; } = "model";

    /// <summary>Creation time as Unix seconds. Gatehouse reports gateway start time.</summary>
    [JsonPropertyName("created")]
    public required long Created { get; init; }

    /// <summary>
    /// The owner. Always <c>gatehouse</c>: from the caller's perspective this gateway owns
    /// the alias, and reporting the upstream vendor here would leak routing decisions that
    /// operators are free to change.
    /// </summary>
    [JsonPropertyName("owned_by")]
    public string OwnedBy { get; init; } = "gatehouse";
}
