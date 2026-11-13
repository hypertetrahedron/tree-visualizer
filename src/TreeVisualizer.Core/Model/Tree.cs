namespace TreeVisualizer.Core.Model;

/// <summary>Declared value type of an attribute key (from GraphML attr.type).</summary>
public enum AttributeType { String, Int, Long, Float, Double, Boolean }

public sealed class TreeNode
{
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    internal TreeNode(string id, IReadOnlyDictionary<string, string>? attributes)
    {
        Id = id;
        Attributes = attributes ?? Empty;
    }

    public string Id { get; }
    public IReadOnlyDictionary<string, string> Attributes { get; }
    public TreeNode? Parent { get; internal set; }
    internal List<TreeNode> ChildList { get; } = new();
    public IReadOnlyList<TreeNode> Children => ChildList;

    /// <summary>Distance from the tree root (root = 0).</summary>
    public int Depth { get; internal set; }
    /// <summary>Number of nodes in this subtree, including this node.</summary>
    public int SubtreeSize { get; internal set; }
    /// <summary>Number of leaves in this subtree (a leaf counts itself).</summary>
    public int LeafCount { get; internal set; }
    /// <summary>Number of levels below this node (leaf = 0).</summary>
    public int Height { get; internal set; }
    /// <summary>Position in pre-order traversal of the whole tree.</summary>
    public int PreorderIndex { get; internal set; }

    public bool IsLeaf => ChildList.Count == 0;

    public string? Get(string key) => Attributes.TryGetValue(key, out var v) ? v : null;

    public bool IsDescendantOf(TreeNode ancestor)
    {
        for (var n = Parent; n != null; n = n.Parent)
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }

    public override string ToString() => Id;
}

/// <summary>A non-hierarchy relationship (e.g. dotted-line). Parsed and kept; not rendered in v1.</summary>
public sealed record SecondaryEdge(string SourceId, string TargetId, string Relation);

public sealed class Tree
{
    internal Tree(TreeNode root, Dictionary<string, TreeNode> byId,
        IReadOnlyDictionary<string, AttributeType> attributeTypes,
        IReadOnlyDictionary<string, string> graphAttributes,
        IReadOnlyList<SecondaryEdge> secondaryEdges)
    {
        Root = root;
        ById = byId;
        AttributeTypes = attributeTypes;
        GraphAttributes = graphAttributes;
        SecondaryEdges = secondaryEdges;
        Nodes = Recompute(root);
    }

    public TreeNode Root { get; }
    public IReadOnlyDictionary<string, TreeNode> ById { get; }
    /// <summary>All nodes in pre-order.</summary>
    public IReadOnlyList<TreeNode> Nodes { get; }
    /// <summary>Node attribute keys and their declared types, in declaration order.</summary>
    public IReadOnlyDictionary<string, AttributeType> AttributeTypes { get; }
    public IReadOnlyList<string> AttributeKeys => AttributeTypes.Keys.ToList();
    public IReadOnlyDictionary<string, string> GraphAttributes { get; }
    public IReadOnlyList<SecondaryEdge> SecondaryEdges { get; }

    public int Count => Nodes.Count;

    public TreeNode? Find(string id) => ById.TryGetValue(id, out var n) ? n : null;

    /// <summary>Computes depth, sizes, heights and pre-order indices. Iterative (no recursion limits).</summary>
    private static List<TreeNode> Recompute(TreeNode root)
    {
        var order = new List<TreeNode>();
        var stack = new Stack<TreeNode>();
        root.Depth = 0;
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            n.PreorderIndex = order.Count;
            order.Add(n);
            for (int i = n.ChildList.Count - 1; i >= 0; i--)
            {
                var c = n.ChildList[i];
                c.Depth = n.Depth + 1;
                stack.Push(c);
            }
        }
        for (int i = order.Count - 1; i >= 0; i--)
        {
            var n = order[i];
            if (n.ChildList.Count == 0)
            {
                n.SubtreeSize = 1; n.LeafCount = 1; n.Height = 0;
                continue;
            }
            int size = 1, leaves = 0, height = 0;
            foreach (var c in n.ChildList)
            {
                size += c.SubtreeSize;
                leaves += c.LeafCount;
                if (c.Height + 1 > height) height = c.Height + 1;
            }
            n.SubtreeSize = size; n.LeafCount = leaves; n.Height = height;
        }
        return order;
    }
}
