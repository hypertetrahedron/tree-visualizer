using System.Globalization;
using TreeVisualizer.Core.Model;

namespace TreeVisualizer.Core.Synthetic;

public sealed record OrgOptions
{
    public int Nodes { get; init; } = 10_000;
    public int Seed { get; init; } = 1;
    public string Snapshot { get; init; } = "2026-06-01";
}

public sealed record MutationPlan(int Add = 200, int Remove = 150, int Move = 40, int Modify = 100);

public sealed record MutationLog(
    IReadOnlyList<string> Added, IReadOnlyList<string> Removed,
    IReadOnlyList<string> Moved, IReadOnlyList<string> Modified);

/// <summary>Generates realistic synthetic org charts (skewed spans, average leaf depth ≈ 6) and mutations of them.</summary>
public static class SyntheticOrg
{
    private static readonly string[] First =
    {
        "Ada", "Alan", "Grace", "Linus", "Margaret", "Dennis", "Barbara", "Ken", "Frances", "Edsger", "Radia", "Donald",
        "Hedy", "Tim", "Katherine", "John", "Mary", "Claude", "Sophie", "Niklaus", "Anita", "Bjarne", "Lynn", "Guido",
        "Shafi", "James", "Joan", "Leslie", "Carol", "Vint", "Ellen", "Larry", "Susan", "Robert", "Jean", "Dana",
        "Karen", "Ivan", "Fran", "Butler", "Priya", "Wei", "Amara", "Diego", "Yuki", "Omar", "Ingrid", "Kofi",
    };

    private static readonly string[] Last =
    {
        "Lovelace", "Turing", "Hopper", "Torvalds", "Hamilton", "Ritchie", "Liskov", "Thompson", "Allen", "Dijkstra",
        "Perlman", "Knuth", "Lamarr", "Berners-Lee", "Johnson", "McCarthy", "Kenneth", "Shannon", "Wilson", "Wirth",
        "Borg", "Stroustrup", "Conway", "Rossum", "Goldwasser", "Gosling", "Clarke", "Lamport", "Shaw", "Cerf",
        "Ochoa", "Page", "Kare", "Tarjan", "Sammet", "Scott", "Jones", "Sutherland", "Bilas", "Lampson", "Patel",
        "Zhang", "Okafor", "Garcia", "Tanaka", "Haddad", "Larsen", "Mensah",
    };

    private static readonly string[] Departments =
        { "Engineering", "Sales", "Operations", "Finance", "Marketing", "People", "Legal", "Product", "Customer Success" };

    private static readonly string[] Locations =
        { "Seattle", "Austin", "New York", "London", "Berlin", "Bangalore", "Singapore", "São Paulo", "Toronto", "Sydney" };

    private static readonly string[] ManagerTitles =
        { "Chief Executive Officer", "Senior Vice President", "Vice President", "Director", "Senior Manager", "Manager", "Team Lead" };

    private static readonly Dictionary<string, string[]> IcTitles = new()
    {
        ["Engineering"] = new[] { "Software Engineer", "Senior Software Engineer", "Staff Engineer", "QA Engineer", "SRE" },
        ["Sales"] = new[] { "Account Executive", "Sales Engineer", "Sales Associate", "Account Manager" },
        ["Operations"] = new[] { "Operations Analyst", "Logistics Coordinator", "Facilities Specialist" },
        ["Finance"] = new[] { "Financial Analyst", "Accountant", "Controller" },
        ["Marketing"] = new[] { "Marketing Specialist", "Content Strategist", "Designer" },
        ["People"] = new[] { "Recruiter", "HR Business Partner", "People Ops Specialist" },
        ["Legal"] = new[] { "Counsel", "Paralegal", "Compliance Analyst" },
        ["Product"] = new[] { "Product Manager", "UX Researcher", "Product Designer" },
        ["Customer Success"] = new[] { "Support Engineer", "Customer Success Manager", "Support Specialist" },
    };

    // Probability that a node at a given depth becomes a manager, and typical span.
    private static readonly double[] ManagerProbability = { 1, 1, 1, 0.85, 0.55, 0.38, 0.28, 0.2, 0.13, 0.07, 0.03, 0 };

