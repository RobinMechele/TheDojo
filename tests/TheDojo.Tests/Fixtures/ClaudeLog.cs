using System.Text.Json.Nodes;

namespace TheDojo.Tests.Fixtures;

/// <summary>
/// Writes Claude Code transcripts line by line in the shape Claude Code itself writes them (one line per content
/// block, usage repeated on each), so parser tests read realistic input without real sessions.
/// </summary>
public sealed class ClaudeLog(string sessionId, string cwd = @"E:\src\shop", string branch = "main", string version = "2.1.289")
{
    private readonly List<string> _lines = [];
    private int _uuid;

    public string SessionId => sessionId;

    public IReadOnlyList<string> Lines => _lines;

    private JsonObject Envelope(string type, DateTimeOffset at, bool sidechain = false, string? agentId = null)
    {
        var o = new JsonObject
        {
            ["parentUuid"] = null,
            ["isSidechain"] = sidechain,
            ["type"] = type,
            ["uuid"] = $"{sessionId}-u{++_uuid}",
            ["timestamp"] = at.ToString("O"),
            ["userType"] = "external",
            ["entrypoint"] = "cli",
            ["cwd"] = cwd,
            ["sessionId"] = sessionId,
            ["version"] = version,
            ["gitBranch"] = branch,
        };
        if (agentId is not null)
        {
            o["agentId"] = agentId;
        }

        return o;
    }

    public ClaudeLog Raw(string line)
    {
        _lines.Add(line);
        return this;
    }

    public ClaudeLog Prompt(DateTimeOffset at, string text, string permissionMode = "default", bool automated = false)
    {
        var o = Envelope("user", at);
        o["message"] = new JsonObject { ["role"] = "user", ["content"] = text };
        o["permissionMode"] = permissionMode;
        o["promptSource"] = "typed";
        o["origin"] = new JsonObject { ["kind"] = automated ? "task-notification" : "human" };
        _lines.Add(o.ToJsonString());
        return this;
    }

    public ClaudeLog Command(DateTimeOffset at, string command, string args = "")
    {
        var o = Envelope("user", at);
        o["message"] = new JsonObject { ["role"] = "user", ["content"] = $"<command-name>{command}</command-name>\n<command-message>{command.TrimStart('/')}</command-message>\n<command-args>{args}</command-args>" };
        _lines.Add(o.ToJsonString());
        return this;
    }

