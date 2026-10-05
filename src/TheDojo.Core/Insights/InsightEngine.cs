using System.Globalization;
using TheDojo.Core.Analysis;
using TheDojo.Core.Limits;
using TheDojo.Core.Model;
using TheDojo.Core.Pricing;

namespace TheDojo.Core.Insights;

/// <summary>
/// Turns a period's usage into lessons. Every rule needs enough data to be fair (a handful of calls says nothing),
/// quantifies what it saw, and says what to try. Savings are estimates: what the same work would have cost with
/// the habit changed, priced with the same rates as the report.
/// </summary>
public static class InsightEngine
{
    /// <summary>The model a quick question or a subagent can usually run on instead of a premium one.</summary>
    public const string EverydayModel = "claude-sonnet-5-5";

    private const long LargeContext = 200_000;
    private const long ComfortableContext = 100_000;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static CoachResult Analyze(DojoReport report, UsageDataset data, CostCalculator costs, IReadOnlyList<ProviderLimits>? limits, DateTimeOffset now)
    {
        var scope = Scope(report, data);
        var calls = data.Calls.Where(c => scope(c)).Select(c => (Call: c, Cost: costs.Cost(c))).ToList();
        var tools = data.Tools.Where(t => scope(t)).ToList();
        var prompts = data.Prompts.Where(p => scope(p)).ToList();
        var compactions = data.Compactions.Where(c => scope(c)).ToList();
        var t = report.Totals;

        var insights = new List<Insight>();
        var premiumSavings = PremiumForSmallTurns(calls, prompts, costs, insights);
        var largeContextShare = ContextBloat(calls, costs, t, insights);
        CacheHealth(t, calls, insights);
        CacheRewrites(calls, costs, t, insights);
        SubagentModels(calls, costs, insights);
        ToolFailures(report, insights);
        Overrides(t, tools, insights);
        FileRereads(tools, insights);
        LongSessions(report, compactions, insights);
        Thinking(t, insights);
        ApiErrors(t, insights);
        SpendTrend(report, insights);
        ExpensivePrompt(report, insights);
        PromptLength(prompts, insights);
        Output(t, insights);
        LimitPace(limits, now, insights);

        var parts = Score(t, premiumSavings, largeContextShare);
        var score = parts.Count == 0 ? 0 : (int)Math.Round(parts.Sum(p => p.Value * p.Weight) / parts.Sum(p => p.Weight) * 100);
        var ordered = insights
            .OrderBy(i => i.Severity)
            .ThenByDescending(i => i.SavingsUsd ?? 0)
            .ToList();
        return new CoachResult(score, CoachResult.BeltFor(score), parts, ordered);
    }

    private static Func<IAgentEvent, bool> Scope(DojoReport report, UsageDataset data) => e =>
        e.Timestamp >= report.From && e.Timestamp <= report.To
        && (report.Filter.Provider is null || e.Provider == report.Filter.Provider)
        && (report.Filter.Project is null
            || (data.Sessions.TryGetValue(e.SessionId, out var s) && string.Equals(s.Project, report.Filter.Project, StringComparison.OrdinalIgnoreCase)));

    private static List<ScorePart> Score(ReportTotals t, double premiumSavings, double largeContextShare)
    {
        if (t.Calls < 10)
        {
            return [];
        }

        var parts = new List<ScorePart>();
        if (t.Tokens.Context > 0)
        {
            parts.Add(new ScorePart("Caching", Math.Clamp(t.CacheHitRate / 0.9, 0, 1), 30,
                $"{Pct(t.CacheHitRate)} of prompt tokens came from cache (90% earns full marks)."));
        }

        parts.Add(new ScorePart("Context discipline", Math.Clamp(1 - largeContextShare * 2, 0, 1), 20,
            $"{Pct(largeContextShare)} of spend went to calls with more than {Tokens(LargeContext)} tokens of context."));

        if (t.CostUsd > 0)
        {
            parts.Add(new ScorePart("Model fit", Math.Clamp(1 - premiumSavings / t.CostUsd * 3, 0, 1), 20,
                $"Quick turns on premium models could have cost {Money(premiumSavings)} less."));
        }

        if (t.ToolCalls > 0)
        {
            parts.Add(new ScorePart("Tool success", Math.Clamp(1 - t.ToolErrorRate * 2, 0, 1), 15,
                $"{Pct(1 - t.ToolErrorRate)} of tool calls succeeded."));
        }

        if (t.Prompts > 0)
        {
            var overrides = (double)(t.Interrupts + t.ToolDenials) / t.Prompts;
            parts.Add(new ScorePart("Flow", Math.Clamp(1 - overrides, 0, 1), 15,
                $"{t.Interrupts + t.ToolDenials} interruptions or denials over {t.Prompts} prompts."));
        }

        return parts;
    }

