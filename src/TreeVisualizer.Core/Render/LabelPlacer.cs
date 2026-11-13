using SkiaSharp;
using TreeVisualizer.Core.Drawing;

namespace TreeVisualizer.Core.Render;

public enum LabelMode
{
    /// <summary>Show labels only where they fit at the current zoom (level of detail).</summary>
    Auto,
    /// <summary>Show every label, shrinking the font to fit (useful for vector posters).</summary>
    All,
    None,
}

public sealed record LabelSettings
{
    public LabelMode Mode { get; init; } = LabelMode.Auto;
    /// <summary>Base font size in output pixels (points for PDF).</summary>
    public float FontPx { get; init; } = 11f;
    /// <summary>Smallest font used in <see cref="LabelMode.All"/>.</summary>
    public float MinFontPx { get; init; } = 0.3f;
    /// <summary>Labels smaller than this get no white halo. Null = backend default (PDF: see Exporter).</summary>
    public float? HaloMinFontPx { get; init; }
}

public enum LabelAlign { Left, Center, Right }

/// <summary>A label resolved for a specific view: screen position, rotation, final text and size.</summary>
public readonly record struct PlacedLabel(string NodeId, string Text, float X, float Y, float RotationDeg, LabelAlign Align, float FontPx);

public readonly record struct ViewTransform(double Scale, double OffsetX, double OffsetY)
{
    public (double X, double Y) ToScreen(double wx, double wy) => (wx * Scale + OffsetX, wy * Scale + OffsetY);
    public (double X, double Y) ToWorld(double sx, double sy) => ((sx - OffsetX) / Scale, (sy - OffsetY) / Scale);

    /// <summary>Centers the scene's full extent in a viewport.</summary>
    public static ViewTransform Fit(Scene scene, double width, double height, double padding = 8) =>
        Fit(scene, 0, 0, width, height, padding);

    /// <summary>Centers the scene's full extent in a rectangle of the viewport.</summary>
    public static ViewTransform Fit(Scene scene, double x, double y, double width, double height, double padding)
    {
        double s = Math.Max(1e-6, (Math.Min(width, height) - 2 * padding) / (2 * scene.Extent));
        return new ViewTransform(s, x + width / 2, y + height / 2);
    }
}

/// <summary>Chooses which labels to draw and how, for a view. Shared by every output backend.</summary>
public static class LabelPlacer
{
    private const float MeasureSize = 100f;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, float> WidthCache = new(StringComparer.Ordinal);
    private static SKFont? _measureFont;

    public static SKTypeface Typeface { get; } =
        SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;

    public const string FontFamilyCss = "'Segoe UI', Arial, sans-serif";

    /// <summary>Text width at a given font size (cached).</summary>
    public static float MeasureWidth(string text, float fontPx)
    {
        lock (Gate)
        {
            if (!WidthCache.TryGetValue(text, out var w))
            {
                _measureFont ??= new SKFont(Typeface, MeasureSize);
                w = _measureFont.MeasureText(text);
                if (WidthCache.Count > 200_000) WidthCache.Clear();
                WidthCache[text] = w;
            }
            return w * fontPx / MeasureSize;
        }
    }

    public static float FontFor(int level, float basePx) =>
        basePx * (level == 0 ? 1.35f : level == 1 ? 1.15f : 1f);

    public static List<PlacedLabel> Place(Scene scene, ViewTransform view, double viewportWidth,
        double viewportHeight, LabelSettings settings)
    {
        var result = new List<PlacedLabel>();
        if (settings.Mode == LabelMode.None) return result;
        double s = view.Scale;

        foreach (var c in scene.Labels)
        {
            float f = FontFor(c.Level, settings.FontPx);
            double across = c.Across * s;
            double along = c.Along * s;
            if (settings.Mode == LabelMode.Auto)
            {
                if (across < f * 1.05) continue;
            }
            else
            {
                f = (float)Math.Min(f, across * 0.9);
                if (f < settings.MinFontPx) continue;
            }

            var (sx, sy) = view.ToScreen(c.X, c.Y);
            double reach = Math.Min(along, 4000);
            if (sx < -reach || sy < -reach || sx > viewportWidth + reach || sy > viewportHeight + reach) continue;

            string text = c.Text;
            float width = MeasureWidth(text, f);
            if (width > along)
            {
                if (settings.Mode == LabelMode.All)
                {
                    f = (float)(f * along / width);
                    if (f < settings.MinFontPx) continue;
                }
                else
                {
                    text = Truncate(text, f, along);
                    if (text.Length == 0) continue;
                }
            }

            float rot;
            LabelAlign align;
            switch (c.Kind)
            {
                case LabelKind.Horizontal:
                    rot = 0; align = LabelAlign.Center;
                    break;
                default:
                    rot = (float)(c.Angle * 180 / Math.PI);
                    bool flip = Math.Cos(c.Angle) < 0;
                    if (flip) rot += 180;
                    align = c.Kind == LabelKind.RadialCentered ? LabelAlign.Center
                        : flip ? LabelAlign.Right : LabelAlign.Left;
                    break;
            }
            result.Add(new PlacedLabel(c.NodeId, text, (float)sx, (float)sy, rot, align, f));
        }
        return result;
    }

    /// <summary>Longest prefix + ellipsis that fits; empty if fewer than 2 characters fit.</summary>
    private static string Truncate(string text, float fontPx, double maxWidth)
    {
        int lo = 0, hi = text.Length - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (MeasureWidth(text[..mid] + "…", fontPx) <= maxWidth) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best < 2 ? "" : text[..best].TrimEnd() + "…";
    }
}
