using System.Globalization;
using System.Text;
using TreeVisualizer.Core.Model;

namespace TreeVisualizer.Core.Diff;

[Flags]
public enum ChangeFlags
{
    None = 0,
    Added = 1,
    Removed = 2,
    /// <summary>The node's parent changed.</summary>
    Moved = 4,
    /// <summary>An ancestor moved; this node's own parent is unchanged.</summary>
    MovedWithAncestor = 8,
    /// <summary>One or more attribute values changed.</summary>
    Modified = 16,
}

/// <summary>The single class used for shading, chosen by precedence from the flags.</summary>
public enum ChangeClass { Unchanged, MovedWithAncestor, Modified, Moved, Removed, Added }

public sealed record AttributeChange(string Key, string? OldValue, string? NewValue);

public sealed class NodeChange
{
    public required string Id { get; init; }
    public ChangeFlags Flags { get; internal set; }
    public string? OldParentId { get; init; }
    public string? NewParentId { get; init; }
    public IReadOnlyList<AttributeChange> AttributeChanges { get; init; } = Array.Empty<AttributeChange>();
    /// <summary>Descendants (in the union tree) that are themselves changed, excluding MovedWithAncestor-only.</summary>
    public int ChangedDescendants { get; internal set; }
    public int Descendants { get; internal set; }
    public double ChangedFraction => Descendants == 0 ? 0 : (double)ChangedDescendants / Descendants;

    public ChangeClass Class =>
        Flags.HasFlag(ChangeFlags.Added) ? ChangeClass.Added :
        Flags.HasFlag(ChangeFlags.Removed) ? ChangeClass.Removed :
        Flags.HasFlag(ChangeFlags.Moved) ? ChangeClass.Moved :
        Flags.HasFlag(ChangeFlags.Modified) ? ChangeClass.Modified :
        Flags.HasFlag(ChangeFlags.MovedWithAncestor) ? ChangeClass.MovedWithAncestor :
        ChangeClass.Unchanged;

    /// <summary>True for changes that count on their own (everything except Unchanged and MovedWithAncestor-only).</summary>
    public bool IsDirectChange => (Flags & ~ChangeFlags.MovedWithAncestor) != ChangeFlags.None;
}

public sealed record DiffOptions
{
    /// <summary>Attribute keys ignored when deciding whether a node was modified.</summary>
    public IReadOnlySet<string> IgnoreKeys { get; init; } = new HashSet<string>();
}

public sealed class DiffResult
{
    internal DiffResult(Tree oldTree, Tree newTree, Tree union, Dictionary<string, NodeChange> changes)
    {
        Old = oldTree; New = newTree; Union = union; Changes = changes;
    }

    public Tree Old { get; }
    public Tree New { get; }
    /// <summary>The new tree plus "ghost" copies of removed nodes. Render this.</summary>
    public Tree Union { get; }
    public IReadOnlyDictionary<string, NodeChange> Changes { get; }

    public NodeChange Get(string id) => Changes[id];

    public int Count(ChangeFlags flag) => Changes.Values.Count(c => c.Flags.HasFlag(flag));

    public string Summary() =>
        $"added={Count(ChangeFlags.Added)} removed={Count(ChangeFlags.Removed)} moved={Count(ChangeFlags.Moved)} " +
        $"moved-with-ancestor={Count(ChangeFlags.MovedWithAncestor)} modified={Count(ChangeFlags.Modified)} " +
        $"unchanged={Changes.Values.Count(c => c.Flags == ChangeFlags.None)}";

    /// <summary>Writes a CSV change report (one row per changed node).</summary>
    public void WriteReport(TextWriter w)
    {
        w.WriteLine("id,change,flags,old_parent,new_parent,changed_attributes");
        foreach (var n in Union.Nodes)
        {
            var c = Changes[n.Id];
            if (c.Flags == ChangeFlags.None) continue;
            var attrs = string.Join("; ", c.AttributeChanges.Select(a => $"{a.Key}: {a.OldValue ?? "∅"} → {a.NewValue ?? "∅"}"));
            w.WriteLine(string.Join(",", Csv(c.Id), c.Class.ToString(), Csv(c.Flags.ToString().Replace(", ", "|")),
                Csv(c.OldParentId ?? ""), Csv(c.NewParentId ?? ""), Csv(attrs)));
        }
    }

