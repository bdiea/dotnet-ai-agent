using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentKit;

/// <summary>Claude via the Anthropic Messages API with native tool use.</summary>
public sealed class AnthropicClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _maxTokens;

    public AnthropicClient(HttpClient http, string apiKey, string model = "claude-sonnet-4-5", int maxTokens = 1024)
    {
        _http = http;
        _model = model;
        _maxTokens = maxTokens;
        _http.BaseAddress ??= new Uri("https://api.anthropic.com/");
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public async Task<LlmResponse> SendAsync(string systemPrompt, IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> tools, CancellationToken ct = default)
    {
        var request = BuildRequest(_model, _maxTokens, systemPrompt, messages, tools);
        using var content = new StringContent(request.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("v1/messages", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Anthropic API {(int)response.StatusCode}: {body}");
        return ParseResponse(body);
    }

    internal static JsonObject BuildRequest(string model, int maxTokens, string system,
        IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools)
    {
        var msgs = new JsonArray();
        foreach (var m in messages)
        {
            var blocks = new JsonArray();
            foreach (var b in m.Content) blocks.Add(ToJson(b));
            msgs.Add(new JsonObject { ["role"] = m.Role, ["content"] = blocks });
        }

        var toolArray = new JsonArray();
        foreach (var t in tools)
        {
            toolArray.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = JsonNode.Parse(t.InputSchema.GetRawText())
            });
        }

        var request = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["system"] = system,
            ["messages"] = msgs
        };
        if (toolArray.Count > 0) request["tools"] = toolArray;
        return request;
    }

    private static JsonObject ToJson(ContentBlock block) => block switch
    {
        TextBlock t => new JsonObject { ["type"] = "text", ["text"] = t.Text },
        ToolUseBlock u => new JsonObject
        {
            ["type"] = "tool_use", ["id"] = u.Id, ["name"] = u.Name, ["input"] = JsonNode.Parse(u.Input.GetRawText())
        },
        ToolResultBlock r => new JsonObject
        {
            ["type"] = "tool_result", ["tool_use_id"] = r.ToolUseId, ["content"] = r.Content, ["is_error"] = r.IsError
        },
        _ => throw new NotSupportedException(block.GetType().Name)
    };

    internal static LlmResponse ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var blocks = new List<ContentBlock>();
        foreach (var c in root.GetProperty("content").EnumerateArray())
        {
            switch (c.GetProperty("type").GetString())
            {
                case "text":
                    blocks.Add(new TextBlock(c.GetProperty("text").GetString() ?? ""));
                    break;
                case "tool_use":
                    blocks.Add(new ToolUseBlock(
                        c.GetProperty("id").GetString()!,
                        c.GetProperty("name").GetString()!,
                        c.GetProperty("input").Clone()));
                    break;
            }
        }
        var stop = root.TryGetProperty("stop_reason", out var s) ? s.GetString() ?? "" : "";
        return new LlmResponse(blocks, stop);
    }
}
