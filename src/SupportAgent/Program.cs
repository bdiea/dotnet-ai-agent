using AgentKit;
using SupportAgent;

var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("Set ANTHROPIC_API_KEY to run the demo.");
    return 1;
}

var data = new ShopData();
var agent = new Agent(
    new AnthropicClient(new HttpClient(), apiKey, Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-sonnet-4-5"),
    new ITool[] { new SearchKnowledgeBaseTool(), new GetOrderTool(data), new IssueRefundTool(data) },
    new ConsoleApprovalGate(),
    new AgentOptions
    {
        SystemPrompt =
            "You are a customer support agent for an online shop. Be concise and friendly. " +
            "Always check the order and the refund policy before refunding. " +
            "Never promise anything the tools did not confirm.",
        MaxSteps = 8
    });

Console.WriteLine("Support agent ready. Try: \"Can I get a refund for order A1001? It arrived damaged.\" (empty line to exit)");
while (true)
{
    Console.Write("\nYou: ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input)) break;

    var result = await agent.RunAsync(input);
    Console.WriteLine($"\nAgent: {result.Answer}");
    Console.ForegroundColor = ConsoleColor.DarkGray;
    foreach (var entry in result.Audit) Console.WriteLine($"  [{entry.Kind}] {entry.Detail}");
    Console.ResetColor();
}
return 0;
