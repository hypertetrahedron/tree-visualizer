using TreeVisualizer.Core.Diff;
using TreeVisualizer.Core.Layout;
using TreeVisualizer.Core.Model;

namespace TreeVisualizer.Core.Drawing;

public enum ColorMode
{
    /// <summary>Each top-level branch below the layout root gets its own color.</summary>
    Branch,
    /// <summary>Color by the value of an attribute key (most frequent values get distinct colors).</summary>
    Attribute,
    /// <summary>Single neutral color.</summary>
    None,
}

public sealed record SceneOptions
{
    public ColorMode ColorMode { get; init; } = ColorMode.Branch;
    public string? ColorKey { get; init; }
    /// <summary>Attribute keys joined to form each label. Empty = node id.</summary>
    public IReadOnlyList<string> LabelKeys { get; init; } = Array.Empty<string>();
    public string LabelSeparator { get; init; } = " · ";
    /// <summary>World radius of the outermost ring.</summary>
    public double Radius { get; init; } = 1000;
    /// <summary>Space beyond the outer ring reserved for leaf labels, as a fraction of Radius.</summary>
    public double LabelMargin { get; init; } = 0.22;
    /// <summary>Shade nodes by change class instead of the color mode (requires a diff).</summary>
    public bool ShowDiff { get; init; } = true;
    /// <summary>Attribute keys drawn as concentric annotation rings outside the tree, innermost first.</summary>
    public IReadOnlyList<string> AnnotationKeys { get; init; } = Array.Empty<string>();
    /// <summary>Thickness of each annotation ring, as a fraction of Radius.</summary>
    public double AnnotationWidth { get; init; } = 0.035;
}

public static class Palette
{
    /// <summary>Tableau 10 — distinguishable categorical colors.</summary>
    public static readonly Rgba[] Categorical =
    {
        Rgba.FromHex("#4E79A7"), Rgba.FromHex("#F28E2B"), Rgba.FromHex("#E15759"), Rgba.FromHex("#76B7B2"),
        Rgba.FromHex("#59A14F"), Rgba.FromHex("#EDC948"), Rgba.FromHex("#B07AA1"), Rgba.FromHex("#FF9DA7"),
        Rgba.FromHex("#9C755F"), Rgba.FromHex("#BAB0AC"),
    };

    public static readonly Rgba Other = Rgba.FromHex("#C7C7C7");
    public static readonly Rgba Neutral = Rgba.FromHex("#6B7A8F");
    public static readonly Rgba Edge = Rgba.FromHex("#9AA3AE");

    public static readonly Rgba Added = Rgba.FromHex("#2CA02C");
    public static readonly Rgba Removed = Rgba.FromHex("#D62728");
    public static readonly Rgba Moved = Rgba.FromHex("#FF8C00");
    public static readonly Rgba MovedWithAncestor = Rgba.FromHex("#FFC973");
    public static readonly Rgba Modified = Rgba.FromHex("#1F77B4");
    public static readonly Rgba Unchanged = Rgba.FromHex("#C9CDD2");
    public static readonly Rgba ContainsChanges = Rgba.FromHex("#8E44AD");

    public static Rgba ForChange(ChangeClass c) => c switch
    {
        ChangeClass.Added => Added,
        ChangeClass.Removed => Removed,
        ChangeClass.Moved => Moved,
        ChangeClass.MovedWithAncestor => MovedWithAncestor,
        ChangeClass.Modified => Modified,
        _ => Unchanged,
    };
}

/// <summary>Turns a layout (+ optional diff) into a backend-independent <see cref="Scene"/>.</summary>
public static class SceneBuilder
{
    private const int MaxCategories = 10;
    private const int RollupBuckets = 5;

