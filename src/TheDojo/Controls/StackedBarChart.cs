using System.Collections;
using System.Windows;
using System.Windows.Media;

namespace TheDojo.Controls;

/// <summary>One column of a stacked bar chart: its segments bottom-up, in fixed series order.</summary>
public sealed record BarColumn(string Label, string Title, double[] Values);

/// <summary>
/// Columns stacked by series over time (spend per day, split by agent). Bars are capped at 24px with a 2px surface
/// gap between segments, a hairline grid with three round ticks, sparse x labels, and a tooltip per column.
/// </summary>
public sealed class StackedBarChart : ChartBase
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(IEnumerable), typeof(StackedBarChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeriesNamesProperty = DependencyProperty.Register(
        nameof(SeriesNames), typeof(IList), typeof(StackedBarChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeriesBrushesProperty = DependencyProperty.Register(
        nameof(SeriesBrushes), typeof(IList), typeof(StackedBarChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format), typeof(ValueFormat), typeof(StackedBarChart), new FrameworkPropertyMetadata(ValueFormat.Money, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Columns
    {
        get => (IEnumerable?)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public IList? SeriesNames
    {
        get => (IList?)GetValue(SeriesNamesProperty);
        set => SetValue(SeriesNamesProperty, value);
    }

    public IList? SeriesBrushes
    {
        get => (IList?)GetValue(SeriesBrushesProperty);
        set => SetValue(SeriesBrushesProperty, value);
    }

    public ValueFormat Format
    {
        get => (ValueFormat)GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var columns = Columns?.Cast<BarColumn>().ToList() ?? [];
        if (ActualWidth < 40 || ActualHeight < 40)
        {
            return;
        }

        const double axisWidth = 52, labelHeight = 22, top = 8;
        var plot = new Rect(axisWidth, top, Math.Max(1, ActualWidth - axisWidth), Math.Max(1, ActualHeight - labelHeight - top));
        var max = NiceMax(columns.Count == 0 ? 0 : columns.Max(c => c.Values.Sum()));

        // Recessive hairline grid with round ticks.
        var gridPen = new Pen(Grid, 1);
        for (var i = 0; i <= 3; i++)
        {
            var y = Math.Round(plot.Bottom - plot.Height * i / 3) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var tick = Text(Formats.Value(max * i / 3, Format, axis: true), 11, TextMuted);
            dc.DrawText(tick, new Point(axisWidth - 8 - tick.Width, y - tick.Height / 2));
        }

        if (columns.Count == 0)
        {
            return;
        }

        var band = plot.Width / columns.Count;
        var barWidth = Math.Clamp(band * 0.62, 2, 24);
        var brushes = SeriesBrushes?.Cast<Brush>().ToList() ?? [];
        var labelEvery = Math.Max(1, (int)Math.Ceiling(columns.Count / Math.Max(1, plot.Width / 56)));
        int? hovered = Mouse is { } m && plot.Contains(new Point(m.X, plot.Top + 1)) && m.Y <= plot.Bottom + labelHeight
            ? Math.Clamp((int)((m.X - plot.Left) / band), 0, columns.Count - 1)
            : null;

        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var x = plot.Left + band * i + (band - barWidth) / 2;
            if (hovered == i)
            {
                dc.DrawRectangle(Resource("SubtleFillBrush", Brushes.Transparent), null, new Rect(plot.Left + band * i, plot.Top, band, plot.Height));
            }

            var baseY = plot.Bottom;
            var visible = column.Values.Select((v, s) => (v, s)).Where(p => p.v > 0).ToList();
            for (var k = 0; k < visible.Count; k++)
            {
                var (value, series) = visible[k];
                var h = plot.Height * value / max;
                var isTop = k == visible.Count - 1;
                var gap = k > 0 ? 2 : 0; // surface gap between stacked segments
                var rect = new Rect(x, baseY - h, barWidth, Math.Max(0, h - gap));
                var brush = series < brushes.Count ? brushes[series] : TextSecondary;
                if (rect.Height > 0)
                {
                    if (isTop)
                    {
                        dc.DrawGeometry(brush, null, TopRoundedBar(rect, 4));
                    }
                    else
                    {
                        dc.DrawRectangle(brush, null, rect);
                    }
                }

                baseY -= h;
            }

            if (i % labelEvery == 0)
            {
                var label = Text(column.Label, 11, TextMuted);
                dc.DrawText(label, new Point(Math.Clamp(plot.Left + band * i + band / 2 - label.Width / 2, plot.Left, ActualWidth - label.Width), plot.Bottom + 5));
            }
        }

        if (hovered is { } hi)
        {
            var column = columns[hi];
            var names = SeriesNames?.Cast<object>().Select(o => o.ToString() ?? string.Empty).ToList() ?? [];
            var lines = new List<(Brush?, string)>();
            for (var s = 0; s < column.Values.Length; s++)
            {
                lines.Add((s < brushes.Count ? brushes[s] : null, $"{(s < names.Count ? names[s] : "Series " + (s + 1))}  {Formats.Value(column.Values[s], Format)}"));
            }

            if (column.Values.Length > 1)
            {
                lines.Add((null, $"Total  {Formats.Value(column.Values.Sum(), Format)}"));
            }

            DrawTooltip(dc, new Point(plot.Left + band * hi + band / 2, Mouse!.Value.Y), column.Title, lines);
        }
    }
}
