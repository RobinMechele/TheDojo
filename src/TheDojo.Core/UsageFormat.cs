using System.Globalization;

namespace TheDojo.Core;

/// <summary>Short, human-facing renderings of usage figures, independent of the machine's regional format.</summary>
public static class UsageFormat
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Compact count with three significant digits: 6.46K, 476M, 2.06B.</summary>
    public static string Compact(double value)
    {
        var sign = value < 0 ? "-" : string.Empty;
        value = Math.Abs(value);
        var (scaled, suffix) = value switch
        {
            >= 1e9 => (value / 1e9, "B"),
            >= 1e6 => (value / 1e6, "M"),
            >= 1e3 => (value / 1e3, "K"),
            _ => (value, string.Empty),
        };

        var digits = scaled >= 100 || suffix.Length == 0 ? 0 : scaled >= 10 ? 1 : 2;
        return sign + scaled.ToString("0." + new string('#', digits), Invariant).TrimEnd('.') + suffix;
    }

    /// <summary>Dollars as "$1,080.76"; amounts under a cent as "$0.004".</summary>
    public static string Money(double usd)
    {
        if (usd != 0 && Math.Abs(usd) < 0.01)
        {
            return (usd < 0 ? "-$" : "$") + Math.Abs(usd).ToString("0.000", Invariant);
        }

        return (usd < 0 ? "-$" : "$") + Math.Abs(usd).ToString("N2", Us);
    }

    public static string Percent(double share) => (share * 100).ToString(share is > 0 and < 0.1 ? "0.#" : "0", Invariant) + "%";

    /// <summary>A length of time: "45s", "12m", "2h 3m", "3d 4h".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
        {
            return "0s";
        }

        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m" : $"{Math.Max(1, (int)span.TotalSeconds)}s";
    }

    public static string Duration(long milliseconds) => Duration(TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>Time until a limit resets, e.g. "2h 3m", "3d 4h", "12m", "now".</summary>
    public static string ResetsIn(TimeSpan remaining) => remaining <= TimeSpan.Zero ? "now" : Duration(remaining);

    /// <summary>A signed trend: "+25%", "-8%".</summary>
    public static string Trend(double change) => (change >= 0 ? "+" : "-") + Percent(Math.Abs(change));
}
