using System.Text.Json;
using AgentKit;
using SupportAgent;
using Xunit;

namespace AgentKit.Tests;

/// <summary>Plays back pre-scripted model turns and records what the agent sent.</summary>
internal sealed class ScriptedLlm(params LlmResponse[] turns) : ILlmClient
{
    private readonly Queue<LlmResponse> _turns = new(turns);
    public List<IReadOnlyList<Message>> Calls { get; } = new();

    public Task<LlmResponse> SendAsync(string systemPrompt, IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> tools, CancellationToken ct = default)
    {
        Calls.Add(messages.ToList());
        return Task.FromResult(_turns.Count > 0 ? _turns.Dequeue() : Answer("done"));
    }

    public static LlmResponse Answer(string text) => new(new ContentBlock[] { new TextBlock(text) }, "end_turn");

    public static LlmResponse Call(string id, string tool, string inputJson) =>
        new(new ContentBlock[] { new ToolUseBlock(id, tool, JsonDocument.Parse(inputJson).RootElement.Clone()) }, "tool_use");
}

internal sealed class FixedApproval(bool approve) : IApprovalGate
{
    public int Asked { get; private set; }
    public Task<bool> ApproveAsync(string toolName, JsonElement input, CancellationToken ct = default)
    {
        Asked++;
        return Task.FromResult(approve);
    }
}

public class AgentTests
{
    private readonly ShopData _data = new();
    private ITool[] Tools => new ITool[] { new SearchKnowledgeBaseTool(), new GetOrderTool(_data), new IssueRefundTool(_data) };

    [Fact]
    public async Task Answers_directly_when_no_tool_is_needed()
    {
        var agent = new Agent(new ScriptedLlm(ScriptedLlm.Answer("Hello!")), Tools);

        var result = await agent.RunAsync("Hi");

        Assert.Equal("Hello!", result.Answer);
        Assert.Equal(1, result.Steps);
    }

    [Fact]
    public async Task Runs_tool_and_feeds_result_back_to_the_model()
    {
        var llm = new ScriptedLlm(
            ScriptedLlm.Call("t1", "get_order", """{"order_id":"A1002"}"""),
            ScriptedLlm.Answer("Your order has shipped."));
        var agent = new Agent(llm, Tools);

        var result = await agent.RunAsync("Where is order A1002?");

        Assert.Equal("Your order has shipped.", result.Answer);
        var lastMessages = llm.Calls[1];
        var toolResult = Assert.IsType<ToolResultBlock>(lastMessages[^1].Content[0]);
        Assert.Equal("t1", toolResult.ToolUseId);
        Assert.Contains("Shipped", toolResult.Content);
        Assert.False(toolResult.IsError);
    }

    [Fact]
    public async Task Sensitive_tool_runs_only_after_approval()
    {
        var approval = new FixedApproval(approve: true);
        var agent = new Agent(new ScriptedLlm(
            ScriptedLlm.Call("t1", "issue_refund", """{"order_id":"A1001","reason":"damaged"}"""),
            ScriptedLlm.Answer("Refund issued.")), Tools, approval);

        await agent.RunAsync("Refund A1001");

        Assert.Equal(1, approval.Asked);
        Assert.Contains("A1001", _data.Refunds);
    }

    [Fact]
    public async Task Denied_action_is_not_executed_and_model_is_told()
    {
        var llm = new ScriptedLlm(
            ScriptedLlm.Call("t1", "issue_refund", """{"order_id":"A1001","reason":"damaged"}"""),
            ScriptedLlm.Answer("A colleague will review your refund."));
        var agent = new Agent(llm, Tools, new FixedApproval(approve: false));

        var result = await agent.RunAsync("Refund A1001");

        Assert.Empty(_data.Refunds);
        var toolResult = Assert.IsType<ToolResultBlock>(llm.Calls[1][^1].Content[0]);
        Assert.True(toolResult.IsError);
        Assert.Contains("DENIED", toolResult.Content);
        Assert.Contains(result.Audit, a => a.Kind == "denied");
    }

    [Fact]
    public async Task Unknown_tool_and_bad_input_are_reported_as_errors_not_exceptions()
    {
        var llm = new ScriptedLlm(
            ScriptedLlm.Call("t1", "delete_database", "{}"),
            ScriptedLlm.Call("t2", "get_order", "{}"),
            ScriptedLlm.Answer("Sorry, I couldn't do that."));
        var agent = new Agent(llm, Tools);

        var result = await agent.RunAsync("Do something weird");

        Assert.Equal(3, result.Steps);
        Assert.Equal(2, result.Audit.Count(a => a.Kind == "tool-error"));
    }

    [Fact]
    public async Task Stops_at_step_limit()
    {
        var looping = Enumerable.Range(0, 10)
            .Select(i => ScriptedLlm.Call($"t{i}", "search_knowledge_base", """{"query":"shipping"}"""))
            .ToArray();
        var agent = new Agent(new ScriptedLlm(looping), Tools, options: new AgentOptions { MaxSteps = 3 });

        var result = await agent.RunAsync("Loop forever");

        Assert.True(result.StoppedByLimit);
        Assert.Equal(3, result.Steps);
    }
}

public class AnthropicClientTests
{
    [Fact]
    public void Builds_request_with_tools_and_tool_results()
    {
        var schema = JsonDocument.Parse("""{"type":"object"}""").RootElement;
        var messages = new List<Message>
        {
            Message.User("Where is A1?"),
            new("assistant", new ContentBlock[] { new ToolUseBlock("t1", "get_order", JsonDocument.Parse("""{"order_id":"A1"}""").RootElement) }),
            new("user", new ContentBlock[] { new ToolResultBlock("t1", "Shipped") })
        };

        var json = AnthropicClient.BuildRequest("model-x", 512, "sys", messages,
            new[] { new ToolDefinition("get_order", "Look up order", schema) }).ToJsonString();

        Assert.Contains("\"input_schema\":{\"type\":\"object\"}", json);
        Assert.Contains("\"type\":\"tool_use\"", json);
        Assert.Contains("\"tool_use_id\":\"t1\"", json);
        Assert.Contains("\"system\":\"sys\"", json);
    }

    [Fact]
    public void Parses_tool_use_response()
    {
        const string body = """
            {"content":[{"type":"text","text":"Let me check."},
                        {"type":"tool_use","id":"toolu_1","name":"get_order","input":{"order_id":"A1"}}],
             "stop_reason":"tool_use"}
            """;

        var response = AnthropicClient.ParseResponse(body);

        Assert.True(response.WantsTools);
        Assert.Equal("Let me check.", response.Text);
        var call = Assert.IsType<ToolUseBlock>(response.Content[1]);
        Assert.Equal("A1", call.Input.GetProperty("order_id").GetString());
    }
}
