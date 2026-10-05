# Data map: what The Dojo tracks and where it comes from

The Dojo reads only what your agents already write to disk, plus the same plan-limit endpoints the CLIs use themselves. Nothing is sent anywhere else.

## Sources

| Source | Where | How it's read |
|---|---|---|
| Claude Code transcripts | `~/.claude/projects/<encoded-cwd>/<session>.jsonl` (`CLAUDE_CONFIG_DIR` respected) | `ClaudeTranscriptParser`, every line |
| Claude Code subagent transcripts | `…/<session>/subagents/agent-<id>.jsonl` | Same parser; the calls carry `agentId` and the agent type |
| Copilot CLI session logs | `~/.copilot/session-state/<session>/events.jsonl` (`COPILOT_HOME` respected) | `CopilotEventsParser` (huge snapshot lines are skipped unparsed) |
| Copilot session names | `…/<session>/workspace.yaml` (`name`, `summary`) | Plain `key: value` lines |
| Claude plan limits | `GET api.anthropic.com/api/oauth/usage` with Claude Code's own OAuth token (`~/.claude/.credentials.json`) | `ClaudePlanLimitsSource`; the token only goes to Anthropic |
| Copilot plan limits | The Copilot CLI's `account.getQuota` (headless JSON-RPC); fallback `api.github.com/copilot_internal/user` with `GH_TOKEN` / `gh auth token` | `CopilotPlanLimitsSource`; the token only goes to GitHub |
| Model prices | LiteLLM's public price table (cached 24h), with Anthropic's published rates built in | `PriceBook`, `ClaudePricing` |
| Limit history | `%LocalAppData%\TheDojo\limit-history.jsonl` (every live reading) + `/usage` reports found in transcripts | `LimitHistoryStore` |

Parsed results are cached per file (by size and last-write time) in `%LocalAppData%\TheDojo\scan-*.json.gz`, so only sessions that changed are read again. `DOJO_HOME` moves that folder.

## Per model call (`ApiCall`)

| Field | Claude Code | Copilot CLI |
|---|---|---|
| Time, session, model | `timestamp`, `sessionId`, `message.model` | `timestamp`, folder name, `modelCall.model` |
| Uncached input | `usage.input_tokens` | `copilotUsage.token_details[input]` (or `prompt_tokens − cached_tokens`) |
| Output (and thinking share) | `usage.output_tokens`, `output_tokens_details.thinking_tokens` | `token_details[output]`, `completion_tokens_details.reasoning_tokens` |
| Cache read | `usage.cache_read_input_tokens` | `token_details[cache_read]` |
| Cache write, 5-minute / 1-hour | `usage.cache_creation.ephemeral_5m/1h_input_tokens` | `token_details[cache_write]` (one tier) |
| Cost | Priced per token kind and cache tier (fast mode ×2); **matches Claude Code's own `cost-state` to the cent** | Reported AI credits: `copilotUsage.total_nano_aiu` (1e9 nano = 1 credit = $0.01) |
| Effort, speed, stop reason | `effort`, `usage.speed`, `message.stop_reason` | `reasoningEffort` |
| Latency | `thinkingDurationMs` | `modelCallDurationMs`, `ttftMs` |
| Web use | `usage.server_tool_use.web_search/fetch_requests` | — |
| Subagent, type | `isSidechain` + `agentId`, `attributionAgent` | — (tools carry `parentToolCallId`) |
| Skill / MCP attribution | `attributionSkill`, `attributionMcpServer` | — |
| Tools requested | `tool_use` blocks | — (see tool calls) |
| Initiator | — | `modelCall.initiator` (user / agent), compaction calls |

Claude Code writes one line per content block and repeats the usage on each; a reply is counted once by `message.id + requestId`. Resumed and forked sessions copy history forward into new files: every event is de-duplicated by id across files, first copy wins.

