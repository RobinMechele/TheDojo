using System.Collections;
using System.Windows;
using System.Windows.Media;

namespace TheDojo.Controls;

public sealed record LinePoint(DateTimeOffset At, double Value, string? Note = null);

public sealed record LineSeries(string Name, Brush Brush, IReadOnlyList<LinePoint> Points);

/// <summary>
/// One or more series over time on one axis: 2px lines, a ~10% area wash for a single series, markers with a
/// surface ring, and a crosshair with a tooltip of every series' nearest value.
/// </summary>
public sealed class LineChart : ChartBase
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IEnumerable), typeof(LineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format), typeof(ValueFormat), typeof(LineChart), new FrameworkPropertyMetadata(ValueFormat.Tokens, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(
        nameof(MaxValue), typeof(double), typeof(LineChart), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowMarkersProperty = DependencyProperty.Register(
        nameof(ShowMarkers), typeof(bool), typeof(LineChart), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Series
    {
        get => (IEnumerable?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public ValueFormat Format
    {
        get => (ValueFormat)GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    /// <summary>A fixed top of the axis (e.g. 100 for percentages); 0 picks one from the data.</summary>
    public double MaxValue
    {
        get => (double)GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    public bool ShowMarkers
    {
        get => (bool)GetValue(ShowMarkersProperty);
        set => SetValue(ShowMarkersProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var series = Series?.Cast<LineSeries>().Where(s => s.Points.Count > 0).ToList() ?? [];
        const double axisWidth = 52, labelHeight = 22, top = 8, right = 8;
        if (ActualWidth < 80 || ActualHeight < 50)
        {
            return;
        }

        var plot = new Rect(axisWidth, top, ActualWidth - axisWidth - right, ActualHeight - labelHeight - top);
        var max = MaxValue > 0 ? MaxValue : NiceMax(series.Count == 0 ? 0 : series.Max(s => s.Points.Max(p => p.Value)));
        var gridPen = new Pen(Grid, 1);
        for (var i = 0; i <= 3; i++)
        {
            var y = Math.Round(plot.Bottom - plot.Height * i / 3) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var tick = Text(Formats.Value(max * i / 3, Format, axis: true), 11, TextMuted);
            dc.DrawText(tick, new Point(axisWidth - 8 - tick.Width, y - tick.Height / 2));
        }

        if (series.Count == 0)
        {
            return;
        }

        var start = series.Min(s => s.Points.Min(p => p.At));
        var end = series.Max(s => s.Points.Max(p => p.At));
        var span = Math.Max(1, (end - start).TotalSeconds);
        double X(DateTimeOffset at) => plot.Left + plot.Width * (at - start).TotalSeconds / span;
        double Y(double v) => plot.Bottom - plot.Height * Math.Clamp(v / max, 0, 1);

        // Time labels: start, middle, end.
        var sameDay = Display.Local(start).Date == Display.Local(end).Date;
        foreach (var at in new[] { start, start + (end - start) / 2, end })
        {
            var label = Text(Display.Format(at, sameDay ? "HH:mm" : "MMM d"), 11, TextMuted);
            dc.DrawText(label, new Point(Math.Clamp(X(at) - label.Width / 2, plot.Left, ActualWidth - label.Width), plot.Bottom + 5));
        }

        foreach (var s in series)
        {
            var ordered = s.Points.OrderBy(p => p.At).ToList();
            var line = new StreamGeometry();
            using (var c = line.Open())
            {
                c.BeginFigure(new Point(X(ordered[0].At), Y(ordered[0].Value)), false, false);
                foreach (var p in ordered.Skip(1))
                {
                    c.LineTo(new Point(X(p.At), Y(p.Value)), true, true);
                }
            }

            if (series.Count == 1 && ordered.Count > 1)
            {
                var area = new StreamGeometry();
                using (var c = area.Open())
                {
                    c.BeginFigure(new Point(X(ordered[0].At), plot.Bottom), true, true);
                    foreach (var p in ordered)
                    {
                        c.LineTo(new Point(X(p.At), Y(p.Value)), false, true);
                    }

                    c.LineTo(new Point(X(ordered[^1].At), plot.Bottom), false, false);
                }

                var wash = s.Brush.Clone();
                wash.Opacity = 0.1;
                dc.DrawGeometry(wash, null, area);
            }

            dc.DrawGeometry(null, new Pen(s.Brush, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
            if (ShowMarkers && ordered.Count <= 60)
            {
                foreach (var p in ordered)
                {
                    dc.DrawEllipse(s.Brush, new Pen(Surface, 2), new Point(X(p.At), Y(p.Value)), 4, 4);
                }
            }
        }

        if (Mouse is { } m && plot.Contains(m))
        {
            var at = start + TimeSpan.FromSeconds((m.X - plot.Left) / plot.Width * span);
            dc.DrawLine(new Pen(TextMuted, 1), new Point(m.X, plot.Top), new Point(m.X, plot.Bottom));
            var lines = new List<(Brush?, string)>();
            DateTimeOffset? shown = null;
            foreach (var s in series)
            {
                var nearest = s.Points.MinBy(p => Math.Abs((p.At - at).TotalSeconds))!;
                shown ??= nearest.At;
                dc.DrawEllipse(s.Brush, new Pen(Surface, 2), new Point(X(nearest.At), Y(nearest.Value)), 5, 5);
                lines.Add((s.Brush, $"{s.Name}  {Formats.Value(nearest.Value, Format)}{(nearest.Note is null ? string.Empty : "  · " + nearest.Note)}"));
            }

            DrawTooltip(dc, m, Display.Format(shown!.Value, sameDay ? "HH:mm:ss" : "ddd MMM d, HH:mm"), lines);
        }
    }
}
