using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using TheDojo.Controls;
using TheDojo.Core;
using TheDojo.Core.Analysis;
using TheDojo.Core.Limits;

namespace TheDojo.ViewModels;

public sealed partial class SessionsViewModel : PageViewModel
{
    private DojoSnapshot? _snapshot;
    private IReadOnlyList<SessionItem> _all = [];

    public override Page Page => Page.Sessions;

    public override string Title => "Sessions";

    public override string Glyph => "";

    [ObservableProperty]
    private string _search = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<SessionItem> _rows = [];

    [ObservableProperty]
    private SessionItem? _selected;

    [ObservableProperty]
    private SessionDetailViewModel? _detail;

    [ObservableProperty]
    private string _summary = string.Empty;

    partial void OnSearchChanged(string value) => Filter();

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void CloseDetail() => Selected = null;

    partial void OnSelectedChanged(SessionItem? value)
    {
        Detail = value is not null && _snapshot is not null && DojoAnalyzer.Session(_snapshot.Data, _snapshot.Costs, value.Row.SessionId) is { } detail
            ? new SessionDetailViewModel(detail)
            : null;
    }

    public override void Update(DojoSnapshot snapshot, IReadOnlyList<ProviderLimits> limits, DateTimeOffset now)
    {
        var keep = Selected?.Row.SessionId;
        _snapshot = snapshot;
        _all = [.. snapshot.Report.Sessions.Select(s => new SessionItem(s))];
        Filter();
        Selected = keep is null ? null : Rows.FirstOrDefault(r => r.Row.SessionId == keep);
    }

    private void Filter()
    {
        var terms = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Rows = terms.Length == 0
            ? _all
            : [.. _all.Where(s => terms.All(t => s.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase)))];
        Summary = $"{Rows.Count} sessions · {UsageFormat.Money(Rows.Sum(r => r.Cost))}";
    }
}

public sealed record SessionItem(SessionRow Row)
{
    public string Title => Row.Title;

    public string Project => Row.Project;

    public string? Branch => Row.Branch;

    public Brush Brush => Palette.For(Row.Provider);

    public string Agent => Row.Provider.ToString();

    public DateTimeOffset Start => Row.Start;

    public string StartText => Display.Format(Row.Start, "MMM d, HH:mm");

    public long ActiveMs => Row.ActiveMs;

    public string ActiveText => UsageFormat.Duration(Row.ActiveMs);

    public int Prompts => Row.Prompts;

    public int Calls => Row.Calls;

    public double Cost => Row.CostUsd;

    public string CostText => UsageFormat.Money(Row.CostUsd);

    public double PerPrompt => Row.CostPerPrompt;

    public string PerPromptText => UsageFormat.Money(Row.CostPerPrompt);

    public long PeakContext => Row.PeakContext;

    public string PeakContextText => UsageFormat.Compact(Row.PeakContext);

    public double CacheHitRate => Row.CacheHitRate;

    public string CacheText => Row.Tokens.Context == 0 ? "—" : UsageFormat.Percent(Row.CacheHitRate);

    public int ToolErrors => Row.ToolErrors;

    public int Compactions => Row.Compactions;

    public string Models => string.Join(", ", Row.Models);

    public string SearchText => $"{Row.Title} {Row.Project} {Row.Branch} {Models} {Row.Provider} {Row.SessionId}";
}

public sealed class SessionDetailViewModel(SessionDetail detail)
{
    public SessionDetail Detail => detail;

    public string Title => detail.Session.Title;

    public string Meta
    {
        get
        {
            var s = detail.Session;
            var parts = new List<string> { s.Provider == Core.Model.Provider.Claude ? "Claude Code" : "Copilot CLI", s.Project };
            if (s.Branch is { } branch)
            {
                parts.Add(branch);
            }

            parts.Add($"{Display.Format(s.Start, "MMM d, HH:mm")} → {Display.Format(s.End, "HH:mm")}");
            if (s.ClientVersion is { } version)
            {
                parts.Add("v" + version);
            }

            return string.Join(" · ", parts);
        }
    }

    public IReadOnlyList<StatTile> Tiles { get; } =
    [
        new("Spend", UsageFormat.Money(detail.Session.CostUsd), $"{UsageFormat.Money(detail.Session.CostPerPrompt)} per prompt"),
        new("Prompts", detail.Session.Prompts.ToString(), $"{detail.Session.Calls} model calls"),
        new("Peak context", UsageFormat.Compact(detail.Session.PeakContext), $"{detail.Session.Compactions} compactions"),
        new("Cache hit", detail.Session.Tokens.Context == 0 ? "—" : UsageFormat.Percent(detail.Session.CacheHitRate), UsageFormat.Compact(detail.Session.Tokens.Total) + " tokens"),
        new("Agent time", UsageFormat.Duration(detail.Session.ActiveMs), $"{detail.Session.ToolCalls} tools · {detail.Session.ToolErrors} failed"),
        new("Code", $"+{UsageFormat.Compact(detail.Session.LinesAdded)} / −{UsageFormat.Compact(detail.Session.LinesRemoved)}", $"{detail.Session.PullRequests} PRs"),
    ];

    /// <summary>How full the context was on every main-thread call: the line that shows a session getting heavy.</summary>
    public IReadOnlyList<LineSeries> Context { get; } = detail.Timeline.Any(p => !p.IsSubagent)
        ?
        [
            new LineSeries("Context", Palette.For(detail.Session.Provider),
                [.. detail.Timeline.Where(p => !p.IsSubagent && p.Context > 0).Select(p => new LinePoint(p.Timestamp, p.Context, $"{p.Model} · {UsageFormat.Money(p.CostUsd)}"))]),
        ]
        : [];

    public bool HasContext => Context.Count > 0 && Context[0].Points.Count > 1;

    public IReadOnlyList<PromptItem> Prompts { get; } = [.. detail.Prompts.Select(p => new PromptItem(p))];

    public IReadOnlyList<ToolItem> Tools { get; } = [.. detail.Tools.Take(12).Select(t => new ToolItem(t, detail.Tools.Count == 0 ? 0 : detail.Tools.Max(x => x.Calls)))];

    public IReadOnlyList<string> PullRequests => detail.PullRequests;

    public string Errors => detail.Errors.Count == 0
        ? string.Empty
        : string.Join(" · ", detail.Errors.GroupBy(e => e.Kind).Select(g => g.Key switch
        {
            Core.Model.ErrorKind.Api => $"{g.Count()} API errors",
            Core.Model.ErrorKind.Interrupt => $"{g.Count()} interruptions",
            Core.Model.ErrorKind.Hook => $"{g.Count()} hook failures",
            _ => $"{g.Count()} session errors",
        }));
}
