using System.Text.Json.Nodes;

namespace TheDojo.Tests.Fixtures;

/// <summary>Writes Copilot CLI <c>events.jsonl</c> logs in the CLI's own envelope (<c>type</c>, <c>data</c>, <c>id</c>, <c>timestamp</c>).</summary>
public sealed class CopilotLog(string sessionId)
{
    private readonly List<string> _lines = [];
    private int _id;

    public string SessionId => sessionId;

    public IReadOnlyList<string> Lines => _lines;

    public CopilotLog Event(string type, DateTimeOffset at, JsonObject data)
    {
        _lines.Add(new JsonObject { ["type"] = type, ["data"] = data, ["id"] = $"e{++_id}", ["timestamp"] = at.ToString("O"), ["parentId"] = null }.ToJsonString());
        return this;
    }

    public CopilotLog Start(DateTimeOffset at, string model = "claude-sonnet-4.6", string cwd = @"E:\src\api", string repository = "acme/api", string branch = "main") =>
        Event("session.start", at, new JsonObject
        {
            ["sessionId"] = sessionId,
            ["copilotVersion"] = "1.0.89",
            ["selectedModel"] = model,
            ["context"] = new JsonObject { ["cwd"] = cwd, ["repository"] = repository, ["branch"] = branch },
        });

    public CopilotLog Prompt(DateTimeOffset at, string text, string? source = null, string messageId = "m1") =>
        Event("user.message", at, new JsonObject { ["content"] = text, ["messageId"] = messageId, ["interactionId"] = "i-" + messageId, ["source"] = source, ["agentMode"] = "interactive" });

    public CopilotLog Turn(DateTimeOffset start, DateTimeOffset end, string turnId, string interactionId) =>
        Event("assistant.turn_start", start, new JsonObject { ["turnId"] = turnId, ["interactionId"] = interactionId })
            .Event("assistant.turn_end", end, new JsonObject { ["turnId"] = turnId });

    public CopilotLog ModelCall(DateTimeOffset at, string callId, string model, long input, long cacheRead, long cacheWrite, long output, long nanoAiu, double? premiumLeft = null)
    {
        var data = new JsonObject
        {
            ["callId"] = callId,
            ["modelCallDurationMs"] = 1800,
            ["ttftMs"] = 600,
            ["modelCall"] = new JsonObject { ["model"] = model, ["initiator"] = "user", ["request_id"] = "r-" + callId },
            ["copilotUsage"] = new JsonObject
            {
                ["token_details"] = new JsonArray(
                    new JsonObject { ["token_type"] = "input", ["token_count"] = input },
                    new JsonObject { ["token_type"] = "cache_read", ["token_count"] = cacheRead },
                    new JsonObject { ["token_type"] = "cache_write", ["token_count"] = cacheWrite },
                    new JsonObject { ["token_type"] = "output", ["token_count"] = output }),
                ["total_nano_aiu"] = nanoAiu,
            },
            ["reasoningEffort"] = "medium",
        };
        if (premiumLeft is { } left)
        {
            data["quotaSnapshots"] = new JsonObject
            {
                ["premium_interactions"] = new JsonObject { ["entitlementRequests"] = 300, ["remainingPercentage"] = left, ["isUnlimitedEntitlement"] = false, ["resetDate"] = "2026-11-01T00:00:00Z" },
                ["chat"] = new JsonObject { ["entitlementRequests"] = 0, ["remainingPercentage"] = 0 },
            };
        }

        return Event("model.model_call_success", at, data);
    }

    public CopilotLog Tool(DateTimeOffset start, DateTimeOffset end, string id, string name, JsonObject args, bool success = true, string? errorCode = null, int linesAdded = 0, int linesRemoved = 0, string? parent = null) =>
        Event("tool.execution_start", start, new JsonObject { ["toolCallId"] = id, ["toolName"] = name, ["arguments"] = args, ["parentToolCallId"] = parent })
            .Event("tool.execution_complete", end, new JsonObject
            {
                ["toolCallId"] = id,
                ["success"] = success,
                ["error"] = errorCode is null ? null : new JsonObject { ["message"] = "failed: " + errorCode, ["code"] = errorCode },
                ["toolTelemetry"] = new JsonObject { ["metrics"] = new JsonObject { ["linesAdded"] = linesAdded, ["linesRemoved"] = linesRemoved } },
                ["parentToolCallId"] = parent,
            });

    public CopilotLog Checkpoint(DateTimeOffset at, long totalNanoAiu, string? model = null, string? callId = null, long prompt = 0, long cacheRead = 0) =>
        Event("session.usage_checkpoint", at, new JsonObject
        {
            ["totalNanoAiu"] = totalNanoAiu,
            ["promptCacheBreakState"] = model is null
                ? new JsonArray()
                : new JsonArray(new JsonObject { ["models"] = new JsonObject { [model] = new JsonObject { ["model_call_id"] = callId, ["prompt_tokens"] = prompt, ["cache_read"] = cacheRead, ["cache_write"] = 0 } } }),
        });

    public CopilotLog Shutdown(DateTimeOffset at, long totalNanoAiu, string model, long input, long cacheRead, long output, int linesAdded = 0, int linesRemoved = 0) =>
        Event("session.shutdown", at, new JsonObject
        {
            ["totalNanoAiu"] = totalNanoAiu,
            ["codeChanges"] = new JsonObject { ["linesAdded"] = linesAdded, ["linesRemoved"] = linesRemoved },
            ["modelMetrics"] = new JsonObject
            {
                [model] = new JsonObject
                {
                    ["totalNanoAiu"] = totalNanoAiu,
                    ["tokenDetails"] = new JsonObject
                    {
                        ["input"] = new JsonObject { ["tokenCount"] = input },
                        ["cache_read"] = new JsonObject { ["tokenCount"] = cacheRead },
                        ["cache_write"] = new JsonObject { ["tokenCount"] = 0 },
                        ["output"] = new JsonObject { ["tokenCount"] = output },
                    },
                },
            },
        });

    public CopilotLog Abort(DateTimeOffset at) => Event("abort", at, new JsonObject { ["reason"] = "user_initiated" });

    public CopilotLog Error(DateTimeOffset at, string type = "model") => Event("session.error", at, new JsonObject { ["errorType"] = type, ["message"] = "The model is unavailable." });

    public CopilotLog Compaction(DateTimeOffset start, DateTimeOffset end, long pre, long post) =>
        Event("session.compaction_start", start, new JsonObject { ["trigger"] = "auto" })
            .Event("session.compaction_complete", end, new JsonObject { ["success"] = true, ["preCompactionTokens"] = pre, ["postCompactionTokens"] = post, ["trigger"] = "auto" });

    public string WriteTo(string sessionStateRoot, string? workspaceName = null)
    {
        var folder = Path.Combine(sessionStateRoot, sessionId);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "events.jsonl");
        File.WriteAllLines(path, _lines);
        if (workspaceName is not null)
        {
            File.WriteAllLines(Path.Combine(folder, "workspace.yaml"), [$"id: {sessionId}", "cwd: E:\\src\\api", $"name: {workspaceName}", "summary_count: 0"]);
        }

        return path;
    }
}
