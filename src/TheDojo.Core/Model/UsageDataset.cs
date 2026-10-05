namespace TheDojo.Core.Model;

/// <summary>
/// Every event from every log, with copies dropped: resumed and forked sessions copy their history forward into a
/// new file, so the same reply or tool call can appear in several logs. The first copy (in scan order) wins.
/// </summary>
public sealed class UsageDataset
{
    public IReadOnlyDictionary<string, SessionInfo> Sessions { get; init; } = new Dictionary<string, SessionInfo>();

    public IReadOnlyList<ApiCall> Calls { get; init; } = [];

    public IReadOnlyList<ToolCall> Tools { get; init; } = [];

    public IReadOnlyList<PromptEvent> Prompts { get; init; } = [];

    public IReadOnlyList<TurnEvent> Turns { get; init; } = [];

    public IReadOnlyList<CompactionEvent> Compactions { get; init; } = [];

    public IReadOnlyList<ErrorEvent> Errors { get; init; } = [];

    public IReadOnlyList<LimitReading> Limits { get; init; } = [];

    public static UsageDataset Empty { get; } = new();

    public static UsageDataset Merge(IEnumerable<SessionLog> logs)
    {
        var sessions = new Dictionary<string, SessionInfo>(StringComparer.Ordinal);
        var calls = new Unique<ApiCall>();
        var tools = new Unique<ToolCall>();
        var prompts = new Unique<PromptEvent>();
        var turns = new Unique<TurnEvent>();
        var compactions = new Unique<CompactionEvent>();
        var errors = new Unique<ErrorEvent>();
        var limits = new List<LimitReading>();
        var limitKeys = new HashSet<(Provider, DateTimeOffset, string)>();

        foreach (var log in logs)
        {
            foreach (var s in log.Sessions)
            {
                sessions[s.SessionId] = sessions.TryGetValue(s.SessionId, out var known) ? known.Merge(s) : s;
            }

            calls.AddRange(log.Calls);
            tools.AddRange(log.Tools);
            prompts.AddRange(log.Prompts);
            turns.AddRange(log.Turns);
            compactions.AddRange(log.Compactions);
            errors.AddRange(log.Errors);
            limits.AddRange(log.Limits.Where(l => limitKeys.Add((l.Provider, l.Timestamp, l.Window))));
        }

        // Events of a session whose metadata was never written still belong to a session.
        foreach (var e in calls.Items.Cast<IAgentEvent>().Concat(prompts.Items))
        {
            if (!sessions.ContainsKey(e.SessionId))
            {
                sessions[e.SessionId] = new SessionInfo(e.Provider, e.SessionId);
            }
        }

        return new UsageDataset
        {
            Sessions = sessions,
            Calls = calls.Sorted(),
            Tools = tools.Sorted(),
            Prompts = prompts.Sorted(),
            Turns = turns.Sorted(),
            Compactions = compactions.Sorted(),
            Errors = errors.Sorted(),
            Limits = [.. limits.OrderBy(l => l.Timestamp)],
        };
    }

    private sealed class Unique<T>
        where T : IAgentEvent
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public List<T> Items { get; } = [];

        public void AddRange(IEnumerable<T> items)
        {
            foreach (var item in items)
            {
                if (_seen.Add(item.Provider + ":" + item.Id))
                {
                    Items.Add(item);
                }
            }
        }

        public IReadOnlyList<T> Sorted() => [.. Items.OrderBy(i => i.Timestamp)];
    }
}