    private static void CacheHealth(ReportTotals t, List<(ApiCall Call, CallCost Cost)> calls, List<Insight> insights)
    {
        if (calls.Count(c => !c.Call.IsAggregate) < 30 || t.Tokens.Context == 0)
        {
            return;
        }

        if (t.CacheHitRate >= 0.85)
        {
            insights.Add(new Insight("cache-strong", InsightSeverity.Strength, InsightCategory.Caching,
                $"Prompt cache is doing its job: {Pct(t.CacheHitRate)} hit rate",
                $"{Pct(t.CacheHitRate)} of the prompt tokens your agents sent were read from cache, which saved about {Money(t.CacheSavingsUsd)} against paying full input price.",
                "Keep sessions going while you're actively working on a task; that's what keeps the cache warm."));
        }
        else if (t.CacheHitRate < 0.6)
        {
            insights.Add(new Insight("cache-weak", InsightSeverity.Warning, InsightCategory.Caching,
                $"Only {Pct(t.CacheHitRate)} of prompt tokens came from cache",
                "Most of each prompt was paid at full or cache-write price. That happens with many short sessions, frequent model switches (each model has its own cache) or edits to instructions mid-session.",
                "Stay on one model within a session, avoid editing CLAUDE.md or instructions mid-task, and continue related work in the same session."));
        }
    }

    /// <summary>A break longer than the cache lifetime makes the next call write the whole context again.</summary>
    private static void CacheRewrites(List<(ApiCall Call, CallCost Cost)> calls, CostCalculator costs, ReportTotals t, List<Insight> insights)
    {
        var rewrites = 0;
        var waste = 0.0;
        foreach (var session in calls.Where(c => !c.Call.IsAggregate && !c.Call.IsSubagent && c.Call.Provider == Provider.Claude).GroupBy(c => c.Call.SessionId))
        {
            ApiCall? previous = null;
            foreach (var (call, _) in session.OrderBy(c => c.Call.Timestamp))
            {
                if (previous is not null && costs.RatesFor(call) is { } rates)
                {
                    var gap = call.Timestamp - previous.Timestamp;
                    var expired = (call.Tokens.CacheWrite1h >= 20_000 && gap > TimeSpan.FromHours(1))
                        || (call.Tokens.CacheWrite5m >= 20_000 && gap > TimeSpan.FromMinutes(5) && call.Tokens.CacheWrite1h == 0);
                    if (expired && call.Tokens.CacheWrite > call.Tokens.CacheRead)
                    {
                        rewrites++;
                        waste += call.Tokens.CacheWrite5m * (rates.CacheWrite5m - rates.CacheRead) + call.Tokens.CacheWrite1h * (rates.CacheWrite1h - rates.CacheRead);
                    }
                }

                previous = call;
            }
        }

        if (rewrites >= 3 && waste >= 0.5 && waste >= t.CostUsd * 0.02)
        {
            insights.Add(new Insight("cache-rewrites", InsightSeverity.Tip, InsightCategory.Caching,
                $"{rewrites} breaks outlived the cache and re-wrote the context",
                $"After a pause longer than the cache lifetime (5 minutes, or 1 hour on the long tier), the next message pays to write the whole conversation into the cache again. That cost about {Money(waste)} this period.",
                "Before a long break, wrap up or /compact the session; after one, consider starting fresh with a short summary instead of resuming a huge context.",
                waste));
        }
    }