    private sealed class Proto
    {
        public required string Id;
        public Proto? Parent;
        public int Depth;
        public string Department = "";
        public string Location = "";
        public readonly List<Proto> Children = new();
    }

    public static Tree Generate(OrgOptions? options = null)
    {
        options ??= new OrgOptions();
        var rng = new Random(options.Seed);
        int target = Math.Max(1, options.Nodes);
        int next = 1;
        string NewId() => "E" + (next++).ToString("D6", CultureInfo.InvariantCulture);

        var root = new Proto { Id = NewId(), Depth = 0, Location = Locations[0], Department = "Executive" };
        var all = new List<Proto> { root };
        // Frontier expanded in random order (not breadth-first) so branch depths vary like a real org.
        var frontier = new List<Proto> { root };

        while (all.Count < target)
        {
            if (frontier.Count == 0)
            {
                // Everything left is a leaf: promote a random shallow leaf to manager.
                var candidates = all.Where(p => p.Children.Count == 0 && p.Depth < 7).ToList();
                frontier.Add(candidates.Count > 0 ? candidates[rng.Next(candidates.Count)] : all[rng.Next(all.Count)]);
            }
            int pick = rng.Next(frontier.Count);
            var m = frontier[pick];
            frontier[pick] = frontier[^1];
            frontier.RemoveAt(frontier.Count - 1);
            double pm = m.Depth < ManagerProbability.Length ? ManagerProbability[m.Depth] : 0;
            if (m.Children.Count == 0 && rng.NextDouble() >= pm && m.Depth > 0) continue;

            int span = m.Depth switch
            {
                0 => 8,
                1 => rng.Next(5, 9),
                _ => SampleSpan(rng),
            };
            span = Math.Min(span, target - all.Count);
            for (int i = 0; i < span; i++)
            {
                var c = new Proto { Id = NewId(), Parent = m, Depth = m.Depth + 1 };
                c.Department = m.Depth == 0 ? Departments[i % Departments.Length] : m.Department;
                c.Location = m.Depth <= 1 || rng.NextDouble() < 0.12 ? Locations[rng.Next(Locations.Length)] : m.Location;
                m.Children.Add(c);
                all.Add(c);
                frontier.Add(c);
            }
        }

        var b = new TreeBuilder();
        b.SetGraphAttribute("snapshot", options.Snapshot);
        b.DeclareAttribute("name", AttributeType.String);
        b.DeclareAttribute("title", AttributeType.String);
        b.DeclareAttribute("department", AttributeType.String);
        b.DeclareAttribute("location", AttributeType.String);
        b.DeclareAttribute("level", AttributeType.Int);
        foreach (var p in all)
        {
            string title;
            if (p.Children.Count > 0)
                title = ManagerTitles[Math.Min(p.Depth, ManagerTitles.Length - 1)];
            else
            {
                var options2 = IcTitles.TryGetValue(p.Department, out var t) ? t : new[] { "Associate" };
                title = options2[rng.Next(options2.Length)];
            }
            b.AddNode(p.Id, new Dictionary<string, string>
            {
                ["name"] = $"{First[rng.Next(First.Length)]} {Last[rng.Next(Last.Length)]}",
                ["title"] = title,
                ["department"] = p.Department,
                ["location"] = p.Location,
                ["level"] = p.Depth.ToString(CultureInfo.InvariantCulture),
            });
        }
        foreach (var p in all)
            if (p.Parent != null) b.AddEdge(p.Parent.Id, p.Id);
        return b.Build();
    }

    /// <summary>Skewed span of control: mostly 3–8, occasionally large.</summary>
    private static int SampleSpan(Random rng)
    {
        // Log-normal with median ≈ 5.
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        double z = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        return Math.Clamp((int)Math.Round(Math.Exp(Math.Log(5) + 0.55 * z)), 1, 45);
    }

