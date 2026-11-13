using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TreeVisualizer.Core.Diff;
using TreeVisualizer.Core.Drawing;
using TreeVisualizer.Core.IO;
using TreeVisualizer.Core.Layout;
using TreeVisualizer.Core.Model;
using TreeVisualizer.Core.Render;
using TreeVisualizer.Core.Synthetic;

namespace TreeVisualizer.App;

public sealed record LegendRow(IBrush Brush, string Text);

/// <summary>One visible row of the outline panel.</summary>
public sealed record OutlineRow(TreeNode Node, bool HasChildren, string Glyph, string Text, Avalonia.Thickness Indent, IBrush Marker)
{
    public override string ToString() => Text; // accessible name of the list item
}

public partial class MainWindow : Window
{
    private const string BranchItem = "(top-level branch)";
    private const string NoColorItem = "(none)";
    private const string ChangesItem = "(changes)";
    private static readonly string[] FixedSorts = { "(id)", "(headcount)", "(input order)" };

    private Tree? _tree;          // the snapshot being viewed (the "new" one when diffing)
    private Tree? _oldTree;
    private DiffResult? _diff;
    private string? _treeName, _oldName;
    private string? _centerId;
    private Scene? _scene;
    private LayoutResult? _layout;
    private bool _updating;
    private List<TreeNode> _searchMatches = new();
    private LayoutResult? _oldLayout;
    private Scene? _oldScene;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private bool _outlineUpdating;
    private bool _wasSideBySide;

    private bool SideBySide => SideBySideBox.IsChecked == true && _diff != null;
    /// <summary>The tree shown in the main canvas: the union (with ghosts) when diffing, or the newer snapshot side by side.</summary>
    private Tree? Displayed => SideBySide ? _tree : _diff?.Union ?? _tree;

