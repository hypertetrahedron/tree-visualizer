using TreeVisualizer.Core.IO;
using TreeVisualizer.Core.Model;

namespace TreeVisualizer.Core.Layout;

public enum LayoutMode
{
    /// <summary>Node-link tree on concentric rings; each parent sits at the middle of its children.</summary>
    Radial,
    /// <summary>Space-filling rings of wedges.</summary>
    Sunburst,
    /// <summary>
    /// Node-link tree like <see cref="Radial"/>, but each parent sits in line with its first child, so the
    /// connector leaves the parent at one end of the children's arc and later siblings follow clockwise.
    /// </summary>
    Cascade,
}

public enum SortMode
{
    /// <summary>Sort siblings by node id (stable across snapshots; best for diffs).</summary>
    Id,
    /// <summary>Sort siblings by an attribute value (numeric if the key is numeric).</summary>
    Attribute,
    /// <summary>Largest subtree first ("ladderized").</summary>
    Headcount,
    /// <summary>Keep the order of the input file.</summary>
    Input,
}

public sealed record LayoutOptions
{
    public LayoutMode Mode { get; init; } = LayoutMode.Radial;
    /// <summary>Node to lay out as the center. Null = tree root.</summary>
    public string? RootId { get; init; }
    /// <summary>Maximum number of levels below the root to show. Null = all.</summary>
    public int? MaxDepth { get; init; }
    /// <summary>Wedge weight exponent: wedge ∝ leafCount^Alpha. 1 = true headcount, smaller favors small teams.</summary>
    public double Alpha { get; init; } = 1.0;
    public SortMode Sort { get; init; } = SortMode.Id;
    public string? SortKey { get; init; }
    public bool SortDescending { get; init; }
    /// <summary>Angular gap left empty (degrees), centered on <see cref="GapCenterDegrees"/>.</summary>
    public double OpenAngleDegrees { get; init; }
    /// <summary>Screen angle (degrees, clockwise from +x) where the gap — or the seam if no gap — is centered. -90 = top.</summary>
    public double GapCenterDegrees { get; init; } = -90;
    /// <summary>
    /// Node-link layouts: spread the leaf children of a crowded manager over this many staggered sub-rings
    /// (1 = off, max 3). Gives each leaf dot up to this many times more room.
    /// </summary>
    public int LeafStagger { get; init; } = 1;
    /// <summary>Minimum number of leaf siblings before they are staggered.</summary>
    public int LeafStaggerMinGroup { get; init; } = 6;
}

public sealed class LayoutNode
{
    internal LayoutNode(TreeNode node, int level, LayoutNode? parent)
    {
        Node = node; Level = level; Parent = parent;
    }

    public TreeNode Node { get; }
    /// <summary>Depth relative to the layout root (root = 0).</summary>
    public int Level { get; }
    public LayoutNode? Parent { get; }
    internal List<LayoutNode> ChildList { get; } = new();
    public IReadOnlyList<LayoutNode> Children => ChildList;
    /// <summary>Wedge start angle (radians, screen coordinates, clockwise).</summary>
    public double A0 { get; internal set; }
    /// <summary>Wedge end angle (radians), A1 ≥ A0.</summary>
    public double A1 { get; internal set; }
    /// <summary>Middle of the wedge.</summary>
    public double Angle => (A0 + A1) * 0.5;
    /// <summary>
    /// Where the node is drawn in node-link layouts. Radial: the wedge middle. Cascade: the first child's
    /// <see cref="NodeAngle"/> (the counter-clockwise end of the children's arc); childless nodes use the middle.
    /// Always inside [A0, A1].
    /// </summary>
    public double NodeAngle { get; internal set; }
    public double Sweep => A1 - A0;
    /// <summary>Number of descendants hidden by the depth limit (0 if none).</summary>
    public int HiddenDescendants { get; internal set; }
    public bool IsTruncated => HiddenDescendants > 0;
    public int Index { get; internal set; }
    /// <summary>Radial offset from the level's ring, in ring widths (≤ 0; leaf sub-ring staggering).</summary>
    public double RingOffset { get; internal set; }
    /// <summary>Number of sub-rings this node's leaf group is spread over (1 = not staggered).</summary>
    public int StaggerFactor { get; internal set; } = 1;