    /// <summary>
    /// One reply: a line per content block (text, then each tool call), every one repeating the usage, the way
    /// Claude Code writes them.
    /// </summary>
    public ClaudeLog Reply(
        DateTimeOffset at,
        string model,
        string messageId,
        long input = 10,
        long output = 100,
        long cacheRead = 0,
        long write5m = 0,
        long write1h = 0,
        long thinking = 0,
        string? text = "Done.",
        (string Id, string Name, JsonObject Input)[]? tools = null,
        string? agentId = null,
        string? agentType = null,
        string effort = "medium",
        string speed = "standard",
        string? stopReason = null)
    {
        var blocks = new List<JsonObject>();
        if (text is not null)
        {
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }

        foreach (var (id, name, args) in tools ?? [])
        {
            blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = args.DeepClone() });
        }

        stopReason ??= tools is { Length: > 0 } ? "tool_use" : "end_turn";
        for (var i = 0; i < blocks.Count; i++)
        {
            var o = Envelope("assistant", at, agentId is not null, agentId);
            o["requestId"] = "req_" + messageId;
            o["effort"] = effort;
            if (agentType is not null)
            {
                o["attributionAgent"] = agentType;
            }

            o["message"] = new JsonObject
            {
                ["model"] = model,
                ["id"] = messageId,
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = new JsonArray(blocks[i].DeepClone()),
                ["stop_reason"] = i == blocks.Count - 1 ? stopReason : null,
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = input,
                    ["cache_creation_input_tokens"] = write5m + write1h,
                    ["cache_read_input_tokens"] = cacheRead,
                    ["output_tokens"] = output,
                    ["output_tokens_details"] = new JsonObject { ["thinking_tokens"] = thinking },
                    ["server_tool_use"] = new JsonObject { ["web_search_requests"] = 0, ["web_fetch_requests"] = 0 },
                    ["service_tier"] = "standard",
                    ["cache_creation"] = new JsonObject { ["ephemeral_1h_input_tokens"] = write1h, ["ephemeral_5m_input_tokens"] = write5m },
                    ["speed"] = speed,
                },
            };
            _lines.Add(o.ToJsonString());
        }

        return this;
    }

    public ClaudeLog ToolResult(DateTimeOffset at, string toolUseId, string content = "ok", bool isError = false, JsonObject? toolUseResult = null, string? denialKind = null, string? agentId = null)
    {
        var o = Envelope("user", at, agentId is not null, agentId);
        o["message"] = new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["tool_use_id"] = toolUseId, ["type"] = "tool_result", ["content"] = content, ["is_error"] = isError }),
        };
        if (toolUseResult is not null)
        {
            o["toolUseResult"] = toolUseResult;
        }

        if (denialKind is not null)
        {
            o["toolDenialKind"] = denialKind;
        }

        _lines.Add(o.ToJsonString());
        return this;
    }

    public ClaudeLog Interrupt(DateTimeOffset at)
    {
        var o = Envelope("user", at);
        o["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "[Request interrupted by user]" }) };
        _lines.Add(o.ToJsonString());
        return this;
    }

    public ClaudeLog TurnDuration(DateTimeOffset at, long ms, int messages)
    {
        var o = Envelope("system", at);
        o["subtype"] = "turn_duration";
        o["durationMs"] = ms;
        o["messageCount"] = messages;
        _lines.Add(o.ToJsonString());
        return this;
    }

    public ClaudeLog Compaction(DateTimeOffset at, long pre, long post, bool manual = false)
    {
        var o = Envelope("system", at);
        o["subtype"] = "compact_boundary";
        o["compactMetadata"] = new JsonObject { ["trigger"] = manual ? "manual" : "auto", ["preTokens"] = pre, ["postTokens"] = post, ["durationMs"] = 30_000 };
        _lines.Add(o.ToJsonString());
        return this;
    }

    public ClaudeLog ApiError(DateTimeOffset at, string code = "ECONNRESET", int attempt = 1)
    {
        var o = Envelope("system", at);
        o["subtype"] = "api_error";
        o["level"] = "error";
        o["error"] = new JsonObject { ["message"] = "Connection error.", ["formatted"] = "Unable to connect to API (" + code + ")", ["connection"] = new JsonObject { ["code"] = code } };
        o["retryAttempt"] = attempt;
        o["maxRetries"] = 10;
        _lines.Add(o.ToJsonString());
        return this;
    }

    public ClaudeLog UsageCommand(DateTimeOffset at, double session, double weekly, DateTimeOffset sessionResets, DateTimeOffset weeklyResets)
    {
        var o = Envelope("system", at);
        o["subtype"] = "local_command";
        o["content"] = "<local-command-stdout>usage</local-command-stdout>";
        o["usageReport"] = new JsonObject
        {
            ["rate_limits"] = new JsonObject
            {
                ["limits"] = new JsonArray(
                    new JsonObject { ["kind"] = "session", ["percent"] = session, ["resets_at"] = sessionResets.ToString("O") },
                    new JsonObject { ["kind"] = "weekly_all", ["percent"] = weekly, ["resets_at"] = weeklyResets.ToString("O") }),
            },
        };
        _lines.Add(o.ToJsonString());
        return this;
    }

    public ClaudeLog Title(string title, bool custom = false)
    {
        _lines.Add(new JsonObject { ["type"] = custom ? "custom-title" : "ai-title", [custom ? "customTitle" : "aiTitle"] = title, ["sessionId"] = sessionId }.ToJsonString());
        return this;
    }

    public ClaudeLog PullRequest(DateTimeOffset at, string url)
    {
        _lines.Add(new JsonObject { ["type"] = "pr-link", ["sessionId"] = sessionId, ["prNumber"] = 1, ["prUrl"] = url, ["prRepository"] = "o/r", ["timestamp"] = at.ToString("O") }.ToJsonString());
        return this;
    }

    public ClaudeLog CostState(double usd, int linesAdded = 0, int linesRemoved = 0)
    {
        _lines.Add(new JsonObject { ["type"] = "cost-state", ["sessionId"] = sessionId, ["totalCostUSD"] = usd, ["totalLinesAdded"] = linesAdded, ["totalLinesRemoved"] = linesRemoved }.ToJsonString());
        return this;
    }

    /// <summary>The <c>toolUseResult</c> Claude Code writes for an edit: a patch with +/- lines.</summary>
    public static JsonObject Patch(params string[] lines) => new()
    {
        ["filePath"] = "x.cs",
        ["structuredPatch"] = new JsonArray(new JsonObject { ["oldStart"] = 1, ["lines"] = new JsonArray([.. lines.Select(l => (JsonNode?)l)]) }),
    };

    /// <summary>Writes the transcript where Claude Code would: <c>projects/&lt;encoded cwd&gt;/&lt;session&gt;.jsonl</c>.</summary>
    public string WriteTo(string projectsRoot, string? subagentOf = null)
    {
        var folder = Path.Combine(projectsRoot, cwd.Replace(':', '-').Replace('\\', '-').Replace('/', '-').Replace(' ', '-').Replace('.', '-'));
        var path = subagentOf is null
            ? Path.Combine(folder, sessionId + ".jsonl")
            : Path.Combine(folder, subagentOf, "subagents", "agent-" + sessionId + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, _lines);
        return path;
    }
}

public static class Tool
{
    public static (string Id, string Name, JsonObject Input) Call(string id, string name, JsonObject input) => (id, name, input);

    public static (string Id, string Name, JsonObject Input) Read(string id, string path) => (id, "Read", new JsonObject { ["file_path"] = path });

    public static (string Id, string Name, JsonObject Input) Bash(string id, string command) => (id, "Bash", new JsonObject { ["command"] = command, ["description"] = "run" });

    public static (string Id, string Name, JsonObject Input) Edit(string id, string path) => (id, "Edit", new JsonObject { ["file_path"] = path, ["old_string"] = "a", ["new_string"] = "b" });

    public static (string Id, string Name, JsonObject Input) Agent(string id, string type) => (id, "Agent", new JsonObject { ["subagent_type"] = type, ["description"] = "look", ["prompt"] = "find it" });
}
