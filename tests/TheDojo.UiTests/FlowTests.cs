using System.Net.Http;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TheDojo.Controls;
using TheDojo.Core.Model;
using TheDojo.ViewModels;
using Page = TheDojo.ViewModels.Page;

namespace TheDojo.UiTests;

/// <summary>What an engineer does in the window: navigate, filter, search, drill into a session, export.</summary>
public sealed class FlowTests
{
    /// <summary>A real click: buttons through UI Automation, radio buttons (no Invoke pattern) through their own click handling.</summary>
    private static void Click(ButtonBase button)
    {
        if (UIElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
        {
            invoke.Invoke();
            return;
        }

        typeof(ButtonBase).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(button, null);
    }

    [UiFact]
    public void Sidebar_NavigatesBetweenPages() => UiThread.Run(() =>
    {
        using var app = new AppHarness();

        var coach = app.Find<RadioButton>(r => r.CommandParameter is CoachViewModel);
        Click(coach);
        UiThread.DoEvents();

        Assert.IsType<CoachViewModel>(app.Vm.Current);
        Assert.True(app.Vm.Current.IsSelected);
        Assert.False(app.Vm.Overview.IsSelected);
        Assert.Contains(AppHarness.Descendants<ItemsControl>(app.Window), i => System.Windows.Automation.AutomationProperties.GetAutomationId(i) == "Lessons");
        app.AssertNoBindingErrors();
    });

    [UiFact]
    public void Overview_ShowsTheSpendChartScoreAndLimits() => UiThread.Run(() =>
    {
        using var app = new AppHarness();

        var chart = app.Find<StackedBarChart>();
        Assert.Equal(30, chart.Columns!.Cast<BarColumn>().Count());
        Assert.StartsWith("$", app.Vm.Overview.Spend);
        Assert.InRange(app.Vm.Overview.Score, 1, 100);
        Assert.Equal(2, app.Vm.Overview.Limits.Count);
        Assert.Contains(app.Vm.Overview.Lessons, l => l.Insight.Id == "context-bloat");
    });

    [UiFact]
    public void Filters_ReloadEveryPage() => UiThread.Run(() =>
    {
        using var app = new AppHarness();
        var allSessions = app.Vm.Sessions.Rows.Count;

        app.Vm.ProviderIndex = 2; // Copilot
        UiThread.Wait(WaitForReload(app));
        Assert.All(app.Vm.Sessions.Rows, r => Assert.Equal(Provider.Copilot, r.Row.Provider));
        Assert.True(app.Vm.Sessions.Rows.Count < allSessions);

        app.Vm.ProviderIndex = 0;
        app.Vm.SelectedProject = "billing-api";
        UiThread.Wait(WaitForReload(app));
        Assert.All(app.Vm.Sessions.Rows, r => Assert.Equal("billing-api", r.Project));
        Assert.Contains("billing-api", app.Vm.Projects);

        app.Vm.SelectedProject = MainViewModel.AllProjects;
        app.Vm.RangeIndex = 0; // 24h
        UiThread.Wait(WaitForReload(app));
        Assert.True(app.Vm.Snapshot!.Report.Hourly);
        app.AssertNoBindingErrors();
    });

    [UiFact]
    public void Sessions_SearchAndDetail() => UiThread.Run(() =>
    {
        using var app = new AppHarness();
        app.Show(Page.Sessions);

        app.Vm.Sessions.Search = "ledger";
        UiThread.DoEvents();
        var only = Assert.Single(app.Vm.Sessions.Rows);

        app.Vm.Sessions.Selected = only;
        UiThread.DoEvents();
        var detail = app.Vm.Sessions.Detail!;
        Assert.True(detail.HasContext);
        Assert.Equal(60, detail.Context[0].Points.Count);
        Assert.Single(detail.Prompts);
        Assert.NotNull(app.Find<LineChart>());

        app.Vm.Sessions.CloseDetailCommand.Execute(null);
        Assert.Null(app.Vm.Sessions.Detail);
        app.AssertNoBindingErrors();
    });

    [UiFact]
    public void Reports_SaveAndCopy() => UiThread.Run(() =>
    {
        using var app = new AppHarness();
        app.Show(Page.Reports);
        var reports = app.Vm.Reports;

        reports.SaveMarkdownCommand.Execute(null);
        reports.SaveJsonCommand.Execute(null);
        reports.ExportCallsCommand.Execute(null);
        reports.ExportSessionsCommand.Execute(null);
        reports.ExportToolsCommand.Execute(null);
        reports.CopyMarkdownCommand.Execute(null);

        Assert.Equal(5, app.Ui.Saved.Count);
        Assert.All(app.Ui.Saved, path => Assert.True(new FileInfo(path).Length > 100, path));
        Assert.Contains("dojo-report-2026-10-05.md", app.Ui.Saved[0]);
        Assert.StartsWith("# The Dojo", app.Ui.Clipboard);
        Assert.DoesNotContain("Most expensive prompts", reports.Preview);

        reports.IncludePrompts = true;
        Assert.Contains("Most expensive prompts", reports.Preview);
    });

    [UiFact]
    public void Activity_SwitchesTheHeatmapBetweenSpendAndPrompts() => UiThread.Run(() =>
    {
        using var app = new AppHarness();
        app.Show(Page.Activity);
        var activity = (ActivityViewModel)app.Vm.Current;
        var spend = activity.Cells.Sum(c => c.Value);

        activity.MetricIndex = 1;

        Assert.Equal(app.Vm.Snapshot!.Report.Totals.Prompts, (int)activity.Cells.Sum(c => c.Value));
        Assert.NotEqual(spend, activity.Cells.Sum(c => c.Value));
    });

    [UiFact]
    public void UpdateBanner_IsHiddenUntilANewerReleaseExistsThenInstallsAndRestarts() => UiThread.Run(() =>
    {
        using var quiet = new AppHarness();
        Assert.Equal(System.Windows.Visibility.Collapsed, quiet.Find<Border>(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "UpdateBanner").Visibility);
        quiet.Dispose();

        var exe = Path.Combine(Directory.CreateTempSubdirectory("dojo-ui-update-").FullName, "TheDojo.exe");
        File.WriteAllBytes(exe, [1]);
        var github = new TheDojo.Tests.Fixtures.FakeGitHub("v2.0.0", [7, 7, 7]);
        using var app = new AppHarness(update: ui => new UpdateViewModel(
            new TheDojo.Core.Updates.UpdateService(new HttpClient(github), "o/r", "TheDojo-win-x64.exe"), exe, new Version(1, 0, 0), ui));

        UiThread.Wait(app.Vm.Update.CheckAsync());
        UiThread.DoEvents();
        var banner = app.Find<Border>(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "UpdateBanner");
        Assert.Equal(System.Windows.Visibility.Visible, banner.Visibility);
        Assert.Contains("2.0.0", app.Vm.Update.Message);

        Click(app.Find<Button>(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "UpdateInstall"));
        UiThread.Wait(Task.Run(async () =>
        {
            while (app.Ui.Restarts.Count == 0)
            {
                await Task.Delay(20);
            }
        }));

        Assert.Equal(new byte[] { 7, 7, 7 }, File.ReadAllBytes(exe));
        Assert.Equal(exe, Assert.Single(app.Ui.Restarts));
        app.AssertNoBindingErrors();
    });

    private static async Task WaitForReload(AppHarness app)
    {
        // Filter changes start a load; wait until it (and the limits that follow) settle.
        await Task.Delay(50);
        while (app.Vm.IsLoading)
        {
            await Task.Delay(20);
        }

        await Task.Delay(50);
    }
}
