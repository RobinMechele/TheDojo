using System.Globalization;
using TheDojo.Core;
using TheDojo.Core.Analysis;
using TheDojo.Core.Insights;
using TheDojo.Core.Limits;
using TheDojo.Core.Model;
using TheDojo.Core.Reports;

namespace TheDojo.Cli;

/// <summary>
/// <c>dojo</c>: the same analysis as the app, for terminals, scripts and scheduled reports.
/// </summary>
public static class DojoCli
{
    public const string Usage = """
        The Dojo — learn from your AI agent usage.

        Usage:
          dojo summary  [options]                 Key numbers and the top lessons
          dojo report   [options] [--format md|json] [--out FILE] [--prompts]
          dojo export   calls|sessions|tools [options] --out FILE.csv
          dojo limits                             Live plan limits (Claude, Copilot)

        Options:
          --range 24h|7d|30d|90d|all   Period (default 30d)
          --provider claude|copilot    Only one agent
          --project NAME               Only one project
          --claude-home DIR            Claude Code folder (default ~/.claude)
          --copilot-home DIR           Copilot CLI folder (default ~/.copilot)
          --cache DIR                  Where The Dojo keeps its caches
          --offline                    No network: built-in prices, no live limits
        """;

    public sealed record Options(
        string Command,
        string? Subject,
        ReportFilter Filter,
        string Format,
        string? Out,
        bool Prompts,
        bool Offline,
        string? ClaudeHome,
        string? CopilotHome,
        string? Cache);

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken, Func<Options, IDojoService>? serviceFactory = null)
    {
        Options options;
        try
        {
            options = Parse(args);
        }
        catch (ArgumentException ex)
        {
            error.WriteLine(ex.Message);
            error.WriteLine();
            error.WriteLine(Usage);
            return 2;
        }

        if (options.Command is "help" or "--help" or "-h")
        {
            output.WriteLine(Usage);
            return 0;
        }

        var service = serviceFactory?.Invoke(options)
            ?? DojoService.CreateDefault(DataSources.From(options.ClaudeHome, options.CopilotHome), options.Offline, options.Cache);

        switch (options.Command)
        {
            case "limits":
                var limits = await service.FetchLimitsAsync(manual: true, cancellationToken);
                WriteLimits(output, limits, DateTimeOffset.Now);
                return 0;

            case "summary":
            {
                var snapshot = await service.LoadAsync(options.Filter, null, cancellationToken);
                WriteSummary(output, snapshot.Report, snapshot.Coach);
                return 0;
            }

            case "report":
            {
                var limits2 = options.Offline ? [] : await service.FetchLimitsAsync(manual: false, cancellationToken);
                var snapshot = await service.LoadAsync(options.Filter, limits2, cancellationToken);
                var text = options.Format == "json"
                    ? JsonReport.Write(snapshot.Report, snapshot.Coach, limits2, options.Prompts)
                    : MarkdownReport.Write(snapshot.Report, snapshot.Coach, limits2, options.Prompts);
                return Emit(text, options.Out, output);
            }

            case "export":
            {
                var snapshot = await service.LoadAsync(options.Filter, null, cancellationToken);
                var csv = options.Subject switch
                {
                    "calls" => CsvExport.Calls(snapshot.Data, snapshot.Costs, snapshot.Report),
                    "sessions" => CsvExport.Sessions(snapshot.Report),
                    "tools" => CsvExport.Tools(snapshot.Data, snapshot.Report),
                    _ => null,
                };
                if (csv is null)
                {
                    error.WriteLine("Export what? Use: dojo export calls|sessions|tools --out FILE.csv");
                    return 2;
                }

                return Emit(csv, options.Out, output);
            }

            default:
                error.WriteLine($"Unknown command '{options.Command}'.");
                error.WriteLine();
                error.WriteLine(Usage);
                return 2;
        }
    }

    internal static Options Parse(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "summary";
        string? subject = null;
        var range = UsageRange.Days30;
        Provider? provider = null;
        string? project = null, outPath = null, claudeHome = null, copilotHome = null, cache = null;
        var format = "md";
        bool prompts = false, offline = false;

        for (var i = 1; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i].ToLowerInvariant())
            {
                case "--range":
                    range = ParseRange(Value());
                    break;
                case "--provider":
                    var p = Value();
                    provider = Enum.TryParse<Provider>(p, ignoreCase: true, out var parsed) ? parsed : throw new ArgumentException($"Unknown provider '{p}'.");
                    break;
                case "--project":
                    project = Value();
                    break;
                case "--format":
                    var f = Value().ToLowerInvariant();
                    format = f switch
                    {
                        "md" or "markdown" => "md",
                        "json" => "json",
                        _ => throw new ArgumentException($"Unknown format '{f}'. Use md or json."),
                    };
                    break;
                case "--out":
                    outPath = Value();
                    break;
                case "--prompts":
                    prompts = true;
                    break;
                case "--offline":
                    offline = true;
                    break;
                case "--claude-home":
                    claudeHome = Value();
                    break;
                case "--copilot-home":
                    copilotHome = Value();
                    break;
                case "--cache":
                    cache = Value();
                    break;
                default:
                    if (subject is null && !args[i].StartsWith('-'))
                    {
                        subject = args[i].ToLowerInvariant();
                        break;
                    }

                    throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
        }

        return new Options(command, subject, new ReportFilter(range, provider, project), format, outPath, prompts, offline, claudeHome, copilotHome, cache);
    }

    internal static UsageRange ParseRange(string value) => value.ToLowerInvariant() switch
    {
        "24h" or "1d" or "day" => UsageRange.Past24Hours,
        "7d" or "week" => UsageRange.Days7,
        "30d" or "month" => UsageRange.Days30,
        "90d" or "quarter" => UsageRange.Days90,
        "all" => UsageRange.All,
        _ => throw new ArgumentException($"Unknown range '{value}'. Use 24h, 7d, 30d, 90d or all."),
    };

    private static int Emit(string text, string? path, TextWriter output)
    {
        if (path is null)
        {
            output.Write(text);
            return 0;
        }

        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        output.WriteLine($"Wrote {full}");
        return 0;
    }

    internal static void WriteSummary(TextWriter o, DojoReport r, CoachResult coach)
    {
        var t = r.Totals;
        o.WriteLine($"{AppIdentity.ProductName} · {MarkdownReport.RangeName(r.Filter.Range)}{(r.Filter.Provider is { } p ? " · " + p : string.Empty)}{(r.Filter.Project is { } proj ? " · " + proj : string.Empty)}");
        o.WriteLine($"Score {coach.Score}/100 · {coach.Belt} belt");
        o.WriteLine();
        Line(o, "Spend", UsageFormat.Money(t.CostUsd) + (t.CostTrend is { } trend ? $"  ({UsageFormat.Trend(trend)} vs previous)" : string.Empty));
        Line(o, "Tokens", $"{UsageFormat.Compact(t.Tokens.Total)}  (cache hit {UsageFormat.Percent(t.CacheHitRate)}, saved {UsageFormat.Money(t.CacheSavingsUsd)})");
        Line(o, "Sessions", $"{t.Sessions} in {t.Projects} projects · {t.Prompts} prompts · {t.Calls} model calls");
        Line(o, "Per prompt", UsageFormat.Money(t.CostPerPrompt));
        Line(o, "Tools", $"{t.ToolCalls} calls · {t.ToolErrors} errors · {t.ToolDenials} denied · {t.Interrupts} interrupts");
        Line(o, "Code", $"+{t.LinesAdded.ToString("N0", CultureInfo.InvariantCulture)} / -{t.LinesRemoved.ToString("N0", CultureInfo.InvariantCulture)} lines · {t.PullRequests} PRs");
        o.WriteLine();

        if (r.Models.Count > 0)
        {
            o.WriteLine("Models");
            foreach (var m in r.Models.Take(6))
            {
                o.WriteLine($"  {m.Model,-28} {UsageFormat.Money(m.CostUsd),10}  {UsageFormat.Percent(m.Share),5}  {m.Calls,6} calls");
            }

            o.WriteLine();
        }

        if (coach.Insights.Count > 0)
        {
            o.WriteLine("Lessons");
            foreach (var i in coach.Insights.Take(6))
            {
                var mark = i.Severity switch { InsightSeverity.Warning => "!", InsightSeverity.Tip => "*", _ => "+" };
                o.WriteLine($"  {mark} {i.Title}{(i.SavingsUsd is > 0 and var s ? $"  (≈ {UsageFormat.Money(s)})" : string.Empty)}");
                o.WriteLine($"    {i.Action}");
            }
        }
    }

    private static void WriteLimits(TextWriter o, IReadOnlyList<ProviderLimits> limits, DateTimeOffset now)
    {
        if (limits.Count == 0)
        {
            o.WriteLine("No limit sources (offline).");
        }

        foreach (var p in limits)
        {
            o.WriteLine($"{p.Provider}{(p.Plan is null ? string.Empty : $" ({p.Plan})")}");
            if (p.Error is not null)
            {
                o.WriteLine("  " + p.Error);
            }

            if (p.Note is not null)
            {
                o.WriteLine("  " + p.Note);
            }

            foreach (var w in p.Windows)
            {
                var resets = w.ResetsAt is { } at ? "resets in " + UsageFormat.ResetsIn(at - now) : string.Empty;
                var pace = w.PaceAt(now) == LimitPace.Ahead ? "  ahead of pace" : string.Empty;
                o.WriteLine($"  {w.Label,-16} {w.PercentUsed,5:0}% used  {resets}{pace}");
            }
        }
    }

    private static void Line(TextWriter o, string name, string value) => o.WriteLine($"  {name,-11} {value}");
}
