using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TheDojo.UiTests;

/// <summary>
/// The one STA thread every UI test runs on. WPF allows a single <see cref="Application"/> per process and ties it
/// (and every window) to the thread that created it, so all tests share this thread and app.
/// </summary>
internal static class UiThread
{
    private static readonly Lazy<Dispatcher> Instance = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Dispatcher Dispatcher => Instance.Value;

    /// <summary>Runs a test body on the UI thread and rethrows its failure on the calling (xUnit) thread.</summary>
    public static void Run(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        Dispatcher.Invoke(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        failure?.Throw();
    }

    /// <summary>Lets queued layout, bindings, Loaded events and command invocations run.</summary>
    public static void DoEvents()
    {
        var frame = new DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>Pumps the dispatcher until <paramref name="task"/> finishes (view models await on this thread).</summary>
    public static void Wait(Task task, int timeoutSeconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("The task didn't finish in time.");
            }

            DoEvents();
            Thread.Sleep(5);
        }

        task.GetAwaiter().GetResult();
        DoEvents();
    }

    private static Dispatcher Start()
    {
        using var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        Exception? startupError = null;
        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

                // The real App.xaml resources (colours, styles, templates), without running OnStartup.
                var app = new App();
                app.InitializeComponent();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                BindingErrors.Install();
            }
            catch (Exception ex)
            {
                startupError = ex;
            }
            finally
            {
                ready.Set();
            }

            System.Windows.Threading.Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "UI tests",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (startupError is not null)
        {
            throw new InvalidOperationException("Could not start the UI test thread.", startupError);
        }

        return dispatcher!;
    }
}

/// <summary>A fact that runs only on a developer machine: skipped on CI (<c>CI=true</c>), where rendering differs.</summary>
public sealed class UiFactAttribute : FactAttribute
{
    public UiFactAttribute()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Skip = "UI tests run locally only.";
        }
    }
}
