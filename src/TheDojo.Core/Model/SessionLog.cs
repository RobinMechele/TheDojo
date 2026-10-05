namespace TheDojo.Core.Model;

/// <summary>Everything parsed from one log file. Serializable, so the scanner can cache it.</summary>
public sealed record SessionLog(
    SessionInfo[] Sessions,
    ApiCall[] Calls,
    ToolCall[] Tools,
    PromptEvent[] Prompts,
    TurnEvent[] Turns,
    CompactionEvent[] Compactions,
    ErrorEvent[] Errors,
    LimitReading[] Limits)
{
    public static SessionLog Empty { get; } = new([], [], [], [], [], [], [], []);
}

/// <summary>Collects a <see cref="SessionLog"/> while a parser walks a file.</summary>
public sealed class SessionLogBuilder
{
    public Dictionary<string, SessionInfo> Sessions { get; } = new(StringComparer.Ordinal);

    public List<ApiCall> Calls { get; } = [];

    public List<ToolCall> Tools { get; } = [];

    public List<PromptEvent> Prompts { get; } = [];

    public List<TurnEvent> Turns { get; } = [];

    public List<CompactionEvent> Compactions { get; } = [];

    public List<ErrorEvent> Errors { get; } = [];

    public List<LimitReading> Limits { get; } = [];

    public SessionInfo Session(Provider provider, string sessionId) =>
        Sessions.TryGetValue(sessionId, out var info) ? info : Sessions[sessionId] = new SessionInfo(provider, sessionId);

    public void Update(Provider provider, string sessionId, Func<SessionInfo, SessionInfo> change) =>
        Sessions[sessionId] = change(Session(provider, sessionId));

    public SessionLog Build() => new(
        [.. Sessions.Values], [.. Calls], [.. Tools], [.. Prompts], [.. Turns], [.. Compactions], [.. Errors], [.. Limits]);
}
