using System.Text.Json;
using AgentKit;

namespace SupportAgent;

/// <summary>Fake back-office data so the demo runs without a database.</summary>
public sealed class ShopData
{
    public record Order(string Id, string Customer, string Status, decimal Total, DateOnly OrderedOn);

    public Dictionary<string, Order> Orders { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A1001"] = new("A1001", "sara@example.com", "Delivered", 89.90m, new DateOnly(2026, 10, 2)),
        ["A1002"] = new("A1002", "omar@example.com", "Shipped", 45.00m, new DateOnly(2026, 10, 15)),
        ["A1003"] = new("A1003", "lina@example.com", "Processing", 120.50m, new DateOnly(2026, 10, 19)),
    };

    public List<string> Refunds { get; } = new();

    public static readonly Dictionary<string, string> KnowledgeBase = new()
    {
        ["refund policy"] = "Refunds are allowed within 30 days of delivery for unused items. Refunds go back to the original payment method within 5 business days.",
        ["shipping"] = "Standard shipping takes 3-5 business days. Express takes 1-2 business days.",
        ["opening hours"] = "Support is available Sunday to Thursday, 9:00-17:00 (GMT+3).",
    };
}

public sealed class SearchKnowledgeBaseTool() : Tool("""
    {"type":"object","properties":{"query":{"type":"string","description":"What to look up, e.g. 'refund policy'"}},"required":["query"]}
    """)
{
    public override string Name => "search_knowledge_base";
    public override string Description => "Search the company's help articles (policies, shipping, opening hours).";

    public override Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var words = Str(input, "query").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = ShopData.KnowledgeBase
            .Where(kb => words.Any(w => kb.Key.Contains(w) || kb.Value.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Select(kb => $"[{kb.Key}] {kb.Value}")
            .ToList();
        return Task.FromResult(hits.Count > 0 ? string.Join("\n", hits) : "No matching articles.");
    }
}

public sealed class GetOrderTool(ShopData data) : Tool("""
    {"type":"object","properties":{"order_id":{"type":"string","description":"Order number, e.g. A1001"}},"required":["order_id"]}
    """)
{
    public override string Name => "get_order";
    public override string Description => "Look up an order's status, total and order date by order number.";

    public override Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var id = Str(input, "order_id");
        return Task.FromResult(data.Orders.TryGetValue(id, out var o)
            ? JsonSerializer.Serialize(o)
            : $"Order {id} not found.");
    }
}

/// <summary>Moves money, so it requires human approval.</summary>
public sealed class IssueRefundTool(ShopData data) : Tool("""
    {"type":"object","properties":{
      "order_id":{"type":"string"},
      "reason":{"type":"string","description":"Short reason for the refund"}},
     "required":["order_id","reason"]}
    """)
{
    public override string Name => "issue_refund";
    public override string Description => "Refund an order in full. Only use after checking the order and the refund policy.";
    public override bool RequiresApproval => true;

    public override Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var id = Str(input, "order_id");
        if (!data.Orders.TryGetValue(id, out var order)) return Task.FromResult($"Order {id} not found.");
        if (data.Refunds.Contains(order.Id)) return Task.FromResult($"Order {id} was already refunded.");
        data.Refunds.Add(order.Id);
        return Task.FromResult($"Refund of {order.Total:0.00} issued for order {order.Id}.");
    }
}

/// <summary>Asks the operator in the console before sensitive actions.</summary>
public sealed class ConsoleApprovalGate : IApprovalGate
{
    public Task<bool> ApproveAsync(string toolName, JsonElement input, CancellationToken ct = default)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"\n⚠ Agent wants to run {toolName} {input.GetRawText()}. Approve? [y/N] ");
        Console.ResetColor();
        return Task.FromResult(string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase));
    }
}
