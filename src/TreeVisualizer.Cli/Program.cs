using System.Diagnostics;
using System.Globalization;
using TreeVisualizer.Core.Diff;
using TreeVisualizer.Core.Drawing;
using TreeVisualizer.Core.IO;
using TreeVisualizer.Core.Layout;
using TreeVisualizer.Core.Model;
using TreeVisualizer.Core.Render;
using TreeVisualizer.Core.Synthetic;

namespace TreeVisualizer.Cli;

internal static class Program
{
    private const string Usage = """
        treeviz — radial tree renderer and differ for GraphML trees (see docs/FORMAT.md)

        Usage:
          treeviz stats    <tree.graphml>
          treeviz render   <tree.graphml> --out <file.png|pdf|svg> [view options]
          treeviz diff     <old.graphml> <new.graphml> --out <file.png|pdf|svg> [--report changes.csv]
                           [--side-by-side] [view options]
          treeviz generate --out <tree.graphml> [--nodes 10000] [--seed 1] [--snapshot 2026-06-01]
          treeviz mutate   <tree.graphml> --out <tree.graphml> [--seed 2] [--add 200] [--remove 150]
                           [--move 40] [--modify 100] [--snapshot 2026-11-01]

        View options:
          --root <id>              lay out this node's subtree (default: tree root)
          --depth <n>              show at most n levels below the root
          --layout radial|cascade|sunburst (default radial)
          --color <key>|branch|none  color by attribute key (default: branch)
          --labels <k1,k2,...>     attribute keys joined into labels (default: name if present, else id)
          --label-mode auto|all|none  auto = only where they fit; all = shrink to fit (default auto)
          --font <px>              base label size (default 11)
          --sort id|headcount|input|<key>  sibling order (default id)
          --desc                   reverse attribute sort
          --alpha <a>              wedge ∝ leaves^a, 0.3–1 (default 1)
          --gap <deg>              open angle at the top (default 0)
          --rings <k1,k2,...>      annotation rings outside the tree, one per attribute key (innermost first)
          --stagger 1|2|3          spread crowded leaf groups over this many sub-rings (radial/cascade; default 1)
          --size <px>              output width/height in px (PDF: points) (default 4000)
          --title <text>           title (default: snapshot + root label)
          --no-legend, --no-title
        """;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return 0;
            }
            var a = new Args(args.Skip(1));
            return args[0] switch
            {
                "stats" => Stats(a),
                "render" => Render(a),
                "diff" => DiffCmd(a),
                "generate" => Generate(a),
                "mutate" => Mutate(a),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}"),
            };
        }
        catch (TreeBuildException ex)
        {
            return Fail(ex.Message);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return Fail(ex.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("error: " + message);
        return 1;
    }

    private static Tree Load(string path)
    {
        var result = GraphMl.Load(path);
        foreach (var issue in result.Issues)
            Console.Error.WriteLine($"{issue.Severity.ToString().ToLowerInvariant()}: {issue.Message}");
        return result.Tree;
    }

    private static int Stats(Args a)
    {
        var tree = Load(a.Positional(0, "tree file"));
        Console.WriteLine(TreeStats.Compute(tree));
        Console.WriteLine("attributes: " + string.Join(", ", tree.AttributeTypes.Select(kv => $"{kv.Key}:{kv.Value.ToString().ToLowerInvariant()}")));
        if (tree.GraphAttributes.Count > 0)
            Console.WriteLine("graph: " + string.Join(", ", tree.GraphAttributes.Select(kv => $"{kv.Key}={kv.Value}")));
        if (tree.SecondaryEdges.Count > 0)
            Console.WriteLine($"secondary edges (not rendered in v1): {tree.SecondaryEdges.Count}");
        return 0;
    }

    private static int Render(Args a)
    {
        var tree = Load(a.Positional(0, "tree file"));
        a.ExpectPositionals(1);
        return RenderTree(tree, null, a);
    }

    private static int DiffCmd(Args a)
    {
        var oldTree = Load(a.Positional(0, "old tree file"));
        var newTree = Load(a.Positional(1, "new tree file"));
        a.ExpectPositionals(2);
        var sw = Stopwatch.StartNew();
        var diff = TreeDiff.Compare(oldTree, newTree);
        Console.WriteLine($"diff: {diff.Summary()} ({sw.ElapsedMilliseconds} ms)");
        if (a.Get("--report") is string report)
        {
            using var w = new StreamWriter(report, false, new System.Text.UTF8Encoding(true));
            diff.WriteReport(w);
            Console.WriteLine($"wrote {report}");
        }
        // Side by side: before and after panels. Otherwise: one union view with removed nodes as ghosts.
        return a.Flag("--side-by-side") ? RenderTree(diff.New, diff, a, sideBySide: true) : RenderTree(diff.Union, diff, a);
    }

    private static int RenderTree(Tree tree, DiffResult? diff, Args a, bool sideBySide = false)
    {
        var outPath = a.Require("--out");
        var format = Exporter.FormatFromPath(outPath);

        var sortArg = a.Get("--sort") ?? "id";
        var (sort, sortKey) = sortArg switch
        {
            "id" => (SortMode.Id, (string?)null),
            "headcount" => (SortMode.Headcount, null),
            "input" => (SortMode.Input, null),
            var key => (SortMode.Attribute, key),
        };
        if (sortKey != null && !tree.AttributeTypes.ContainsKey(sortKey))
            throw new ArgumentException($"Unknown sort key '{sortKey}'. Keys: {string.Join(", ", tree.AttributeKeys)}");

        var layoutOptions = new LayoutOptions
        {
            Mode = (a.Get("--layout") ?? "radial") switch
            {
                "radial" => LayoutMode.Radial,
                "sunburst" => LayoutMode.Sunburst,
                "cascade" => LayoutMode.Cascade,
                var m => throw new ArgumentException($"Unknown layout '{m}'."),
            },
            RootId = a.Get("--root"),
            MaxDepth = a.GetInt("--depth"),
            Alpha = a.GetDouble("--alpha") ?? 1.0,
            Sort = sort,
            SortKey = sortKey,
            SortDescending = a.Flag("--desc"),
            OpenAngleDegrees = a.GetDouble("--gap") ?? 0,
            LeafStagger = a.GetInt("--stagger") ?? 1,
        };

        var colorArg = a.Get("--color") ?? "branch";
        var labelKeys = a.Get("--labels")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        ?? (tree.AttributeTypes.ContainsKey("name") ? new[] { "name" } : Array.Empty<string>());
        foreach (var k in labelKeys)
            if (k != "id" && !tree.AttributeTypes.ContainsKey(k))
                throw new ArgumentException($"Unknown label key '{k}'. Keys: {string.Join(", ", tree.AttributeKeys)}");
        var ringKeys = a.Get("--rings")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       ?? Array.Empty<string>();
        foreach (var k in ringKeys)
            if (!tree.AttributeTypes.ContainsKey(k))
                throw new ArgumentException($"Unknown ring key '{k}'. Keys: {string.Join(", ", tree.AttributeKeys)}");
        if (colorArg is not ("branch" or "none") && !tree.AttributeTypes.ContainsKey(colorArg))
            throw new ArgumentException($"Unknown color key '{colorArg}'. Keys: {string.Join(", ", tree.AttributeKeys)}");

        var sceneOptions = new SceneOptions
        {
            ColorMode = colorArg switch { "branch" => ColorMode.Branch, "none" => ColorMode.None, _ => ColorMode.Attribute },
            ColorKey = colorArg is "branch" or "none" ? null : colorArg,
            LabelKeys = labelKeys,
            AnnotationKeys = ringKeys,
        };

        var sw = Stopwatch.StartNew();
        var layout = PolarLayout.Compute(tree, layoutOptions);
        var tLayout = sw.ElapsedMilliseconds;
        var scene = SceneBuilder.Build(layout, sceneOptions, diff);
        var tScene = sw.ElapsedMilliseconds - tLayout;
        scene.Title = a.Get("--title") ?? DefaultTitle(tree, layout, sceneOptions, diff);

        var labelMode = (a.Get("--label-mode") ?? "auto") switch
        {
            "auto" => LabelMode.Auto,
            "all" => LabelMode.All,
            "none" => LabelMode.None,
            var m => throw new ArgumentException($"Unknown label mode '{m}'."),
        };
        int size = a.GetInt("--size") ?? 4000;
        var settings = new ExportSettings
        {
            Width = size,
            Labels = new LabelSettings { Mode = labelMode, FontPx = (float)(a.GetDouble("--font") ?? 11) },
            Overlay = new OverlaySettings { Legend = !a.Flag("--no-legend"), Title = !a.Flag("--no-title") },
        };
        a.RejectUnknown();

        sw.Restart();
        if (sideBySide && diff != null)
        {
            var old = diff.Old;
            var oldOptions = layoutOptions with { RootId = layoutOptions.RootId != null && old.Find(layoutOptions.RootId) != null ? layoutOptions.RootId : null };
            var oldScene = SceneBuilder.Build(PolarLayout.Compute(old, oldOptions), sceneOptions, diff);
            PairExporter.Export(
                new ExportPanel(oldScene, "Before: " + (old.GraphAttributes.GetValueOrDefault("snapshot") ?? "old")),
                new ExportPanel(scene, "After: " + (tree.GraphAttributes.GetValueOrDefault("snapshot") ?? "new")),
                scene.Title, outPath, settings);
            Console.WriteLine($"rendered before/after side by side → {outPath} [{format}, 2 x {size}px; export {sw.ElapsedMilliseconds} ms]");
            return 0;
        }
        Exporter.Export(scene, outPath, settings);
        Console.WriteLine($"rendered {layout.Nodes.Count:N0} nodes ({scene.PrimitiveCount:N0} primitives) → {outPath} " +
                          $"[{format}, {size}px; layout {tLayout} ms, scene {tScene} ms, export {sw.ElapsedMilliseconds} ms]");
        return 0;
    }

    private static string DefaultTitle(Tree tree, LayoutResult layout, SceneOptions o, DiffResult? diff)
    {
        var rootLabel = SceneBuilder.LabelFor(layout.Root.Node, o.LabelKeys, o.LabelSeparator);
        if (diff != null)
        {
            var from = diff.Old.GraphAttributes.GetValueOrDefault("snapshot");
            var to = diff.New.GraphAttributes.GetValueOrDefault("snapshot");
            var range = from != null && to != null ? $"{from} → {to}" : "changes";
            return $"{rootLabel} — {range}";
        }
        var snap = tree.GraphAttributes.GetValueOrDefault("snapshot");
        return snap != null ? $"{rootLabel} — {snap}" : rootLabel;
    }

    private static int Generate(Args a)
    {
        var outPath = a.Require("--out");
        var tree = SyntheticOrg.Generate(new OrgOptions
        {
            Nodes = a.GetInt("--nodes") ?? 10_000,
            Seed = a.GetInt("--seed") ?? 1,
            Snapshot = a.Get("--snapshot") ?? "2026-06-01",
        });
        a.RejectUnknown();
        GraphMl.Save(tree, outPath);
        Console.WriteLine($"wrote {outPath}");
        Console.WriteLine(TreeStats.Compute(tree));
        return 0;
    }

    private static int Mutate(Args a)
    {
        var tree = Load(a.Positional(0, "tree file"));
        var outPath = a.Require("--out");
        var plan = new MutationPlan(
            a.GetInt("--add") ?? 200, a.GetInt("--remove") ?? 150, a.GetInt("--move") ?? 40, a.GetInt("--modify") ?? 100);
        var (mutated, log) = SyntheticOrg.Mutate(tree, plan, a.GetInt("--seed") ?? 2, a.Get("--snapshot") ?? "2026-11-01");
        a.RejectUnknown();
        GraphMl.Save(mutated, outPath);
        Console.WriteLine($"wrote {outPath}: added={log.Added.Count} removed={log.Removed.Count} moved={log.Moved.Count} modified={log.Modified.Count}");
        return 0;
    }
}

