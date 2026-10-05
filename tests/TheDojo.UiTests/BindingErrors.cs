using System.Diagnostics;
using System.Text;

namespace TheDojo.UiTests;

/// <summary>
/// Collects WPF data binding errors (normally only visible in a debugger's output window), so a broken binding
/// path in XAML fails a test instead of silently showing nothing.
/// </summary>
internal sealed class BindingErrors : TraceListener
{
    private static readonly BindingErrors Listener = new();
    private readonly List<string> _errors = [];
    private readonly StringBuilder _line = new();

    public static void Install()
    {
        PresentationTraceSources.Refresh();
        var source = PresentationTraceSources.DataBindingSource;
        source.Listeners.Add(Listener);
        source.Switch.Level = SourceLevels.Warning;
    }

    /// <summary>Errors since the last call, which also clears them.</summary>
    public static IReadOnlyList<string> Take()
    {
        lock (Listener._errors)
        {
            var errors = Listener._errors.ToList();
            Listener._errors.Clear();
            return errors;
        }
    }

    public override void Write(string? message) => _line.Append(message);

    public override void WriteLine(string? message)
    {
        _line.Append(message);
        lock (_errors)
        {
            _errors.Add(_line.ToString());
        }

        _line.Clear();
    }
}