    public static Scene Build(LayoutResult layout, SceneOptions? options = null, DiffResult? diff = null)
    {
        options ??= new SceneOptions();
        double R = options.Radius;
        double u = R / 1000.0;
        var bandKeys = options.AnnotationKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().ToList();
        double bandW = options.AnnotationWidth * R, bandGap = 3 * u;
        // Annotation rings start just outside the tree, clear of the depth-limit markers.
        bool anyTruncated = layout.Nodes.Any(n => n.IsTruncated);
        double bandsInner = layout.IsNodeLink
            ? R + 8 * u + (anyTruncated ? 14 * u : 0)
            : R + 4 * u + (anyTruncated ? Math.Max(4 * u, layout.RingWidth * R * 0.12) + 2 * u : 0);
        double bandsOuter = bandKeys.Count == 0 ? R : bandsInner + bandKeys.Count * bandW + (bandKeys.Count - 1) * bandGap;
        var scene = new Scene { Radius = R, Extent = bandsOuter + R * options.LabelMargin };
        bool diffMode = diff != null && options.ShowDiff;

        var colorOf = diffMode ? DiffColors(layout, diff!, scene) : CategoryColors(layout, options, scene);

        if (layout.IsNodeLink) BuildRadial(layout, options, scene, colorOf, diffMode ? diff : null, bandKeys.Count > 0 ? bandsOuter : null);
        else BuildSunburst(layout, options, scene, colorOf, diffMode ? diff : null);

        for (int i = 0; i < bandKeys.Count; i++)
        {
            double r0 = bandsInner + i * (bandW + bandGap);
            var title = bandKeys.Count == 1 ? $"Outer ring: {bandKeys[i]}"
                : $"Outer ring {i + 1}{(i == 0 ? " (inner)" : "")}: {bandKeys[i]}";
            AddAnnotationRing(layout, bandKeys[i], title, r0, r0 + bandW, scene);
        }

        return scene;
    }

    public static string LabelFor(TreeNode node, IReadOnlyList<string> keys, string separator)
    {
        if (keys.Count == 0) return node.Id;
        var parts = new List<string>(keys.Count);
        foreach (var k in keys)
        {
            var v = k == "id" && !node.Attributes.ContainsKey("id") ? node.Id : node.Get(k);
            if (!string.IsNullOrWhiteSpace(v)) parts.Add(v);
        }
        return parts.Count == 0 ? node.Id : string.Join(separator, parts);
    }

    // ---------------- colors ----------------

    private static Func<LayoutNode, Rgba> CategoryColors(LayoutResult layout, SceneOptions o, Scene scene)
    {
        switch (o.ColorMode)
        {
            case ColorMode.Attribute when o.ColorKey != null:
            {
                var key = o.ColorKey;
                var counts = layout.Nodes.GroupBy(n => n.Node.Get(key) ?? "(none)")
                    .Select(g => (Value: g.Key, Count: g.Count()))
                    .OrderByDescending(x => x.Count).ThenBy(x => x.Value, StringComparer.Ordinal).ToList();
                var map = new Dictionary<string, Rgba>(StringComparer.Ordinal);
                bool overflow = counts.Count > MaxCategories;
                int distinct = overflow ? MaxCategories - 1 : counts.Count;
                for (int i = 0; i < counts.Count; i++)
                {
                    var c = i < distinct ? Palette.Categorical[i] : Palette.Other;
                    map[counts[i].Value] = c;
                    if (i < distinct) scene.Legend.Add(new LegendEntry(counts[i].Value, c, counts[i].Count));
                }
                if (overflow)
                    scene.Legend.Add(new LegendEntry($"Other ({counts.Count - distinct} values)", Palette.Other,
                        counts.Skip(distinct).Sum(x => x.Count)));
                scene.LegendTitle = key;
                return n => map[n.Node.Get(key) ?? "(none)"];
            }
            case ColorMode.None:
                return _ => Palette.Neutral;
            default:
            {
                var branchColor = new Dictionary<LayoutNode, Rgba>();
                for (int i = 0; i < layout.Root.Children.Count; i++)
                {
                    var b = layout.Root.Children[i];
                    var c = Palette.Categorical[i % Palette.Categorical.Length];
                    branchColor[b] = c;
                    if (i < 12)
                        scene.Legend.Add(new LegendEntry(LabelFor(b.Node, o.LabelKeys, o.LabelSeparator), c, b.Node.SubtreeSize));
                }
                scene.LegendTitle = "Branch";
                return n =>
                {
                    if (n.Level == 0) return Palette.Neutral;
                    var b = n;
                    while (b.Level > 1) b = b.Parent!;
                    return branchColor[b];
                };
            }
        }
    }

