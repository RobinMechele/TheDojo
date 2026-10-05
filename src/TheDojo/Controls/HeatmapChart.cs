using System.Collections;
using System.Windows;
using System.Windows.Media;

namespace TheDojo.Controls;

/// <summary>One cell of the week heatmap. <see cref="Row"/> 0 is Monday.</summary>
public sealed record HeatmapCell(int Row, int Column, double Value, string Title, string Detail);

/// <summary>
/// Weekday × hour grid on a single-hue sequential ramp (blue; empty cells recede toward the surface), with
/// row and column labels and a tooltip per cell.
/// </summary>
public sealed class HeatmapChart : ChartBase
{
    private static readonly string[] Days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    private static readonly string[] Ramp = ["Seq1Color", "Seq2Color", "Seq3Color", "Seq4Color", "Seq5Color"];

    public static readonly DependencyProperty CellsProperty = DependencyProperty.Register(
        nameof(Cells), typeof(IEnumerable), typeof(HeatmapChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Cells
    {
        get => (IEnumerable?)GetValue(CellsProperty);
        set => SetValue(CellsProperty, value);
    }

    private static Color ColorOf(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) is Color c ? c : fallback;

    protected override void OnRender(DrawingContext dc)
    {
        var cells = Cells?.Cast<HeatmapCell>().ToList() ?? [];
        const double rowLabel = 36, colLabel = 20, gap = 2;
        var cellW = (ActualWidth - rowLabel) / 24;
        var cellH = (ActualHeight - colLabel) / 7;
        if (cellW < 4 || cellH < 4)
        {
            return;
        }

        var max = cells.Count == 0 ? 0 : cells.Max(c => c.Value);
        var empty = new SolidColorBrush(ColorOf("Seq0Color", Color.FromRgb(0x1A, 0x22, 0x34)));
        var ramp = Ramp.Select(k => (Brush)new SolidColorBrush(ColorOf(k, Colors.SteelBlue))).ToList();

        for (var r = 0; r < 7; r++)
        {
            var label = Text(Days[r], 11, TextMuted);
            dc.DrawText(label, new Point(0, colLabel + cellH * r + (cellH - label.Height) / 2));
        }

        for (var h = 0; h < 24; h += 3)
        {
            var label = Text(h.ToString("00"), 11, TextMuted);
            dc.DrawText(label, new Point(rowLabel + cellW * h, 0));
        }

        HeatmapCell? hovered = null;
        Rect hoveredRect = default;
        foreach (var cell in cells)
        {
            var rect = new Rect(rowLabel + cellW * cell.Column + gap / 2, colLabel + cellH * cell.Row + gap / 2, cellW - gap, cellH - gap);
            Brush fill = empty;
            if (max > 0 && cell.Value > 0)
            {
                // Square-root scale so a few heavy hours don't wash out the rest.
                var step = (int)Math.Min(ramp.Count - 1, Math.Floor(Math.Sqrt(cell.Value / max) * ramp.Count));
                fill = ramp[step];
            }

            dc.DrawRoundedRectangle(fill, null, rect, 3, 3);
            if (Mouse is { } m && rect.Contains(m))
            {
                hovered = cell;
                hoveredRect = rect;
            }
        }

        if (hovered is not null)
        {
            dc.DrawRoundedRectangle(null, new Pen(TextPrimary, 1.5), hoveredRect, 3, 3);
            DrawTooltip(dc, new Point(hoveredRect.Right, hoveredRect.Top + hoveredRect.Height / 2), hovered.Title, [(null, hovered.Detail)]);
        }
    }
}