    /// <summary>
    /// Applies disjoint random changes: adds new leaves, removes existing leaves, moves subtrees
    /// to new managers, and modifies attributes. The log lists exactly what changed.
    /// </summary>
    public static (Tree Tree, MutationLog Log) Mutate(Tree tree, MutationPlan plan, int seed, string? snapshot = null)
    {
        var rng = new Random(seed);
        var parent = tree.Nodes.ToDictionary(n => n.Id, n => n.Parent?.Id, StringComparer.Ordinal);
        var attrs = tree.Nodes.ToDictionary(n => n.Id, n => new Dictionary<string, string>(n.Attributes), StringComparer.Ordinal);
        var order = tree.Nodes.Select(n => n.Id).ToList();
        var touched = new HashSet<string>(StringComparer.Ordinal) { tree.Root.Id };

        T Pick<T>(IReadOnlyList<T> list) => list[rng.Next(list.Count)];

        // Removals: leaves only, so no one else's parent changes implicitly.
        var removed = new List<string>();
        var leaves = tree.Nodes.Where(n => n.IsLeaf && n.Parent != null).Select(n => n.Id).ToList();
        Shuffle(leaves, rng);
        foreach (var id in leaves)
        {
            if (removed.Count >= plan.Remove) break;
            removed.Add(id);
            touched.Add(id);
        }
        var removedSet = removed.ToHashSet(StringComparer.Ordinal);

        // Moves: re-parent a node under a manager outside its own subtree.
        var moved = new List<string>();
        var managers = tree.Nodes.Where(n => !n.IsLeaf).ToList();
        int guard = 0;
        while (moved.Count < plan.Move && guard++ < plan.Move * 50)
        {
            var n = Pick(tree.Nodes);
            if (touched.Contains(n.Id) || n.Parent == null) continue;
            var target = Pick(managers);
            if (removedSet.Contains(target.Id) || target.Id == parent[n.Id] || target == n || target.IsDescendantOf(n)) continue;
            // Avoid moving into a subtree that is itself moving (keeps flags exact and cycles impossible).
            if (moved.Any(m => target.Id == m || target.IsDescendantOf(tree.ById[m]) || tree.ById[m].IsDescendantOf(n))) continue;
            parent[n.Id] = target.Id;
            moved.Add(n.Id);
            touched.Add(n.Id);
        }

        // Modifications.
        var modified = new List<string>();
        guard = 0;
        while (modified.Count < plan.Modify && guard++ < plan.Modify * 50)
        {
            var n = Pick(tree.Nodes);
            if (touched.Contains(n.Id)) continue;
            var a = attrs[n.Id];
            if (rng.Next(2) == 0) a["title"] = (a.GetValueOrDefault("title") ?? "") + " II";
            else a["location"] = Locations[(Array.IndexOf(Locations, a.GetValueOrDefault("location")) + 1 + Locations.Length) % Locations.Length];
            modified.Add(n.Id);
            touched.Add(n.Id);
        }

        // Additions: new leaves under existing, non-removed managers.
        var added = new List<string>();
        int seq = 1;
        for (int i = 0; i < plan.Add; i++)
        {
            var m = Pick(managers);
            if (removedSet.Contains(m.Id)) { i--; continue; }
            string id;
            do id = "N" + (seq++).ToString("D6", CultureInfo.InvariantCulture); while (attrs.ContainsKey(id));
            var src = attrs[m.Id];
            attrs[id] = new Dictionary<string, string>
            {
                ["name"] = $"{Pick(First)} {Pick(Last)}",
                ["title"] = "New Hire",
                ["department"] = src.GetValueOrDefault("department") ?? "",
                ["location"] = src.GetValueOrDefault("location") ?? "",
                ["level"] = ((tree.ById[m.Id].Depth + 1)).ToString(CultureInfo.InvariantCulture),
            };
            parent[id] = m.Id;
            order.Add(id);
            added.Add(id);
        }

        var b = new TreeBuilder();
        foreach (var (k, v) in tree.GraphAttributes) b.SetGraphAttribute(k, v);
        if (snapshot != null) b.SetGraphAttribute("snapshot", snapshot);
        foreach (var (k, t) in tree.AttributeTypes) b.DeclareAttribute(k, t);
        foreach (var id in order)
            if (!removedSet.Contains(id)) b.AddNode(id, attrs[id]);
        foreach (var id in order)
            if (!removedSet.Contains(id) && parent[id] is string p) b.AddEdge(p, id);
        return (b.Build(), new MutationLog(added, removed, moved, modified));
    }

    private static void Shuffle<T>(IList<T> list, Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
