using System.Text;
using System.Xml.Linq;
using TreeVisualizer.Core.Diff;
using TreeVisualizer.Core.Drawing;
using TreeVisualizer.Core.IO;
using TreeVisualizer.Core.Layout;
using TreeVisualizer.Core.Model;
using TreeVisualizer.Core.Render;
using TreeVisualizer.Core.Synthetic;

namespace TreeVisualizer.Tests;

internal static class Fixtures
{
    private static readonly Lazy<Tree> LazyOrg = new(() => SyntheticOrg.Generate(new OrgOptions { Nodes = 10_000, Seed = 1 }));
    /// <summary>Shared 10k-node synthetic org (read-only).</summary>
    public static Tree Org => LazyOrg.Value;

    public static Tree Parse(string graphml) =>
        GraphMl.Read(new MemoryStream(Encoding.UTF8.GetBytes(graphml))).Tree;

    public static Tree Small(params (string Parent, string Child)[] edges)
    {
        var b = new TreeBuilder();
        var ids = edges.SelectMany(e => new[] { e.Parent, e.Child }).Distinct().ToList();
        foreach (var id in ids) b.AddNode(id, new Dictionary<string, string> { ["name"] = "N" + id });
        foreach (var (p, c) in edges) b.AddEdge(p, c);
        return b.Build();
    }
}

