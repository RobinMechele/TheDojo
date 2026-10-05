using System.Text.Json;
using System.Text.Json.Serialization;
using TheDojo.Core.Analysis;
using TheDojo.Core.Insights;
using TheDojo.Core.Limits;

namespace TheDojo.Core.Reports;

/// <summary>The whole report as JSON, for dashboards or for comparing periods and engineers in another tool.</summary>
public static class JsonReport
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Write(DojoReport report, CoachResult coach, IReadOnlyList<ProviderLimits>? limits = null, bool includePrompts = false) =>
        JsonSerializer.Serialize(new
        {
            generator = AppIdentity.ProductName,
            generatedAt = DateTimeOffset.Now,
            report = includePrompts ? report : report with { TopPrompts = [] },
            coach,
            limits,
        }, Options);
}