Older Copilot sessions (before per-call events) only log running totals: cost comes from the growth of `session.usage_checkpoint.totalNanoAiu`, attributed to the model that really answered (even in "auto" mode); tokens from the per-model `session.shutdown.modelMetrics` on a clean exit, or the per-call prompt numbers in the checkpoints otherwise. These are marked as aggregates (they carry cost and tokens but don't count as calls).

## Per tool call (`ToolCall`)

| Field | Claude Code | Copilot CLI |
|---|---|---|
| Tool, time | `tool_use.name`, the reply's timestamp | `tool.execution_start.toolName` |
| Outcome | `tool_result.is_error`, `toolDenialKind` (user-rejected, permission-rule, automode-blocked), `toolUseResult.interrupted` | `success`, `error.code` (`rejected` = denied) |
| Duration | result time − call time | complete − start |
| Lines added/removed | `toolUseResult.structuredPatch` (+/− lines), every line of a created file | `toolTelemetry.metrics.linesAdded/Removed` |
| Target | file path (Read/Edit/Write), program (Bash/PowerShell: `cd x && dotnet test` → `dotnet`), host (WebFetch), agent type (Agent/Task), skill name | same, from `arguments` |
| MCP server | `mcp__<server>__<tool>` | `mcpServerName` |
| Subagent | `agentId` | `parentToolCallId` |
| Error text | first 200 chars of the result | `error.message` |

## Prompts, turns and behaviour

| What | Claude Code | Copilot CLI |
|---|---|---|
| Prompts (typed / slash command / automated) | `user` lines with text; `<command-name>`; `origin.kind ≠ human`, task notifications, scheduled tasks | `user.message`; `/…`; `source` (autopilot) |
| Prompt preview, length, attachments, mode | first 200 chars, images/documents, `permissionMode` | `content`, `attachments`, `agentMode` |
| Turn time (prompt → final answer) | `system/turn_duration.durationMs`, `messageCount` | first `assistant.turn_start` → last `turn_end` per `interactionId` |
| Compactions | `system/compact_boundary.compactMetadata` (manual/auto, pre/post tokens, duration) | `session.compaction_start/complete` |
| Interruptions | `[Request interrupted by user]` | `abort` |
| API errors and retries | `system/api_error` (code, retry attempt), `isApiErrorMessage` | `model.model_call_failure`, `session.error` |
| Hook failures | `system/stop_hook_summary.hookErrors` | `hook.end` with `success: false` |
| Limit readings | `system/local_command.usageReport.rate_limits` (every `/usage`) | `model_call_success.quotaSnapshots` |

## Per session (`SessionInfo`)

Title (`custom-title` over `ai-title`; Copilot `workspace.yaml`), working folder, repository, branch, CLI version, entry point, permission mode, pull requests (`pr-link`), Claude Code's own cost and line counts (`cost-state`), Copilot code changes (`session.shutdown.codeChanges`), and the session it was continued in. The **project** is the repository name, or the working folder's name, with worktrees (`.worktrees/…`, `.claude/worktrees/…`) folded into their repository.

## What is derived (reports and coaching)

- **Totals:** spend (with trend vs the previous period), tokens by kind, cache hit rate and cache savings, sessions, projects, prompts, model calls, cost per prompt and per session, agent time, tool calls/errors/denials, interruptions, compactions, API errors, lines changed, pull requests, subagent share, thinking share, web searches/fetches.
- **Breakdowns:** spend per day/hour by agent; per model (calls, tokens, cache hit, average context and output, subagent calls); per project; per session (peak context, compactions, cost per prompt, models used); per tool (error rate, average and p95 duration, lines); shell programs; subagent types; MCP servers; skills; slash commands; effort levels; stop reasons; error kinds; context-size buckets; weekday × hour heatmap.
- **Prompt cost:** every call and tool belongs to the prompt that set it off (the latest prompt of the session before it), which gives the most expensive prompts.
- **Session timeline:** context size and cost of every call, showing how a session gets heavier and where compactions happened.
- **Coach (lessons, each quantified):** context bloat (calls over 200K tokens), cache rewrites after breaks longer than the cache lifetime, low/high cache hit rate, quick questions on premium models (what Sonnet 5.5 would have cost), subagents on premium models, failing tools (and which shell programs), interruptions and denials, files read again and again, sessions compacted more than once, heavy thinking, API errors, spend jumps, the single most expensive prompt, very short prompts, cost per changed line, limits on pace to run out.
- **Dojo score and belt:** caching (30), context discipline (20), model fit (20), tool success (15), flow (15) → 0-100 → White, Yellow, Orange, Green, Blue, Brown, Black.
- **Limits:** live windows with an even-pace tick, "ahead of pace" and projected run-out time, and a history chart of every reading.