    /// <summary>Share of spend on calls whose context was over 200K tokens; reports it when it's a real share.</summary>
    private static double ContextBloat(List<(ApiCall Call, CallCost Cost)> calls, CostCalculator costs, ReportTotals t, List<Insight> insights)
    {
        if (t.CostUsd <= 0)
        {
            return 0;
        }

        var large = calls.Where(c => !c.Call.IsAggregate && c.Call.Tokens.Context > LargeContext).ToList();
        var share = large.Sum(c => c.Cost.Usd) / t.CostUsd;
        if (large.Count < 10 || share < 0.25 || large.Sum(c => c.Cost.Usd) < 2)
        {
            return share;
        }

        // What the prompt side would have cost at a comfortable context size.
        var savings = 0.0;
        foreach (var (call, cost) in large)
        {
            if (costs.RatesFor(call) is not { } rates)
            {
                continue;
            }

            var promptCost = Math.Max(0, cost.Usd - call.Tokens.Output * rates.Output);
            savings += promptCost * (call.Tokens.Context - ComfortableContext) / call.Tokens.Context;
        }

        insights.Add(new Insight("context-bloat", InsightSeverity.Warning, InsightCategory.Context,
            $"{Pct(share)} of spend went to calls with over {Tokens(LargeContext)} tokens of context",
            $"{large.Count} calls carried a very full context. Every message re-sends it, so a long-running session gets more expensive per message the longer it runs, even when the next step is small.",
            "Use /clear between unrelated tasks, /compact once a task's exploration is done, and start a new session for a new feature. Hand over with a short summary rather than the whole history.",
            savings));
        return share;
    }

    /// <summary>Quick turns (little output, few tools) answered by Opus or Fable: Sonnet would usually do.</summary>
    private static double PremiumForSmallTurns(List<(ApiCall Call, CallCost Cost)> calls, List<PromptEvent> prompts, CostCalculator costs, List<Insight> insights)
    {
        var bySession = calls.Where(c => !c.Call.IsAggregate && c.Call.Provider == Provider.Claude).ToLookup(c => c.Call.SessionId);
        var quickTurns = 0;
        var savings = 0.0;
        foreach (var group in prompts.Where(p => p.Kind == PromptKind.Typed).GroupBy(p => p.SessionId))
        {
            var starts = group.Select(p => p.Timestamp).Order().ToList();
            var sessionCalls = bySession[group.Key].OrderBy(c => c.Call.Timestamp).ToList();
            for (var i = 0; i < starts.Count; i++)
            {
                var end = i + 1 < starts.Count ? starts[i + 1] : DateTimeOffset.MaxValue;
                var turn = sessionCalls.Where(c => c.Call.Timestamp >= starts[i] && c.Call.Timestamp < end).ToList();
                if (turn.Count == 0 || turn.Count > 4 || turn.Sum(c => c.Call.Tokens.Output) > 2_000 || !turn.All(c => ClaudePricing.IsPremium(c.Call.Model)))
                {
                    continue;
                }

                var now = turn.Sum(c => c.Cost.Usd);
                var instead = turn.Sum(c => costs.CostOn(c.Call, EverydayModel) ?? c.Cost.Usd);
                if (now > instead)
                {
                    quickTurns++;
                    savings += now - instead;
                }
            }
        }

        if (quickTurns >= 5 && savings >= 1)
        {
            insights.Add(new Insight("premium-small-turns", InsightSeverity.Tip, InsightCategory.Models,
                $"{quickTurns} quick questions ran on a premium model",
                $"These turns produced under 2K tokens with at most four calls (questions, small edits, lookups) on Opus or Fable. On Sonnet 5.5 they would have cost about {Money(savings)} less.",
                "Use a cheaper default model and switch up (/model) for hard design or debugging work, or set a lower /effort for routine asks.",
                savings));
        }

        return savings;
    }

