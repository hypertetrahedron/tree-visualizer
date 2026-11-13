namespace TreeVisualizer.Core.Model;

public enum IssueSeverity { Warning, Error }

public sealed record ValidationIssue(IssueSeverity Severity, string Message);

public sealed class TreeBuildException : Exception
{
    public TreeBuildException(IReadOnlyList<ValidationIssue> issues)
        : base(FormatMessage(issues)) => Issues = issues;

    public IReadOnlyList<ValidationIssue> Issues { get; }

    private static string FormatMessage(IReadOnlyList<ValidationIssue> issues)
    {
        var errors = issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
        var head = string.Join(Environment.NewLine, errors.Take(10).Select(e => "  " + e.Message));
        var more = errors.Count > 10 ? $"{Environment.NewLine}  ... and {errors.Count - 10} more" : "";
        return $"Tree is invalid ({errors.Count} error(s)):{Environment.NewLine}{head}{more}";
    }
}

/// <summary>
/// Collects nodes and parent→child edges, validates tree constraints, and builds a <see cref="Tree"/>.
/// </summary>
public sealed class TreeBuilder
{
    public const string SyntheticRootId = "__root__";

    private readonly Dictionary<string, Dictionary<string, string>> _nodes = new(StringComparer.Ordinal);
    private readonly List<string> _nodeOrder = new();
    private readonly List<(string Parent, string Child)> _edges = new();
    private readonly List<SecondaryEdge> _secondary = new();
    private readonly List<ValidationIssue> _issues = new();
    private readonly Dictionary<string, AttributeType> _attrTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _graphAttrs = new(StringComparer.Ordinal);

    public IReadOnlyList<ValidationIssue> Issues => _issues;

    public void DeclareAttribute(string key, AttributeType type) => _attrTypes.TryAdd(key, type);

    public void SetGraphAttribute(string key, string value) => _graphAttrs[key] = value;

    public void AddWarning(string message) => _issues.Add(new(IssueSeverity.Warning, message));
    public void AddError(string message) => _issues.Add(new(IssueSeverity.Error, message));

    public void AddNode(string id, IDictionary<string, string>? attributes = null)
    {
        if (string.IsNullOrEmpty(id)) { AddError("Node with empty id."); return; }
        if (_nodes.ContainsKey(id)) { AddError($"Duplicate node id '{id}'."); return; }
        var attrs = attributes == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(attributes, StringComparer.Ordinal);
        foreach (var k in attrs.Keys) _attrTypes.TryAdd(k, AttributeType.String);
        _nodes[id] = attrs;
        _nodeOrder.Add(id);
    }

    public void AddEdge(string parentId, string childId) => _edges.Add((parentId, childId));

    public void AddSecondaryEdge(string sourceId, string targetId, string relation) =>
        _secondary.Add(new SecondaryEdge(sourceId, targetId, relation));

    /// <summary>Builds the tree. Throws <see cref="TreeBuildException"/> if any error was found.</summary>
    public Tree Build()
    {
        if (_nodes.Count == 0) AddError("Graph contains no nodes.");

        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (p, c) in _edges)
        {
            bool ok = true;
            if (!_nodes.ContainsKey(p)) { AddError($"Edge references unknown source node '{p}'."); ok = false; }
            if (!_nodes.ContainsKey(c)) { AddError($"Edge references unknown target node '{c}'."); ok = false; }
            if (!ok) continue;
            if (p == c) { AddError($"Self-loop on node '{p}'."); continue; }
            if (parentOf.TryGetValue(c, out var existing))
            {
                if (existing == p) AddWarning($"Duplicate edge '{p}' -> '{c}' ignored.");
                else AddError($"Node '{c}' has more than one parent ('{existing}' and '{p}').");
                continue;
            }
            parentOf[c] = p;
            if (!children.TryGetValue(p, out var list)) children[p] = list = new List<string>();
            list.Add(c);
        }

        foreach (var e in _secondary)
        {
            if (!_nodes.ContainsKey(e.SourceId) || !_nodes.ContainsKey(e.TargetId))
                AddWarning($"Secondary edge '{e.SourceId}' -> '{e.TargetId}' ({e.Relation}) references an unknown node; ignored.");
        }

        if (_issues.Any(i => i.Severity == IssueSeverity.Error))
            throw new TreeBuildException(_issues);

        var roots = _nodeOrder.Where(id => !parentOf.ContainsKey(id)).ToList();

        // Reachability from roots detects cycles: nodes in a cycle are never reached.
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(roots);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!reached.Add(id)) continue;
            if (children.TryGetValue(id, out var cs)) foreach (var c in cs) stack.Push(c);
        }
        if (reached.Count != _nodes.Count)
        {
            var inCycle = _nodeOrder.Where(id => !reached.Contains(id)).Take(10);
            AddError($"Cycle detected; {_nodes.Count - reached.Count} node(s) are unreachable from any root (e.g. {string.Join(", ", inCycle)}).");
            throw new TreeBuildException(_issues);
        }

        var built = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
        foreach (var id in _nodeOrder) built[id] = new TreeNode(id, _nodes[id]);
        foreach (var id in _nodeOrder)
        {
            if (!children.TryGetValue(id, out var cs)) continue;
            var parent = built[id];
            foreach (var c in cs)
            {
                var child = built[c];
                child.Parent = parent;
                parent.ChildList.Add(child);
            }
        }

        TreeNode root;
        if (roots.Count == 1)
        {
            root = built[roots[0]];
        }
        else
        {
            if (built.ContainsKey(SyntheticRootId))
            {
                AddError($"Multiple roots found and the synthetic root id '{SyntheticRootId}' is already in use.");
                throw new TreeBuildException(_issues);
            }
            AddWarning($"Found {roots.Count} root nodes; added synthetic root '{SyntheticRootId}'.");
            root = new TreeNode(SyntheticRootId, new Dictionary<string, string> { ["name"] = "(root)" });
            foreach (var r in roots)
            {
                var n = built[r];
                n.Parent = root;
                root.ChildList.Add(n);
            }
            built[SyntheticRootId] = root;
        }

        return new Tree(root, built, new Dictionary<string, AttributeType>(_attrTypes),
            new Dictionary<string, string>(_graphAttrs), _secondary.ToList());
    }
}
