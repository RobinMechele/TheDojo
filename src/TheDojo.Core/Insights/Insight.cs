namespace TheDojo.Core.Insights;

public enum InsightSeverity
{
    /// <summary>Costing money or about to hit a limit.</summary>
    Warning,

    /// <summary>A habit worth changing.</summary>
    Tip,

    /// <summary>Something already going well: keep doing it.</summary>
    Strength,
}

public enum InsightCategory
{
    Cost,
    Caching,
    Context,
    Models,
    Tools,
    Workflow,
    Limits,
    Reliability,
}

/// <summary>One lesson from the data: what was seen, why it matters, what to try, and what it could save.</summary>
public sealed record Insight(
    string Id,
    InsightSeverity Severity,
    InsightCategory Category,
    string Title,
    string Detail,
    string Action,
    double? SavingsUsd = null);

/// <summary>One part of the Dojo score, 0-1.</summary>
public sealed record ScorePart(string Name, double Value, double Weight, string Explanation);

/// <summary>The Dojo's verdict on a period: a 0-100 score, the belt it earns, and the lessons behind it.</summary>
public sealed record CoachResult(int Score, string Belt, IReadOnlyList<ScorePart> Parts, IReadOnlyList<Insight> Insights)
{
    public static CoachResult Empty { get; } = new(0, "White", [], []);

    public double PotentialSavingsUsd => Insights.Sum(i => i.SavingsUsd ?? 0);

    public static string BeltFor(int score) => score switch
    {
        >= 90 => "Black",
        >= 80 => "Brown",
        >= 70 => "Blue",
        >= 60 => "Green",
        >= 50 => "Orange",
        >= 40 => "Yellow",
        _ => "White",
    };
}
