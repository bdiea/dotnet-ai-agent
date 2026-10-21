using System.Text.Json;

namespace AgentKit;

/// <summary>A capability the agent can call. Keep tools small and well described.</summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }

    /// <summary>JSON Schema for the tool input.</summary>
    JsonElement InputSchema { get; }

    /// <summary>Actions with side effects (payments, emails, deletes) should require a human.</summary>
    bool RequiresApproval { get; }

    Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default);
}

/// <summary>Convenience base class: define the schema as a JSON string.</summary>
public abstract class Tool : ITool
{
    private readonly JsonElement _schema;

    protected Tool(string schemaJson) => _schema = JsonDocument.Parse(schemaJson).RootElement.Clone();

    public abstract string Name { get; }
    public abstract string Description { get; }
    public JsonElement InputSchema => _schema;
    public virtual bool RequiresApproval => false;
    public abstract Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default);

    protected static string Str(JsonElement input, string property) =>
        input.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new ArgumentException($"Missing required string '{property}'.");
}

/// <summary>Human-in-the-loop gate for sensitive tools.</summary>
public interface IApprovalGate
{
    Task<bool> ApproveAsync(string toolName, JsonElement input, CancellationToken ct = default);
}

public sealed class AlwaysDenyApprovalGate : IApprovalGate
{
    public Task<bool> ApproveAsync(string toolName, JsonElement input, CancellationToken ct = default) =>
        Task.FromResult(false);
}