    private static Func<LayoutNode, Rgba> DiffColors(LayoutResult layout, DiffResult diff, Scene scene)
    {
        scene.LegendTitle = "Changes";
        foreach (var cls in new[] { ChangeClass.Added, ChangeClass.Removed, ChangeClass.Moved,
                     ChangeClass.MovedWithAncestor, ChangeClass.Modified, ChangeClass.Unchanged })
        {
            int count = layout.Nodes.Count(n => diff.Changes.TryGetValue(n.Node.Id, out var c) && c.Class == cls);
            var label = cls switch
            {
                ChangeClass.MovedWithAncestor => "Moved with manager",
                ChangeClass.Unchanged => "Unchanged",
                _ => cls.ToString(),
            };
            scene.Legend.Add(new LegendEntry(label, Palette.ForChange(cls), count));
        }
        scene.Legend.Add(new LegendEntry("Contains changes (halo)", Palette.ContainsChanges, 0));
        return n => diff.Changes.TryGetValue(n.Node.Id, out var c) ? Palette.ForChange(c.Class) : Palette.Unchanged;
    }

    /// <summary>Rollup intensity bucket 1..RollupBuckets for unchanged nodes with changed descendants; 0 = none.</summary>
    private static int RollupBucket(NodeChange c)
    {
        if (c.IsDirectChange || c.ChangedDescendants == 0) return 0;
        // Round (not ceiling) so a large org with a few scattered changes is not tinted at all.
        return Math.Clamp((int)Math.Round(c.ChangedFraction * RollupBuckets), 0, RollupBuckets);
    }

    // ---------------- annotation rings ----------------

    /// <summary>
    /// Draws one ring outside the tree, colored by <paramref name="key"/> for every visible tip (leaf or
    /// depth-limited node). The ring represents leaf people (individual contributors): a depth-limited node
    /// shows the most common value among the leaves below it, and legend counts are leaf counts.
    /// </summary>
    private static void AddAnnotationRing(LayoutResult layout, string key, string title, double r0, double r1, Scene scene)
    {
        var tips = layout.Nodes.Where(n => n.Children.Count == 0 && n.Level > 0).ToList(); // pre-order = angle order
        if (tips.Count == 0) return;

        var people = new Dictionary<string, int>(StringComparer.Ordinal);
        var tipValue = new string[tips.Count];
        var local = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<TreeNode>();
        for (int i = 0; i < tips.Count; i++)
        {
            var t = tips[i].Node;
            if (t.IsLeaf)
            {
                var v = t.Get(key) ?? "(none)";
                tipValue[i] = v;
                people[v] = people.GetValueOrDefault(v) + 1;
                continue;
            }
            local.Clear();
            stack.Push(t);
            while (stack.Count > 0)
            {
                var x = stack.Pop();
                if (x.IsLeaf)
                {
                    var v = x.Get(key) ?? "(none)";
                    local[v] = local.GetValueOrDefault(v) + 1;
                }
                foreach (var c in x.Children) stack.Push(c);
            }
            foreach (var (v, c) in local) people[v] = people.GetValueOrDefault(v) + c;
            tipValue[i] = local.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
        }

        var ranked = people.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        bool overflow = ranked.Count > MaxCategories;
        int distinct = overflow ? MaxCategories - 1 : ranked.Count;
        var colorOf = new Dictionary<string, Rgba>(StringComparer.Ordinal);
        var entries = new List<LegendEntry>();
        for (int i = 0; i < ranked.Count; i++)
        {
            var c = i < distinct ? Palette.Categorical[i] : Palette.Other;
            colorOf[ranked[i].Key] = c;
            if (i < distinct) entries.Add(new LegendEntry(ranked[i].Key, c, ranked[i].Value));
        }
        if (overflow)
            entries.Add(new LegendEntry($"Other ({ranked.Count - distinct} values)", Palette.Other,
                ranked.Skip(distinct).Sum(kv => kv.Value)));
        scene.ExtraLegends.Add(new LegendSection(title, entries));

        // Merge runs of adjacent tips with the same color into one wedge.
        int start = 0;
        for (int i = 1; i <= tips.Count; i++)
        {
            bool breakRun = i == tips.Count
                || !colorOf[tipValue[i]].Equals(colorOf[tipValue[start]])
                || Math.Abs(tips[i].A0 - tips[i - 1].A1) > 1e-9;
            if (!breakRun) continue;
            var col = colorOf[tipValue[start]];
            scene.Add(ZOrder.Annotations, new Style(col, null), Prim.Wedge(r0, r1, tips[start].A0, tips[i - 1].A1));
            start = i;
        }
    }

