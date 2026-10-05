using System.Globalization;
using System.Text.Json;
using TheDojo.Core.Model;

namespace TheDojo.Core.Ingest;

/// <summary>
/// Reads a Claude Code transcript (<c>~/.claude/projects/&lt;encoded-cwd&gt;/&lt;session&gt;.jsonl</c>, and the
/// subagent transcripts under <c>&lt;session&gt;/subagents/</c>) into a <see cref="SessionLog"/>.
/// </summary>
/// <remarks>
/// Claude Code writes one line per content block of a reply and each repeats the reply's usage, so a reply is
/// keyed by message id + request id and counted once. Tool calls are matched to their results by tool-use id,
/// which gives each call its outcome, duration and (for edits) the lines it changed.
/// </remarks>
public static class ClaudeTranscriptParser
{
    private const int PreviewLength = 200;

    public static SessionLog Parse(string path)
    {
        var fallbackSession = Path.GetFileNameWithoutExtension(path);
        return ParseLines(Json.ReadLines(path), fallbackSession);
    }

    public static SessionLog ParseLines(IEnumerable<string> lines, string fallbackSession = "")
    {
        var reader = new Reader(fallbackSession);
        foreach (var line in lines)
        {
            using var doc = Json.TryParse(line);
            if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                reader.Read(doc.RootElement);
            }
        }

