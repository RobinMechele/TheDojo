using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Time.Testing;
using TheDojo.Core;
using TheDojo.Core.Ingest;
using TheDojo.Core.Limits;
using TheDojo.Core.Model;
using TheDojo.Core.Pricing;
using TheDojo.Services;
using TheDojo.Tests.Fixtures;
using TheDojo.ViewModels;
using Xunit.Sdk;

namespace TheDojo.UiTests;

/// <summary>
/// The real main window and view model on the sample world: off-screen, with a fixed clock (UTC), built-in prices,
/// fixed plan limits and a recording UI service. Create and use it inside <see cref="UiThread.Run"/>.
/// </summary>
internal sealed class AppHarness : IDisposable
{
    public AppHarness(double width = 1440, double height = 920, Func<RecordingUi, UpdateViewModel>? update = null)
    {
        World = new SampleWorld();
        Time = new FakeTimeProvider(SampleWorld.Now);
        Time.SetLocalTimeZone(TimeZoneInfo.Utc);
        Ui = new RecordingUi(World.Root);

        var history = new LimitHistoryStore(Path.Combine(World.Cache, "limit-history.jsonl"));
        Service = new DojoService(
            new LogScanner(World.ClaudeProjects, "*.jsonl", ClaudeTranscriptParser.Parse, Path.Combine(World.Cache, "claude.json.gz")),
            new LogScanner(World.CopilotSessions, "events.jsonl", CopilotEventsParser.Parse, Path.Combine(World.Cache, "copilot.json.gz")),
            _ => Task.FromResult(new PriceBook()),
            Time,
            new PlanLimitsService([new FixedLimits(Provider.Claude), new FixedLimits(Provider.Copilot)], Time, history),
            history,
            TimeZoneInfo.Utc);

        Vm = new MainViewModel(Service, Ui, Time, update?.Invoke(Ui));
        Window = new MainWindow
        {
            DataContext = Vm,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Width = width,
            Height = height,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        Window.Show();
        UiThread.Wait(Vm.LoadAsync());
        BindingErrors.Take();
    }

    public SampleWorld World { get; }

    public FakeTimeProvider Time { get; }

    public RecordingUi Ui { get; }

    public DojoService Service { get; }

    public MainViewModel Vm { get; }

    public MainWindow Window { get; }

    public void Show(Page page)
    {
        Vm.Navigate(page);
        UiThread.DoEvents();
        Window.UpdateLayout();
        UiThread.DoEvents();
    }

    /// <summary>Fails on any binding error since the last check.</summary>
    public void AssertNoBindingErrors()
    {
        var errors = BindingErrors.Take();
        if (errors.Count > 0)
        {
            throw new XunitException("Binding errors:\n" + string.Join("\n", errors.Distinct().Take(10)));
        }
    }

    public T Find<T>(Func<T, bool>? match = null)
        where T : DependencyObject =>
        Descendants<T>(Window).FirstOrDefault(match ?? (_ => true)) ?? throw new XunitException($"No {typeof(T).Name} found.");

    public static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var inner in Descendants<T>(child))
            {
                yield return inner;
            }
        }
    }

    public void Dispose()
    {
        Window.Close();
        World.Dispose();
    }

    private sealed class FixedLimits(Provider provider) : IPlanLimitsSource
    {
        public Provider Provider => provider;

        public Task<ProviderLimits> FetchAsync(CancellationToken cancellationToken) => Task.FromResult(provider == Provider.Claude
            ? new ProviderLimits(Provider.Claude, "Max",
            [
                new LimitWindow("Session", 61, SampleWorld.Now.AddHours(2), TimeSpan.FromHours(5)),
                new LimitWindow("Weekly", 83, SampleWorld.Now.AddDays(2), TimeSpan.FromDays(7)),
            ])
            : new ProviderLimits(Provider.Copilot, "Business",
                [new LimitWindow("Premium requests", 38, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromDays(30), "186 of 300 requests left")]));
    }
}

internal sealed class RecordingUi(string root) : IUiService
{
    public List<string> Saved { get; } = [];

    public string? Clipboard { get; private set; }

    public string? AskSavePath(string title, string suggestedName, string filter)
    {
        var path = Path.Combine(root, "exports", suggestedName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Saved.Add(path);
        return path;
    }

    public void SetClipboardText(string text) => Clipboard = text;

    public void Open(string path)
    {
    }

    public List<string> Restarts { get; } = [];

    public void Restart(string exePath) => Restarts.Add(exePath);

    public void Post(Action action) => action();
}
