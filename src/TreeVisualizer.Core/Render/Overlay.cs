using TreeVisualizer.Core.Drawing;

namespace TreeVisualizer.Core.Render;

public enum OverlayItemKind { Text, Swatch }

/// <summary>A positioned title/legend element in output pixels. Text Y is the baseline.</summary>
public readonly record struct OverlayItem(OverlayItemKind Kind, float X, float Y, float Size, string? Text, bool Bold, Rgba Color);

public sealed record OverlayLayoutResult(IReadOnlyList<OverlayItem> Items, float TitleHeight, float LegendWidth)
{
    public bool IsEmpty => Items.Count == 0;
}

/// <summary>Lays out the title (top band) and legend (left column) of an exported image.</summary>
public static class OverlayLayout
{
    public static OverlayLayoutResult Compute(Drawing.Scene scene, OverlaySettings settings, float width, float height)
    {
        var items = new List<OverlayItem>();
        float unit = Math.Clamp((settings.ScaleBasis ?? Math.Min(width, height)) / 900f, 0.6f, 6f);
        float x = 16 * unit, y = 16 * unit;
        float titleHeight = 0, legendWidth = 0;
        var color = scene.LabelColor;

        if (settings.Title && !string.IsNullOrEmpty(scene.Title))
        {
            items.Add(new OverlayItem(OverlayItemKind.Text, x, y + 18 * unit, 18 * unit, scene.Title, true, color));
            y += 30 * unit;
            titleHeight = y;
        }
        if (settings.Legend && (scene.Legend.Count > 0 || scene.ExtraLegends.Count > 0))
        {
            float font = 11 * unit;
            void Block(string? title, IReadOnlyList<LegendEntry> entries)
            {
                if (!string.IsNullOrEmpty(title))
                {
                    items.Add(new OverlayItem(OverlayItemKind.Text, x, y + 11 * unit, font, title, true, color));
                    legendWidth = Math.Max(legendWidth, LabelPlacer.MeasureWidth(title, font) * 1.08f);
                    y += 17 * unit;
                }
                foreach (var e in entries)
                {
                    items.Add(new OverlayItem(OverlayItemKind.Swatch, x, y + unit, 11 * unit, null, false, e.Color));
                    var label = e.Count > 0 ? $"{e.Label}  ({e.Count:N0})" : e.Label;
                    items.Add(new OverlayItem(OverlayItemKind.Text, x + 17 * unit, y + 10.5f * unit, font, label, false, color));
                    legendWidth = Math.Max(legendWidth, 17 * unit + LabelPlacer.MeasureWidth(label, font));
                    y += 16 * unit;
                }
            }
            if (scene.Legend.Count > 0) Block(scene.LegendTitle, scene.Legend);
            foreach (var section in scene.ExtraLegends)
            {
                y += 10 * unit;
                Block(section.Title, section.Entries);
            }
            legendWidth += x + 12 * unit;
        }
        return new OverlayLayoutResult(items, titleHeight, legendWidth);
    }
}