        return reader.Finish();
    }

    private sealed class CallDraft
    {
        public required string Id;
        public required string SessionId;
        public required string Model;
        public DateTimeOffset Timestamp;
        public TokenCounts Tokens;
        public string? StopReason;
        public string? Effort;
        public string? Speed;
        public int WebSearches;
        public int WebFetches;
        public long? ThinkingMs;
        public string? AgentId;
        public string? AgentType;
        public string? Skill;
        public string? McpServer;
        public readonly List<string> Tools = [];
    }

    private sealed record ToolDraft(string Id, string SessionId, DateTimeOffset Timestamp, string Name, string? Target, string? McpServer, string? AgentId);

    private sealed class Reader(string fallbackSession)
    {
        private readonly SessionLogBuilder _log = new();
        private readonly Dictionary<string, CallDraft> _calls = new(StringComparer.Ordinal);
        private readonly List<CallDraft> _callOrder = [];
        private readonly Dictionary<string, ToolDraft> _pendingTools = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _aiTitles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _customTitles = new(StringComparer.Ordinal);
        private int _anonymous;

        public void Read(JsonElement root)
        {
            var type = Json.Str(root, "type");
            var sessionId = Json.Str(root, "sessionId") ?? fallbackSession;
            var timestamp = Json.Time(root, "timestamp");

            if (Json.Str(root, "cwd") is { } cwd && !Json.Bool(root, "isSidechain"))
            {
                NoteContext(sessionId, root, cwd);
            }

            switch (type)
            {
                case "assistant" when timestamp is { } at:
                    ReadAssistant(root, sessionId, at);
                    break;
                case "user" when timestamp is { } at:
                    ReadUser(root, sessionId, at);
                    break;
                case "system" when timestamp is { } at:
                    ReadSystem(root, sessionId, at);
                    break;
                case "cost-state":
                    ReadCostState(root, sessionId);
                    break;
                case "ai-title" when Json.Str(root, "aiTitle") is { Length: > 0 } title:
                    _aiTitles[sessionId] = title;
                    break;
                case "custom-title" when Json.Str(root, "customTitle") is { Length: > 0 } title:
                    _customTitles[sessionId] = title;
                    break;
                case "pr-link" when Json.Str(root, "prUrl") is { Length: > 0 } url:
                    _log.Update(Provider.Claude, sessionId, s => s with { PullRequests = [.. (s.PullRequests ?? []).Append(url).Distinct()] });
                    break;
                case "permission-mode" when Json.Str(root, "permissionMode") is { Length: > 0 } mode:
                    _log.Update(Provider.Claude, sessionId, s => s with { PermissionMode = mode });
                    break;
                case "continued-in" when Json.Str(root, "continuedInSessionId") is { Length: > 0 } next:
                    _log.Update(Provider.Claude, sessionId, s => s with { ContinuedIn = next });
                    break;
            }
        }

        private void NoteContext(string sessionId, JsonElement root, string cwd)
        {
            var session = _log.Session(Provider.Claude, sessionId);
            if (session.Cwd is not null)
            {
                return;
            }

            var branch = Json.Str(root, "gitBranch");
            _log.Sessions[sessionId] = session with
            {
                Cwd = cwd,
                Branch = branch is null or "HEAD" or "" ? null : branch,
                ClientVersion = Json.Str(root, "version"),
                Client = Json.Str(root, "entrypoint"),
            };
        }

        private void ReadAssistant(JsonElement root, string sessionId, DateTimeOffset at)
        {
            if (Json.Obj(root, "message") is not { } message)
            {
                return;
            }

            var model = Json.Str(message, "model");
            if (Json.Bool(root, "isApiErrorMessage"))
            {
                _log.Errors.Add(new ErrorEvent(Provider.Claude, at, sessionId, EventId(root), ErrorKind.Api,
                    Json.Str(root, "error") ?? "api_error", Json.Preview(Json.Text(Json.Prop(message, "content")), PreviewLength)));
                return;
            }

            if (model is null or "<synthetic>" || Json.Obj(message, "usage") is not { } usage)
            {
                return;
            }

            var messageId = Json.Str(message, "id");
            var requestId = Json.Str(root, "requestId");
            var key = messageId is null && requestId is null ? EventId(root) : $"{messageId}:{requestId}";

            if (!_calls.TryGetValue(key, out var call))
            {
                call = new CallDraft { Id = key, SessionId = sessionId, Model = model, Timestamp = at };
                _calls[key] = call;
                _callOrder.Add(call);
            }

            // Every content-block line repeats the reply's usage; the most complete one (most output) wins.
            var tokens = ReadTokens(usage);
            if (tokens.Output >= call.Tokens.Output)
            {
                call.Tokens = tokens;
            }

            call.StopReason = Json.Str(message, "stop_reason") ?? call.StopReason;
            call.Effort ??= Json.Str(root, "effort");
            call.Speed ??= Json.Str(usage, "speed");
            if (Json.Obj(usage, "server_tool_use") is { } server)
            {
                call.WebSearches = Math.Max(call.WebSearches, (int)Json.Count(server, "web_search_requests"));
                call.WebFetches = Math.Max(call.WebFetches, (int)Json.Count(server, "web_fetch_requests"));
            }

            call.ThinkingMs ??= Json.OptionalCount(root, "thinkingDurationMs");
            call.AgentId ??= Json.Str(root, "agentId") is { Length: > 0 } agent && Json.Bool(root, "isSidechain") ? agent : null;
            call.AgentType ??= Json.Str(root, "attributionAgent");
            call.Skill ??= Json.Str(root, "attributionSkill");
            call.McpServer ??= Json.Str(root, "attributionMcpServer");

            if (Json.Arr(message, "content") is { } content)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (Json.Str(block, "type") != "tool_use" || Json.Str(block, "id") is not { } toolId || Json.Str(block, "name") is not { } name)
                    {
                        continue;
                    }

                    if (!call.Tools.Contains(name))
                    {
                        call.Tools.Add(name);
                    }

                    _pendingTools.TryAdd(toolId, new ToolDraft(toolId, sessionId, at, name, ToolTargets.Of(name, Json.Prop(block, "input")),
                        ToolTargets.McpServer(name), call.AgentId));
                }
            }
        }

        private static TokenCounts ReadTokens(JsonElement usage)
        {
            long write5m, write1h;
            if (Json.Obj(usage, "cache_creation") is { } tiers)
            {
                write5m = Json.Count(tiers, "ephemeral_5m_input_tokens");
                write1h = Json.Count(tiers, "ephemeral_1h_input_tokens");
                var total = Json.Count(usage, "cache_creation_input_tokens");
                if (write5m + write1h < total)
                {
                    write5m += total - write5m - write1h;
                }
            }
            else
            {
                write5m = Json.Count(usage, "cache_creation_input_tokens");
                write1h = 0;
            }

            var reasoning = Json.Obj(usage, "output_tokens_details") is { } details ? Json.Count(details, "thinking_tokens") : 0;
            return new TokenCounts(
                Json.Count(usage, "input_tokens"),
                Json.Count(usage, "output_tokens"),
                Json.Count(usage, "cache_read_input_tokens"),
                write5m,
                write1h,
                reasoning);
        }

        private void ReadUser(JsonElement root, string sessionId, DateTimeOffset at)
        {
            if (Json.Obj(root, "message") is not { } message)
            {
                return;
            }

            var content = Json.Prop(message, "content");
            var toolResults = content is { ValueKind: JsonValueKind.Array } blocks
                ? blocks.EnumerateArray().Where(b => Json.Str(b, "type") == "tool_result").ToList()
                : [];

            foreach (var result in toolResults)
            {
                FinishTool(root, result, toolResults.Count == 1, at);
            }

            if (toolResults.Count > 0)
            {
                return;
            }

            var text = Json.Text(content);
            if (text.StartsWith("[Request interrupted by user", StringComparison.Ordinal))
            {
                _log.Errors.Add(new ErrorEvent(Provider.Claude, at, sessionId, EventId(root), ErrorKind.Interrupt, "user_interrupt"));
                return;
            }

            var isSubagent = Json.Bool(root, "isSidechain") || Json.Str(root, "agentId") is not null;
            if (isSubagent || Json.Bool(root, "isMeta") || Json.Bool(root, "isCompactSummary") || Json.Bool(root, "isVisibleInTranscriptOnly")
                || text.Length == 0
                || text.StartsWith("<local-command-", StringComparison.Ordinal)
                || text.StartsWith("Caveat: The messages below", StringComparison.Ordinal))
            {
                return;
            }

            var images = content is { ValueKind: JsonValueKind.Array } parts
                ? parts.EnumerateArray().Count(b => Json.Str(b, "type") is "image" or "document")
                : 0;

            var kind = PromptKind.Typed;
            string? command = null;
            var shown = text;
            if (Between(text, "<command-name>", "</command-name>") is { } name)
            {
                kind = PromptKind.Command;
                command = name.StartsWith('/') ? name : "/" + name;
                shown = (command + " " + (Between(text, "<command-args>", "</command-args>") ?? string.Empty)).Trim();
            }
            else if (IsAutomated(root, text))
            {
                kind = PromptKind.Automated;
            }

            _log.Prompts.Add(new PromptEvent(Provider.Claude, at, sessionId, EventId(root), kind, shown.Length,
                Json.Preview(shown, PreviewLength), command, Json.Str(root, "permissionMode"), images));
        }

        private static bool IsAutomated(JsonElement root, string text)
        {
            if (Json.Obj(root, "origin") is { } origin && Json.Str(origin, "kind") is { } kind && kind != "human")
            {
                return true;
            }

            return text.StartsWith("<task-notification>", StringComparison.Ordinal)
                || text.StartsWith("<scheduled-task", StringComparison.Ordinal)
                || Json.Str(root, "scheduledTaskId") is not null;
        }

        private void FinishTool(JsonElement root, JsonElement result, bool ownsToolUseResult, DateTimeOffset at)
        {
            if (Json.Str(result, "tool_use_id") is not { } toolId || !_pendingTools.Remove(toolId, out var draft))
            {
                return;
            }

            var isError = Json.Bool(result, "is_error");
            var detail = ownsToolUseResult ? Json.Prop(root, "toolUseResult") : null;
            var text = Json.Text(Json.Prop(result, "content"));
            var denial = Json.Str(root, "toolDenialKind");

            ToolOutcome outcome;
            if (denial is not null)
            {
                outcome = ToolOutcome.Denied;
            }
            else if (detail is { ValueKind: JsonValueKind.Object } d && Json.Bool(d, "interrupted"))
            {
                outcome = ToolOutcome.Interrupted;
            }
            else if (!isError)
            {
                outcome = ToolOutcome.Success;
            }
            else if (text.StartsWith("The user doesn't want to proceed", StringComparison.Ordinal) || text.Contains("was rejected", StringComparison.OrdinalIgnoreCase))
            {
                outcome = ToolOutcome.Denied;
                denial = "user-rejected";
            }
            else if (text.Contains("[Request interrupted by user", StringComparison.Ordinal))
            {
                outcome = ToolOutcome.Interrupted;
            }
            else
            {
                outcome = ToolOutcome.Error;
            }

            var (added, removed) = detail is { ValueKind: JsonValueKind.Object } changes ? CountLines(changes) : (0, 0);
            var duration = (long)(at - draft.Timestamp).TotalMilliseconds;
            _log.Tools.Add(new ToolCall(Provider.Claude, draft.Timestamp, draft.SessionId, draft.Id, draft.Name, outcome, draft.Target, draft.McpServer,
                denial, outcome == ToolOutcome.Error ? Json.Preview(text, PreviewLength) : null, duration >= 0 ? duration : null, added, removed, draft.AgentId));
        }

        /// <summary>Lines an Edit/Write changed: the +/- lines of its patch, or every line of a newly created file.</summary>
        private static (int Added, int Removed) CountLines(JsonElement detail)
        {
            int added = 0, removed = 0;
            if (Json.Arr(detail, "structuredPatch") is { } hunks)
            {
                foreach (var hunk in hunks.EnumerateArray())
                {
                    if (Json.Arr(hunk, "lines") is not { } lines)
                    {
                        continue;
                    }

                    foreach (var line in lines.EnumerateArray())
                    {
                        var s = line.ValueKind == JsonValueKind.String ? line.GetString() : null;
                        if (s is { Length: > 0 })
                        {
                            if (s[0] == '+')
                            {
                                added++;
                            }
                            else if (s[0] == '-')
                            {
                                removed++;
                            }
                        }
                    }
                }
            }

            if (added == 0 && removed == 0 && Json.Str(detail, "type") == "create" && Json.Str(detail, "content") is { Length: > 0 } created)
            {
                added = created.Count(c => c == '\n') + (created.EndsWith('\n') ? 0 : 1);
            }

            return (added, removed);
        }

        private void ReadSystem(JsonElement root, string sessionId, DateTimeOffset at)
        {
            switch (Json.Str(root, "subtype"))
            {
                case "turn_duration":
                    _log.Turns.Add(new TurnEvent(Provider.Claude, at, sessionId, EventId(root), Json.Count(root, "durationMs"), (int)Json.Count(root, "messageCount")));
                    break;

                case "compact_boundary":
                    var meta = Json.Obj(root, "compactMetadata");
                    _log.Compactions.Add(new CompactionEvent(Provider.Claude, at, sessionId, EventId(root),
                        meta is { } m && Json.Str(m, "trigger") == "manual",
                        meta is { } pre ? Json.Count(pre, "preTokens") : 0,
                        meta is { } post ? Json.Count(post, "postTokens") : 0,
                        meta is { } dur ? Json.OptionalCount(dur, "durationMs") : null));
                    break;

                case "api_error":
                    var error = Json.Obj(root, "error");
                    var code = error is { } e
                        ? (Json.Obj(e, "connection") is { } connection ? Json.Str(connection, "code") : null)
                          ?? (Json.Num(e, "status") is { } status ? ((int)status).ToString(CultureInfo.InvariantCulture) : null)
                          ?? Json.Str(e, "type")
                        : null;
                    _log.Errors.Add(new ErrorEvent(Provider.Claude, at, sessionId, EventId(root), ErrorKind.Api, code ?? "api_error",
                        error is { } msg ? Json.Str(msg, "formatted") ?? Json.Str(msg, "message") : null, (int)Json.Count(root, "retryAttempt")));
                    break;

                case "stop_hook_summary" when Json.Arr(root, "hookErrors") is { } hookErrors:
                    var index = 0;
                    foreach (var hookError in hookErrors.EnumerateArray())
                    {
                        var message = hookError.ValueKind == JsonValueKind.String ? hookError.GetString() : Json.Str(hookError, "message") ?? hookError.GetRawText();
                        _log.Errors.Add(new ErrorEvent(Provider.Claude, at, sessionId, EventId(root) + ":" + index++, ErrorKind.Hook, "stop_hook", Json.Preview(message ?? string.Empty, PreviewLength)));
                    }

                    break;

                case "local_command" when Json.Obj(root, "usageReport") is { } report:
                    ReadLimits(report, at);
                    break;
            }
        }

        /// <summary>Claude Code's <c>/usage</c> report: a free reading of the subscription limits at that moment.</summary>
        private void ReadLimits(JsonElement report, DateTimeOffset at)
        {
            if (Json.Obj(report, "rate_limits") is not { } rateLimits || Json.Arr(rateLimits, "limits") is not { } limits)
            {
                return;
            }

            foreach (var limit in limits.EnumerateArray())
            {
                if (Json.Num(limit, "percent") is not { } percent || Json.Str(limit, "kind") is not { } kind)
                {
                    continue;
                }

                _log.Limits.Add(new LimitReading(Provider.Claude, at, WindowName(kind), percent, Json.Time(limit, "resets_at")));
            }
        }

        internal static string WindowName(string kind) => kind switch
        {
            "session" or "five_hour" => "Session",
            "weekly_all" or "seven_day" => "Weekly",
            "weekly_opus" or "seven_day_opus" => "Weekly Opus",
            "weekly_sonnet" or "seven_day_sonnet" => "Weekly Sonnet",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(kind.Replace('_', ' ')),
        };

        private void ReadCostState(JsonElement root, string sessionId)
        {
            var cost = Json.Num(root, "totalCostUSD");
            var added = Json.OptionalCount(root, "totalLinesAdded");
            var removed = Json.OptionalCount(root, "totalLinesRemoved");
            _log.Update(Provider.Claude, sessionId, s => s with
            {
                ReportedCostUsd = cost ?? s.ReportedCostUsd,
                LinesAdded = added is { } a ? (int)a : s.LinesAdded,
                LinesRemoved = removed is { } r ? (int)r : s.LinesRemoved,
            });
        }

        private string EventId(JsonElement root) => Json.Str(root, "uuid") ?? $"{fallbackSession}#{_anonymous++}";

        private static string? Between(string text, string start, string end)
        {
            var from = text.IndexOf(start, StringComparison.Ordinal);
            if (from < 0)
            {
                return null;
            }

            from += start.Length;
            var to = text.IndexOf(end, from, StringComparison.Ordinal);
            return to < 0 ? null : text[from..to].Trim();
        }

        public SessionLog Finish()
        {
            foreach (var c in _callOrder)
            {
                _log.Calls.Add(new ApiCall(Provider.Claude, c.Timestamp, c.SessionId, c.Id, c.Model, c.Tokens,
                    StopReason: c.StopReason, Effort: c.Effort, Speed: c.Speed, WebSearches: c.WebSearches, WebFetches: c.WebFetches,
                    ThinkingMs: c.ThinkingMs, AgentId: c.AgentId, AgentType: c.AgentType, Skill: c.Skill, McpServer: c.McpServer,
                    Tools: c.Tools.Count > 0 ? [.. c.Tools] : null));
            }

            // A tool whose result never arrived: the session ended or crashed while it ran.
            foreach (var t in _pendingTools.Values)
            {
                _log.Tools.Add(new ToolCall(Provider.Claude, t.Timestamp, t.SessionId, t.Id, t.Name, ToolOutcome.Unknown, t.Target, t.McpServer, AgentId: t.AgentId));
            }

            foreach (var id in _aiTitles.Keys.Union(_customTitles.Keys))
            {
                var title = _customTitles.GetValueOrDefault(id) ?? _aiTitles[id];
                _log.Update(Provider.Claude, id, s => s with { Title = title });
            }

            return _log.Build();
        }
    }
}
