using System.Globalization;
using System.Security;
using System.Text;
using SkiaSharp;
using TreeVisualizer.Core.Drawing;

namespace TreeVisualizer.Core.Render;

public enum ExportFormat { Png, Pdf, Svg }

public sealed record ExportSettings
{
    /// <summary>Output width in pixels (PNG/SVG) or points (PDF).</summary>
    public int Width { get; init; } = 4000;
    /// <summary>Output height; null = same as width.</summary>
    public int? Height { get; init; }
    public LabelSettings Labels { get; init; } = new();
    public OverlaySettings Overlay { get; init; } = new();
    /// <summary>PNG pixel density metadata is not written; this only guards memory use.</summary>
    public long MaxPngPixels { get; init; } = 16384L * 16384L;
    /// <summary>
    /// PDF only, when <see cref="LabelSettings.HaloMinFontPx"/> is not set: labels below this size (points) get no
    /// white halo. Skia writes stroked text to PDF as outlines, so halos on thousands of tiny labels dominate the
    /// file size (10k-node poster: 36 MB with all halos, 7 MB with this default).
    /// </summary>
    public float PdfHaloMinFontPt { get; init; } = 4f;
}

public static class Exporter
{
    /// <summary>
    /// Computes the canvas size and view: the tree gets a Width x Height area, and the title band and
    /// legend column (if any) are added above and to the left so they never cover the tree.
    /// </summary>
    public static (int Width, int Height, ViewTransform View, ExportSettings Settings) LayoutCanvas(Drawing.Scene scene, ExportSettings settings)
    {
        int w = Math.Max(16, settings.Width);
        int h = Math.Max(16, settings.Height ?? settings.Width);
        settings = settings with { Overlay = settings.Overlay with { ScaleBasis = Math.Min(w, h) } };
        var o = OverlayLayout.Compute(scene, settings.Overlay, w, h);
        int left = (int)Math.Ceiling(o.LegendWidth);
        int top = (int)Math.Ceiling(o.TitleHeight);
        // Legend rows that extend below the title band sit beside the tree; make sure they fit vertically.
        float legendBottom = o.Items.Count == 0 ? 0 : o.Items.Max(i => i.Y + i.Size * 0.4f);
        int totalH = Math.Max(h + top, (int)Math.Ceiling(legendBottom) + 8);
        var view = ViewTransform.Fit(scene, left, top, w, totalH - top, Math.Min(w, h) * 0.02);
        return (w + left, totalH, view, settings);
    }

    public static ExportFormat FormatFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => ExportFormat.Png,
        ".pdf" => ExportFormat.Pdf,
        ".svg" => ExportFormat.Svg,
        var ext => throw new ArgumentException($"Unsupported output format '{ext}'. Use .png, .pdf or .svg."),
    };

    public static void Export(Drawing.Scene scene, string path, ExportSettings settings)
    {
        var format = FormatFromPath(path);
        using var stream = File.Create(path);
        Export(scene, stream, format, settings);
    }

    public static void Export(Drawing.Scene scene, Stream stream, ExportFormat format, ExportSettings settings)
    {
        (int w, int h, var view, settings) = LayoutCanvas(scene, settings);
        switch (format)
        {
            case ExportFormat.Png:
            {
                if ((long)w * h > settings.MaxPngPixels)
                    throw new InvalidOperationException(
                        $"PNG of {w}x{h} exceeds the {Math.Sqrt(settings.MaxPngPixels):F0}px² limit; use PDF or SVG for posters.");
                using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul))
                    ?? throw new InvalidOperationException($"Could not allocate a {w}x{h} image.");
                using (var renderer = new SkiaRenderer(scene))
                    renderer.Draw(surface.Canvas, view, w, h, settings.Labels, settings.Overlay);
                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                data.SaveTo(stream);
                break;
            }
            case ExportFormat.Pdf:
            {
                using var doc = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata
                {
                    Title = scene.Title ?? "Tree", Creator = "Tree Visualizer", Producer = "Tree Visualizer", RasterDpi = 300,
                }) ?? throw new InvalidOperationException("Could not create PDF document.");
                var canvas = doc.BeginPage(w, h);
                var labels = settings.Labels.HaloMinFontPx == null
                    ? settings.Labels with { HaloMinFontPx = settings.PdfHaloMinFontPt }
                    : settings.Labels;
                using (var renderer = new SkiaRenderer(scene))
                    renderer.Draw(canvas, view, w, h, labels, settings.Overlay);
                doc.EndPage();
                doc.Close();
                break;
            }
            case ExportFormat.Svg:
                SvgWriter.Write(scene, stream, view, w, h, settings);
                break;
        }
    }
}