    // ---------------- radial node-link ----------------

    private static void BuildRadial(LayoutResult layout, SceneOptions o, Scene scene,
        Func<LayoutNode, Rgba> colorOf, DiffResult? diff, double? bandsOuter)
    {
        double R = o.Radius;
        double ring = layout.RingWidth * R;
        double u = R / 1000.0; // size unit
        var edgeStyleCache = new Dictionary<Rgba, Style>();
        var ghostEdge = new Style(null, Palette.Removed.WithAlpha(0.8), 1f, new[] { 3f, 2f });

        Style EdgeStyle(Rgba c)
        {
            if (!edgeStyleCache.TryGetValue(c, out var s))
                edgeStyleCache[c] = s = new Style(null, diff != null ? Palette.Edge.WithAlpha(0.7) : c.Mix(Palette.Edge, 0.35).WithAlpha(0.75), 0.8f);
            return s;
        }

        bool IsRemoved(LayoutNode n) =>
            diff != null && diff.Changes.TryGetValue(n.Node.Id, out var c) && c.Class == ChangeClass.Removed;

        // Direct changes (not "moved with manager") are drawn larger, on top, with a colored incoming edge.
        bool IsEmphasized(LayoutNode n) =>
            diff != null && diff.Changes.TryGetValue(n.Node.Id, out var c) && c.IsDirectChange;

        var changeEdgeCache = new Dictionary<Rgba, Style>();
        Style ChildEdge(LayoutNode c)
        {
            if (IsRemoved(c)) return ghostEdge;
            if (!IsEmphasized(c)) return EdgeStyle(colorOf(c));
            var col = colorOf(c);
            if (!changeEdgeCache.TryGetValue(col, out var s))
                changeEdgeCache[col] = s = new Style(null, col.WithAlpha(0.9), 1.3f);
            return s;
        }

        // Angular room around each node. Radial: the node is centered in its wedge, so the wedge is its room.
        // Cascade: nodes sit off-center, so use the gap to the nearest neighbor on the same ring.
        double[]? cascadeRoom = null;
        if (layout.Mode == LayoutMode.Cascade)
        {
            cascadeRoom = new double[layout.Nodes.Count];
            for (int level = 1; level <= layout.MaxLevel; level++)
            {
                var row = layout.NodesAtLevel(level); // sorted by A0, hence by NodeAngle
                bool fullCircle = layout.TotalSweep >= 2 * Math.PI - 1e-9;
                double end = layout.StartAngle + layout.TotalSweep;
                for (int i = 0; i < row.Count; i++)
                {
                    double a = row[i].NodeAngle;
                    double prev = i > 0 ? a - row[i - 1].NodeAngle
                        : fullCircle && row.Count > 1 ? a + 2 * Math.PI - row[^1].NodeAngle
                        : 2 * (a - layout.StartAngle);
                    double next = i < row.Count - 1 ? row[i + 1].NodeAngle - a
                        : fullCircle && row.Count > 1 ? row[0].NodeAngle + 2 * Math.PI - a
                        : 2 * (end - a);
                    cascadeRoom[row[i].Index] = Math.Max(0, Math.Min(prev, next));
                }
            }
        }
        double Room(LayoutNode n) => (cascadeRoom?[n.Index] ?? n.Sweep) * n.StaggerFactor;

        double DotRadius(LayoutNode n)
        {
            if (n.Level == 0) return 9 * u;
            double r = layout.NodeRadius(n) * R;
            double room = 0.42 * Room(n) * r;
            if (n.Node.IsLeaf) return Math.Clamp(room, 0.5 * u, 3.5 * u);
            double size = 2.0 + 0.22 * Math.Sqrt(n.Node.SubtreeSize);
            return Math.Clamp(Math.Min(size, Math.Max(room, 2.0)) * u, 1.2 * u, 8 * u);
        }

        foreach (var n in layout.Nodes)
        {
            double r = layout.NodeRadius(n) * R;
            var color = colorOf(n);
            double dot = DotRadius(n);
            var (x, y) = Polar(r, n.NodeAngle);
            bool removed = IsRemoved(n);

            // Edges to children.
            if (n.Children.Count > 0)
            {
                double RChild(LayoutNode c) => layout.NodeRadius(c) * R;
                if (n.Level == 0)
                {
                    foreach (var c in n.Children)
                    {
                        var (cx, cy) = Polar(RChild(c), c.NodeAngle);
                        scene.Add(ZOrder.Edges, ChildEdge(c), Prim.Line(0, 0, cx, cy));
                    }
                }
                else
                {
                    double rm = r + ring * 0.5;
                    var stub = removed ? ghostEdge : EdgeStyle(color);
                    var (mx, my) = Polar(rm, n.NodeAngle);
                    scene.Add(ZOrder.Edges, stub, Prim.Line(x, y, mx, my));
                    double aMin = n.Children[0].NodeAngle, aMax = n.Children[^1].NodeAngle;
                    if (aMax - aMin > 1e-9) scene.Add(ZOrder.Edges, stub, Prim.Arc(rm, aMin, aMax));
                    foreach (var c in n.Children)
                    {
                        var (sx, sy) = Polar(rm, c.NodeAngle);
                        var (ex, ey) = Polar(RChild(c), c.NodeAngle);
                        scene.Add(IsEmphasized(c) ? ZOrder.Edges + 1 : ZOrder.Edges, ChildEdge(c), Prim.Line(sx, sy, ex, ey));
                    }
                }
            }

            // Rollup halo for unchanged ancestors of changes.
            if (diff != null && diff.Changes.TryGetValue(n.Node.Id, out var nc))
            {
                int bucket = RollupBucket(nc);
                if (bucket > 0)
                {
                    double t = bucket / (double)RollupBuckets;
                    var halo = new Style(Palette.ContainsChanges.WithAlpha(0.08 + 0.5 * t), null);
                    scene.Add(ZOrder.Halos, halo, Prim.Circle(x, y, dot + (1.5 + 6 * t) * u));
                }
            }

            // Node dot.
            bool emphasized = IsEmphasized(n);
            var nodeStyle = removed
                ? new Style(Rgba.White, Palette.Removed, 1f, new[] { 2f, 1.5f })
                : emphasized ? new Style(color, Rgba.White, 0.8f)
                : new Style(color, n.Node.IsLeaf ? null : Rgba.White, 0.6f);
            double drawDot = emphasized ? Math.Max(dot, 3.2 * u) : dot;
            scene.Add(emphasized ? ZOrder.Changes : ZOrder.Nodes, nodeStyle, Prim.Circle(x, y, drawDot));

            // Depth-limit marker: a small faded fan just outside the node.
            if (n.IsTruncated)
            {
                double r0 = r + dot + 1.5 * u, r1 = r0 + Math.Min(ring * 0.18, 12 * u);
                double inset = n.Sweep * 0.1;
                scene.Add(ZOrder.Markers, new Style(color.WithAlpha(0.22), null),
                    Prim.Wedge(r0, r1, n.A0 + inset, n.A1 - inset));
            }

            // Label candidate.
            string text = LabelFor(n.Node, o.LabelKeys, o.LabelSeparator);
            if (n.IsTruncated) text += $" (+{n.HiddenDescendants})";
            if (n.Level == 0)
            {
                scene.Labels.Add(new LabelCandidate(n.Node.Id, text, 0, dot + 4 * u, 0, ring * 0.6, ring * 1.6,
                    LabelKind.Horizontal, 0));
            }
            else
            {
                bool tip = n.Children.Count == 0 && !n.IsTruncated;
                // Staggered leaves start their labels beyond the outermost sub-ring so they don't cross siblings.
                double start = Math.Max(r, layout.NodeRadius(n.Level) * R) + dot + 2.5 * u;
                // With annotation rings, leaf labels go outside the rings.
                if (tip && bandsOuter is double bo) start = Math.Max(start, bo + 3 * u);
                double along = tip ? scene.Extent - start - 2 * u : ring * 0.85 - dot;
                double across = Room(n) / n.StaggerFactor * r;
                var (lx, ly) = Polar(start, n.NodeAngle);
                scene.Labels.Add(new LabelCandidate(n.Node.Id, text, lx, ly, n.NodeAngle, across, along,
                    LabelKind.Radial, n.Level));
            }
        }
    }