    private static void SubagentModels(List<(ApiCall Call, CallCost Cost)> calls, CostCalculator costs, List<Insight> insights)
    {
        var premium = calls.Where(c => c.Call.IsSubagent && ClaudePricing.IsPremium(c.Call.Model)).ToList();
        var spent = premium.Sum(c => c.Cost.Usd);
        var instead = premium.Sum(c => costs.CostOn(c.Call, EverydayModel) ?? c.Cost.Usd);
        var agents = premium.Select(c => c.Call.AgentId).Distinct().Count();
        if (agents >= 2 && spent - instead >= 1)
        {
            insights.Add(new Insight("subagent-models", InsightSeverity.Tip, InsightCategory.Models,
                $"{agents} subagents ran on a premium model",
                $"Subagents mostly search, read and summarize. Those {agents} cost {Money(spent)} on Opus/Fable; on Sonnet 5.5 the same work would have cost about {Money(instead)}.",
                "Give subagents a cheaper model: set model: sonnet (or haiku for pure search) in the agent definition, or ask for it when delegating.",
                spent - instead));
        }
    }

    private static void ToolFailures(DojoReport report, List<Insight> insights)
    {
        foreach (var tool in report.Tools.Where(t => t.Calls >= 10 && t.ErrorRate >= 0.2).OrderByDescending(t => t.Errors).Take(2))
        {
            var shell = report.Programs.Where(p => p.Errors > 0).OrderByDescending(p => p.Errors).Take(3).ToList();
            var detail = $"{tool.Name} failed {tool.Errors} of {tool.Calls} times ({Pct(tool.ErrorRate)}). Every failure is a round trip that re-sends the whole context.";
            var action = tool.Name.ToLowerInvariant() switch
            {
                "edit" or "multiedit" or "str_replace" or "apply_patch" => "Edits fail when the text to replace doesn't match exactly. Let the agent read the file first, and avoid editing files it's working on at the same time.",
                "bash" or "powershell" or "shell" when shell.Count > 0 =>
                    $"Most failing programs: {string.Join(", ", shell.Select(p => $"{p.Label} ({p.Errors})"))}. Document the right commands (build, test, run) in CLAUDE.md / AGENTS.md so the agent stops guessing.",
                _ => "Check what the failures have in common (wrong paths, missing tools, wrong shell) and put the fix in your project instructions.",
            };
            insights.Add(new Insight("tool-failures-" + tool.Name, InsightSeverity.Warning, InsightCategory.Tools, $"{tool.Name} fails often", detail, action));
        }

        if (report.Totals.ToolCalls >= 50 && report.Totals.ToolErrorRate < 0.05)
        {
            insights.Add(new Insight("tools-strong", InsightSeverity.Strength, InsightCategory.Tools,
                $"{Pct(1 - report.Totals.ToolErrorRate)} of tool calls succeeded",
                "Agents rarely hit failing commands or mismatched edits, so little context is spent on retries.",
                "Keep your build/test commands documented where the agent can read them."));
        }
    }

    private static void Overrides(ReportTotals t, List<ToolCall> tools, List<Insight> insights)
    {
        if (t.Prompts < 20)
        {
            return;
        }

        var overrides = t.Interrupts + t.ToolDenials;
        var rate = (double)overrides / t.Prompts;
        if (rate < 0.15)
        {
            return;
        }

        var ruleBlocks = tools.Count(x => x.DenialKind is "permission-rule" or "automode-blocked");
        var action = ruleBlocks > t.ToolDenials / 2 && ruleBlocks >= 5
            ? $"{ruleBlocks} calls were blocked by permission rules or auto mode. If they were safe, allow them in settings; if not, tell the agent up front what it may not do."
            : "Agree on the approach first: plan mode, or ask for a short plan before edits. Spell out constraints (files not to touch, commands not to run) in the first prompt.";
        insights.Add(new Insight("overrides", InsightSeverity.Tip, InsightCategory.Workflow,
            $"You stopped or overruled the agent {overrides} times",
            $"{t.Interrupts} interruptions and {t.ToolDenials} denied tool calls over {t.Prompts} prompts ({rate.ToString("0.##", Invariant)} per prompt). Each one usually means the agent was heading somewhere you didn't want.",
            action));
    }

