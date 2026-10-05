using System.Windows;
using System.Windows.Media;

namespace TheDojo.Controls;

/// <summary>
/// A limit meter: the fill carries severity (blue, then warning at 75%, critical at 90%) on a track of the same
/// ramp, with an optional tick showing how much of the window has elapsed (the "even pace" mark).
/// </summary>
public sealed class Meter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Meter), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HigherIsBetterProperty = DependencyProperty.Register(
        nameof(HigherIsBetter), typeof(bool), typeof(Meter), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkerProperty = DependencyProperty.Register(
        nameof(Marker), typeof(double?), typeof(Meter), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Percent used, 0-100.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>A score rather than a limit: low values carry the severity (critical under 40, warning under 70).</summary>
    public bool HigherIsBetter
    {
        get => (bool)GetValue(HigherIsBetterProperty);
        set => SetValue(HigherIsBetterProperty, value);
    }

    /// <summary>Percent of the window elapsed, 0-100, or null.</summary>
    public double? Marker
    {
        get => (double?)GetValue(MarkerProperty);
        set => SetValue(MarkerProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 10);

    private static Brush Find(string key, Brush fallback) => Application.Current?.TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        var h = ActualHeight;
        var w = ActualWidth;
        var radius = h / 2;
        dc.DrawRoundedRectangle(Find("TrackBrush", Brushes.DimGray), null, new Rect(0, 0, w, h), radius, radius);

        var fill = HigherIsBetter
            ? Value < 40 ? Find("CriticalBrush", Brushes.IndianRed) : Value < 70 ? Find("WarningBrush", Brushes.Goldenrod) : Find("SeriesBlueBrush", Brushes.SteelBlue)
            : Value >= 90 ? Find("CriticalBrush", Brushes.IndianRed) : Value >= 75 ? Find("WarningBrush", Brushes.Goldenrod) : Find("SeriesBlueBrush", Brushes.SteelBlue);
        var fw = w * Math.Clamp(Value, 0, 100) / 100;
        if (fw > 0)
        {
            dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, Math.Max(fw, h), h), radius, radius);
        }

        if (Marker is { } marker)
        {
            var x = Math.Round(w * Math.Clamp(marker, 0, 100) / 100) + 0.5;
            dc.DrawLine(new Pen(Find("TextBrush", Brushes.White), 2), new Point(x, -3), new Point(x, h + 3));
        }
    }
}

/// <summary>A thin horizontal share bar for ranked lists (models, tools, projects).</summary>
public sealed class ShareBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ShareBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(ShareBar), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0-1.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 6);

    protected override void OnRender(DrawingContext dc)
    {
        var h = ActualHeight;
        dc.DrawRoundedRectangle(Application.Current?.TryFindResource("TrackBrush") as Brush ?? Brushes.DimGray, null, new Rect(0, 0, ActualWidth, h), h / 2, h / 2);
        var w = ActualWidth * Math.Clamp(Value, 0, 1);
        if (w > 0)
        {
            dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(w, h), h), h / 2, h / 2);
        }
    }
}
