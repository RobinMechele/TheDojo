using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit.Sdk;

namespace TheDojo.UiTests;

/// <summary>
/// Screenshot baselines. Renders an element to a PNG and compares it with <c>Snapshots/&lt;name&gt;.png</c>.
/// A mismatch writes <c>&lt;name&gt;.actual.png</c> and <c>&lt;name&gt;.diff.png</c> next to the baseline.
/// Set <c>DOJO_UPDATE_SNAPSHOTS=1</c> to accept the current rendering as the new baseline.
/// </summary>
internal static class Snapshot
{
    // A pixel counts as changed when any channel moves by more than this (absorbs anti-aliasing noise).
    private const int ChannelTolerance = 40;

    // Share of pixels allowed to differ (font hinting between machines).
    private const double PixelTolerance = 0.002;

    public static string Folder { get; } = Path.Combine(SourceFolder(), "Snapshots");

    public static bool UpdateMode => Environment.GetEnvironmentVariable("DOJO_UPDATE_SNAPSHOTS") is "1" or "true";

    public static void Match(FrameworkElement element, string name)
    {
        var actual = Render(element);
        Directory.CreateDirectory(Folder);
        var baselinePath = Path.Combine(Folder, name + ".png");
        var actualPath = Path.Combine(Folder, name + ".actual.png");
        var diffPath = Path.Combine(Folder, name + ".diff.png");
        File.Delete(actualPath);
        File.Delete(diffPath);

        if (UpdateMode || !File.Exists(baselinePath))
        {
            var isNew = !File.Exists(baselinePath);
            Save(actual, baselinePath);
            if (isNew && !UpdateMode)
            {
                throw new XunitException($"No baseline yet for '{name}'; wrote {baselinePath}. Look at it, then run the test again.");
            }

            return;
        }

        var expected = Load(baselinePath);
        var (message, diff) = Compare(expected, actual);
        if (message is not null)
        {
            Save(actual, actualPath);
            if (diff is not null)
            {
                Save(diff, diffPath);
            }

            throw new XunitException($"'{name}' does not match its baseline: {message}.\n  baseline: {baselinePath}\n  actual:   {actualPath}\n" +
                                     "If the change is intended, rerun with DOJO_UPDATE_SNAPSHOTS=1.");
        }
    }

    public static BitmapSource Render(FrameworkElement element)
    {
        element.UpdateLayout();
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        if (width <= 0 || height <= 0)
        {
            throw new XunitException($"Cannot snapshot an element of size {width}x{height}.");
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }

    public static void Save(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static BitmapSource Load(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Pbgra32, null, 0);
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static (string? Message, BitmapSource? Diff) Compare(BitmapSource expected, BitmapSource actual)
    {
        if (expected.PixelWidth != actual.PixelWidth || expected.PixelHeight != actual.PixelHeight)
        {
            return ($"size {actual.PixelWidth}x{actual.PixelHeight}, expected {expected.PixelWidth}x{expected.PixelHeight}", null);
        }

        var a = Pixels(expected);
        var b = Pixels(actual);
        var diff = new byte[b.Length];
        var changed = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            var delta = Math.Max(Math.Max(Math.Abs(a[i] - b[i]), Math.Abs(a[i + 1] - b[i + 1])), Math.Max(Math.Abs(a[i + 2] - b[i + 2]), Math.Abs(a[i + 3] - b[i + 3])));
            if (delta > ChannelTolerance)
            {
                changed++;
                diff[i + 2] = 255;
                diff[i + 3] = 255;
            }
            else
            {
                diff[i] = (byte)(b[i] / 4);
                diff[i + 1] = (byte)(b[i + 1] / 4);
                diff[i + 2] = (byte)(b[i + 2] / 4);
                diff[i + 3] = 255;
            }
        }

        var share = changed / (double)(a.Length / 4);
        if (share <= PixelTolerance)
        {
            return (null, null);
        }

        var image = BitmapSource.Create(actual.PixelWidth, actual.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, diff, actual.PixelWidth * 4);
        return ($"{changed} pixels differ ({share:P2})", image);
    }

    private static string SourceFolder([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
