using TheDojo.ViewModels;

namespace TheDojo.UiTests;

/// <summary>Every page rendered on the sample world and compared with its baseline (binding errors fail first).</summary>
public sealed class ScreenTests
{
    [UiFact]
    public void Overview() => Screen(Page.Overview);

    [UiFact]
    public void Coach() => Screen(Page.Coach);

    [UiFact]
    public void Sessions() => Screen(Page.Sessions);

    [UiFact]
    public void Models() => Screen(Page.Models);

    [UiFact]
    public void Projects() => Screen(Page.Projects);

    [UiFact]
    public void Tools() => Screen(Page.Tools);

    [UiFact]
    public void Activity() => Screen(Page.Activity);

    [UiFact]
    public void Limits() => Screen(Page.Limits);

    [UiFact]
    public void Reports() => Screen(Page.Reports);

    [UiFact]
    public void SessionDetail() => UiThread.Run(() =>
    {
        using var app = new AppHarness();
        app.Show(Page.Sessions);
        app.Vm.Sessions.Selected = app.Vm.Sessions.Rows.First(r => r.Row.SessionId == "sess-marathon");
        UiThread.DoEvents();
        app.Window.UpdateLayout();
        app.AssertNoBindingErrors();
        Snapshot.Match(app.Window, "session-detail");
    });

    private static void Screen(Page page) => UiThread.Run(() =>
    {
        using var app = new AppHarness();
        app.Show(page);
        app.AssertNoBindingErrors();
        Snapshot.Match(app.Window, page.ToString().ToLowerInvariant());
    });
}