    private static void FileRereads(List<ToolCall> tools, List<Insight> insights)
    {
        var rereads = tools
            .Where(x => x.Name is "Read" or "view" && x.Target is not null && x.Outcome == ToolOutcome.Success)
            .GroupBy(x => (x.SessionId, x.Target))
            .Where(g => g.Count() >= 4)
            .ToList();
        var extra = rereads.Sum(g => g.Count() - 1);
        if (extra < 20)
        {
            return;
        }

        var top = rereads.OrderByDescending(g => g.Count()).First();
        insights.Add(new Insight("file-rereads", InsightSeverity.Tip, InsightCategory.Context,
            $"Files were read again and again ({extra} extra reads)",
            $"{rereads.Count} files were read four or more times in the same session; the top one, {Path.GetFileName(top.Key.Target)}, {top.Count()} times. Re-reads usually follow a compaction or a vague task that keeps the agent searching.",
            "Point the agent at the exact files and lines in your prompt, and keep sessions on one task so it doesn't lose track of what it already read."));
    }

    private static void LongSessions(DojoReport report, List<CompactionEvent> compactions, List<Insight> insights)
    {
        var heavy = compactions.GroupBy(c => c.SessionId).Where(g => g.Count() >= 2).ToList();
        if (heavy.Count == 0)
        {
            return;
        }

        var auto = compactions.Count(c => !c.Manual);
        insights.Add(new Insight("long-sessions", InsightSeverity.Tip, InsightCategory.Context,
            $"{heavy.Count} sessions filled the context more than once",
            $"{compactions.Count} compactions this period ({auto} automatic). Each compaction summarizes the history and loses detail; the agent then re-reads files to recover it.",
            "Split big jobs into sessions per sub-task, and /compact yourself at a natural break (with a hint of what to keep) rather than waiting for the automatic one."));
    }

    private static void Thinking(ReportTotals t, List<Insight> insights)
    {
        if (t.Calls < 50 || t.Tokens.Reasoning == 0 || t.ReasoningShare < 0.5)
        {
            return;
        }

        insights.Add(new Insight("thinking-heavy", InsightSeverity.Tip, InsightCategory.Models,
            $"{Pct(t.ReasoningShare)} of output tokens were thinking",
            "Most of what the models generated was reasoning rather than answers or code. That's right for hard problems, but routine edits rarely need it.",
            "Lower the effort for routine work (/effort low or medium) and raise it only for hard debugging or design."));
    }

    private static void ApiErrors(ReportTotals t, List<Insight> insights)
    {
        if (t.ApiErrors < 10)
        {
            return;
        }

        insights.Add(new Insight("api-errors", InsightSeverity.Tip, InsightCategory.Reliability,
            $"{t.ApiErrors} API or session errors",
            "Connection drops, overloads and sign-in problems interrupted the agents. Retried calls re-send the whole context.",
            "If they cluster at certain times, schedule long autonomous runs outside them; check VPN or proxy settings when connection resets dominate."));
    }

    private static void SpendTrend(DojoReport report, List<Insight> insights)
    {
        if (report.Totals.CostTrend is not { } trend || report.Totals.CostUsd < 5)
        {
            return;
        }

        if (trend >= 0.5)
        {
            insights.Add(new Insight("spend-up", InsightSeverity.Warning, InsightCategory.Cost,
                $"Spend is up {Pct(trend)} on the previous period",
                $"{Money(report.Totals.CostUsd)} this period against {Money(report.Totals.PreviousCostUsd)} the period before.",
                "Check the Sessions page sorted by cost: one long session usually explains most of a jump."));
        }
        else if (trend <= -0.2)
        {
            insights.Add(new Insight("spend-down", InsightSeverity.Strength, InsightCategory.Cost,
                $"Spend is down {Pct(-trend)} on the previous period",
                $"{Money(report.Totals.CostUsd)} this period against {Money(report.Totals.PreviousCostUsd)} the period before.",
                "Whatever changed is working; keep it up."));
        }
    }