    // ---------------- sunburst ----------------

    private static void BuildSunburst(LayoutResult layout, SceneOptions o, Scene scene,
        Func<LayoutNode, Rgba> colorOf, DiffResult? diff)
    {
        double R = o.Radius;
        double ring = layout.RingWidth * R;
        double u = R / 1000.0;

        foreach (var n in layout.Nodes)
        {
            var (inner, outer) = layout.RingBounds(n.Level);
            inner *= R; outer *= R;
            var baseColor = colorOf(n);
            NodeChange? nc = null;
            bool removed = diff != null && diff.Changes.TryGetValue(n.Node.Id, out nc) && nc.Class == ChangeClass.Removed;

            Rgba fill;
            if (diff != null && nc != null && RollupBucket(nc) is int b && b > 0)
                fill = Palette.Unchanged.Mix(Palette.ContainsChanges, 0.12 + 0.5 * b / RollupBuckets);
            else if (diff != null)
                fill = removed ? Palette.Removed.WithAlpha(0.55) : baseColor;
            else
                fill = baseColor.Mix(Rgba.White, Math.Min(0.25, 0.04 * Math.Max(0, n.Level - 1)));

            // Outlines only where the wedge is wide enough for them not to swamp the fill.
            bool outline = n.Level == 0 || n.Sweep * outer > 4 * u;
            var style = removed
                ? new Style(fill, Palette.Removed, 0.8f, new[] { 3f, 2f })
                : new Style(fill, outline ? Rgba.White : null, 0.6f);

            if (n.Level == 0) scene.Add(ZOrder.Wedges, style, Prim.Circle(0, 0, outer));
            else scene.Add(ZOrder.Wedges, style, Prim.Wedge(inner, outer, n.A0, n.A1));

            if (n.IsTruncated)
            {
                scene.Add(ZOrder.Markers, new Style(fill.WithAlpha(0.5), null),
                    Prim.Wedge(outer + 1.5 * u, outer + Math.Max(4 * u, ring * 0.12), n.A0, n.A1));
            }

            string text = LabelFor(n.Node, o.LabelKeys, o.LabelSeparator);
            if (n.IsTruncated) text += $" (+{n.HiddenDescendants})";
            if (n.Level == 0)
            {
                scene.Labels.Add(new LabelCandidate(n.Node.Id, text, 0, 0, 0, outer * 0.9, outer * 1.8,
                    LabelKind.Horizontal, 0));
            }
            else
            {
                double mid = (inner + outer) / 2;
                var (lx, ly) = Polar(mid, n.Angle);
                scene.Labels.Add(new LabelCandidate(n.Node.Id, text, lx, ly, n.Angle, n.Sweep * mid,
                    (outer - inner) * 0.92, LabelKind.RadialCentered, n.Level));
            }
        }
    }

    private static (double X, double Y) Polar(double r, double a) => (r * Math.Cos(a), r * Math.Sin(a));
}
