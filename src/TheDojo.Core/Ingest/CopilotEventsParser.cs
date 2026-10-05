using System.Text.Json;
using TheDojo.Core.Model;

namespace TheDojo.Core.Ingest;

/// <summary>
/// Reads a GitHub Copilot CLI session log (<c>~/.copilot/session-state/&lt;session&gt;/events.jsonl</c>) into a
/// <see cref="SessionLog"/>.
/// </summary>
/// <remarks>
/// Newer CLI versions log every model request (<c>model.model_call_success</c>) with exact tokens and AI-credit
/// cost; those become one <see cref="ApiCall"/> each. Older sessions only log running totals
/// (<c>session.usage_checkpoint</c>) and a per-model summary on a clean exit (<c>session.shutdown</c>); for those
/// the growth between readings becomes aggregate records, attributed to the model that really answered (even
/// when the session was set to "auto").
/// </remarks>
public static class CopilotEventsParser
{
    /// <summary>1 AI credit = 1e9 nano-AIU = US$0.01.</summary>
    public const double NanoAiuPerUsd = 100_000_000_000.0;

    private const string UnknownModel = "copilot";
    private const int PreviewLength = 200;

    public static SessionLog Parse(string eventsPath)
    {
        var folder = Path.GetDirectoryName(eventsPath) ?? string.Empty;
        var sessionId = Path.GetFileName(folder);
        var log = ParseLines(Json.ReadLines(eventsPath), sessionId);
        return ReadWorkspace(Path.Combine(folder, "workspace.yaml")) is { } title
            ? log with { Sessions = [.. log.Sessions.Select(s => s.Title is null ? s with { Title = title } : s)] }
            : log;
    }

    public static SessionLog ParseLines(IEnumerable<string> lines, string sessionId)
    {
        var reader = new Reader(sessionId);
        foreach (var line in lines)
        {
            // Most of a log is huge message snapshots this never needs.
            if (line.Contains("\"type\":\"model.messages_snapshot\"", StringComparison.Ordinal)
                || line.Contains("\"type\":\"system.message\"", StringComparison.Ordinal)
                || line.Contains("\"type\":\"model.message\"", StringComparison.Ordinal)
                || line.Contains("\"type\":\"model.response\"", StringComparison.Ordinal)
                || line.Contains("\"type\":\"session.binary_asset\"", StringComparison.Ordinal))
            {
                continue;
            }

            using var doc = Json.TryParse(line);
            if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                reader.Read(doc.RootElement);
            }
        }

