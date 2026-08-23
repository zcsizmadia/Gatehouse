using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gatehouse.Wire;

/// <summary>
/// Source-generated serialization metadata for the Gatehouse wire contract.
/// </summary>
/// <remarks>
/// <para>
/// Every JSON operation in Gatehouse goes through this context. Reflection-based
/// serialization is not merely discouraged, it is a build error: shipping projects compile
/// with <c>IL2026</c> and <c>IL3050</c> promoted to errors, so a stray
/// <c>JsonSerializer.Serialize(value)</c> overload fails the build rather than failing at
/// runtime in the NativeAOT binary that nobody tested.
/// </para>
/// <para>
/// The practical consequence for contributors: when you add a type to the wire contract,
/// add a <see cref="JsonSerializableAttribute"/> for it here. The compiler will tell you if
/// you forget.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(ChatCompletionChunk))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ChatChoice))]
[JsonSerializable(typeof(ChatChunkChoice))]
[JsonSerializable(typeof(ChatDelta))]
[JsonSerializable(typeof(TokenUsage))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ErrorDetail))]
[JsonSerializable(typeof(ModelListResponse))]
[JsonSerializable(typeof(ModelDescriptor))]
[JsonSerializable(typeof(IReadOnlyList<ChatMessage>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
public sealed partial class GatehouseJsonContext : JsonSerializerContext
{
    /// <summary>
    /// The options every Gatehouse component serializes with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unknown request members are ignored rather than rejected, which is the default and
    /// the behaviour we want: providers add fields to the OpenAI request surface faster
    /// than any gateway ships releases, and refusing a request because it carried a member
    /// Gatehouse has not heard of would break callers for no benefit. The provider layer
    /// forwards what it does not interpret.
    /// </para>
    /// <para>
    /// Expression-bodied rather than a field initializer: the generated <c>Default</c>
    /// context is itself a static of this same type, and reading it from a static field
    /// initializer would depend on initialization order the compiler cannot guarantee.
    /// </para>
    /// </remarks>
    public static JsonSerializerOptions WireOptions => Default.Options;
}
