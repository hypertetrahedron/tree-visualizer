using System.Globalization;

namespace TreeVisualizer.Core.Drawing;

/// <summary>Straight sRGB color with alpha.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        uint v = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return hex.Length == 8
            ? new Rgba((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new Rgba((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public Rgba WithAlpha(double a) => this with { A = (byte)Math.Clamp(Math.Round(a * 255), 0, 255) };

    /// <summary>Linear blend toward another color (t = 0 → this, 1 → other).</summary>
    public Rgba Mix(Rgba o, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte L(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
        return new Rgba(L(R, o.R), L(G, o.G), L(B, o.B), L(A, o.A));
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
    public double Opacity => A / 255.0;

    public static readonly Rgba White = new(255, 255, 255);
    public static readonly Rgba Black = new(0, 0, 0);
}

/// <summary>Paint style for a batch of primitives.</summary>
/// <param name="StrokeWidth">Stroke width in screen pixels (constant regardless of zoom).</param>
/// <param name="Dash">Dash pattern in screen pixels, or null.</param>
public sealed record Style(Rgba? Fill, Rgba? Stroke, float StrokeWidth = 1f, float[]? Dash = null)
{
    public bool Equals(Style? other) =>
        other is not null && Fill == other.Fill && Stroke == other.Stroke && StrokeWidth == other.StrokeWidth &&
        (Dash == null ? other.Dash == null : other.Dash != null && Dash.SequenceEqual(other.Dash));

    public override int GetHashCode() => HashCode.Combine(Fill, Stroke, StrokeWidth, Dash?.Length ?? 0);
}

public enum PrimKind : byte
{
    /// <summary>(x1, y1, x2, y2)</summary>
    Line,
    /// <summary>Origin-centered arc: (r, a0, a1)</summary>
    Arc,
    /// <summary>(cx, cy, r)</summary>
    Circle,
    /// <summary>Origin-centered annular sector: (r0, r1, a0, a1)</summary>
    Wedge,
}

/// <summary>A geometric primitive in world coordinates. Angles in radians, clockwise on screen.</summary>
public readonly record struct Prim(PrimKind Kind, double P0, double P1, double P2, double P3 = 0)
{
    public static Prim Line(double x1, double y1, double x2, double y2) => new(PrimKind.Line, x1, y1, x2, y2);
    public static Prim Arc(double r, double a0, double a1) => new(PrimKind.Arc, r, a0, a1);
    public static Prim Circle(double cx, double cy, double r) => new(PrimKind.Circle, cx, cy, r);
    public static Prim Wedge(double r0, double r1, double a0, double a1) => new(PrimKind.Wedge, r0, r1, a0, a1);
}

/// <summary>A batch of primitives drawn with one style. Layers draw in ascending Z order.</summary>
public sealed class Layer
{
    internal Layer(Style style, int z, int order) { Style = style; Z = z; Order = order; }
    public Style Style { get; }
    public int Z { get; }
    internal int Order { get; }
    public List<Prim> Prims { get; } = new();
}

public enum LabelKind : byte
{
    /// <summary>Starts at the anchor and runs outward along the radius.</summary>
    Radial,
    /// <summary>Centered on the anchor, oriented along the radius (sunburst wedges).</summary>
    RadialCentered,
    /// <summary>Horizontal, centered on the anchor (layout root).</summary>
    Horizontal,
}

/// <summary>A label that may be drawn if it fits at the current zoom.</summary>
/// <param name="Angle">Radial direction (radians) used for rotation.</param>
/// <param name="Across">World space available across the text (perpendicular to reading direction).</param>
/// <param name="Along">World space available along the reading direction.</param>
/// <param name="Level">Layout level; lower levels get priority and a larger font.</param>
public sealed record LabelCandidate(
    string NodeId, string Text, double X, double Y, double Angle, double Across, double Along,
    LabelKind Kind, int Level);

public sealed record LegendEntry(string Label, Rgba Color, int Count);

/// <summary>An additional titled legend block (e.g. one per annotation ring).</summary>
public sealed record LegendSection(string Title, IReadOnlyList<LegendEntry> Entries);

/// <summary>Everything needed to draw one view of a tree, independent of output backend.</summary>
public sealed class Scene
{
    private readonly Dictionary<(Style, int), Layer> _index = new();
    private readonly List<Layer> _layers = new();

    /// <summary>Outer radius of the tree in world units.</summary>
    public double Radius { get; init; }
    /// <summary>Radius of the full drawing including the label margin, in world units.</summary>
    public double Extent { get; init; }
    public Rgba Background { get; init; } = Rgba.White;
    public Rgba LabelColor { get; init; } = Rgba.FromHex("#222222");
    public Rgba LabelHalo { get; init; } = Rgba.White;
    public string? Title { get; set; }
    public string? LegendTitle { get; set; }
    public List<LegendEntry> Legend { get; } = new();
    /// <summary>Legend blocks shown after the main legend.</summary>
    public List<LegendSection> ExtraLegends { get; } = new();
    public List<LabelCandidate> Labels { get; } = new();

    public IReadOnlyList<Layer> Layers => _layers;

    /// <summary>Layers sorted for drawing.</summary>
    public IEnumerable<Layer> DrawOrder => _layers.OrderBy(l => l.Z).ThenBy(l => l.Order);

    public void Add(int z, Style style, Prim prim) => GetLayer(z, style).Prims.Add(prim);

    public Layer GetLayer(int z, Style style)
    {
        if (!_index.TryGetValue((style, z), out var layer))
        {
            layer = new Layer(style, z, _layers.Count);
            _index[(style, z)] = layer;
            _layers.Add(layer);
        }
        return layer;
    }

    public int PrimitiveCount => _layers.Sum(l => l.Prims.Count);
}

public static class ZOrder
{
    public const int Background = 0;
    public const int Wedges = 10;
    public const int Annotations = 15;
    public const int Edges = 20;
    public const int Halos = 30;
    public const int Nodes = 40;
    public const int Markers = 50;
    public const int Changes = 60;
}
