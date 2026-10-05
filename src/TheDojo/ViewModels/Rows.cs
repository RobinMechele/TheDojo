using System.Windows;
using System.Windows.Media;
using TheDojo.Core;
using TheDojo.Core.Analysis;
using TheDojo.Core.Insights;
using TheDojo.Core.Limits;
using TheDojo.Core.Model;

namespace TheDojo.ViewModels;

/// <summary>Theme brushes for view models; plain fallbacks when there is no application (tests).</summary>
public static class Palette
{
    private static Brush Find(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    public static Brush Claude => Find("ClaudeBrush", Color.FromRgb(0xD9, 0x59, 0x26));

    public static Brush Copilot => Find("CopilotBrush", Color.FromRgb(0x39, 0x87, 0xE5));

    public static Brush Blue => Find("SeriesBlueBrush", Color.FromRgb(0x39, 0x87, 0xE5));

    public static Brush Aqua => Find("SeriesAquaBrush", Color.FromRgb(0x19, 0x9E, 0x70));

    public static Brush Violet => Find("SeriesVioletBrush", Color.FromRgb(0x90, 0x85, 0xE9));

    public static Brush Good => Find("GoodBrush", Color.FromRgb(0x0C, 0xA3, 0x0C));

    public static Brush Warning => Find("WarningBrush", Color.FromRgb(0xFA, 0xB2, 0x19));

    public static Brush Critical => Find("CriticalBrush", Color.FromRgb(0xD0, 0x3B, 0x3B));

    public static Brush Accent => Find("AccentBrush", Color.FromRgb(0x3E, 0xE0, 0x8F));

    public static Brush Muted => Find("TextMutedBrush", Color.FromRgb(0x6B, 0x75, 0x90));

    public static Brush For(Provider provider) => provider == Provider.Claude ? Claude : Copilot;

    public static Brush Belt(string belt) => belt switch
    {
        "Black" => new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)),
        "Brown" => new SolidColorBrush(Color.FromRgb(0x8B, 0x5A, 0x2B)),
        "Blue" => new SolidColorBrush(Color.FromRgb(0x2A, 0x78, 0xD6)),
        "Green" => new SolidColorBrush(Color.FromRgb(0x1B, 0xAF, 0x5A)),
        "Orange" => new SolidColorBrush(Color.FromRgb(0xEB, 0x68, 0x34)),
        "Yellow" => new SolidColorBrush(Color.FromRgb(0xED, 0xC1, 0x00)),
        _ => new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xF2)),
    };
}

/// <summary>A key figure: label, value and an optional line of context.</summary>
public sealed record StatTile(string Label, string Value, string? Detail = null);

/// <summary>A ranked row with a share bar (models, projects, tools, programs...).</summary>
public sealed record RankRow(string Name, string Value, double Share, Brush Fill, string? Detail = null);

public sealed record InsightItem(Insight Insight)
{
    public string Title => Insight.Title;

    public string Detail => Insight.Detail;

    public string Action => Insight.Action;

    public string Category => Insight.Category.ToString();

    public string SeverityText => Insight.Severity switch
    {
        InsightSeverity.Warning => "Warning",
        InsightSeverity.Tip => "Tip",
        _ => "Strength",
    };

    /// <summary>Status never travels by colour alone: each severity has its own icon.</summary>
    public string Glyph => Insight.Severity switch
    {
        InsightSeverity.Warning => "",
        InsightSeverity.Tip => "",
        _ => "",
    };

    public Brush Brush => Insight.Severity switch
    {
        InsightSeverity.Warning => Palette.Warning,
        InsightSeverity.Tip => Palette.Blue,
        _ => Palette.Good,
    };

    public string? SavingsText => Insight.SavingsUsd is > 0.005 and var s ? "≈ " + UsageFormat.Money(s) + " to save" : null;
}

public sealed record ScorePartItem(ScorePart Part)
{
    public string Name => Part.Name;

    public double Value => Part.Value * 100;

    public string ValueText => $"{Part.Value * 100:0}";

    public string Explanation => Part.Explanation;
}

public sealed record LimitRowItem(LimitWindow Window, DateTimeOffset Now)
{
    public string Label => Window.Label;

    public double PercentUsed => Window.PercentUsed;

    public string UsedText => $"{Window.PercentUsed:0}% used";

    public string LeftText => $"{Window.PercentLeft:0}% left";

    public double? Elapsed => Window.ElapsedAt(Now) is { } e ? e * 100 : null;

    public string ResetsText => Window.ResetsAt is { } at ? "Resets in " + UsageFormat.ResetsIn(at - Now) : string.Empty;

    public string? Detail => Window.Detail;

    public string PaceText => Window.PaceAt(Now) switch
    {
        LimitPace.Ahead when Window.ProjectedExhaustion(Now) is { } runsOut => $"Ahead of pace · runs out in ~{UsageFormat.Duration(runsOut - Now)}",
        LimitPace.Ahead => "Ahead of pace",
        LimitPace.OnTrack => "On pace",
        _ => string.Empty,
    };

    public bool IsAhead => Window.PaceAt(Now) == LimitPace.Ahead;
}

public sealed class ProviderLimitsItem(ProviderLimits limits, DateTimeOffset now)
{
    public string Name => limits.Provider == Provider.Claude ? "Claude Code" : "GitHub Copilot";

    public Brush Brush => Palette.For(limits.Provider);

    public string? Plan => limits.Plan is null ? null : limits.Plan + " plan";

    public string? Error => limits.Error;

    public string? Note => limits.Note;

    public IReadOnlyList<LimitRowItem> Windows { get; } = [.. limits.Windows.Select(w => new LimitRowItem(w, now))];
}

public sealed record PromptItem(PromptRow Row)
{
    public string When => Display.Format(Row.Timestamp, "MMM d, HH:mm");

    public string Preview => Row.Preview.Length == 0 ? "(empty)" : Row.Preview;

    public string Project => Row.Project;

    public string Kind => Row.Kind.ToString();

    public string Cost => UsageFormat.Money(Row.CostUsd);

    public double CostValue => Row.CostUsd;

    public string Work => $"{Row.Calls} calls · {Row.ToolCalls} tools · {UsageFormat.Compact(Row.Output)} out";

    public Brush Brush => Palette.For(Row.Provider);
}

public sealed record ToolItem(ToolRow Row, int MaxCalls)
{
    public string Name => Row.McpServer is { } server && Row.Name.StartsWith("mcp__", StringComparison.Ordinal)
        ? $"{Row.Name[(Row.Name.LastIndexOf("__", StringComparison.Ordinal) + 2)..]} ({server})"
        : Row.Name;

    public int Calls => Row.Calls;

    public double Share => MaxCalls == 0 ? 0 : (double)Row.Calls / MaxCalls;

    public int Errors => Row.Errors;

    public int Denied => Row.Denied;

    public double ErrorRate => Row.ErrorRate;

    public string ErrorRateText => Row.Calls == 0 ? "—" : UsageFormat.Percent(Row.ErrorRate);

    public long AverageMs => Row.AverageMs ?? 0;

    public string AverageText => Row.AverageMs is { } ms ? UsageFormat.Duration(ms) : "—";

    public string P95Text => Row.P95Ms is { } ms ? UsageFormat.Duration(ms) : "—";

    public string Lines => Row.LinesAdded + Row.LinesRemoved > 0 ? $"+{UsageFormat.Compact(Row.LinesAdded)} / −{UsageFormat.Compact(Row.LinesRemoved)}" : "—";

    public int SubagentCalls => Row.SubagentCalls;
}
