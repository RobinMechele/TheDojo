using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TheDojo.Controls;

/// <summary>Shared drawing for the charts: theme brushes, text, the hover tooltip and hit testing by mouse position.</summary>
public abstract class ChartBase : FrameworkElement
{
    protected static readonly Typeface UiFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    protected static readonly Typeface UiFaceBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    protected Point? Mouse { get; private set; }

    protected ChartBase()
    {
        SnapsToDevicePixels = true;
        ClipToBounds = false;
    }

    protected static Brush Resource(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    protected static Brush TextMuted => Resource("TextMutedBrush", new SolidColorBrush(Color.FromRgb(0x6B, 0x75, 0x90)));

    protected static Brush TextPrimary => Resource("TextBrush", Brushes.WhiteSmoke);

    protected static Brush TextSecondary => Resource("TextSecondaryBrush", Brushes.Silver);

    protected static Brush Grid => Resource("GridLineBrush", new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x39)));

    protected static Brush Surface => Resource("SurfaceBrush", new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x24)));

    protected static Brush TooltipBackground => Resource("SurfaceRaisedBrush", new SolidColorBrush(Color.FromRgb(0x1A, 0x20, 0x30)));

    protected static Brush TooltipBorder => Resource("BorderStrongBrush", new SolidColorBrush(Color.FromRgb(0x2F, 0x39, 0x50)));

    protected FormattedText Text(string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? UiFaceBold : UiFace, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Mouse = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Mouse = null;
        InvalidateVisual();
    }

    /// <summary>Hit testing everywhere in the plot, not just on painted pixels.</summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        new PointHitTestResult(this, hitTestParameters.HitPoint);

    /// <summary>A two-part tooltip (bold title, then lines) placed beside <paramref name="anchor"/> and kept inside the control.</summary>
    protected void DrawTooltip(DrawingContext dc, Point anchor, string title, IReadOnlyList<(Brush? Swatch, string Text)> lines)
    {
        var titleText = Text(title, 12, TextPrimary, bold: true);
        var lineTexts = lines.Select(l => (l.Swatch, Text: Text(l.Text, 12, TextSecondary))).ToList();
        const double pad = 10, swatch = 10, lineGap = 4;
        var width = Math.Max(titleText.Width, lineTexts.Count == 0 ? 0 : lineTexts.Max(l => l.Text.Width + (l.Swatch is null ? 0 : swatch + 6))) + pad * 2;
        var height = pad * 2 + titleText.Height + lineTexts.Sum(l => l.Text.Height + lineGap);

        var x = anchor.X + 14;
        if (x + width > ActualWidth)
        {
            x = anchor.X - width - 14;
        }

        x = Math.Max(0, x);
        var y = Math.Clamp(anchor.Y - height / 2, 0, Math.Max(0, ActualHeight - height));
        dc.DrawRoundedRectangle(TooltipBackground, new Pen(TooltipBorder, 1), new Rect(x, y, width, height), 8, 8);
        dc.DrawText(titleText, new Point(x + pad, y + pad));
        var ty = y + pad + titleText.Height + lineGap;
        foreach (var (brush, text) in lineTexts)
        {
            var tx = x + pad;
            if (brush is not null)
            {
                dc.DrawRoundedRectangle(brush, null, new Rect(tx, ty + (text.Height - swatch) / 2, swatch, swatch), 3, 3);
                tx += swatch + 6;
            }

            dc.DrawText(text, new Point(tx, ty));
            ty += text.Height + lineGap;
        }
    }

    /// <summary>A bar from the baseline up: 4px rounded data end, square at the baseline.</summary>
    protected static Geometry TopRoundedBar(Rect r, double radius)
    {
        radius = Math.Min(radius, Math.Min(r.Width / 2, r.Height));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(r.Left, r.Bottom), true, true);
            c.LineTo(new Point(r.Left, r.Top + radius), false, false);
            c.ArcTo(new Point(r.Left + radius, r.Top), new Size(radius, radius), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(r.Right - radius, r.Top), false, false);
            c.ArcTo(new Point(r.Right, r.Top + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(r.Right, r.Bottom), false, false);
        }

        g.Freeze();
        return g;
    }

    /// <summary>Clean axis ticks: 0 and three round steps up to at least <paramref name="max"/>.</summary>
    protected static double NiceMax(double max)
    {
        if (max <= 0)
        {
            return 1;
        }

        var step = max / 3;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(step)));
        var normalized = step / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 2.5 ? 2.5 : normalized <= 5 ? 5 : 10;
        return nice * magnitude * 3;
    }
}
