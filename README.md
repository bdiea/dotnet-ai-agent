# .NET AI Agent with Tool Calling & Human Approval

[![CI](../../actions/workflows/ci.yml/badge.svg)](../../actions/workflows/ci.yml)

A small, dependency-free **AI agent framework for .NET 8** built on Claude's native tool use, plus a
**customer-support agent** demo that looks up orders, searches help articles and issues refunds,
with a **human approval step** before any action that moves money.

The goal is to show how to build agents that are useful *and* safe to run in a business:
bounded loops, explicit tools, approvals, error handling and a full audit trail.

## Demo

```
You: Can I get a refund for order A1001? It arrived damaged.

  [tool] get_order {"order_id":"A1001"} -> {"Id":"A1001","Status":"Delivered","Total":89.90,...}
  [tool] search_knowledge_base {"query":"refund policy"} -> [refund policy] Refunds are allowed within 30 days...
⚠ Agent wants to run issue_refund {"order_id":"A1001","reason":"Arrived damaged"}. Approve? [y/N] y
  [approved] issue_refund {"order_id":"A1001","reason":"Arrived damaged"}
  [tool] issue_refund -> Refund of 89.90 issued for order A1001.

Agent: I'm sorry your order arrived damaged. I've refunded 89.90 to your original payment
method; it should appear within 5 business days.
```

## How it works

```
             ┌───────────────────────── Agent loop (max N steps) ─────────────────────────┐
 request ──▶ │ Claude ──tool_use──▶ approval gate? ──▶ run tool (timeout) ──tool_result──┐ │
             │   ▲                                                                       │ │
             │   └───────────────────────────────────────────────────────────────────────┘ │
             └──────────────────────────── final answer + audit log ──────────────────────┘
```

| Piece | File | Notes |
|---|---|---|
| Agent loop | [`Agent.cs`](src/AgentKit/Agent.cs) | Step limit, per-tool timeout, errors fed back to the model, audit log |
| Tools | [`Tools.cs`](src/AgentKit/Tools.cs) | `ITool` with JSON Schema input and `RequiresApproval` flag |
| LLM client | [`AnthropicClient.cs`](src/AgentKit/AnthropicClient.cs) | Messages API with native tool use, no SDK needed |
| Demo tools | [`SupportTools.cs`](src/SupportAgent/SupportTools.cs) | `search_knowledge_base`, `get_order`, `issue_refund` (needs approval) |

### Guardrails

- **Human-in-the-loop** – tools that change data (`RequiresApproval = true`) only run after a person approves; a denial is reported back so the model can explain instead of retrying
- **Bounded** – the loop stops after `MaxSteps` and hands over to a human
- **Fail safe** – unknown tools, invalid input and exceptions become tool errors the model can react to, never crashes
- **Timeouts** – each tool call has its own timeout
- **Auditability** – every user message, tool call, approval and answer is logged with a timestamp
- **Least privilege** – the model can only call the tools you register

## Run the demo

```bash
export ANTHROPIC_API_KEY=your-key
dotnet run --project src/SupportAgent
```

## Add your own tool

```csharp
public sealed class CreateTicketTool(ITicketService tickets) : Tool("""
    {"type":"object","properties":{"title":{"type":"string"},"priority":{"type":"string","enum":["low","high"]}},
     "required":["title"]}
    """)
{
    public override string Name => "create_ticket";
    public override string Description => "Create a support ticket for issues the agent cannot solve.";
    public override async Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default) =>
        $"Created ticket {await tickets.CreateAsync(Str(input, "title"), ct)}";
}
```

## Tests

```bash
dotnet test
```

The agent is tested with a scripted fake model, so tests are fast, free and deterministic:
tool round-trips, approval granted/denied, unknown tools, invalid input, step limit, and request/response mapping.

## Ideas to extend

- Persist conversations and audit logs (SQL / Cosmos DB)
- Approval via Microsoft Teams or Slack instead of the console
- Expose the agent as an ASP.NET Core API or MCP server
- Add evaluation scenarios to measure answer quality over time

## License

MIT