    public override string ToString() => $"{Node.Id} L{Level} [{A0:F4},{A1:F4}]";
}

public sealed class LayoutResult
{
    private readonly List<LayoutNode[]> _byLevel;

    internal LayoutResult(LayoutOptions options, LayoutNode root, List<LayoutNode> nodes, double start, double span)
    {
        Options = options;
        Root = root;
        Nodes = nodes;
        StartAngle = start;
        TotalSweep = span;
        MaxLevel = nodes.Count == 0 ? 0 : nodes.Max(n => n.Level);
        ById = nodes.ToDictionary(n => n.Node.Id, StringComparer.Ordinal);
        _byLevel = nodes.GroupBy(n => n.Level).OrderBy(g => g.Key)
            .Select(g => g.OrderBy(n => n.A0).ToArray()).ToList();
    }

    public LayoutOptions Options { get; }
    public LayoutNode Root { get; }
    /// <summary>Visible nodes in pre-order.</summary>
    public IReadOnlyList<LayoutNode> Nodes { get; }
    public IReadOnlyDictionary<string, LayoutNode> ById { get; }
    public int MaxLevel { get; }
    public double StartAngle { get; }
    public double TotalSweep { get; }
    public LayoutMode Mode => Options.Mode;
    /// <summary>True for node-link layouts (nodes are dots on rings) as opposed to sunburst wedges.</summary>
    public bool IsNodeLink => Mode != LayoutMode.Sunburst;

    // ---- Ring geometry, normalized to an outer radius of 1 ----

    /// <summary>Radial: distance between consecutive levels. Sunburst: ring thickness.</summary>
    public double RingWidth => IsNodeLink ? 1.0 / Math.Max(1, MaxLevel) : 1.0 / (MaxLevel + 1);

    /// <summary>Node-link node radius for a level.</summary>
    public double NodeRadius(int level) => level * RingWidth;

    /// <summary>Node-link radius of a specific node, including any sub-ring stagger offset.</summary>
    public double NodeRadius(LayoutNode n) => (n.Level + n.RingOffset) * RingWidth;

    /// <summary>Radial step between staggered leaf sub-rings, in ring widths.</summary>
    public const double StaggerStep = 0.18;

    /// <summary>Sunburst-mode ring bounds for a level (root is a disc).</summary>
    public (double Inner, double Outer) RingBounds(int level) => (level * RingWidth, (level + 1) * RingWidth);

    /// <summary>Finds the node under normalized polar coordinates (r in [0,1], θ in radians). O(log n).</summary>
    public LayoutNode? HitTest(double r, double theta)
    {
        if (r < 0) return null;
        int level = IsNodeLink
            ? (int)Math.Round(r / RingWidth)
            : (int)Math.Floor(r / RingWidth);
        if (level < 0 || level >= _byLevel.Count) return null;
        if (level == 0) return Root;

        // Normalize θ into [StartAngle, StartAngle + 2π).
        double t = theta - StartAngle;
        t -= Math.Floor(t / (2 * Math.PI)) * 2 * Math.PI;
        t += StartAngle;

        var row = _byLevel[level];
        int lo = 0, hi = row.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (row[mid].A0 <= t) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return null;
        var n = row[found];
        return t <= n.A1 ? n : null;
    }

    public IReadOnlyList<LayoutNode> NodesAtLevel(int level) =>
        level >= 0 && level < _byLevel.Count ? _byLevel[level] : Array.Empty<LayoutNode>();
}

