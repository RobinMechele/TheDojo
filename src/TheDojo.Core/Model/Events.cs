namespace TheDojo.Core.Model;

/// <summary>Something an agent log records. <see cref="Id"/> is stable across copies of the same history (resumed or forked sessions), so events can be deduplicated.</summary>
public interface IAgentEvent
{
    Provider Provider { get; }

    DateTimeOffset Timestamp { get; }

    string SessionId { get; }

    string Id { get; }
}

/// <summary>
/// One billed model request. <see cref="IsAggregate"/> marks a record that sums several requests (Copilot sessions
/// that only logged running totals): it carries tokens and cost but doesn't count as a call.
/// </summary>
public sealed record ApiCall(
    Provider Provider,
    DateTimeOffset Timestamp,
    string SessionId,
    string Id,
    string Model,
    TokenCounts Tokens,
    double? ReportedCostUsd = null,
    bool IsAggregate = false,
    string? StopReason = null,
    string? Effort = null,
    string? Speed = null,
    int WebSearches = 0,
    int WebFetches = 0,
    long? DurationMs = null,
    long? FirstTokenMs = null,
    long? ThinkingMs = null,
    string? AgentId = null,
    string? AgentType = null,
    string? Skill = null,
    string? McpServer = null,
    string[]? Tools = null,
    string? Initiator = null) : IAgentEvent
{
    public bool IsSubagent => AgentId is not null;
}

public enum ToolOutcome
{
    Success,
    Error,
    Denied,
    Interrupted,
    Unknown,
}

/// <summary>
/// One tool the agent ran. <see cref="Target"/> is what it ran on: a file path for file tools, the program for
/// shell commands, the host for web fetches, the agent type for subagents, the skill name for skills.
/// </summary>
public sealed record ToolCall(
    Provider Provider,
    DateTimeOffset Timestamp,
    string SessionId,
    string Id,
    string Name,
    ToolOutcome Outcome,
    string? Target = null,
    string? McpServer = null,
    string? DenialKind = null,
    string? ErrorText = null,
    long? DurationMs = null,
    int LinesAdded = 0,
    int LinesRemoved = 0,
    string? AgentId = null) : IAgentEvent;

public enum PromptKind
{
    /// <summary>Typed by the engineer.</summary>
    Typed,

    /// <summary>A slash command such as <c>/model</c> or <c>/compact</c>.</summary>
    Command,

    /// <summary>Sent by something else: a scheduled task, a background agent finishing, an autopilot continuation.</summary>
    Automated,
}

/// <summary>A message from the engineer (or on their behalf). <see cref="Preview"/> is the start of the text, kept local.</summary>
public sealed record PromptEvent(
    Provider Provider,
    DateTimeOffset Timestamp,
    string SessionId,
    string Id,
    PromptKind Kind,
    int Length,
    string Preview,
    string? Command = null,
    string? Mode = null,
    int Attachments = 0) : IAgentEvent;

/// <summary>From a prompt to the agent's final answer: how long the engineer waited and how many messages it took.</summary>
public sealed record TurnEvent(
    Provider Provider,
    DateTimeOffset Timestamp,
    string SessionId,
    string Id,
    long DurationMs,
    int MessageCount) : IAgentEvent;

public sealed record CompactionEvent(
    Provider Provider,
    DateTimeOffset Timestamp,
    string SessionId,
    string Id,
    bool Manual,
    long PreTokens,
    long PostTokens,
    long? DurationMs = null) : IAgentEvent;

public enum ErrorKind
{
    /// <summary>The model API failed (network, overload, rate limit); usually retried.</summary>
    Api,

    /// <summary>The engineer stopped the agent mid-answer.</summary>
    Interrupt,

    /// <summary>A hook script failed.</summary>
    Hook,

    /// <summary>The session itself failed (sign-in, model unavailable).</summary>
    Session,
}

public sealed record ErrorEvent(
    Provider Provider,
    DateTimeOffset Timestamp,
    string SessionId,
    string Id,
    ErrorKind Kind,
    string Code,
    string? Message = null,
    int RetryAttempt = 0) : IAgentEvent;

/// <summary>A reading of a subscription limit, from a <c>/usage</c> command, a Copilot call or a live check.</summary>
public sealed record LimitReading(
    Provider Provider,
    DateTimeOffset Timestamp,
    string Window,
    double PercentUsed,
    DateTimeOffset? ResetsAt = null);

/// <summary>What a session is about and where it ran. Usage, timing and counts come from its events.</summary>
public sealed record SessionInfo(
    Provider Provider,
    string SessionId,
    string? Title = null,
    string? Cwd = null,
    string? Repository = null,
    string? Branch = null,
    string? ClientVersion = null,
    string? Client = null,
    string? PermissionMode = null,
    string[]? PullRequests = null,
    int? LinesAdded = null,
    int? LinesRemoved = null,
    double? ReportedCostUsd = null,
    string? ContinuedIn = null)
{
    /// <summary>The project a session belongs to: the repository name, or else the last folder of its working directory.</summary>
    public string Project => ProjectName(Repository, Cwd);

    public static string ProjectName(string? repository, string? cwd)
    {
        if (!string.IsNullOrWhiteSpace(repository))
        {
            var slash = repository.LastIndexOf('/');
            return slash >= 0 ? repository[(slash + 1)..] : repository;
        }

        if (string.IsNullOrWhiteSpace(cwd))
        {
            return "(unknown)";
        }

        var path = cwd.Replace('\\', '/').TrimEnd('/');

        // Worktrees live under <repo>/.worktrees/<name> or <repo>/.claude/worktrees/<name>: they belong to the repo.
        foreach (var marker in new[] { "/.worktrees/", "/.claude/worktrees/" })
        {
            var at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at > 0)
            {
                path = path[..at];
                break;
            }
        }

        var last = path.LastIndexOf('/');
        var name = last >= 0 ? path[(last + 1)..] : path;
        return name.Length == 0 ? path : name;
    }

    /// <summary>Combines two partial views of the same session (e.g. a transcript and its resumed copy); this one wins where both know.</summary>
    public SessionInfo Merge(SessionInfo other) => this with
    {
        Title = Title ?? other.Title,
        Cwd = Cwd ?? other.Cwd,
        Repository = Repository ?? other.Repository,
        Branch = Branch ?? other.Branch,
        ClientVersion = ClientVersion ?? other.ClientVersion,
        Client = Client ?? other.Client,
        PermissionMode = PermissionMode ?? other.PermissionMode,
        PullRequests = (PullRequests ?? []).Union(other.PullRequests ?? []).ToArray() is { Length: > 0 } prs ? prs : null,
        LinesAdded = LinesAdded ?? other.LinesAdded,
        LinesRemoved = LinesRemoved ?? other.LinesRemoved,
        ReportedCostUsd = ReportedCostUsd ?? other.ReportedCostUsd,
        ContinuedIn = ContinuedIn ?? other.ContinuedIn,
    };
}
