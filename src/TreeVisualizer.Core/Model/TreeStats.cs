namespace TreeVisualizer.Core.Model;

public sealed record TreeStats(
    int NodeCount,
    int LeafCount,
    int InternalCount,
    int MaxDepth,
    double AverageLeafDepth,
    double AverageSpan,
    int MaxSpan,
    double MedianSpan,
    IReadOnlyList<int> NodesPerLevel)
{
    public static TreeStats Compute(Tree tree)
    {
        var levels = new List<int>();
        long leafDepthSum = 0;
        int leaves = 0;
        var spans = new List<int>();
        foreach (var n in tree.Nodes)
        {
            while (levels.Count <= n.Depth) levels.Add(0);
            levels[n.Depth]++;
            if (n.IsLeaf) { leaves++; leafDepthSum += n.Depth; }
            else spans.Add(n.Children.Count);
        }
        spans.Sort();
        double median = spans.Count == 0 ? 0
            : spans.Count % 2 == 1 ? spans[spans.Count / 2]
            : (spans[spans.Count / 2 - 1] + spans[spans.Count / 2]) / 2.0;
        return new TreeStats(
            tree.Count, leaves, tree.Count - leaves, levels.Count - 1,
            leaves == 0 ? 0 : (double)leafDepthSum / leaves,
            spans.Count == 0 ? 0 : spans.Average(),
            spans.Count == 0 ? 0 : spans[^1],
            median, levels);
    }

    public override string ToString() =>
        $"nodes={NodeCount} leaves={LeafCount} managers={InternalCount} maxDepth={MaxDepth} " +
        $"avgLeafDepth={AverageLeafDepth:F2} span(avg={AverageSpan:F2}, median={MedianSpan:F1}, max={MaxSpan})" +
        Environment.NewLine + "per level: " + string.Join(" ", NodesPerLevel.Select((c, i) => $"L{i}:{c}"));
}