/// <summary>Writes a scene as compact SVG: one &lt;path&gt; per layer, labels as &lt;text&gt;.</summary>
public static class SvgWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Write(Drawing.Scene scene, Stream stream, ViewTransform view, int w, int h, ExportSettings settings)
    {
        using var sw = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
        sw.WriteLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\">");
        if (!string.IsNullOrEmpty(scene.Title)) sw.WriteLine($"<title>{Esc(scene.Title)}</title>");
        WriteScene(sw, scene, view, w, h, settings.Labels);
        WriteOverlay(sw, scene, settings.Overlay, w, h);
        sw.WriteLine("</svg>");
    }

    /// <summary>Writes a scene's background, geometry and labels for a w x h viewport.</summary>
    internal static void WriteScene(StreamWriter sw, Drawing.Scene scene, ViewTransform view, int w, int h, LabelSettings labelSettings)
    {
        sw.WriteLine($"<rect width=\"{w}\" height=\"{h}\" fill=\"{scene.Background.ToHex()}\"/>");
        sw.WriteLine($"<g transform=\"translate({F(view.OffsetX)},{F(view.OffsetY)}) scale({F(view.Scale)})\">");
        var d = new StringBuilder();
        foreach (var layer in scene.DrawOrder)
        {
            d.Clear();
            foreach (var p in layer.Prims) AppendPath(d, p);
            var st = layer.Style;
            var attrs = new StringBuilder();
            attrs.Append(st.Fill is Rgba f ? $" fill=\"{f.ToHex()}\"" + (f.A < 255 ? $" fill-opacity=\"{F(f.Opacity)}\"" : "") : " fill=\"none\"");
            if (st.Stroke is Rgba s && st.StrokeWidth > 0)
            {
                attrs.Append($" stroke=\"{s.ToHex()}\" stroke-width=\"{F(st.StrokeWidth)}\" vector-effect=\"non-scaling-stroke\"");
                if (s.A < 255) attrs.Append($" stroke-opacity=\"{F(s.Opacity)}\"");
                if (st.Dash != null) attrs.Append($" stroke-dasharray=\"{string.Join(" ", st.Dash.Select(x => F(x / view.Scale)))}\"");
            }
            sw.WriteLine($"<path{attrs} d=\"{d}\"/>");
        }
        sw.WriteLine("</g>");

        var labels = LabelPlacer.Place(scene, view, w, h, labelSettings);
        if (labels.Count > 0)
        {
            sw.WriteLine($"<g font-family=\"{LabelPlacer.FontFamilyCss}\" fill=\"{scene.LabelColor.ToHex()}\" stroke=\"{scene.LabelHalo.ToHex()}\" stroke-linejoin=\"round\" paint-order=\"stroke\">");
            foreach (var l in labels)
            {
                var anchor = l.Align switch { LabelAlign.Center => "middle", LabelAlign.Right => "end", _ => "start" };
                var rot = l.RotationDeg != 0 ? $" rotate({F(l.RotationDeg)})" : "";
                sw.WriteLine($"<text transform=\"translate({F(l.X)},{F(l.Y)}){rot}\" y=\"{F(l.FontPx * 0.35)}\" font-size=\"{F(l.FontPx)}\" stroke-width=\"{F(Math.Max(1.5, l.FontPx * 0.28))}\" text-anchor=\"{anchor}\">{Esc(l.Text)}</text>");
            }
            sw.WriteLine("</g>");
        }
    }

    internal static void WriteText(StreamWriter sw, float x, float y, float size, bool bold, Rgba color, string text) =>
        sw.WriteLine($"<text x=\"{F(x)}\" y=\"{F(y)}\" font-family=\"{LabelPlacer.FontFamilyCss}\" font-size=\"{F(size)}\"{(bold ? " font-weight=\"bold\"" : "")} fill=\"{color.ToHex()}\">{Esc(text)}</text>");

    internal static void WriteOverlay(StreamWriter sw, Drawing.Scene scene, OverlaySettings overlay, int w, int h)
    {
        var layout = OverlayLayout.Compute(scene, overlay, w, h);
        if (layout.IsEmpty) return;
        sw.WriteLine($"<g font-family=\"{LabelPlacer.FontFamilyCss}\">");
        foreach (var item in layout.Items)
        {
            if (item.Kind == OverlayItemKind.Swatch)
                sw.WriteLine($"<rect x=\"{F(item.X)}\" y=\"{F(item.Y)}\" width=\"{F(item.Size)}\" height=\"{F(item.Size)}\" rx=\"{F(item.Size * 0.18)}\" fill=\"{item.Color.ToHex()}\"/>");
            else
                sw.WriteLine($"<text x=\"{F(item.X)}\" y=\"{F(item.Y)}\" font-size=\"{F(item.Size)}\"{(item.Bold ? " font-weight=\"bold\"" : "")} fill=\"{item.Color.ToHex()}\">{Esc(item.Text ?? "")}</text>");
        }
        sw.WriteLine("</g>");
    }

    private static void AppendPath(StringBuilder d, Prim p)
    {
        switch (p.Kind)
        {
            case PrimKind.Line:
                d.Append('M').Append(F(p.P0)).Append(' ').Append(F(p.P1))
                 .Append('L').Append(F(p.P2)).Append(' ').Append(F(p.P3));
                break;
            case PrimKind.Circle:
            {
                double cx = p.P0, cy = p.P1, r = p.P2;
                d.Append('M').Append(F(cx - r)).Append(' ').Append(F(cy))
                 .Append('a').Append(F(r)).Append(' ').Append(F(r)).Append(" 0 1 0 ").Append(F(2 * r)).Append(" 0")
                 .Append('a').Append(F(r)).Append(' ').Append(F(r)).Append(" 0 1 0 ").Append(F(-2 * r)).Append(" 0Z");
                break;
            }
            case PrimKind.Arc:
                foreach (var (a0, a1) in Geometry.SplitSweep(p.P1, p.P2))
                {
                    d.Append('M').Append(Pt(p.P0, a0));
                    d.Append('A').Append(F(p.P0)).Append(' ').Append(F(p.P0)).Append(" 0 0 1 ").Append(Pt(p.P0, a1));
                }
                break;
            case PrimKind.Wedge:
                foreach (var (a0, a1) in Geometry.SplitSweep(p.P2, p.P3))
                {
                    double r0 = p.P0, r1 = p.P1;
                    d.Append('M').Append(Pt(r1, a0));
                    d.Append('A').Append(F(r1)).Append(' ').Append(F(r1)).Append(" 0 0 1 ").Append(Pt(r1, a1));
                    if (r0 > 1e-9)
                    {
                        d.Append('L').Append(Pt(r0, a1));
                        d.Append('A').Append(F(r0)).Append(' ').Append(F(r0)).Append(" 0 0 0 ").Append(Pt(r0, a0));
                    }
                    else d.Append("L0 0");
                    d.Append('Z');
                }
                break;
        }
    }

    private static string Pt(double r, double a) => F(r * Math.Cos(a)) + " " + F(r * Math.Sin(a));
    internal static string F(double v) => v.ToString("0.###", Inv);
    internal static string Esc(string s) => SecurityElement.Escape(s) ?? "";
}
