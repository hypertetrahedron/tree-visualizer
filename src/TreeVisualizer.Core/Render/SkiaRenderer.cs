using SkiaSharp;
using TreeVisualizer.Core.Drawing;

namespace TreeVisualizer.Core.Render;

public sealed record OverlaySettings
{
    public bool Title { get; init; } = true;
    public bool Legend { get; init; } = true;
    /// <summary>Size (px) the overlay text scales with; null = the smaller canvas dimension.</summary>
    public float? ScaleBasis { get; init; }
}

/// <summary>
/// Draws a <see cref="Scene"/> onto any <see cref="SKCanvas"/> (window, raster image or PDF page).
/// Geometry is converted to one cached <see cref="SKPath"/> per layer, so panning and zooming are cheap.
/// </summary>
public sealed class SkiaRenderer : IDisposable
{
    private readonly Dictionary<Layer, SKPath> _paths = new();
    private readonly object _gate = new();
    private bool _disposed;

    public SkiaRenderer(Drawing.Scene scene) => Scene = scene;

    public Drawing.Scene Scene { get; }

    public static SKColor ToSk(Rgba c) => new(c.R, c.G, c.B, c.A);

    public void Draw(SKCanvas canvas, ViewTransform view, float width, float height,
        LabelSettings labels, OverlaySettings? overlay = null, IReadOnlyCollection<string>? highlight = null)
    {
        // Callers may be on a render thread while the UI thread swaps scenes; serialize use of cached paths.
        lock (_gate)
        {
            if (_disposed) return;
            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, width, height));
            using (var bg = new SKPaint { Color = ToSk(Scene.Background) })
                canvas.DrawRect(new SKRect(0, 0, width, height), bg);
            DrawCore(canvas, view, width, height, labels, overlay, highlight);
            canvas.Restore();
        }
    }

    private void DrawCore(SKCanvas canvas, ViewTransform view, float width, float height,
        LabelSettings labels, OverlaySettings? overlay, IReadOnlyCollection<string>? highlight)
    {
        float s = (float)view.Scale;

        canvas.Save();
        canvas.Translate((float)view.OffsetX, (float)view.OffsetY);
        canvas.Scale(s);
        using (var paint = new SKPaint { IsAntialias = true })
        {
            foreach (var layer in Scene.DrawOrder)
            {
                var path = GetPath(layer);
                var st = layer.Style;
                if (st.Fill is Rgba fill)
                {
                    paint.Style = SKPaintStyle.Fill;
                    paint.Color = ToSk(fill);
                    paint.PathEffect = null;
                    canvas.DrawPath(path, paint);
                }
                if (st.Stroke is Rgba stroke && st.StrokeWidth > 0)
                {
                    paint.Style = SKPaintStyle.Stroke;
                    paint.Color = ToSk(stroke);
                    paint.StrokeWidth = st.StrokeWidth / s;
                    paint.StrokeCap = SKStrokeCap.Butt;
                    using var dash = st.Dash == null ? null : SKPathEffect.CreateDash(st.Dash.Select(d => d / s).ToArray(), 0);
                    paint.PathEffect = dash;
                    canvas.DrawPath(path, paint);
                    paint.PathEffect = null;
                }
            }
        }
        canvas.Restore();

        DrawLabels(canvas, LabelPlacer.Place(Scene, view, width, height, labels), highlight, labels.HaloMinFontPx ?? 0);
        if (overlay != null) DrawOverlay(canvas, overlay, width, height);
    }

    private void DrawLabels(SKCanvas canvas, List<PlacedLabel> placed, IReadOnlyCollection<string>? highlight, float haloMin)
    {
        if (placed.Count == 0) return;
        using var font = new SKFont(LabelPlacer.Typeface, 11) { Subpixel = true, Edging = SKFontEdging.Antialias };
        using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = ToSk(Scene.LabelHalo), StrokeJoin = SKStrokeJoin.Round };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = ToSk(Scene.LabelColor) };
        using var hiFill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0xC0, 0x1A, 0x1A) };
        foreach (var l in placed)
        {
            font.Size = l.FontPx;
            halo.StrokeWidth = Math.Max(1.5f, l.FontPx * 0.28f);
            var align = l.Align switch { LabelAlign.Center => SKTextAlign.Center, LabelAlign.Right => SKTextAlign.Right, _ => SKTextAlign.Left };
            canvas.Save();
            canvas.Translate(l.X, l.Y);
            if (l.RotationDeg != 0) canvas.RotateDegrees(l.RotationDeg);
            float baseline = l.FontPx * 0.35f;
            if (l.FontPx >= haloMin) canvas.DrawText(l.Text, 0, baseline, align, font, halo);
            canvas.DrawText(l.Text, 0, baseline, align, font, highlight != null && highlight.Contains(l.NodeId) ? hiFill : fill);
            canvas.Restore();
        }
    }

    private void DrawOverlay(SKCanvas canvas, OverlaySettings overlay, float width, float height)
    {
        var layout = OverlayLayout.Compute(Scene, overlay, width, height);
        if (layout.IsEmpty) return;
        using var regular = new SKFont(LabelPlacer.Typeface, 11);
        using var bold = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? LabelPlacer.Typeface, 11);
        using var paint = new SKPaint { IsAntialias = true };
        foreach (var item in layout.Items)
        {
            paint.Color = ToSk(item.Color);
            if (item.Kind == OverlayItemKind.Swatch)
            {
                float r = item.Size * 0.18f;
                canvas.DrawRoundRect(item.X, item.Y, item.Size, item.Size, r, r, paint);
            }
            else
            {
                var font = item.Bold ? bold : regular;
                font.Size = item.Size;
                canvas.DrawText(item.Text, item.X, item.Y, SKTextAlign.Left, font, paint);
            }
        }
    }

    private SKPath GetPath(Layer layer)
    {
        if (_paths.TryGetValue(layer, out var p)) return p;
        p = new SKPath();
        foreach (var prim in layer.Prims) Append(p, prim);
        _paths[layer] = p;
        return p;
    }

    internal static void Append(SKPath p, Prim prim)
    {
        switch (prim.Kind)
        {
            case PrimKind.Line:
                p.MoveTo((float)prim.P0, (float)prim.P1);
                p.LineTo((float)prim.P2, (float)prim.P3);
                break;
            case PrimKind.Circle:
                p.AddCircle((float)prim.P0, (float)prim.P1, (float)prim.P2);
                break;
            case PrimKind.Arc:
            {
                var oval = Oval(prim.P0);
                foreach (var (a0, a1) in Geometry.SplitSweep(prim.P1, prim.P2))
                    p.AddArc(oval, Deg(a0), Deg(a1 - a0));
                break;
            }
            case PrimKind.Wedge:
            {
                double r0 = prim.P0, r1 = prim.P1;
                var outer = Oval(r1);
                var inner = Oval(r0);
                foreach (var (a0, a1) in Geometry.SplitSweep(prim.P2, prim.P3))
                {
                    float sweep = Deg(a1 - a0);
                    p.ArcTo(outer, Deg(a0), sweep, true);
                    if (r0 > 1e-9) p.ArcTo(inner, Deg(a1), -sweep, false);
                    else p.LineTo(0, 0);
                    p.Close();
                }
                break;
            }
        }
    }

    private static SKRect Oval(double r) => new((float)-r, (float)-r, (float)r, (float)r);
    private static float Deg(double rad) => (float)(rad * 180 / Math.PI);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var p in _paths.Values) p.Dispose();
            _paths.Clear();
        }
    }
}

public static class Geometry
{
    /// <summary>Splits an angular range into pieces of at most π so arcs never degenerate.</summary>
    public static IEnumerable<(double A0, double A1)> SplitSweep(double a0, double a1)
    {
        double sweep = a1 - a0;
        if (sweep <= 0) yield break;
        int pieces = Math.Max(1, (int)Math.Ceiling(sweep / Math.PI - 1e-9));
        double step = sweep / pieces;
        for (int i = 0; i < pieces; i++) yield return (a0 + i * step, i == pieces - 1 ? a1 : a0 + (i + 1) * step);
    }
}