    private static string Csv(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

/// <summary>
/// Compares two snapshots matched by node id. Produces a union tree (new tree + ghosts of removed nodes)
/// and a per-node change record with a "contains changes" rollup.
/// </summary>
public static class TreeDiff
{
    public static DiffResult Compare(Tree oldTree, Tree newTree, DiffOptions? options = null)
    {
        options ??= new DiffOptions();
        var changes = new Dictionary<string, NodeChange>(StringComparer.Ordinal);

        // 1. Classify nodes present in the new tree.
        foreach (var n in newTree.Nodes)
        {
            var old = oldTree.Find(n.Id);
            if (old == null)
            {
                changes[n.Id] = new NodeChange { Id = n.Id, Flags = ChangeFlags.Added, NewParentId = n.Parent?.Id };
                continue;
            }
            var flags = ChangeFlags.None;
            if (old.Parent?.Id != n.Parent?.Id) flags |= ChangeFlags.Moved;
            var attrChanges = CompareAttributes(old, n, options.IgnoreKeys);
            if (attrChanges.Count > 0) flags |= ChangeFlags.Modified;
            changes[n.Id] = new NodeChange
            {
                Id = n.Id, Flags = flags, OldParentId = old.Parent?.Id, NewParentId = n.Parent?.Id,
                AttributeChanges = attrChanges,
            };
        }

        // 2. Mark nodes carried along by a moved ancestor (pre-order: parents are processed first).
        foreach (var n in newTree.Nodes)
        {
            if (n.Parent == null) continue;
            var c = changes[n.Id];
            if (c.Flags.HasFlag(ChangeFlags.Added) || c.Flags.HasFlag(ChangeFlags.Moved)) continue;
            var pc = changes[n.Parent.Id];
            if (pc.Flags.HasFlag(ChangeFlags.Moved) || pc.Flags.HasFlag(ChangeFlags.MovedWithAncestor))
                c.Flags |= ChangeFlags.MovedWithAncestor;
        }

        // 3. Build the union tree: new tree, then removed nodes attached as ghosts.
        var b = new TreeBuilder();
        foreach (var (k, t) in newTree.AttributeTypes) b.DeclareAttribute(k, t);
        foreach (var (k, t) in oldTree.AttributeTypes) b.DeclareAttribute(k, t);
        foreach (var (k, v) in newTree.GraphAttributes) b.SetGraphAttribute(k, v);

        bool newHasSynthetic = newTree.Root.Id == TreeBuilder.SyntheticRootId;
        foreach (var n in newTree.Nodes)
        {
            if (newHasSynthetic && n == newTree.Root) continue; // re-created by the builder
            b.AddNode(n.Id, new Dictionary<string, string>(n.Attributes));
            if (n.Parent != null && !(newHasSynthetic && n.Parent == newTree.Root)) b.AddEdge(n.Parent.Id, n.Id);
        }

        string unionRootId = newTree.Root.Id;
        foreach (var o in oldTree.Nodes)
        {
            if (newTree.Find(o.Id) != null) continue;
            if (o.Id == TreeBuilder.SyntheticRootId) continue;
            string? attachTo;
            if (o.Parent == null || o.Parent.Id == TreeBuilder.SyntheticRootId)
                attachTo = newHasSynthetic ? null : unionRootId; // old root removed
            else
                attachTo = o.Parent.Id; // either a surviving node or an earlier ghost (pre-order)
            b.AddNode(o.Id, new Dictionary<string, string>(o.Attributes));
            if (attachTo != null) b.AddEdge(attachTo, o.Id);
            changes[o.Id] = new NodeChange
            {
                Id = o.Id, Flags = ChangeFlags.Removed, OldParentId = o.Parent?.Id, NewParentId = null,
            };
        }

        var union = b.Build();
        if (!changes.ContainsKey(union.Root.Id))
            changes[union.Root.Id] = new NodeChange { Id = union.Root.Id };

        // 4. Rollup: count changed descendants (post-order over the union tree).
        var changedBelow = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = union.Nodes.Count - 1; i >= 0; i--)
        {
            var n = union.Nodes[i];
            int count = 0;
            foreach (var c in n.Children)
                count += changedBelow[c.Id] + (changes[c.Id].IsDirectChange ? 1 : 0);
            changedBelow[n.Id] = count;
            var nc = changes[n.Id];
            nc.ChangedDescendants = count;
            nc.Descendants = n.SubtreeSize - 1;
        }

        return new DiffResult(oldTree, newTree, union, changes);
    }

    private static List<AttributeChange> CompareAttributes(TreeNode a, TreeNode b, IReadOnlySet<string> ignore)
    {
        var result = new List<AttributeChange>();
        foreach (var (k, va) in a.Attributes)
        {
            if (ignore.Contains(k)) continue;
            var vb = b.Get(k);
            if (!string.Equals(va, vb, StringComparison.Ordinal)) result.Add(new AttributeChange(k, va, vb));
        }
        foreach (var (k, vb) in b.Attributes)
        {
            if (ignore.Contains(k) || a.Attributes.ContainsKey(k)) continue;
            result.Add(new AttributeChange(k, null, vb));
        }
        return result;
    }
}