    private static void ExpensivePrompt(DojoReport report, List<Insight> insights)
    {
        if (report.TopPrompts.FirstOrDefault() is not { } top || report.Totals.CostUsd <= 0 || top.CostUsd < 5 || top.CostUsd < report.Totals.CostUsd * 0.1)
        {
            return;
        }

        insights.Add(new Insight("expensive-prompt", InsightSeverity.Tip, InsightCategory.Cost,
            $"One prompt cost {Money(top.CostUsd)} ({Pct(top.CostUsd / report.Totals.CostUsd)} of the period)",
            $"“{top.Preview}” set off {top.Calls} calls and {top.ToolCalls} tool runs in {top.Project}.",
            "For big asks, ask for a plan first and approve it step by step; it keeps the agent from exploring far beyond what you needed."));
    }

    private static void PromptLength(List<PromptEvent> prompts, List<Insight> insights)
    {
        var typed = prompts.Where(p => p.Kind == PromptKind.Typed).Select(p => p.Length).Order().ToList();
        if (typed.Count < 30)
        {
            return;
        }

        var median = typed[typed.Count / 2];
        if (median < 30)
        {
            insights.Add(new Insight("short-prompts", InsightSeverity.Tip, InsightCategory.Workflow,
                $"Half your prompts are under {median + 1} characters",
                "Very short prompts work for follow-ups, but as the main way of steering they lead to back-and-forth: each round trip re-sends the whole context.",
                "Give the goal, the constraints and how to verify in one message. A good prompt for a task is often three to five sentences."));
        }
    }

    private static void Output(ReportTotals t, List<Insight> insights)
    {
        var lines = t.LinesAdded + t.LinesRemoved;
        if (lines < 200 || t.CostUsd <= 0)
        {
            return;
        }

        insights.Add(new Insight("output", InsightSeverity.Strength, InsightCategory.Workflow,
            $"{lines.ToString("N0", Invariant)} lines changed{(t.PullRequests > 0 ? $" and {t.PullRequests} pull requests" : string.Empty)}",
            $"About {Money(t.CostUsd / lines * 100)} per 100 changed lines this period. Track this over time: it falls as prompts get sharper and sessions shorter.",
            "Compare it month to month on the Reports page."));
    }

    private static void LimitPace(IReadOnlyList<ProviderLimits>? limits, DateTimeOffset now, List<Insight> insights)
    {
        foreach (var provider in limits ?? [])
        {
            foreach (var window in provider.Windows)
            {
                if (window.PaceAt(now) != Limits.LimitPace.Ahead || window.PercentUsed < 50 || window.ProjectedExhaustion(now) is not { } runsOut)
                {
                    continue;
                }

                var left = runsOut - now;
                insights.Add(new Insight("limit-" + provider.Provider + "-" + window.Label, InsightSeverity.Warning, InsightCategory.Limits,
                    $"{provider.Provider} {window.Label.ToLowerInvariant()} limit: {window.PercentUsed:0}% used, on pace to run out",
                    $"At the current rate it runs out in about {Duration(left)}, before it resets in {Duration((window.ResetsAt ?? now) - now)}.",
                    "Move routine work to a cheaper model or lower effort until it resets, and save the premium model for the hard parts."));
            }
        }
    }

    private static string Pct(double share) => (share * 100).ToString("0", Invariant) + "%";

    private static string Money(double usd) => UsageFormat.Money(usd);

    private static string Tokens(long tokens) => UsageFormat.Compact(tokens);

    private static string Duration(TimeSpan span) => UsageFormat.Duration(span);
}
