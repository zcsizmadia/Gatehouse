namespace Gatehouse.Wire;

/// <summary>
/// The role values defined by the OpenAI chat completion wire format.
/// </summary>
public static class ChatRoles
{
    /// <summary>Instructions that frame the conversation.</summary>
    public const string System = "system";

    /// <summary>Input from the end user.</summary>
    public const string User = "user";

    /// <summary>Output produced by the model.</summary>
    public const string Assistant = "assistant";

    /// <summary>The result of a tool invocation being returned to the model.</summary>
    public const string Tool = "tool";
}
