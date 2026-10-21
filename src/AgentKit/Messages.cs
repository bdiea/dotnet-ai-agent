using System.Text.Json;

namespace AgentKit;

/// <summary>Conversation content blocks (mirrors the Anthropic Messages API shape).</summary>
public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

public sealed record ToolUseBlock(string Id, string Name, JsonElement Input) : ContentBlock;

public sealed record ToolResultBlock(string ToolUseId, string Content, bool IsError = false) : ContentBlock;

public sealed record Message(string Role, IReadOnlyList<ContentBlock> Content)
{
    public static Message User(string text) => new("user", new ContentBlock[] { new TextBlock(text) });
}

/// <summary>What the model returned for one turn.</summary>
public sealed record LlmResponse(IReadOnlyList<ContentBlock> Content, string StopReason)
{
    public bool WantsTools => StopReason == "tool_use" && Content.OfType<ToolUseBlock>().Any();

    public string Text => string.Concat(Content.OfType<TextBlock>().Select(t => t.Text));
}

public sealed record ToolDefinition(string Name, string Description, JsonElement InputSchema);

public interface ILlmClient
{
    Task<LlmResponse> SendAsync(
        string systemPrompt,
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct = default);
}