        return reader.Finish();
    }

    /// <summary>The session's name or summary from its <c>workspace.yaml</c> (flat <c>key: value</c> lines).</summary>
    internal static string? ReadWorkspace(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string? name = null, summary = null;
            foreach (var line in File.ReadLines(path))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0 || char.IsWhiteSpace(line[0]))
                {
                    continue;
                }

                var key = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim().Trim('"', '\'');
                if (value.Length == 0 || value is "|" or ">" or "null")
                {
                    continue;
                }

                if (key == "name")
                {
                    name = value;
                }
                else if (key == "summary")
                {
                    summary = value;
                }
            }

            return name ?? summary;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record ModelMetric(long Input, long Output, long CacheRead, long CacheWrite, long Reasoning, long Nano);

    private sealed record CheckpointCall(string Id, long Prompt, long CacheRead, long CacheWrite);

    private sealed record Checkpoint(DateTimeOffset At, long Nano, Dictionary<string, CheckpointCall> Calls);

    private sealed record Shutdown(DateTimeOffset At, Dictionary<string, ModelMetric> Models, long Nano);

    private sealed record ToolDraft(string Id, DateTimeOffset At, string Name, string? Target, string? McpServer, string? AgentId);

    private sealed class Interaction
    {
        public DateTimeOffset Start;
        public DateTimeOffset End;
        public int Turns;
    }

    private sealed class Reader(string sessionId)
    {
        private readonly SessionLogBuilder _log = new();
        private readonly List<ApiCall> _perCall = [];
        private readonly List<Checkpoint> _checkpoints = [];
        private readonly List<Shutdown> _shutdowns = [];
        private readonly List<(DateTimeOffset At, string Id, long Tokens, string? Model)> _outputs = [];
        private readonly Dictionary<string, ToolDraft> _tools = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Interaction> _interactions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _turnInteraction = new(StringComparer.Ordinal);
        private DateTimeOffset? _compactionStart;
        private DateTimeOffset? _firstEvent;
        private string? _currentModel;
        private int _anonymous;

        public void Read(JsonElement root)
        {
            var type = Json.Str(root, "type");
            if (Json.Time(root, "timestamp") is not { } at || Json.Obj(root, "data") is not { } data)
            {
                return;
            }

            _firstEvent ??= at;
            var id = sessionId + ":" + (Json.Str(root, "id") ?? "#" + _anonymous++);

            switch (type)
            {
                case "session.start":
                case "session.resume":
                    ReadStart(data);
                    break;
                case "session.model_change" when Json.Str(data, "newModel") is { Length: > 0 } model && model != "auto":
                    _currentModel = model;
                    break;
                case "session.auto_mode_resolved" when Json.Str(data, "chosenModel") is { Length: > 0 } chosen:
                    _currentModel = chosen;
                    break;
                case "user.message":
                    ReadPrompt(data, at, id);
                    break;
                case "assistant.turn_start":
                    ReadTurnStart(data, at);
                    break;
                case "assistant.turn_end":
                    ReadTurnEnd(data, at);
                    break;
                case "assistant.message":
                    ReadOutput(data, at);
                    break;
                case "tool.execution_start":
                    ReadToolStart(data, at);
                    break;
                case "tool.execution_complete":
                    ReadToolEnd(data, at);
                    break;
                case "session.usage_checkpoint":
                    ReadCheckpoint(data, at);
                    break;
                case "session.shutdown":
                    ReadShutdown(data, at);
                    break;
                case "model.model_call_success":
                    ReadModelCall(data, at, id);
                    break;
                case "model.model_call_failure":
                    var call = Json.Obj(data, "modelCall");
                    _log.Errors.Add(new ErrorEvent(Provider.Copilot, at, sessionId, id, ErrorKind.Api,
                        (call is { } c ? Json.Str(c, "failure_kind") ?? Json.Str(c, "errorCode") : null) ?? Json.Str(data, "kind") ?? "model_call_failure",
                        call is { } e && Json.Str(e, "error") is { } error ? Json.Preview(error, PreviewLength) : null));
                    break;
                case "session.error":
                    _log.Errors.Add(new ErrorEvent(Provider.Copilot, at, sessionId, id, ErrorKind.Session,
                        Json.Str(data, "errorType") ?? "session_error", Json.Preview(Json.Str(data, "message") ?? string.Empty, PreviewLength)));
                    break;
                case "abort":
                    _log.Errors.Add(new ErrorEvent(Provider.Copilot, at, sessionId, id, ErrorKind.Interrupt, Json.Str(data, "reason") ?? "abort"));
                    break;
                case "hook.end" when Json.Prop(data, "success") is { ValueKind: JsonValueKind.False }:
                    _log.Errors.Add(new ErrorEvent(Provider.Copilot, at, sessionId, id, ErrorKind.Hook, Json.Str(data, "hookType") ?? "hook"));
                    break;
                case "session.compaction_start":
                    _compactionStart = at;
                    break;
                case "session.compaction_complete":
                    ReadCompaction(data, at, id);
                    break;
            }
        }

        private void ReadStart(JsonElement data)
        {
            if (Json.Str(data, "selectedModel") is { Length: > 0 } model && model != "auto")
            {
                _currentModel = model;
            }

            var context = Json.Obj(data, "context");
            _log.Update(Provider.Copilot, sessionId, s => s with
            {
                Cwd = s.Cwd ?? (context is { } c ? Json.Str(c, "cwd") : null),
                Repository = s.Repository ?? (context is { } r ? Json.Str(r, "repository") : null),
                Branch = s.Branch ?? (context is { } b ? Json.Str(b, "branch") : null),
                ClientVersion = Json.Str(data, "copilotVersion") ?? s.ClientVersion,
                Client = s.Client ?? "copilot-cli",
            });
        }

        private void ReadPrompt(JsonElement data, DateTimeOffset at, string id)
        {
            var text = Json.Str(data, "content") ?? string.Empty;
            var kind = PromptKind.Typed;
            string? command = null;
            if (Json.Str(data, "source") is { Length: > 0 } || Json.Bool(data, "isAutopilotContinuation"))
            {
                kind = PromptKind.Automated;
            }
            else if (text.StartsWith('/'))
            {
                kind = PromptKind.Command;
                command = text.Split(' ', 2)[0];
            }

            var attachments = Json.Arr(data, "attachments") is { } list ? list.GetArrayLength() : 0;
            _log.Prompts.Add(new PromptEvent(Provider.Copilot, at, sessionId, sessionId + ":" + (Json.Str(data, "messageId") ?? id), kind, text.Length,
                Json.Preview(text, PreviewLength), command, Json.Str(data, "agentMode"), attachments));
        }

        private void ReadTurnStart(JsonElement data, DateTimeOffset at)
        {
            var interaction = Json.Str(data, "interactionId") ?? Json.Str(data, "turnId");
            if (interaction is null)
            {
                return;
            }

            if (Json.Str(data, "turnId") is { } turn)
            {
                _turnInteraction[turn] = interaction;
            }

            if (!_interactions.TryGetValue(interaction, out var i))
            {
                _interactions[interaction] = i = new Interaction { Start = at, End = at };
            }

            i.Turns++;
        }

        private void ReadTurnEnd(JsonElement data, DateTimeOffset at)
        {
            if (Json.Str(data, "turnId") is { } turn && _turnInteraction.TryGetValue(turn, out var interaction)
                && _interactions.TryGetValue(interaction, out var i) && at > i.End)
            {
                i.End = at;
            }
        }

        private void ReadOutput(JsonElement data, DateTimeOffset at)
        {
            if (Json.Str(data, "model") is { Length: > 0 } model)
            {
                _currentModel = model;
            }

            if (Json.Count(data, "outputTokens") is > 0 and var tokens && Json.Str(data, "messageId") is { Length: > 0 } messageId)
            {
                _outputs.Add((at, messageId, tokens, Json.Str(data, "model")));
            }
        }

        private void ReadToolStart(JsonElement data, DateTimeOffset at)
        {
            if (Json.Str(data, "toolCallId") is not { } id || Json.Str(data, "toolName") is not { } name)
            {
                return;
            }

            _tools.TryAdd(id, new ToolDraft(id, at, name, ToolTargets.Of(name, Json.Prop(data, "arguments")),
                Json.Str(data, "mcpServerName"), Json.Str(data, "parentToolCallId")));
        }

        private void ReadToolEnd(JsonElement data, DateTimeOffset at)
        {
            if (Json.Str(data, "toolCallId") is not { } id || !_tools.Remove(id, out var draft))
            {
                return;
            }

            var error = Json.Obj(data, "error");
            var code = error is { } e ? Json.Str(e, "code") : null;
            var outcome = Json.Bool(data, "success") ? ToolOutcome.Success
                : code is "rejected" or "denied" ? ToolOutcome.Denied
                : code is "aborted" or "cancelled" ? ToolOutcome.Interrupted
                : ToolOutcome.Error;

            int added = 0, removed = 0;
            if (Json.Obj(data, "toolTelemetry") is { } telemetry && Json.Obj(telemetry, "metrics") is { } metrics)
            {
                added = (int)Json.Count(metrics, "linesAdded");
                removed = (int)Json.Count(metrics, "linesRemoved");
            }

            var message = error is { } m ? Json.Str(m, "message") : null;
            _log.Tools.Add(new ToolCall(Provider.Copilot, draft.At, sessionId, sessionId + ":" + draft.Id, draft.Name, outcome, draft.Target, draft.McpServer,
                outcome == ToolOutcome.Denied ? "user-rejected" : null,
                outcome == ToolOutcome.Error && message is not null ? Json.Preview(message, PreviewLength) : null,
                Math.Max(0, (long)(at - draft.At).TotalMilliseconds), added, removed, draft.AgentId));
        }

        private void ReadModelCall(JsonElement data, DateTimeOffset at, string eventId)
        {
            var call = Json.Obj(data, "modelCall");
            var model = (call is { } c ? Json.Str(c, "model") : null) ?? Json.Str(data, "model") ?? _currentModel ?? UnknownModel;
            var tokens = new TokenCounts();
            long nano = 0;

            if (Json.Obj(data, "copilotUsage") is { } usage)
            {
                nano = Json.Count(usage, "total_nano_aiu");
                if (Json.Arr(usage, "token_details") is { } details)
                {
                    foreach (var d in details.EnumerateArray())
                    {
                        var count = Json.Count(d, "token_count");
                        tokens = Json.Str(d, "token_type") switch
                        {
                            "input" => tokens with { Input = tokens.Input + count },
                            "output" => tokens with { Output = tokens.Output + count },
                            "cache_read" => tokens with { CacheRead = tokens.CacheRead + count },
                            "cache_write" => tokens with { CacheWrite5m = tokens.CacheWrite5m + count },
                            _ => tokens,
                        };
                    }
                }
            }

            if (tokens.Total == 0 && Json.Obj(data, "responseUsage") is { } response)
            {
                var prompt = Json.Count(response, "prompt_tokens");
                var cached = Json.Obj(response, "prompt_tokens_details") is { } pd ? Json.Count(pd, "cached_tokens") : 0;
                tokens = new TokenCounts(Math.Max(0, prompt - cached), Json.Count(response, "completion_tokens"), cached);
            }

            if (Json.Obj(data, "responseUsage") is { } r && Json.Obj(r, "completion_tokens_details") is { } cd)
            {
                tokens = tokens with { Reasoning = Json.Count(cd, "reasoning_tokens") };
            }

            _currentModel = model;
            var id = Json.Str(data, "callId") ?? (call is { } rc ? Json.Str(rc, "request_id") : null) ?? eventId;
            _perCall.Add(new ApiCall(Provider.Copilot, at, sessionId, $"copilot:{sessionId}:{id}", model, tokens,
                ReportedCostUsd: nano / NanoAiuPerUsd,
                Effort: Json.Str(data, "reasoningEffort"),
                DurationMs: Json.OptionalCount(data, "modelCallDurationMs"),
                FirstTokenMs: Json.OptionalCount(data, "ttftMs"),
                Initiator: call is { } ic ? Json.Str(ic, "initiator") : null));

            if (Json.Obj(data, "quotaSnapshots") is { } quotas)
            {
                // Same rules and names as the live check (CopilotPlanLimitsSource), so readings line up in the history.
                foreach (var (key, credits, requests) in new[] { ("premium_interactions", "AI credits", "Premium requests"), ("chat", "Chat credits", "Chat messages") })
                {
                    if (Json.Obj(quotas, key) is { } q && !Json.Bool(q, "isUnlimitedEntitlement") && Json.Num(q, "entitlementRequests") is > 0
                        && Json.Num(q, "remainingPercentage") is { } left)
                    {
                        _log.Limits.Add(new LimitReading(Provider.Copilot, at, Json.Bool(q, "tokenBasedBilling") ? credits : requests, Math.Clamp(100 - left, 0, 100), Json.Time(q, "resetDate")));
                    }
                }
            }
        }

        private void ReadCompaction(JsonElement data, DateTimeOffset at, string id)
        {
            var duration = _compactionStart is { } start ? (long?)Math.Max(0, (long)(at - start).TotalMilliseconds) : null;
            _compactionStart = null;
            _log.Compactions.Add(new CompactionEvent(Provider.Copilot, at, sessionId, id, Json.Str(data, "trigger") == "manual",
                Json.Count(data, "preCompactionTokens"), Json.Count(data, "postCompactionTokens"), duration));

            if (Json.Obj(data, "compactionTokensUsed") is { } used)
            {
                var read = Json.Count(used, "cacheReadTokens");
                var write = Json.Count(used, "cacheWriteTokens");
                var nano = Json.Obj(used, "copilotUsage") is { } cu ? Json.Count(cu, "totalNanoAiu") : 0;
                var tokens = new TokenCounts(Math.Max(0, Json.Count(used, "inputTokens") - read - write), Json.Count(used, "outputTokens"), read, write);
                _perCallCompactions.Add(new ApiCall(Provider.Copilot, at, sessionId, "copilot:compaction:" + id,
                    Json.Str(used, "model") ?? _currentModel ?? UnknownModel, tokens, nano / NanoAiuPerUsd, Initiator: "compaction"));
            }
        }

        private readonly List<ApiCall> _perCallCompactions = [];

        private void ReadCheckpoint(JsonElement data, DateTimeOffset at)
        {
            var calls = new Dictionary<string, CheckpointCall>();
            if (Json.Arr(data, "promptCacheBreakState") is { } states)
            {
                foreach (var state in states.EnumerateArray())
                {
                    if (Json.Obj(state, "models") is not { } models)
                    {
                        continue;
                    }

                    foreach (var model in models.EnumerateObject())
                    {
                        if (model.Value.ValueKind == JsonValueKind.Object
                            && (Json.Str(model.Value, "request_id") ?? Json.Str(model.Value, "model_call_id")) is { Length: > 0 } callId)
                        {
                            calls[model.Name] = new CheckpointCall(callId, Json.Count(model.Value, "prompt_tokens"),
                                Json.Count(model.Value, "cache_read"), Json.Count(model.Value, "cache_write"));
                        }
                    }
                }
            }

            _checkpoints.Add(new Checkpoint(at, Json.Count(data, "totalNanoAiu"), calls));
        }

        private void ReadShutdown(JsonElement data, DateTimeOffset at)
        {
            if (Json.Obj(data, "codeChanges") is { } changes)
            {
                var added = (int)Json.Count(changes, "linesAdded");
                var removed = (int)Json.Count(changes, "linesRemoved");
                _log.Update(Provider.Copilot, sessionId, s => s with { LinesAdded = (s.LinesAdded ?? 0) + added, LinesRemoved = (s.LinesRemoved ?? 0) + removed });
            }

            var models = new Dictionary<string, ModelMetric>();
            if (Json.Obj(data, "modelMetrics") is { } metrics)
            {
                foreach (var model in metrics.EnumerateObject())
                {
                    var v = model.Value;
                    long input, output, read, write, reasoning = 0;
                    if (Json.Obj(v, "tokenDetails") is { } d)
                    {
                        input = Detail(d, "input");
                        output = Detail(d, "output");
                        read = Detail(d, "cache_read");
                        write = Detail(d, "cache_write");
                        if (Json.Obj(v, "usage") is { } ru)
                        {
                            reasoning = Json.Count(ru, "reasoningTokens");
                        }
                    }
                    else if (Json.Obj(v, "usage") is { } u)
                    {
                        read = Json.Count(u, "cacheReadTokens");
                        write = Json.Count(u, "cacheWriteTokens");
                        input = Math.Max(0, Json.Count(u, "inputTokens") - read - write);
                        output = Json.Count(u, "outputTokens");
                        reasoning = Json.Count(u, "reasoningTokens");
                    }
                    else
                    {
                        continue;
                    }

                    models[model.Name] = new ModelMetric(input, output, read, write, reasoning, Json.Count(v, "totalNanoAiu"));
                }
            }

            _shutdowns.Add(new Shutdown(at, models, Json.Count(data, "totalNanoAiu")));
        }

        private static long Detail(JsonElement details, string name) =>
            Json.Obj(details, name) is { } d ? Json.Count(d, "tokenCount") : 0;

        public SessionLog Finish()
        {
            if (_perCall.Count > 0)
            {
                _log.Calls.AddRange(_perCall);
                _log.Calls.AddRange(_perCallCompactions);
                AddUnloggedRemainder();
            }
            else
            {
                AddFromRunningTotals();
            }

            foreach (var (key, i) in _interactions)
            {
                _log.Turns.Add(new TurnEvent(Provider.Copilot, i.Start, sessionId, $"copilot:turn:{sessionId}:{key}", Math.Max(0, (long)(i.End - i.Start).TotalMilliseconds), i.Turns));
            }

            foreach (var t in _tools.Values)
            {
                _log.Tools.Add(new ToolCall(Provider.Copilot, t.At, sessionId, sessionId + ":" + t.Id, t.Name, ToolOutcome.Unknown, t.Target, t.McpServer, AgentId: t.AgentId));
            }

            _log.Session(Provider.Copilot, sessionId);
            return _log.Build();
        }

        /// <summary>
        /// A session can mix CLI versions (resumed in a newer one): cost its running total reports beyond the
        /// per-call records is kept as one aggregate, so the session's spend stays complete.
        /// </summary>
        private void AddUnloggedRemainder()
        {
            var total = Math.Max(_checkpoints.Count > 0 ? _checkpoints.Max(c => c.Nano) : 0, _shutdowns.Count > 0 ? _shutdowns.Max(s => s.Nano) : 0);
            var logged = _perCall.Concat(_perCallCompactions).Sum(c => (c.ReportedCostUsd ?? 0) * NanoAiuPerUsd);
            var missing = total - logged;
            if (missing > 0 && missing > total * 0.01)
            {
                var at = _firstEvent ?? _perCall[0].Timestamp;
                _log.Calls.Add(new ApiCall(Provider.Copilot, at, sessionId, "copilot:unlogged:" + sessionId, _perCall[0].Model, new TokenCounts(),
                    missing / NanoAiuPerUsd, IsAggregate: true));
            }
        }

        private void AddFromRunningTotals()
        {
            _shutdowns.Sort((a, b) => a.At.CompareTo(b.At));
            _checkpoints.Sort((a, b) => a.At.CompareTo(b.At));

            // Cost: growth of the running total, per checkpoint, split by the models' share of the session's cost when a clean exit reported it.
            Dictionary<string, long>? weights = null;
            if (_shutdowns.Count > 0)
            {
                weights = _shutdowns[^1].Models.Where(m => m.Value.Nano > 0).ToDictionary(m => m.Key, m => m.Value.Nano);
                if (weights.Count == 0)
                {
                    weights = null;
                }
            }

            long previousNano = 0;
            var previousCalls = new Dictionary<string, string>();
            string? lastModel = null;
            var index = 0;
            foreach (var cp in _checkpoints)
            {
                var changed = cp.Calls.Where(c => !previousCalls.TryGetValue(c.Key, out var id) || id != c.Value.Id).Select(c => c.Key).ToList();
                if (changed.Count > 0)
                {
                    lastModel = changed[^1];
                }

                var delta = cp.Nano - previousNano;
                if (delta > 0)
                {
                    var usd = delta / NanoAiuPerUsd;
                    IEnumerable<(string Model, double Share)> split = weights is not null
                        ? weights.Select(w => (w.Key, w.Value / (double)weights.Values.Sum()))
                        : changed.Count > 0
                            ? changed.Select(m => (m, 1.0 / changed.Count))
                            : [(lastModel ?? _currentModel ?? UnknownModel, 1.0)];

                    foreach (var (model, share) in split)
                    {
                        _log.Calls.Add(new ApiCall(Provider.Copilot, cp.At, sessionId, $"copilot:cost:{sessionId}:{index}:{model}", model, new TokenCounts(),
                            usd * share, IsAggregate: true));
                    }
                }

                index++;
                previousNano = Math.Max(previousNano, cp.Nano);
                foreach (var c in cp.Calls)
                {
                    previousCalls[c.Key] = c.Value.Id;
                }
            }

            // Tokens: exact per-model totals from each clean exit, as growth since the previous exit.
            var previousMetrics = new Dictionary<string, ModelMetric>();
            index = 0;
            foreach (var shutdown in _shutdowns)
            {
                foreach (var (model, metric) in shutdown.Models)
                {
                    var before = previousMetrics.GetValueOrDefault(model) ?? new ModelMetric(0, 0, 0, 0, 0, 0);
                    var tokens = new TokenCounts(
                        Math.Max(0, metric.Input - before.Input),
                        Math.Max(0, metric.Output - before.Output),
                        Math.Max(0, metric.CacheRead - before.CacheRead),
                        Math.Max(0, metric.CacheWrite - before.CacheWrite),
                        0,
                        Math.Max(0, metric.Reasoning - before.Reasoning));
                    if (tokens.Total > 0)
                    {
                        _log.Calls.Add(new ApiCall(Provider.Copilot, shutdown.At, sessionId, $"copilot:exit:{sessionId}:{index}:{model}", model, tokens, IsAggregate: true));
                    }

                    previousMetrics[model] = metric;
                }

                index++;
            }

            // After the last clean exit (or without one) only the per-call prompt numbers and the replies' output exist.
            var cutoff = _shutdowns.Count > 0 ? _shutdowns[^1].At : DateTimeOffset.MinValue;
            var seen = new HashSet<string>();
            foreach (var cp in _checkpoints.Where(c => c.At > cutoff))
            {
                foreach (var (model, call) in cp.Calls)
                {
                    if (seen.Add(call.Id))
                    {
                        _log.Calls.Add(new ApiCall(Provider.Copilot, cp.At, sessionId, $"copilot:call:{sessionId}:{call.Id}", model,
                            new TokenCounts(Math.Max(0, call.Prompt - call.CacheRead - call.CacheWrite), 0, call.CacheRead, call.CacheWrite)));
                    }
                }
            }

            foreach (var (at, id, tokens, model) in _outputs.Where(o => o.At > cutoff))
            {
                _log.Calls.Add(new ApiCall(Provider.Copilot, at, sessionId, $"copilot:message:{sessionId}:{id}", model ?? lastModel ?? _currentModel ?? UnknownModel,
                    new TokenCounts(Output: tokens), IsAggregate: true));
            }
        }
    }
}
