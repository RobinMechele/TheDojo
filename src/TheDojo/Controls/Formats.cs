using TheDojo.Core;

namespace TheDojo.Controls;

public enum ValueFormat
{
    Money,
    Count,
    Percent,
    Tokens,
}

internal static class Formats
{
    public static string Value(double value, ValueFormat format, bool axis = false) => format switch
    {
        ValueFormat.Money => axis && value >= 10 ? "$" + UsageFormat.Compact(Math.Round(value)) : UsageFormat.Money(value),
        ValueFormat.Percent => value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%",
        _ => UsageFormat.Compact(value),
    };
}
