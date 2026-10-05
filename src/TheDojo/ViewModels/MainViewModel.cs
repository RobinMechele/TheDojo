using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TheDojo.Core;
using TheDojo.Core.Analysis;
using TheDojo.Core.Insights;
using TheDojo.Core.Limits;
using TheDojo.Core.Model;
using TheDojo.Services;

namespace TheDojo.ViewModels;

/// <summary>
/// The window: navigation, the period/agent/project filters, and loading. Data loads first; live limits arrive
/// later (the Copilot CLI can take a while to answer) and then refresh the pages that show them.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const string AllProjects = "All projects";

    private readonly IDojoService _service;
    private readonly IUiService _ui;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _load;
    private IReadOnlyList<ProviderLimits> _limits = [];
    private bool _suppressReload;

    public MainViewModel(IDojoService service, IUiService ui, TimeProvider? time = null, UpdateViewModel? update = null)
    {
        _service = service;
        _ui = ui;
        _time = time ?? TimeProvider.System;
        Update = update ?? new UpdateViewModel(null, null, new Version(0, 0, 0), ui);
        Display.Zone = _time.LocalTimeZone;
        Overview = new OverviewViewModel();
        Sessions = new SessionsViewModel();
        Reports = new ReportsViewModel(ui);
        Pages = [Overview, new CoachViewModel(), Sessions, new ModelsViewModel(), new ProjectsViewModel(), new ToolsViewModel(), new ActivityViewModel(), new LimitsViewModel(), Reports];
        _current = Overview;
        Overview.IsSelected = true;
    }

    public IReadOnlyList<PageViewModel> Pages { get; }

    public OverviewViewModel Overview { get; }

    public UpdateViewModel Update { get; }

    public SessionsViewModel Sessions { get; }

    public ReportsViewModel Reports { get; }

    public DojoSnapshot? Snapshot { get; private set; }

    public IReadOnlyList<ProviderLimits> Limits => _limits;

    public static IReadOnlyList<string> RangeNames { get; } = ["24h", "7d", "30d", "90d", "All"];

    public static IReadOnlyList<string> ProviderNames { get; } = ["All agents", "Claude Code", "Copilot"];

    [ObservableProperty]
    private PageViewModel _current;

    /// <summary>Index into <see cref="RangeNames"/>; 30 days by default.</summary>
    [ObservableProperty]
    private int _rangeIndex = 2;

    [ObservableProperty]
    private int _providerIndex;

    [ObservableProperty]
    private IReadOnlyList<string> _projects = [AllProjects];

    [ObservableProperty]
    private string _selectedProject = AllProjects;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _status = "Reading your agent sessions…";

    [ObservableProperty]
    private string? _error;

    public ReportFilter Filter => new(
        (UsageRange)RangeIndex,
        ProviderIndex switch { 1 => Provider.Claude, 2 => Provider.Copilot, _ => null },
        SelectedProject == AllProjects ? null : SelectedProject);

    partial void OnRangeIndexChanged(int value) => ReloadSoon();

    partial void OnProviderIndexChanged(int value) => ReloadSoon();

    partial void OnSelectedProjectChanged(string value)
    {
        // A combo box clears its selection while its list is replaced; that isn't a choice.
        if (value is null)
        {
            _suppressReload = true;
            SelectedProject = AllProjects;
            _suppressReload = false;
            return;
        }

        ReloadSoon();
    }

    partial void OnCurrentChanged(PageViewModel? oldValue, PageViewModel newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        newValue.IsSelected = true;
    }

    [RelayCommand]
    private void Navigate(PageViewModel page) => Current = page;

    public void Navigate(Page page) => Current = Pages.First(p => p.Page == page);

    private void ReloadSoon()
    {
        if (!_suppressReload)
        {
            _ = LoadAsync(manual: false);
        }
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync(manual: true);

    /// <summary>Reads the logs (only changed files are parsed again) and rebuilds every page; then checks the live limits.</summary>
    public async Task LoadAsync(bool manual = false)
    {
        _load?.Cancel();
        var cts = _load = new CancellationTokenSource();
        IsLoading = true;
        Error = null;
        var limitsTask = _service.FetchLimitsAsync(manual, cts.Token);

        try
        {
            var snapshot = await _service.LoadAsync(Filter, _limits, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            Apply(snapshot);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = "Couldn't read the agent logs: " + ex.Message;
        }
        finally
        {
            if (_load == cts)
            {
                IsLoading = false;
            }
        }

        try
        {
            var limits = await limitsTask;
            if (!cts.IsCancellationRequested && Snapshot is { } current)
            {
                _limits = limits;
                // Limits feed the coach (pace warnings) as well as the pages that show them.
                var coach = InsightEngine.Analyze(current.Report, current.Data, current.Costs, limits, _time.GetUtcNow());
                Apply(current with { Coach = coach });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Apply(DojoSnapshot snapshot)
    {
        Snapshot = snapshot;
        var now = _time.GetUtcNow();
        foreach (var page in Pages)
        {
            page.Update(snapshot, _limits, now);
        }

        _suppressReload = true;
        try
        {
            var selected = SelectedProject;
            Projects = [AllProjects, .. snapshot.Report.AllProjects];
            SelectedProject = Projects.Contains(selected) ? selected : AllProjects;
        }
        finally
        {
            _suppressReload = false;
        }

        var t = snapshot.Report.Totals;
        Status = snapshot.Report.IsEmpty
            ? "No agent activity in this period."
            : $"{t.Sessions} sessions · {UsageFormat.Compact(t.Tokens.Total)} tokens · updated {Display.Format(now, "HH:mm")}";
    }
}