public class GraphMlTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="UTF-8"?>
        <graphml xmlns="http://graphml.graphdrawing.org/xmlns">
          <key id="snap" for="graph" attr.name="snapshot" attr.type="string"/>
          <key id="d0" for="node" attr.name="name" attr.type="string"/>
          <key id="d1" for="node" attr.name="level" attr.type="int"/>
          <key id="d2" for="node" attr.name="department" attr.type="string"><default>Unassigned</default></key>
          <key id="rel" for="edge" attr.name="relation" attr.type="string"/>
          <graph id="g" edgedefault="directed">
            <data key="snap">2026-11-01</data>
            <node id="E1"><data key="d0">Ada</data><data key="d1">0</data></node>
            <node id="E2"><data key="d0">Grace</data><data key="d2">Engineering</data></node>
            <node id="E3"><data key="d0">Linus</data></node>
            <edge source="E1" target="E2"/>
            <edge source="E2" target="E3"><data key="rel">hierarchy</data></edge>
            <edge source="E1" target="E3"><data key="rel">dotted-line</data></edge>
          </graph>
        </graphml>
        """;

    [Fact]
    public void ReadsNodesAttributesDefaultsAndSecondaryEdges()
    {
        var t = Fixtures.Parse(Sample);
        Assert.Equal(3, t.Count);
        Assert.Equal("E1", t.Root.Id);
        Assert.Equal("Engineering", t.Find("E2")!.Get("department"));
        Assert.Equal("Unassigned", t.Find("E3")!.Get("department"));
        Assert.Equal(AttributeType.Int, t.AttributeTypes["level"]);
        Assert.Equal("2026-11-01", t.GraphAttributes["snapshot"]);
        Assert.Equal("E2", t.Find("E3")!.Parent!.Id);
        var sec = Assert.Single(t.SecondaryEdges);
        Assert.Equal("dotted-line", sec.Relation);
        Assert.DoesNotContain("relation", t.AttributeTypes.Keys);
    }

    [Fact]
    public void RoundTripPreservesStructureAndAttributes()
    {
        var org = Fixtures.Org;
        var ms = new MemoryStream();
        GraphMl.Write(org, ms);
        ms.Position = 0;
        var back = GraphMl.Read(ms).Tree;
        Assert.Equal(org.Count, back.Count);
        foreach (var n in org.Nodes)
        {
            var m = back.Find(n.Id)!;
            Assert.Equal(n.Parent?.Id, m.Parent?.Id);
            Assert.Equal(n.Attributes, m.Attributes);
        }
        Assert.Equal(org.AttributeTypes, back.AttributeTypes);
    }

    [Fact]
    public void AcceptsGraphMlWithoutNamespace()
    {
        var t = Fixtures.Parse("""<graphml><graph edgedefault="directed"><node id="a"/><node id="b"/><edge source="a" target="b"/></graph></graphml>""");
        Assert.Equal(2, t.Count);
    }

    [Theory]
    [InlineData("""<node id="a"/><node id="a"/>""", "Duplicate node id")]
    [InlineData("""<node id="a"/><edge source="a" target="x"/>""", "unknown target")]
    [InlineData("""<node id="a"/><node id="b"/><node id="c"/><edge source="a" target="c"/><edge source="b" target="c"/>""", "more than one parent")]
    [InlineData("""<node id="r"/><node id="a"/><node id="b"/><edge source="a" target="b"/><edge source="b" target="a"/>""", "Cycle")]
    [InlineData("""<node id="a"/><edge source="a" target="a"/>""", "Self-loop")]
    public void RejectsInvalidTrees(string body, string expected)
    {
        var ex = Assert.Throws<TreeBuildException>(() =>
            Fixtures.Parse($"""<graphml xmlns="http://graphml.graphdrawing.org/xmlns"><graph edgedefault="directed">{body}</graph></graphml>"""));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void MultipleRootsGetSyntheticRoot()
    {
        var r = GraphMl.Read(new MemoryStream(Encoding.UTF8.GetBytes(
            """<graphml><graph><node id="a"/><node id="b"/></graph></graphml>""")));
        Assert.Equal(TreeBuilder.SyntheticRootId, r.Tree.Root.Id);
        Assert.Equal(2, r.Tree.Root.Children.Count);
        Assert.Contains(r.Issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("synthetic"));
    }

    [Fact]
    public void RejectsNestedGraphs()
    {
        Assert.Throws<FormatException>(() => Fixtures.Parse(
            """<graphml><graph><node id="a"><graph/></node></graph></graphml>"""));
    }
}

public class ModelTests
{
    [Fact]
    public void ComputesSizesDepthsAndHeights()
    {
        var t = Fixtures.Small(("r", "a"), ("r", "b"), ("a", "c"), ("a", "d"), ("d", "e"));
        Assert.Equal(6, t.Root.SubtreeSize);
        Assert.Equal(3, t.Root.LeafCount); // b, c, e
        Assert.Equal(3, t.Root.Height);
        Assert.Equal(3, t.Find("e")!.Depth);
        Assert.Equal(new[] { "r", "a", "c", "d", "e", "b" }, t.Nodes.Select(n => n.Id));
    }

    [Fact]
    public void SyntheticOrgHasTargetShape()
    {
        var s = TreeStats.Compute(Fixtures.Org);
        Assert.Equal(10_000, s.NodeCount);
        Assert.InRange(s.AverageLeafDepth, 5.5, 7.0);
        Assert.InRange(s.MaxDepth, 8, 14);
    }
}

public class LayoutTests
{
    private const double Eps = 1e-9;

    [Theory]
    [InlineData(LayoutMode.Radial, 1.0, SortMode.Id, 0)]
    [InlineData(LayoutMode.Sunburst, 0.5, SortMode.Headcount, 30)]
    [InlineData(LayoutMode.Radial, 0.7, SortMode.Attribute, 0)]
    [InlineData(LayoutMode.Cascade, 1.0, SortMode.Id, 15)]
    public void ChildrenPartitionTheirParentWedge(LayoutMode mode, double alpha, SortMode sort, double gap)
    {
        var layout = PolarLayout.Compute(Fixtures.Org, new LayoutOptions
        {
            Mode = mode, Alpha = alpha, Sort = sort, SortKey = "name", OpenAngleDegrees = gap,
        });
        Assert.Equal(Fixtures.Org.Count, layout.Nodes.Count);
        Assert.Equal(2 * Math.PI - gap * Math.PI / 180, layout.Root.Sweep, 9);
        foreach (var n in layout.Nodes)
        {
            Assert.True(n.A1 >= n.A0);
            if (n.Children.Count == 0) continue;
            Assert.Equal(n.A0, n.Children[0].A0, 12);
            Assert.Equal(n.A1, n.Children[^1].A1, 12);
            for (int i = 1; i < n.Children.Count; i++)
                Assert.True(Math.Abs(n.Children[i].A0 - n.Children[i - 1].A1) < Eps, "siblings must be contiguous");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    public void CascadePlacesParentsInLineWithTheirFirstChild(double gap)
    {
        var layout = PolarLayout.Compute(Fixtures.Org, new LayoutOptions { Mode = LayoutMode.Cascade, OpenAngleDegrees = gap });
        foreach (var n in layout.Nodes)
        {
            Assert.InRange(n.NodeAngle, n.A0, n.A1);
            if (n.Children.Count == 0) { Assert.Equal(n.Angle, n.NodeAngle, 12); continue; }
            Assert.Equal(n.Children[0].NodeAngle, n.NodeAngle, 12);
            // Later siblings follow clockwise (increasing screen angle).
            for (int i = 1; i < n.Children.Count; i++)
                Assert.True(n.Children[i].NodeAngle > n.Children[i - 1].NodeAngle);
        }
        // Radial keeps parents centered on their wedge.
        var radial = PolarLayout.Compute(Fixtures.Org);
        Assert.All(radial.Nodes, n => Assert.Equal(n.Angle, n.NodeAngle, 12));
    }

    [Fact]
    public void LeafStaggerSpreadsCrowdedLeafGroupsInwardAndStaysHittable()
    {
        var layout = PolarLayout.Compute(Fixtures.Org, new LayoutOptions { LeafStagger = 3 });
        var staggered = layout.Nodes.Where(n => n.StaggerFactor == 3).ToList();
        Assert.NotEmpty(staggered);
        Assert.All(staggered, n => Assert.True(n.Node.IsLeaf));
        Assert.Contains(staggered, n => n.RingOffset < 0);
        Assert.All(layout.Nodes, n => Assert.InRange(n.RingOffset, -2 * LayoutResult.StaggerStep - 1e-12, 0));
        foreach (var n in staggered)
            Assert.Same(n, layout.HitTest(layout.NodeRadius(n), n.NodeAngle));
        // Small leaf groups and non-leaves are not staggered.
        Assert.All(layout.Nodes.Where(n => !n.Node.IsLeaf), n => Assert.Equal(0, n.RingOffset));
        // Sunburst ignores it.
        Assert.All(PolarLayout.Compute(Fixtures.Org, new LayoutOptions { Mode = LayoutMode.Sunburst, LeafStagger = 3 }).Nodes,
            n => Assert.Equal(1, n.StaggerFactor));
    }

    [Fact]
    public void AlphaOneGivesWedgesProportionalToLeaves()
    {
        var layout = PolarLayout.Compute(Fixtures.Org);
        double perLeaf = layout.Root.Sweep / Fixtures.Org.Root.LeafCount;
        foreach (var n in layout.Nodes.Take(500))
            Assert.Equal(n.Node.LeafCount * perLeaf, n.Sweep, 9);
    }

    [Fact]
    public void DepthLimitAndSubtreeRoot()
    {
        var org = Fixtures.Org;
        var vp = org.Root.Children[0];
        var layout = PolarLayout.Compute(org, new LayoutOptions { RootId = vp.Id, MaxDepth = 2 });
        Assert.Same(vp, layout.Root.Node);
        Assert.Equal(2, layout.MaxLevel);
        Assert.All(layout.Nodes, n => Assert.True(n.Node.IsDescendantOf(vp) || n.Node == vp));
        int hidden = layout.Nodes.Sum(n => n.HiddenDescendants);
        Assert.Equal(vp.SubtreeSize, layout.Nodes.Count + hidden);
    }

    [Fact]
    public void LayoutIsDeterministic()
    {
        var a = PolarLayout.Compute(Fixtures.Org, new LayoutOptions { Sort = SortMode.Headcount });
        var b = PolarLayout.Compute(Fixtures.Org, new LayoutOptions { Sort = SortMode.Headcount });
        Assert.Equal(a.Nodes.Select(n => (n.Node.Id, n.A0, n.A1)), b.Nodes.Select(n => (n.Node.Id, n.A0, n.A1)));
    }

    [Fact]
    public void HitTestFindsEveryNodeAtItsCenter()
    {
        foreach (var mode in new[] { LayoutMode.Radial, LayoutMode.Sunburst, LayoutMode.Cascade })
        {
            var layout = PolarLayout.Compute(Fixtures.Org, new LayoutOptions { Mode = mode, OpenAngleDegrees = 20 });
            foreach (var n in layout.Nodes)
            {
                double r = layout.IsNodeLink ? layout.NodeRadius(n.Level)
                    : (layout.RingBounds(n.Level).Inner + layout.RingBounds(n.Level).Outer) / 2;
                double a = layout.IsNodeLink ? n.NodeAngle : n.Angle; // where the node is drawn
                // Probe at an equivalent angle one turn away to exercise normalization.
                var hit = layout.HitTest(r, a + 2 * Math.PI);
                Assert.Same(n, hit);
            }
            // Inside the open gap there is nothing.
            Assert.Null(layout.HitTest(layout.NodeRadius(2) + 1e-6, -Math.PI / 2));
        }
    }
}

public class DiffTests
{
    [Fact]
    public void ClassifiesHandBuiltChanges()
    {
        // old: r -> a -> (x, y), r -> b -> z
        var oldB = new TreeBuilder();
        foreach (var id in new[] { "r", "a", "b", "x", "y", "z" })
            oldB.AddNode(id, new Dictionary<string, string> { ["title"] = "t" });
        oldB.AddEdge("r", "a"); oldB.AddEdge("r", "b"); oldB.AddEdge("a", "x"); oldB.AddEdge("a", "y"); oldB.AddEdge("b", "z");
        var oldT = oldB.Build();

        // new: a moved under b (x, y come along), z removed, w added under r, y retitled.
        var newB = new TreeBuilder();
        newB.AddNode("r", new Dictionary<string, string> { ["title"] = "t" });
        newB.AddNode("a", new Dictionary<string, string> { ["title"] = "t" });
        newB.AddNode("b", new Dictionary<string, string> { ["title"] = "t" });
        newB.AddNode("x", new Dictionary<string, string> { ["title"] = "t" });
        newB.AddNode("y", new Dictionary<string, string> { ["title"] = "boss" });
        newB.AddNode("w", new Dictionary<string, string> { ["title"] = "t" });
        newB.AddEdge("r", "b"); newB.AddEdge("b", "a"); newB.AddEdge("a", "x"); newB.AddEdge("a", "y"); newB.AddEdge("r", "w");
        var newT = newB.Build();

        var d = TreeDiff.Compare(oldT, newT);
        Assert.Equal(ChangeClass.Unchanged, d.Get("r").Class);
        Assert.Equal(ChangeClass.Moved, d.Get("a").Class);
        Assert.Equal(ChangeClass.MovedWithAncestor, d.Get("x").Class);
        Assert.Equal(ChangeFlags.Modified | ChangeFlags.MovedWithAncestor, d.Get("y").Flags);
        Assert.Equal(ChangeClass.Modified, d.Get("y").Class);
        Assert.Equal(ChangeClass.Removed, d.Get("z").Class);
        Assert.Equal(ChangeClass.Added, d.Get("w").Class);
        var change = Assert.Single(d.Get("y").AttributeChanges);
        Assert.Equal(("title", "t", "boss"), (change.Key, change.OldValue, change.NewValue));

        // Ghost z is kept under its old parent b; union has every node from both trees.
        Assert.Equal("b", d.Union.Find("z")!.Parent!.Id);
        Assert.Equal(7, d.Union.Count);

        // Rollup on b: a (moved), y (modified), z (removed) are direct changes below; x is not.
        Assert.Equal(3, d.Get("b").ChangedDescendants);
        Assert.Equal(4, d.Get("b").Descendants);
    }

    [Fact]
    public void RemovedSubtreeKeepsItsShapeAsGhosts()
    {
        var oldT = Fixtures.Small(("r", "a"), ("a", "b"), ("b", "c"), ("r", "d"));
        var newT = Fixtures.Small(("r", "d"));
        var d = TreeDiff.Compare(oldT, newT);
        Assert.Equal("r", d.Union.Find("a")!.Parent!.Id);
        Assert.Equal("a", d.Union.Find("b")!.Parent!.Id);
        Assert.Equal("b", d.Union.Find("c")!.Parent!.Id);
        Assert.Equal(3, d.Count(ChangeFlags.Removed));
    }

    [Fact]
    public void DetectsExactlyTheGeneratedMutations()
    {
        var (mutated, log) = SyntheticOrg.Mutate(Fixtures.Org, new MutationPlan(250, 180, 60, 120), seed: 7);
        var d = TreeDiff.Compare(Fixtures.Org, mutated);

        Assert.Equal(log.Added.OrderBy(x => x), Ids(d, ChangeClass.Added));
        Assert.Equal(log.Removed.OrderBy(x => x), Ids(d, ChangeClass.Removed));
        Assert.Equal(log.Moved.OrderBy(x => x), Ids(d, ChangeClass.Moved));
        Assert.Equal(log.Modified.OrderBy(x => x),
            d.Changes.Values.Where(c => c.Flags.HasFlag(ChangeFlags.Modified)).Select(c => c.Id).OrderBy(x => x));
        Assert.Equal(Fixtures.Org.Count + log.Added.Count, d.Union.Count);

        // MovedWithAncestor == exactly the descendants (in the new tree) of moved nodes, minus direct changes.
        var expected = new HashSet<string>();
        foreach (var id in log.Moved)
            foreach (var n in Descendants(mutated.Find(id)!))
                if (d.Get(n.Id).Class is not (ChangeClass.Added or ChangeClass.Moved)) expected.Add(n.Id);
        var actual = d.Changes.Values.Where(c => c.Flags.HasFlag(ChangeFlags.MovedWithAncestor)).Select(c => c.Id).ToHashSet();
        Assert.True(expected.SetEquals(actual));
    }

    [Fact]
    public void IdenticalTreesHaveNoChanges()
    {
        var d = TreeDiff.Compare(Fixtures.Org, Fixtures.Org);
        Assert.All(d.Changes.Values, c => Assert.Equal(ChangeFlags.None, c.Flags));
        Assert.Equal(Fixtures.Org.Count, d.Union.Count);
    }

    [Fact]
    public void ReportListsOnlyChangedNodes()
    {
        var (mutated, _) = SyntheticOrg.Mutate(Fixtures.Org, new MutationPlan(5, 5, 5, 5), seed: 3);
        var d = TreeDiff.Compare(Fixtures.Org, mutated);
        var sw = new StringWriter();
        d.WriteReport(sw);
        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1 + d.Changes.Values.Count(c => c.Flags != ChangeFlags.None), lines.Length);
    }

    private static IEnumerable<string> Ids(DiffResult d, ChangeClass cls) =>
        d.Changes.Values.Where(c => c.Class == cls).Select(c => c.Id).OrderBy(x => x);

    private static IEnumerable<TreeNode> Descendants(TreeNode n)
    {
        foreach (var c in n.Children)
        {
            yield return c;
            foreach (var g in Descendants(c)) yield return g;
        }
    }
}

public class RenderTests
{
    private static Scene BuildScene(LayoutMode mode, bool diff = false)
    {
        var options = new LayoutOptions { Mode = mode };
        if (!diff)
            return SceneBuilder.Build(PolarLayout.Compute(Fixtures.Org, options), new SceneOptions { LabelKeys = new[] { "name" } });
        var (mutated, _) = SyntheticOrg.Mutate(Fixtures.Org, new MutationPlan(), 5);
        var d = TreeDiff.Compare(Fixtures.Org, mutated);
        return SceneBuilder.Build(PolarLayout.Compute(d.Union, options), new SceneOptions(), d);
    }

    [Theory]
    [InlineData(LayoutMode.Radial, false)]
    [InlineData(LayoutMode.Sunburst, false)]
    [InlineData(LayoutMode.Radial, true)]
    [InlineData(LayoutMode.Sunburst, true)]
    [InlineData(LayoutMode.Cascade, false)]
    [InlineData(LayoutMode.Cascade, true)]
    public void ExportsAllFormats(LayoutMode mode, bool diff)
    {
        var scene = BuildScene(mode, diff);
        scene.Title = "Test";
        var settings = new ExportSettings { Width = 800 };

        var png = new MemoryStream();
        Exporter.Export(scene, png, ExportFormat.Png, settings);
        using (var bmp = SkiaSharp.SKBitmap.Decode(png.ToArray()))
        {
            Assert.NotNull(bmp);
            Assert.True(bmp.Width >= 800 && bmp.Height >= 800);
            // Center pixel region of the tree is not background.
            var (_, _, view, _) = Exporter.LayoutCanvas(scene, settings);
            int nonWhite = 0;
            for (int dx = -40; dx <= 40; dx += 4)
            for (int dy = -40; dy <= 40; dy += 4)
            {
                var c = bmp.GetPixel((int)view.OffsetX + dx, (int)view.OffsetY + dy);
                if (c.Red < 250 || c.Green < 250 || c.Blue < 250) nonWhite++;
            }
            Assert.True(nonWhite > 0);
        }

        var pdf = new MemoryStream();
        Exporter.Export(scene, pdf, ExportFormat.Pdf, settings);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf.ToArray(), 0, 4));

        var svg = new MemoryStream();
        Exporter.Export(scene, svg, ExportFormat.Svg, settings);
        svg.Position = 0;
        var doc = XDocument.Load(svg);
        Assert.Equal("svg", doc.Root!.Name.LocalName);
        Assert.Contains(doc.Descendants(), e => e.Name.LocalName == "path");
    }

    [Fact]
    public void LabelLevelOfDetailRevealsMoreLabelsWhenZoomedIn()
    {
        var scene = BuildScene(LayoutMode.Radial);
        var far = ViewTransform.Fit(scene, 800, 800);
        var near = far with { Scale = far.Scale * 20 };
        var settings = new LabelSettings();
        int nFar = LabelPlacer.Place(scene, far, 800, 800, settings).Count;
        // Zoomed in, count labels across the whole canvas extent (no viewport culling).
        int nNear = LabelPlacer.Place(scene, near with { OffsetX = 0, OffsetY = 0 }, 1e9, 1e9,
            settings).Count;
        Assert.InRange(nFar, 1, scene.Labels.Count / 4);
        Assert.True(nNear > nFar * 5);
        Assert.Equal(scene.Labels.Count, LabelPlacer.Place(scene, far, 800, 800, settings with { Mode = LabelMode.All, MinFontPx = 0 }).Count);
    }

    [Theory]
    [InlineData(LayoutMode.Radial, 0)]
    [InlineData(LayoutMode.Sunburst, 0)]
    [InlineData(LayoutMode.Cascade, 3)]
    public void AnnotationRingsCoverTheCircleOutsideTheTree(LayoutMode mode, int depth)
    {
        var layout = PolarLayout.Compute(Fixtures.Org, new LayoutOptions { Mode = mode, MaxDepth = depth > 0 ? depth : null });
        var plain = SceneBuilder.Build(layout, new SceneOptions());
        var scene = SceneBuilder.Build(layout, new SceneOptions { AnnotationKeys = new[] { "location", "department" } });

        Assert.True(scene.Extent > plain.Extent);
        Assert.Equal(2, scene.ExtraLegends.Count);
        Assert.Contains("location", scene.ExtraLegends[0].Title);
        // Rings represent leaf people; each is counted once, including those hidden by a depth limit.
        Assert.Equal(Fixtures.Org.Root.LeafCount, scene.ExtraLegends[0].Entries.Sum(e => e.Count));

        var ringPrims = scene.Layers.Where(l => l.Z == ZOrder.Annotations).SelectMany(l => l.Prims).ToList();
        double r0 = ringPrims.Min(p => p.P0);
        Assert.True(r0 > scene.Radius);
        double inner = ringPrims.Where(p => Math.Abs(p.P0 - r0) < 1e-9).Sum(p => p.P3 - p.P2);
        Assert.Equal(layout.TotalSweep, inner, 6);

        // The overlay lists both ring legends.
        var overlay = OverlayLayout.Compute(scene, new OverlaySettings(), 1000, 1000);
        Assert.Contains(overlay.Items, i => i.Text != null && i.Text.Contains("department"));
    }

    [Fact]
    public void SideBySideExportHasBothPanelsAndAMergedLegend()
    {
        var (mutated, _) = SyntheticOrg.Mutate(Fixtures.Org, new MutationPlan(), 5);
        var d = TreeDiff.Compare(Fixtures.Org, mutated);
        var before = SceneBuilder.Build(PolarLayout.Compute(d.Old), new SceneOptions(), d);
        var after = SceneBuilder.Build(PolarLayout.Compute(d.New), new SceneOptions(), d);
        var left = new ExportPanel(before, "Before");
        var right = new ExportPanel(after, "After");
        var settings = new ExportSettings { Width = 600 };

        var layout = PairExporter.Layout(left, right, "Compare", settings);
        Assert.True(layout.Width > 2 * 600);
        Assert.True(layout.RightPanel.X >= layout.LeftPanel.X + 600);
        // "Removed" is only counted in the before panel and "Added" only in the after panel; the legend has both.
        Assert.Equal(d.Count(ChangeFlags.Removed), layout.Overlay.Legend.Single(e => e.Label == "Removed").Count);
        Assert.Equal(d.Count(ChangeFlags.Added), layout.Overlay.Legend.Single(e => e.Label == "Added").Count);

        var png = new MemoryStream();
        PairExporter.Export(left, right, "Compare", png, ExportFormat.Png, settings);
        using (var bmp = SkiaSharp.SKBitmap.Decode(png.ToArray()))
        {
            Assert.Equal(layout.Width, bmp.Width);
            Assert.Equal(layout.Height, bmp.Height);
        }
        var pdf = new MemoryStream();
        PairExporter.Export(left, right, "Compare", pdf, ExportFormat.Pdf, settings);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf.ToArray(), 0, 4));
        var svg = new MemoryStream();
        PairExporter.Export(left, right, "Compare", svg, ExportFormat.Svg, settings);
        svg.Position = 0;
        var doc = XDocument.Load(svg);
        Assert.Equal(2, doc.Root!.Elements().Count(e => e.Name.LocalName == "svg"));
        Assert.Contains(doc.Descendants(), e => e.Name.LocalName == "text" && e.Value == "Before");
    }

    [Fact]
    public void PdfSkipsHalosOnTinyLabels()
    {
        var scene = BuildScene(LayoutMode.Radial);
        var labels = new LabelSettings { Mode = LabelMode.All };
        long Size(ExportSettings st)
        {
            var ms = new MemoryStream();
            Exporter.Export(scene, ms, ExportFormat.Pdf, st);
            return ms.Length;
        }
        long allHalos = Size(new ExportSettings { Width = 2000, Labels = labels with { HaloMinFontPx = 0 } });
        long byDefault = Size(new ExportSettings { Width = 2000, Labels = labels });
        Assert.True(byDefault < allHalos / 2, $"default {byDefault} vs all halos {allHalos}");
    }

    [Fact]
    public void PngGuardRejectsHugeImages()
    {
        var scene = BuildScene(LayoutMode.Sunburst);
        Assert.Throws<InvalidOperationException>(() =>
            Exporter.Export(scene, new MemoryStream(), ExportFormat.Png, new ExportSettings { Width = 20_000 }));
    }
}
