using System.Globalization;

namespace TheDojo;

/// <summary>
/// How times and dates are shown: in the app clock's time zone and in English month/day names, whatever the
/// machine's regional format (the UI is English; "okt 3" next to "Spend" reads as a bug).
/// </summary>
public static class Display
{
    public static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Set from the app's <see cref="TimeProvider"/>; tests pin it to UTC.</summary>
    public static TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

    public static DateTime Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Zone).DateTime;

    public static string Format(DateTimeOffset at, string format) => Local(at).ToString(format, Culture);

    public static string Format(DateTime at, string format) => at.ToString(format, Culture);
}
