using System.Text;
using SkiaSharp;
using TreeVisualizer.Core.Drawing;

namespace TreeVisualizer.Core.Render;

/// <summary>One side of a side-by-side export.</summary>
public sealed record ExportPanel(Drawing.Scene Scene, string Caption);

/// <summary>
/// Exports two scenes next to each other (e.g. before | after) in one PNG, PDF or SVG. Each panel gets a
/// Width x Height area; both use the same scale, so unchanged parts of the tree are the same size in both.
/// One title and one merged legend sit outside the panels.
/// </summary>
public static class PairExporter
{
    public static void Export(ExportPanel left, ExportPanel right, string? title, string path, ExportSettings settings)
    {
        var format = Exporter.FormatFromPath(path);
        using var stream = File.Create(path);
        Export(left, right, title, stream, format, settings);
    }

    public static void Export(ExportPanel left, ExportPanel right, string? title, Stream stream, ExportFormat format, ExportSettings settings)
    {
        var g = Layout(left, right, title, settings);
        switch (format)
        {
            case ExportFormat.Png:
            {
                if ((long)g.Width * g.Height > settings.MaxPngPixels)
                    throw new InvalidOperationException(
                        $"PNG of {g.Width}x{g.Height} exceeds the {Math.Sqrt(settings.MaxPngPixels):F0}px² limit; use PDF or SVG for posters.");
                using var surface = SKSurface.Create(new SKImageInfo(g.Width, g.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
                    ?? throw new InvalidOperationException($"Could not allocate a {g.Width}x{g.Height} image.");
                Draw(surface.Canvas, g, left, right, settings.Labels, settings);
                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                data.SaveTo(stream);
                break;
            }
            case ExportFormat.Pdf:
            {
                using var doc = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata
                {
                    Title = title ?? "Comparison", Creator = "Tree Visualizer", Producer = "Tree Visualizer", RasterDpi = 300,
                }) ?? throw new InvalidOperationException("Could not create PDF document.");
                var canvas = doc.BeginPage(g.Width, g.Height);
                var labels = settings.Labels.HaloMinFontPx == null
                    ? settings.Labels with { HaloMinFontPx = settings.PdfHaloMinFontPt }
                    : settings.Labels;
                Draw(canvas, g, left, right, labels, settings);
                doc.EndPage();
                doc.Close();
                break;
            }
            case ExportFormat.Svg:
                WriteSvg(stream, g, left, right, title, settings);
                break;
        }
    }

    /// <summary>Canvas geometry for a pair export.</summary>
    public sealed record PairLayout(int Width, int Height, int PanelWidth, int PanelHeight,
        (float X, float Y) LeftPanel, (float X, float Y) RightPanel, float CaptionHeight, float CaptionFont,
        ViewTransform PanelView, Drawing.Scene Overlay);

    public static PairLayout Layout(ExportPanel left, ExportPanel right, string? title, ExportSettings settings)
    {
        int w = Math.Max(16, settings.Width);
        int h = Math.Max(16, settings.Height ?? settings.Width);
        float unit = Math.Clamp(Math.Min(w, h) / 900f, 0.6f, 6f);
        var overlayScene = MergedOverlay(left.Scene, right.Scene, title);
        var overlaySettings = settings.Overlay with { ScaleBasis = Math.Min(w, h) };
        var o = OverlayLayout.Compute(overlayScene, overlaySettings, w, h);

        float captionFont = 14 * unit, captionH = 26 * unit, gap = 16 * unit;
        float x0 = (float)Math.Ceiling(o.LegendWidth), y0 = (float)Math.Ceiling(o.TitleHeight) + captionH;
        float legendBottom = o.Items.Count == 0 ? 0 : o.Items.Max(i => i.Y + i.Size * 0.4f);
        int totalW = (int)Math.Ceiling(x0 + 2 * w + gap);
        int totalH = (int)Math.Ceiling(Math.Max(y0 + h, legendBottom + 8));

        // Same scale in both panels: fit the larger of the two drawings.
        double extent = Math.Max(left.Scene.Extent, right.Scene.Extent);
        double pad = Math.Min(w, h) * 0.02;
        double s = Math.Max(1e-6, (Math.Min(w, h) - 2 * pad) / (2 * extent));
        var view = new ViewTransform(s, w / 2.0, h / 2.0);
        return new PairLayout(totalW, totalH, w, h, (x0, y0), (x0 + w + gap, y0), captionH, captionFont, view,
            overlayScene);
    }

    /// <summary>
    /// A geometry-free scene carrying the shared title and legend. Legend entries are merged by label, taking
    /// the larger count (e.g. "Removed" is only counted in the before panel, "Added" only in the after panel).
    /// </summary>
    private static Drawing.Scene MergedOverlay(Drawing.Scene a, Drawing.Scene b, string? title)
    {
        var scene = new Drawing.Scene { Radius = b.Radius, Extent = b.Extent, Background = b.Background, LabelColor = b.LabelColor };
        scene.Title = title;
        scene.LegendTitle = b.LegendTitle ?? a.LegendTitle;
        scene.Legend.AddRange(Merge(a.Legend, b.Legend));
        var sections = b.ExtraLegends.Select(x => x.Title).Concat(a.ExtraLegends.Select(x => x.Title)).Distinct();
        foreach (var t in sections)
        {
            var ea = a.ExtraLegends.FirstOrDefault(x => x.Title == t)?.Entries ?? Array.Empty<LegendEntry>();
            var eb = b.ExtraLegends.FirstOrDefault(x => x.Title == t)?.Entries ?? Array.Empty<LegendEntry>();
            scene.ExtraLegends.Add(new LegendSection(t, Merge(ea, eb)));
        }
        return scene;
    }

    private static List<LegendEntry> Merge(IReadOnlyList<LegendEntry> a, IReadOnlyList<LegendEntry> b)
    {
        var result = new List<LegendEntry>();
        foreach (var e in b.Concat(a))
        {
            int i = result.FindIndex(x => x.Label == e.Label);
            if (i < 0) result.Add(e);
            else if (e.Count > result[i].Count) result[i] = result[i] with { Count = e.Count };
        }
        return result;
    }

    private static void Draw(SKCanvas canvas, PairLayout g, ExportPanel left, ExportPanel right, LabelSettings labels, ExportSettings settings)
    {
        var overlay = settings.Overlay with { ScaleBasis = Math.Min(g.PanelWidth, g.PanelHeight) };
        // Background, title and legend (the overlay scene has no geometry).
        using (var r = new SkiaRenderer(g.Overlay))
            r.Draw(canvas, g.PanelView, g.Width, g.Height, labels with { Mode = LabelMode.None }, overlay);

        using var captionFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? LabelPlacer.Typeface, g.CaptionFont);
        using var captionPaint = new SKPaint { IsAntialias = true, Color = SkiaRenderer.ToSk(g.Overlay.LabelColor) };
        using var border = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(0, 0, 0, 0x30) };
        foreach (var (panel, (x, y)) in new[] { (left, g.LeftPanel), (right, g.RightPanel) })
        {
            canvas.DrawText(panel.Caption, x + 4, y - g.CaptionHeight * 0.3f, SKTextAlign.Left, captionFont, captionPaint);
            canvas.Save();
            canvas.Translate(x, y);
            using (var r = new SkiaRenderer(panel.Scene))
                r.Draw(canvas, g.PanelView, g.PanelWidth, g.PanelHeight, labels, null);
            canvas.DrawRect(0.5f, 0.5f, g.PanelWidth - 1, g.PanelHeight - 1, border);
            canvas.Restore();
        }
    }

    private static void WriteSvg(Stream stream, PairLayout g, ExportPanel left, ExportPanel right, string? title, ExportSettings settings)
    {
        using var sw = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
        sw.WriteLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{g.Width}\" height=\"{g.Height}\" viewBox=\"0 0 {g.Width} {g.Height}\">");
        if (!string.IsNullOrEmpty(title)) sw.WriteLine($"<title>{SvgWriter.Esc(title)}</title>");
        sw.WriteLine($"<rect width=\"100%\" height=\"100%\" fill=\"{g.Overlay.Background.ToHex()}\"/>");
        foreach (var (panel, (x, y)) in new[] { (left, g.LeftPanel), (right, g.RightPanel) })
        {
            SvgWriter.WriteText(sw, x + 4, y - g.CaptionHeight * 0.3f, g.CaptionFont, true, g.Overlay.LabelColor, panel.Caption);
            // A nested <svg> clips its content to the panel.
            sw.WriteLine($"<svg x=\"{SvgWriter.F(x)}\" y=\"{SvgWriter.F(y)}\" width=\"{g.PanelWidth}\" height=\"{g.PanelHeight}\" overflow=\"hidden\">");
            SvgWriter.WriteScene(sw, panel.Scene, g.PanelView, g.PanelWidth, g.PanelHeight, settings.Labels);
            sw.WriteLine($"<rect x=\"0.5\" y=\"0.5\" width=\"{g.PanelWidth - 1}\" height=\"{g.PanelHeight - 1}\" fill=\"none\" stroke=\"#000\" stroke-opacity=\"0.19\"/>");
            sw.WriteLine("</svg>");
        }
        SvgWriter.WriteOverlay(sw, g.Overlay, settings.Overlay with { ScaleBasis = Math.Min(g.PanelWidth, g.PanelHeight) }, g.Width, g.Height);
        sw.WriteLine("</svg>");
    }
}