public static class PolarLayout
{
    public static LayoutResult Compute(Tree tree, LayoutOptions? options = null)
    {
        options ??= new LayoutOptions();
        var rootNode = options.RootId == null ? tree.Root
            : tree.Find(options.RootId) ?? throw new ArgumentException($"Unknown root node '{options.RootId}'.");

        double gap = Math.Clamp(options.OpenAngleDegrees, 0, 300) * Math.PI / 180;
        double span = 2 * Math.PI - gap;
        double start = options.GapCenterDegrees * Math.PI / 180 + gap / 2;

        var comparer = BuildComparer(tree, options);
        double alpha = Math.Clamp(options.Alpha, 0.05, 2.0);
        int maxDepth = options.MaxDepth is int md && md >= 0 ? md : int.MaxValue;

        var root = new LayoutNode(rootNode, 0, null) { A0 = start, A1 = start + span };
        var nodes = new List<LayoutNode>(Math.Min(rootNode.SubtreeSize, 1 << 20));
        var stack = new Stack<LayoutNode>();
        stack.Push(root);
        var buffer = new List<TreeNode>();
        while (stack.Count > 0)
        {
            var ln = stack.Pop();
            ln.Index = nodes.Count;
            nodes.Add(ln);
            var tn = ln.Node;
            if (tn.IsLeaf) continue;
            if (ln.Level >= maxDepth)
            {
                ln.HiddenDescendants = tn.SubtreeSize - 1;
                continue;
            }

            buffer.Clear();
            buffer.AddRange(tn.Children);
            if (comparer != null) buffer.Sort(comparer);

            double total = 0;
            foreach (var c in buffer) total += Math.Pow(c.LeafCount, alpha);
            double a = ln.A0;
            var created = new LayoutNode[buffer.Count];
            for (int i = 0; i < buffer.Count; i++)
            {
                var c = buffer[i];
                double sweep = ln.Sweep * Math.Pow(c.LeafCount, alpha) / total;
                var cl = new LayoutNode(c, ln.Level + 1, ln) { A0 = a, A1 = i == buffer.Count - 1 ? ln.A1 : a + sweep };
                a += sweep;
                ln.ChildList.Add(cl);
                created[i] = cl;
            }
            for (int i = created.Length - 1; i >= 0; i--) stack.Push(created[i]);
        }

        // Leaf sub-ring staggering (node-link layouts only). Offsets point inward so the outer edge stays at radius 1.
        int stagger = Math.Clamp(options.LeafStagger, 1, 3);
        if (stagger > 1 && options.Mode != LayoutMode.Sunburst)
        {
            foreach (var n in nodes)
            {
                int leaves = 0;
                foreach (var c in n.ChildList) if (c.Node.IsLeaf) leaves++;
                if (leaves < Math.Max(2, options.LeafStaggerMinGroup)) continue;
                int j = 0;
                foreach (var c in n.ChildList)
                {
                    if (!c.Node.IsLeaf) continue;
                    c.StaggerFactor = stagger;
                    c.RingOffset = -(j++ % stagger) * LayoutResult.StaggerStep;
                }
            }
        }

        // Node positions. Reverse pre-order visits children before their parent.
        bool cascade = options.Mode == LayoutMode.Cascade;
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            var n = nodes[i];
            n.NodeAngle = cascade && n.ChildList.Count > 0 ? n.ChildList[0].NodeAngle : n.Angle;
        }
        return new LayoutResult(options, root, nodes, start, span);
    }

    private static IComparer<TreeNode>? BuildComparer(Tree tree, LayoutOptions o)
    {
        Comparison<TreeNode>? cmp = o.Sort switch
        {
            SortMode.Input => null,
            SortMode.Id => (a, b) => string.CompareOrdinal(a.Id, b.Id),
            SortMode.Headcount => (a, b) =>
            {
                int c = b.SubtreeSize.CompareTo(a.SubtreeSize);
                return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
            },
            SortMode.Attribute when o.SortKey != null => AttributeComparison(tree, o.SortKey),
            _ => (a, b) => string.CompareOrdinal(a.Id, b.Id),
        };
        if (cmp == null) return null;
        if (o.SortDescending && o.Sort != SortMode.Headcount)
        {
            var inner = cmp;
            cmp = (a, b) => inner(b, a);
        }
        return Comparer<TreeNode>.Create(cmp);
    }

    private static Comparison<TreeNode> AttributeComparison(Tree tree, string key)
    {
        bool numeric = tree.AttributeTypes.TryGetValue(key, out var t) &&
                       t is AttributeType.Int or AttributeType.Long or AttributeType.Float or AttributeType.Double;
        return (a, b) =>
        {
            var va = a.Get(key);
            var vb = b.Get(key);
            int c;
            if (numeric && GraphMl.TryParseNumber(va, out var na) && GraphMl.TryParseNumber(vb, out var nb))
                c = na.CompareTo(nb);
            else if (va == null || vb == null)
                c = (va == null ? 1 : 0) - (vb == null ? 1 : 0); // missing values sort last
            else
                c = string.Compare(va, vb, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        };
    }
}
