using System.Diagnostics;
using System.Text.Json;

namespace AgentKit;

public sealed record AuditEntry(DateTimeOffset At, string Kind, string Detail);

public sealed record AgentResult(string Answer, int Steps, IReadOnlyList<AuditEntry> Audit, bool StoppedByLimit);

public sealed class AgentOptions
{
    public string SystemPrompt { get; init; } = "You are a helpful assistant. Use tools when needed.";
    public int MaxSteps { get; init; } = 8;
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The agent loop: ask the model, run the tools it requests, feed results back,
/// repeat until it answers or the step limit is reached.
/// Guardrails: step limit, per-tool timeout, approval gate, errors returned to the model, full audit log.
/// </summary>
public sealed class Agent
{
    private readonly ILlmClient _llm;
    private readonly IReadOnlyDictionary<string, ITool> _tools;
    private readonly IApprovalGate _approvals;
    private readonly AgentOptions _options;

    public Agent(ILlmClient llm, IEnumerable<ITool> tools, IApprovalGate? approvals = null, AgentOptions? options = null)
    {
        _llm = llm;
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _approvals = approvals ?? new AlwaysDenyApprovalGate();
        _options = options ?? new AgentOptions();
    }

    public async Task<AgentResult> RunAsync(string userRequest, CancellationToken ct = default)
    {
        var history = new List<Message> { Message.User(userRequest) };
        var audit = new List<AuditEntry>();
        var definitions = _tools.Values.Select(t => new ToolDefinition(t.Name, t.Description, t.InputSchema)).ToList();

        void Log(string kind, string detail) => audit.Add(new AuditEntry(DateTimeOffset.UtcNow, kind, detail));
        Log("user", userRequest);

        for (var step = 1; step <= _options.MaxSteps; step++)
        {
            var response = await _llm.SendAsync(_options.SystemPrompt, history, definitions, ct);
            history.Add(new Message("assistant", response.Content));

            if (!response.WantsTools)
            {
                Log("answer", response.Text);
                return new AgentResult(response.Text, step, audit, StoppedByLimit: false);
            }

            var results = new List<ContentBlock>();
            foreach (var call in response.Content.OfType<ToolUseBlock>())
            {
                results.Add(await RunToolAsync(call, Log, ct));
            }
            history.Add(new Message("user", results));
        }

        Log("limit", $"Stopped after {_options.MaxSteps} steps");
        return new AgentResult(
            "I couldn't finish this request within the allowed number of steps. A human should take a look.",
            _options.MaxSteps, audit, StoppedByLimit: true);
    }

    private async Task<ToolResultBlock> RunToolAsync(ToolUseBlock call, Action<string, string> log, CancellationToken ct)
    {
        var input = call.Input.GetRawText();
        if (!_tools.TryGetValue(call.Name, out var tool))
        {
            log("tool-error", $"{call.Name}: unknown tool");
            return new ToolResultBlock(call.Id, $"Error: tool '{call.Name}' does not exist.", IsError: true);
        }

        if (tool.RequiresApproval)
        {
            var approved = await _approvals.ApproveAsync(tool.Name, call.Input, ct);
            log(approved ? "approved" : "denied", $"{tool.Name} {input}");
            if (!approved)
                return new ToolResultBlock(call.Id,
                    "A human reviewer DENIED this action. Do not retry it; explain the situation to the user.",
                    IsError: true);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.ToolTimeout);
            var output = await tool.ExecuteAsync(call.Input, timeout.Token);
            log("tool", $"{tool.Name} {input} -> {Truncate(output)} ({sw.ElapsedMilliseconds} ms)");
            return new ToolResultBlock(call.Id, output);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log("tool-error", $"{tool.Name} {input} -> {ex.GetType().Name}: {ex.Message}");
            return new ToolResultBlock(call.Id, $"Error: {ex.Message}", IsError: true);
        }
    }

    private static string Truncate(string s, int max = 200) => s.Length <= max ? s : s[..max] + "…";
}