    public MainWindow()
    {
        InitializeComponent();

        LayoutBox.SelectionChanged += (_, _) => Rebuild(resetView: true);
        ColorBox.SelectionChanged += (_, _) => Rebuild(resetView: false);
        SortBox.SelectionChanged += (_, _) => Rebuild(resetView: false);
        DepthBox.ValueChanged += (_, _) => Rebuild(resetView: true);
        AlphaSlider.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) Rebuild(false); };
        LabelModeBox.SelectionChanged += (_, _) =>
        {
            Canvas.Labels = OldCanvas.Labels = CurrentLabelSettings();
            Canvas.InvalidateVisual(); OldCanvas.InvalidateVisual();
        };
        LabelKeysBox.SelectionChanged += (_, _) => Rebuild(resetView: false);
        RingKeysBox.SelectionChanged += (_, _) => Rebuild(resetView: false);
        StaggerBox.SelectionChanged += (_, _) => Rebuild(resetView: false);
        SideBySideBox.IsCheckedChanged += (_, _) => { Rebuild(resetView: true); RunSearch(); };
        OutlineList.SelectionChanged += (_, _) => OnOutlineChosen();
        SearchBox.TextChanged += (_, _) => RunSearch();
        SearchResults.SelectionChanged += (_, _) => OnSearchResultChosen();

        Canvas.HoverChanged += n => { OldCanvas.LinkedId = n?.Node.Id; OldCanvas.InvalidateVisual(); ShowInspector(); };
        OldCanvas.HoverChanged += n => { Canvas.LinkedId = n?.Node.Id; Canvas.InvalidateVisual(); ShowInspector(); };
        Canvas.SelectionChanged += n =>
        {
            if (SideBySide) OldCanvas.Select(n == null ? null : OldCanvas.Layout?.ById.GetValueOrDefault(n.Node.Id));
            ShowInspector();
            if (n != null) RevealInOutline(n.Node);
        };
        OldCanvas.SelectionChanged += n =>
        {
            // Mirror into the after panel; a removed person exists only in the before panel.
            if (n != null && Canvas.Layout?.ById.GetValueOrDefault(n.Node.Id) is { } other) Canvas.Select(other);
            ShowInspector();
        };
        Canvas.NodeActivated += OnNodeActivated;
        OldCanvas.NodeActivated += OnNodeActivated;
        // Linked pan and zoom between the two side-by-side views.
        Canvas.ViewChanged += v => { if (SideBySide) OldCanvas.SetView(v); };
        OldCanvas.ViewChanged += v => { if (SideBySide) Canvas.SetView(v); };

        Opened += async (_, _) =>
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            if (args.Length >= 1) await LoadAsync(args[0], isOld: false);
            if (args.Length >= 2 && _tree != null) await LoadAsync(args[1], isOld: true);
        };
    }

    // ---------------- loading ----------------

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        var path = await PickGraphMl("Open tree (GraphML)");
        if (path != null) await LoadAsync(path, isOld: false);
    }

    private async void OnCompare(object? sender, RoutedEventArgs e)
    {
        var path = await PickGraphMl("Open the OLDER snapshot to compare against");
        if (path != null) await LoadAsync(path, isOld: true);
    }

    private void OnClearDiff(object? sender, RoutedEventArgs e)
    {
        _oldTree = null; _diff = null; _oldName = null;
        AfterDataChanged();
    }

    private async void OnSample(object? sender, RoutedEventArgs e)
    {
        SetStatus("Generating synthetic organization…");
        var (a, b) = await Task.Run(() =>
        {
            var old = SyntheticOrg.Generate(new OrgOptions { Nodes = 10_000, Seed = 1, Snapshot = "2026-06-01" });
            var (now, _) = SyntheticOrg.Mutate(old, new MutationPlan(), 2, "2026-11-01");
            return (old, now);
        });
        _oldTree = a; _tree = b; _oldName = "sample 2026-06-01"; _treeName = "sample 2026-11-01";
        _diff = TreeDiff.Compare(a, b);
        _centerId = null;
        AfterDataChanged();
    }

    private async Task<string?> PickGraphMl(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("GraphML") { Patterns = new[] { "*.graphml", "*.xml" } },
                FilePickerFileTypes.All,
            },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task LoadAsync(string path, bool isOld)
    {
        SetStatus($"Loading {Path.GetFileName(path)}…");
        try
        {
            var result = await Task.Run(() => GraphMl.Load(path));
            var warnings = result.Issues.Where(i => i.Severity == IssueSeverity.Warning).Select(i => i.Message).ToList();
            if (isOld)
            {
                _oldTree = result.Tree; _oldName = Path.GetFileName(path);
            }
            else
            {
                _tree = result.Tree; _treeName = Path.GetFileName(path);
                _centerId = null;
                if (_oldTree != null && _diff == null) _oldTree = null;
            }
            _diff = _oldTree != null && _tree != null ? await Task.Run(() => TreeDiff.Compare(_oldTree, _tree)) : null;
            AfterDataChanged();
            if (warnings.Count > 0) SetStatus("Warnings: " + string.Join(" ", warnings.Take(5)));
        }
        catch (Exception ex) when (ex is TreeBuildException or FormatException or IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            SetStatus("Could not load file: " + ex.Message, error: true);
        }
    }

    private void AfterDataChanged()
    {
        var tree = Displayed;
        CompareButton.IsEnabled = _tree != null;
        ClearDiffButton.IsVisible = _diff != null;
        SideBySideBox.IsVisible = _diff != null;
        if (tree == null) return;
        if (_centerId != null && tree.Find(_centerId) == null) _centerId = null;

        var stats = TreeStats.Compute(_tree!);
        var info = new StringBuilder();
        info.AppendLine($"{_treeName}: {stats.NodeCount:N0} nodes, {stats.LeafCount:N0} leaves, depth {stats.MaxDepth}, avg leaf depth {stats.AverageLeafDepth:F1}");
        if (_tree!.SecondaryEdges.Count > 0) info.AppendLine($"{_tree.SecondaryEdges.Count:N0} secondary relationships (not drawn in v1)");
        if (_diff != null) info.AppendLine($"Compared with {_oldName}: {_diff.Summary().Replace(" ", ", ")}");
        FileInfo.Text = info.ToString().TrimEnd();

        _updating = true;
        try
        {
            var keys = tree.AttributeKeys;
            var prevColor = ColorBox.SelectedItem as string;
            var colorItems = new List<string>();
            if (_diff != null) colorItems.Add(ChangesItem);
            colorItems.Add(BranchItem);
            colorItems.Add(NoColorItem);
            colorItems.AddRange(keys);
            ColorBox.ItemsSource = colorItems;
            ColorBox.SelectedItem = _diff != null ? ChangesItem : prevColor != null && colorItems.Contains(prevColor) && prevColor != ChangesItem ? prevColor : BranchItem;

            var prevSort = SortBox.SelectedItem as string;
            var sortItems = FixedSorts.Concat(keys).ToList();
            SortBox.ItemsSource = sortItems;
            SortBox.SelectedItem = prevSort != null && sortItems.Contains(prevSort) ? prevSort : FixedSorts[0];

            var prevLabels = SelectedLabelKeys();
            var labelItems = new List<string> { "id" };
            labelItems.AddRange(keys.Where(k => k != "id"));
            LabelKeysBox.ItemsSource = labelItems;
            LabelKeysBox.SelectedItems!.Clear();
            var want = prevLabels.Where(labelItems.Contains).Distinct().ToList();
            if (want.Count == 0) want.Add(keys.Contains("name") ? "name" : "id");
            foreach (var k in want) LabelKeysBox.SelectedItems.Add(k);

            var prevRings = SelectedRingKeys();
            RingKeysBox.ItemsSource = keys.ToList();
            RingKeysBox.SelectedItems!.Clear();
            foreach (var k in prevRings.Where(keys.Contains)) RingKeysBox.SelectedItems.Add(k);
        }
        finally { _updating = false; }

        Rebuild(resetView: true);
        RunSearch();
    }

    // ---------------- view building ----------------

    private LabelSettings CurrentLabelSettings() => new()
    {
        Mode = LabelModeBox.SelectedIndex switch { 1 => LabelMode.All, 2 => LabelMode.None, _ => LabelMode.Auto },
    };

    private (LayoutOptions Layout, SceneOptions Scene, bool ShowDiff) CurrentOptions(Tree tree)
    {
        var sortItem = SortBox.SelectedItem as string ?? FixedSorts[0];
        var (sort, sortKey) = sortItem switch
        {
            "(id)" => (SortMode.Id, (string?)null),
            "(headcount)" => (SortMode.Headcount, null),
            "(input order)" => (SortMode.Input, null),
            var key => (SortMode.Attribute, key),
        };
        int depth = (int)(DepthBox.Value ?? 0);
        var layout = new LayoutOptions
        {
            Mode = LayoutBox.SelectedIndex switch { 1 => LayoutMode.Sunburst, 2 => LayoutMode.Cascade, _ => LayoutMode.Radial },
            RootId = _centerId,
            MaxDepth = depth > 0 ? depth : null,
            Alpha = AlphaSlider.Value,
            Sort = sort,
            SortKey = sortKey,
            LeafStagger = StaggerBox.SelectedIndex + 1,
        };
        var colorItem = ColorBox.SelectedItem as string ?? BranchItem;
        var labelKeys = SelectedLabelKeys();
        var scene = new SceneOptions
        {
            ColorMode = colorItem switch { BranchItem or ChangesItem => ColorMode.Branch, NoColorItem => ColorMode.None, _ => ColorMode.Attribute },
            ColorKey = colorItem is BranchItem or NoColorItem or ChangesItem ? null : colorItem,
            LabelKeys = labelKeys,
            AnnotationKeys = SelectedRingKeys(),
            ShowDiff = colorItem == ChangesItem,
        };
        return (layout, scene, colorItem == ChangesItem);
    }

    private void Rebuild(bool resetView)
    {
        if (_updating) return;
        var tree = Displayed;
        if (tree == null) return;
        var sw = Stopwatch.StartNew();
        var (lo, so, showDiff) = CurrentOptions(tree);
        if (lo.RootId != null && tree.Find(lo.RootId) == null) lo = lo with { RootId = null };
        _layout = PolarLayout.Compute(tree, lo);
        _scene = SceneBuilder.Build(_layout, so, showDiff ? _diff : null);
        _scene.Title = BuildTitle(so);

        bool sbs = SideBySide;
        OldPanel.IsVisible = sbs;
        MainCaptionBar.IsVisible = sbs;
        Grid.SetColumn(MainPanel, sbs ? 1 : 0);
        Grid.SetColumnSpan(MainPanel, sbs ? 1 : 2);
        if (sbs)
        {
            // Same options (ordering, weights, depth, center) so unchanged regions line up.
            var old = _diff!.Old;
            var olo = lo with { RootId = _centerId != null && old.Find(_centerId) != null ? _centerId : null };
            _oldLayout = PolarLayout.Compute(old, olo);
            var oldScene = _oldScene = SceneBuilder.Build(_oldLayout, so, showDiff ? _diff : null);
            OldCanvas.Labels = CurrentLabelSettings();
            OldCanvas.Highlights = Canvas.Highlights;
            OldCanvas.SetScene(_oldLayout, oldScene, resetView);
            OldCaption.Text = $"Before: {SnapshotName(_diff.Old, _oldName)} · {_oldLayout.Nodes.Count:N0} shown";
            MainCaption.Text = $"After: {SnapshotName(_diff.New, _treeName)} · {_layout.Nodes.Count:N0} shown";
        }
        else { _oldLayout = null; _oldScene = null; }

        Canvas.Labels = CurrentLabelSettings();
        Canvas.SetScene(_layout, _scene, resetView);
        if (sbs != _wasSideBySide)
        {
            // The canvases change size after this layout pass; refit once they have.
            _wasSideBySide = sbs;
            Dispatcher.UIThread.Post(() => Canvas.FitToView(), DispatcherPriority.Background);
        }
        _expanded.Add(_layout.Root.Node.Id);
        RefreshOutline(Canvas.Selected?.Node.Id);
        UpdateLegend();
        UpdateBreadcrumbs();
        ShowInspector();
        SetStatus($"{_layout.Nodes.Count:N0} nodes shown · {_scene.PrimitiveCount:N0} primitives · built in {sw.ElapsedMilliseconds} ms. " +
                  "Drag to pan, wheel to zoom, double-click a node to center it.");
    }

    private static string SnapshotName(Tree t, string? fallback) =>
        t.GraphAttributes.GetValueOrDefault("snapshot") ?? fallback ?? "";

    private string BuildTitle(SceneOptions so)
    {
        var rootLabel = SceneBuilder.LabelFor(_layout!.Root.Node, so.LabelKeys, so.LabelSeparator);
        if (so.ShowDiff && _diff != null)
        {
            var from = _diff.Old.GraphAttributes.GetValueOrDefault("snapshot") ?? _oldName;
            var to = _diff.New.GraphAttributes.GetValueOrDefault("snapshot") ?? _treeName;
            return $"{rootLabel} — {from} → {to}";
        }
        var snap = Displayed!.GraphAttributes.GetValueOrDefault("snapshot");
        return snap != null ? $"{rootLabel} — {snap}" : rootLabel;
    }

    private void UpdateLegend()
    {
        if (_scene == null) return;
        LegendTitle.Text = _scene.LegendTitle ?? "Legend";
        static LegendRow Row(LegendEntry e) => new(
            new SolidColorBrush(Color.FromArgb(e.Color.A, e.Color.R, e.Color.G, e.Color.B)),
            e.Count > 0 ? $"{e.Label} ({e.Count:N0})" : e.Label);
        var rows = _scene.Legend.Select(Row).ToList();
        foreach (var section in _scene.ExtraLegends)
        {
            rows.Add(new LegendRow(Brushes.Transparent, ""));
            rows.Add(new LegendRow(Brushes.Transparent, section.Title));
            rows.AddRange(section.Entries.Select(Row));
        }
        LegendItems.ItemsSource = rows;
    }

    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Children.Clear();
        if (_layout == null) return;
        var chain = new List<TreeNode>();
        for (var n = _layout.Root.Node; n != null; n = n.Parent) chain.Add(n);
        chain.Reverse();
        var keys = SelectedLabelKeys();
        foreach (var n in chain)
        {
            var label = SceneBuilder.LabelFor(n, keys, " · ");
            var b = new Button
            {
                Content = label.Length > 28 ? label[..27] + "…" : label,
                FontSize = 11, Padding = new Avalonia.Thickness(6, 2), Margin = new Avalonia.Thickness(0, 0, 4, 4),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                IsEnabled = n != _layout.Root.Node,
            };
            var id = n.Id;
            b.Click += (_, _) => SetCenter(n.Parent == null ? null : id);
            Breadcrumbs.Children.Add(b);
        }
    }

    private void SetCenter(string? id)
    {
        _centerId = id;
        Rebuild(resetView: true);
    }

    private void OnNodeActivated(LayoutNode node)
    {
        if (node == _layout?.Root) { OnUp(null, new RoutedEventArgs()); return; }
        if (node.Node.IsLeaf) { Canvas.Select(node); return; }
        SetCenter(node.Node.Id);
    }

    private void OnUp(object? sender, RoutedEventArgs e)
    {
        var parent = _layout?.Root.Node.Parent;
        if (parent != null) SetCenter(parent.Parent == null ? null : parent.Id);
    }

    private void OnFit(object? sender, RoutedEventArgs e) => Canvas.FitToView();

    private void OnFocusSelected(object? sender, RoutedEventArgs e)
    {
        if (Canvas.Selected is { } n && !n.Node.IsLeaf) SetCenter(n.Node.Id);
    }

    private List<string> SelectedRingKeys()
    {
        var selected = RingKeysBox.SelectedItems?.Cast<string>().ToHashSet() ?? new HashSet<string>();
        return (RingKeysBox.ItemsSource as IEnumerable<string> ?? Enumerable.Empty<string>()).Where(selected.Contains).ToList();
    }

    /// <summary>Selected label keys in list order (stable regardless of click order), without duplicates.</summary>
    private List<string> SelectedLabelKeys()
    {
        var selected = LabelKeysBox.SelectedItems?.Cast<string>().ToHashSet() ?? new HashSet<string>();
        return (LabelKeysBox.ItemsSource as IEnumerable<string> ?? Enumerable.Empty<string>()).Where(selected.Contains).ToList();
    }

    // ---------------- inspector ----------------

    private void ShowInspector()
    {
        bool before = Canvas.Hovered == null && OldCanvas.Hovered != null && SideBySide;
        var n = Canvas.Hovered ?? (SideBySide ? OldCanvas.Hovered : null) ?? Canvas.Selected
                ?? (SideBySide ? OldCanvas.Selected : null);
        before |= n != null && SideBySide && n == OldCanvas.Selected && Canvas.Selected == null && Canvas.Hovered == null;
        FocusButton.IsVisible = Canvas.Selected is { } s && !s.Node.IsLeaf && s != _layout?.Root;
        if (n == null) { Inspector.Text = "Hover or click a node."; return; }
        var t = n.Node;
        var sb = new StringBuilder();
        if (before) sb.AppendLine("(before)");
        sb.AppendLine($"id: {t.Id}");
        foreach (var (k, v) in t.Attributes) sb.AppendLine($"{k}: {v}");
        sb.AppendLine();
        sb.AppendLine($"direct reports: {t.Children.Count:N0}");
        sb.AppendLine($"total below: {t.SubtreeSize - 1:N0}");
        sb.AppendLine($"layers below: {t.Height}");
        sb.AppendLine($"level: {t.Depth}");
        if (t.Parent != null) sb.AppendLine($"parent: {t.Parent.Id}");
        if (n.IsTruncated) sb.AppendLine($"hidden by depth limit: {n.HiddenDescendants:N0}");
        if (_diff != null && _diff.Changes.TryGetValue(t.Id, out var c))
        {
            sb.AppendLine();
            sb.AppendLine($"change: {c.Class}{(c.Flags != ChangeFlags.None && c.Flags.ToString() != c.Class.ToString() ? $" ({c.Flags})" : "")}");
            if (c.Flags.HasFlag(ChangeFlags.Moved)) sb.AppendLine($"moved: {c.OldParentId} → {c.NewParentId}");
            foreach (var a in c.AttributeChanges) sb.AppendLine($"  {a.Key}: {a.OldValue ?? "∅"} → {a.NewValue ?? "∅"}");
            if (c.Descendants > 0) sb.AppendLine($"changed below: {c.ChangedDescendants:N0} of {c.Descendants:N0} ({c.ChangedFraction:P0})");
        }
        Inspector.Text = sb.ToString().TrimEnd();
    }

    // ---------------- search ----------------

    private void RunSearch()
    {
        var tree = Displayed;
        var q = SearchBox.Text?.Trim();
        if (tree == null || string.IsNullOrEmpty(q) || q.Length < 2)
        {
            _searchMatches = new();
            SearchResults.ItemsSource = null;
            SearchInfo.Text = "";
            Canvas.Highlights = OldCanvas.Highlights = new HashSet<string>();
            Canvas.InvalidateVisual();
            OldCanvas.InvalidateVisual();
            return;
        }
        _searchMatches = tree.Nodes.Where(n =>
            n.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            n.Attributes.Values.Any(v => v.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        SearchInfo.Text = _searchMatches.Count == 0 ? "No matches." : $"{_searchMatches.Count:N0} match(es){(_searchMatches.Count > 200 ? " — first 200 listed" : "")}";
        var keys = SelectedLabelKeys();
        SearchResults.ItemsSource = _searchMatches.Take(200).Select(n => $"{SceneBuilder.LabelFor(n, keys, " · ")}  [{n.Id}]").ToList();
        Canvas.Highlights = OldCanvas.Highlights = _searchMatches.Select(n => n.Id).ToHashSet();
        Canvas.InvalidateVisual();
        OldCanvas.InvalidateVisual();
    }

    private void OnSearchResultChosen()
    {
        int i = SearchResults.SelectedIndex;
        if (i < 0 || i >= _searchMatches.Count) return;
        RevealNode(_searchMatches[i]);
    }

    /// <summary>Zooms to a node, re-centering on its manager first if it is not currently visible.</summary>
    private void RevealNode(TreeNode node)
    {
        if (_layout == null) return;
        if (!_layout.ById.ContainsKey(node.Id))
        {
            // Not visible: re-center on its manager so it and its siblings show.
            _centerId = node.Parent?.Parent == null ? null : node.Parent.Id;
            _updating = true; DepthBox.Value = 0; _updating = false;
            Rebuild(resetView: true);
        }
        if (_layout.ById.TryGetValue(node.Id, out var ln)) Canvas.ZoomTo(ln);
    }

    // ---------------- outline ----------------

    private IEnumerable<TreeNode> OutlineChildren(TreeNode node) =>
        _layout != null && _layout.ById.TryGetValue(node.Id, out var ln) && ln.Children.Count > 0
            ? ln.Children.Select(c => c.Node)                       // same order as the chart
            : node.Children.OrderBy(c => c.Id, StringComparer.Ordinal);

    private void RefreshOutline(string? selectId)
    {
        if (_layout == null) { OutlineList.ItemsSource = null; return; }
        var keys = SelectedLabelKeys();
        var rows = new List<OutlineRow>();
        var stack = new Stack<(TreeNode Node, int Depth)>();
        stack.Push((_layout.Root.Node, 0));
        while (stack.Count > 0)
        {
            var (n, depth) = stack.Pop();
            bool open = _expanded.Contains(n.Id);
            var text = SceneBuilder.LabelFor(n, keys, " · ");
            if (!n.IsLeaf) text += $"  ({n.SubtreeSize - 1:N0})";
            IBrush marker = Brushes.Transparent;
            if (_diff != null && _diff.Changes.TryGetValue(n.Id, out var c) && c.IsDirectChange)
            {
                var col = Palette.ForChange(c.Class);
                marker = new SolidColorBrush(Color.FromArgb(255, col.R, col.G, col.B));
            }
            rows.Add(new OutlineRow(n, !n.IsLeaf, open ? "▾" : "▸", text, new Avalonia.Thickness(depth * 14, 0, 0, 0), marker));
            if (open && !n.IsLeaf)
                foreach (var child in OutlineChildren(n).Reverse()) stack.Push((child, depth + 1));
        }
        _outlineUpdating = true;
        try
        {
            OutlineList.ItemsSource = rows;
            int index = selectId == null ? -1 : rows.FindIndex(r => r.Node.Id == selectId);
            OutlineList.SelectedIndex = index;
            if (index >= 0) OutlineList.ScrollIntoView(index);
        }
        finally { _outlineUpdating = false; }
    }

    private void OnOutlineToggle(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not OutlineRow row) return;
        if (!_expanded.Remove(row.Node.Id)) _expanded.Add(row.Node.Id);
        RefreshOutline((OutlineList.SelectedItem as OutlineRow)?.Node.Id);
        e.Handled = true;
    }

    private void OnOutlineChosen()
    {
        if (_outlineUpdating || OutlineList.SelectedItem is not OutlineRow row) return;
        if (_layout != null && _layout.ById.TryGetValue(row.Node.Id, out var ln)) Canvas.Select(ln);
        else RevealNode(row.Node);
    }

    /// <summary>Expands the outline down to a node and selects its row.</summary>
    private void RevealInOutline(TreeNode node)
    {
        if (_layout == null) return;
        for (var p = node.Parent; p != null; p = p.Parent)
        {
            _expanded.Add(p.Id);
            if (p == _layout.Root.Node) break;
        }
        RefreshOutline(node.Id);
    }

    // ---------------- export ----------------

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (_scene == null || _layout == null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export view",
            SuggestedFileName = SuggestName(),
            DefaultExtension = "png",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } },
                new FilePickerFileType("PDF (vector)") { Patterns = new[] { "*.pdf" } },
                new FilePickerFileType("SVG (vector)") { Patterns = new[] { "*.svg" } },
            },
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return;
        var scene = _scene;
        var oldScene = SideBySide ? _oldScene : null;
        var captions = (Old: "Before: " + SnapshotName(_diff?.Old ?? _tree!, _oldName), New: "After: " + SnapshotName(_diff?.New ?? _tree!, _treeName));
        var settings = new ExportSettings
        {
            Width = (int)(ExportSizeBox.Value ?? 4000),
            Labels = CurrentLabelSettings(),
        };
        SetStatus($"Exporting {Path.GetFileName(path)}…");
        try
        {
            var sw = Stopwatch.StartNew();
            if (oldScene != null)
                await Task.Run(() => PairExporter.Export(new ExportPanel(oldScene, captions.Old), new ExportPanel(scene, captions.New),
                    scene.Title, path, settings));
            else
                await Task.Run(() => Exporter.Export(scene, path, settings));
            SetStatus($"Exported {path} in {sw.ElapsedMilliseconds} ms." + (oldScene != null ? " (before and after side by side)" : ""));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            SetStatus("Export failed: " + ex.Message, error: true);
        }
    }

    private string SuggestName()
    {
        var root = _layout!.Root.Node;
        var name = root.Get("name") ?? root.Id;
        var safe = new string(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '-' : ch).ToArray());
        return (SideBySide ? "compare-" : _diff != null ? "diff-" : "") + safe;
    }

    private void SetStatus(string text, bool error = false)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Status.Text = text;
            if (error) Status.Foreground = Brushes.Firebrick;
            else Status.ClearValue(TextBlock.ForegroundProperty);
            Status.Opacity = error ? 1 : 0.6;
        });
    }
}