/// <summary>Minimal argument parser: positionals plus --name value / --flag options.</summary>
internal sealed class Args
{
    private static readonly HashSet<string> Flags = new() { "--desc", "--no-legend", "--no-title", "--side-by-side" };
    private readonly List<string> _positionals = new();
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public Args(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (s.StartsWith("--", StringComparison.Ordinal))
            {
                if (Flags.Contains(s)) _options[s] = null;
                else if (i + 1 < list.Count) _options[s] = list[++i];
                else throw new ArgumentException($"Option {s} needs a value.");
            }
            else _positionals.Add(s);
        }
    }

    public string Positional(int index, string what) =>
        index < _positionals.Count ? _positionals[index] : throw new ArgumentException($"Missing {what}.");

    public void ExpectPositionals(int count)
    {
        if (_positionals.Count > count) throw new ArgumentException($"Unexpected argument '{_positionals[count]}'.");
    }

    public string? Get(string name)
    {
        _used.Add(name);
        return _options.TryGetValue(name, out var v) ? v : null;
    }

    public string Require(string name) => Get(name) ?? throw new ArgumentException($"Missing required option {name}.");

    public bool Flag(string name)
    {
        _used.Add(name);
        return _options.ContainsKey(name);
    }

    public int? GetInt(string name) => Get(name) is string s
        ? int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v
          : throw new ArgumentException($"{name} expects an integer, got '{s}'.")
        : null;

    public double? GetDouble(string name) => Get(name) is string s
        ? double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v
          : throw new ArgumentException($"{name} expects a number, got '{s}'.")
        : null;

    public void RejectUnknown()
    {
        var unknown = _options.Keys.Where(k => !_used.Contains(k)).ToList();
        if (unknown.Count > 0) throw new ArgumentException($"Unknown option(s): {string.Join(", ", unknown)}.");
    }
}
