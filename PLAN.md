# Tree Visualizer — Plan

Last updated: 2026-11-07

> **Decisions made 2026-11-07:**
> - **Input is GraphML** (the standard format), with a small tree profile documented in [docs/FORMAT.md](docs/FORMAT.md). CSV/XLSX import is dropped.
> - Nodes carry arbitrary key/value pairs, and users choose which keys to display. Stable IDs are guaranteed.
> - No privacy constraints: sensitive data is removed before this app sees it.
> - Dotted-line/matrix relationships are **not in v1**, but are planned for. The format already carries them as `relation` edges.
> - The stack is .NET 10 + Avalonia 12 + SkiaSharp 3.

## 1. Goal

A Windows desktop app (plus a command-line renderer) that:

1. Loads a hierarchy of ~10k nodes and draws it as a **circular (radial) tree**.
2. Exports an image of the tree **at any level**: whole tree, any subtree, any depth cutoff, any resolution.
3. Shows a **diff between two trees** by shading the nodes that changed.

The first target is the **org structure of a large corporation**.

## 2. What the data looks like

| Property | Value | Consequence |
|---|---|---|
| Total nodes | ~10,000 | Layout and rendering are cheap (milliseconds). The hard part is readability, not speed. |
| Average root-to-leaf depth | ~6 | Effective branching factor ≈ 10,000^(1/6) ≈ **4.6**, close to a typical manager span of 5–8. |
| Leaves (individual contributors) | ≈ 75–85% → **~8,000** | The outer rings are crowded. The inner rings (leadership) are sparse and fully readable. |
| Managers | ~1,500–2,500 | The interesting structure lives here. |
| Depth range | probably 2 to 10–12 | Leaves sit at many depths, unlike a phylogeny where they all align on the rim. |
| Fan-out | not binary; skewed (2 reports up to 50+) | Wide nodes need angle control so they don't take over the circle. |
| Edge lengths | none (one edge = one reporting level) | Ring radius = org level. This is a cladogram, not a phylogram. |

**How much space each leaf gets:**

| View | Circumference | Space per leaf |
|---|---|---|
| Screen, 1000px radius | ≈ 6,300px | ≈ 0.8px |
| Poster PDF, 1 m diameter | ≈ 3.1 m | ≈ 0.4 mm |

Therefore:

- **Full view:** shows structure, levels, headcount distribution and color-coded attributes. Labels fit only for roughly the top 3 levels (~150 nodes).
- **Subtree view or export:** a VP's org of ~500 people laid out over the full 360° has readable labels on every node.
- **Poster PDF:** vector output, so every label can be included and read when the print is zoomed or examined up close.

## 3. Visualization design

### 3.1 Layouts (they share one polar model)

- **Radial tree (primary).** Nodes are dots on concentric rings, ring = org level. Edges are drawn "elbow" style: a radial segment plus an arc at the parent's ring. This style looks clean and is cheap to draw.
- **Sunburst (secondary).** Each node is an annular segment, with arc length proportional to headcount. It is the best "where are the people" view, and it carries diff shading very well.

Both layouts need only `(angleStart, angleEnd, depth)` per node, so a second layout mode costs very little.

**Angle allocation** (this decides how readable the chart is):

- Default: a node's wedge is proportional to `leafCount^α`, with α ≈ 0.7–1.0 as a user slider. α = 1 is true headcount; lower values give small teams more room.
- A minimum wedge per node, so a 2-person team still shows.
- Ordering: siblings sorted deterministically (by ID, name or headcount; user-selectable). This is essential for diff stability (§5).
- Optional open angle (a 10–30° gap) for a legend and the root label.

**Leaf crowding relief (optional):** an IC-heavy manager's leaf children can be packed into 2–3 staggered sub-rings instead of one ring. This roughly halves the angular demand of the outer rings.

### 3.2 Making 10k nodes useful

- **Color by attribute:** department, location, cost center, level, tenure, or vacancy.
- **Size by:** headcount, or a flat size.
- **Annotation rings:** concentric bands outside the tree, one per attribute (for example location or job family). They read well even at under 1px per leaf.
- **Semantic zoom / level of detail (LOD):**
  - Labels are drawn only when the arc length available at the current zoom exceeds the text height.
  - Leaf dots and edges fade out below about 0.5px.
  - Subtrees below a pixel threshold collapse into a "+N" wedge.
- **Focus subtree:** double-click a manager to re-lay out their org over 360°. A breadcrumb trail leads back up.
- **Depth limit:** for example "show 4 levels below the focus".
- **Search:** name, ID or title. Matches and their path to the root stay highlighted while everything else dims.
- **Linked panels:**
  - A virtualized outline tree with expand/collapse that stays in sync with the chart.
  - An inspector showing node attributes plus metrics: span of control, total headcount, layers below.
- **Dotted-line or matrix relationships** (optional, later): drawn as bundled chords across the inside of the circle (hierarchical edge bundling).

### 3.3 Performance approach

- Layout is O(n) and takes under 10ms at 10k nodes. It is recomputed only when the focus, sorting or weights change.
- The layout produces a **scene**: a flat list of primitives (arc, line, circle, wedge, text) with resolved styles. Every output target renders from the scene.
- **Batched drawing:** one path per style bucket (for example 30 department colors), not 10k individual draw calls.
- **Hit-testing in polar coordinates:**
  1. Convert the cursor position to (θ, r).
  2. r gives the ring.
  3. A binary search over that ring's nodes, sorted by angle, finds the node.
  This costs O(log n) and needs no spatial index.
- Text uses LOD culling and is cached per zoom bucket.

## 4. Export "at any level"

**Export parameters:**

| Parameter | Options |
|---|---|
| Root | whole org, or any node |
| Depth limit | how many levels below the root to include |
| Layout | radial or sunburst |
| Color and annotation settings | as configured in the viewer |
| Diff overlay | on or off |
| Label policy | auto LOD, all, or none |
| Format | PNG, SVG, PDF |
| Size | pixels, or physical size + DPI |

**Format notes:**

| Format | Use | Notes |
|---|---|---|
| PNG | slides, wikis | Up to ~12–16k px square in memory (16k² RGBA ≈ 1 GB). Larger requests get a warning and a suggestion to use PDF. |
| PDF | posters, print | Vector output via Skia `SKDocument.CreatePdf`. All labels can be included. |
| SVG | further editing (Illustrator, Inkscape, web) | Written by our own small SVG emitter from the scene primitives. Skia's `SKSvgCanvas` is marked experimental, and the primitive set is small enough to emit directly. |

- **The same scene goes to every backend**, so exports match the screen exactly.
- **Command-line interface for batch and automation.** Example:

  ```
  treeviz render org.graphml --root E1042 --depth 4 --layout sunburst --color department --out vp-sales.pdf
  ```

## 5. Diff between two trees

### 5.1 Matching

- Nodes are matched by a **stable key**: employee ID or position ID, which HR systems provide.
- Position ID vs. person ID is a real choice. It decides whether "person left, role backfilled" shows as *one modified node* or as *remove + add*. Make the key column configurable.
- Fallback when no IDs exist: match on a normalized name + title, and flag uncertain matches.
- General tree-edit-distance algorithms (Zhang–Shasha, APTED) are O(n²–n³) and unnecessary when keys exist. They are deferred.

### 5.2 Change classes

| Class | Definition | Default shading |
|---|---|---|
| Added | in B, not in A | green |
| Removed | in A, not in B | red, drawn as a dashed "ghost" |
| Moved | parent changed | amber; the subtree root is marked strongly |
| Moved with parent | ancestor moved, own parent unchanged | light amber (avoids flooding a reorg) |
| Modified | attributes changed (title, department, location…) | blue; inspector lists the fields |
| Unchanged | — | neutral gray |
| Contains changes | an unchanged ancestor whose subtree has changes | halo or ring tint, intensity = share of changed descendants |

The "contains changes" rollup matters most at the full-org scale. A reorg three levels down should stand out from the center of the chart.

### 5.3 Rendering a diff

- **Union-tree view (default):**
  - Lay out B (the new tree).
  - Insert each removed node as a ghost under its old parent (or under the nearest surviving ancestor).
  - Deterministic sibling ordering keeps unchanged regions in the same place as in a plain render of A or B.
- **Moved nodes:** drawn at the new position, with a ghost at the old position. An optional thin chord links the two.
- **Side-by-side view:** A and B rendered with the same ordering and weights, with linked hover.
- **Change report:** exportable CSV listing every change (ID, class, old parent, new parent, changed fields). Summary counts are shown in a legend.
- **Diff CLI:**

  ```
  treeviz diff org-2026-06.graphml org-2026-11.graphml --out reorg.pdf --report changes.csv
  ```

## 6. Technology choice

**Recommended: C# / .NET 10 + Avalonia 12 + SkiaSharp 3.**

Reasons:

- **One drawing API for screen, PNG and PDF.** Skia renders the same scene to a GPU-backed window, a raster bitmap, or a PDF document. This is the key requirement behind "export exactly what I see, at any size."
- Windows-native performance. 10k nodes is trivial for Skia.
- Avalonia renders through Skia, so a custom control can draw directly on an `SKCanvas`. It also has a virtualized `TreeDataGrid` for the outline panel. Avalonia 12 targets .NET 10 and SkiaSharp 3.
- The same core library runs headless for the CLI, with no UI dependencies.
- Packaging: a self-contained single-file `.exe`, or an installer (Velopack or MSIX).
- Cross-platform later at no extra cost (macOS/Linux) if it is ever needed.

**Alternatives considered:**

| Option | Pros | Cons |
|---|---|---|
| Tauri + TypeScript (Canvas/WebGL, d3-hierarchy) | Huge viz ecosystem, small binary | Two languages; PDF/SVG export needs a separate path; a headless CLI needs a JS runtime |
| Electron + TypeScript | Easiest web stack | ~150MB+, same export issues |
| Python + PySide6 (Qt) | QPainter exports to PNG/SVG/PDF natively | Packaging pain; slower interaction; weaker typed core |
| WPF | Windows-native | No Skia-equivalent shared export path; Windows-only forever |

## 7. Architecture

```
TreeVisualizer.Core        (no UI dependencies)
  Model/           Node, Tree, attributes, metrics (headcount, span, layers)
  IO/              GraphML tree profile reader/writer (docs/FORMAT.md);
                   validation: cycles, multiple roots, orphans, duplicate IDs
  Layout/          PolarLayout (angle allocation, ordering), RadialTree, Sunburst
  Diff/            Matcher (key-based + fallback), Classifier, Rollup, UnionTree
  Scene/           primitives + styles; SceneBuilder(layout, style, LOD, diff)
  Render/          SkiaRenderer (screen/PNG/PDF), SvgWriter
TreeVisualizer.App         Avalonia UI: TreeCanvas control, outline panel, inspector,
                   search, legend, export dialog, diff mode
TreeVisualizer.Cli         render | diff | stats commands
TreeVisualizer.Tests       unit tests + golden-image tests
tools/SynthOrg     synthetic org generator + mutator (for tests and demos)
```

**Data privacy:** employee data is sensitive, so all processing is local. No network calls and no telemetry. Recent files and settings are stored in the user profile, never the data itself.

## 8. Milestones

| # | Milestone | Deliverable / exit criteria |
|---|---|---|
| M0 | Scaffold + synthetic data | Solution builds. SynthOrg generates a 10k-node org (realistic skewed spans, depth ~6) plus a mutated copy for diffs. |
| M1 | Core model + import | GraphML import (see docs/FORMAT.md). Validation report. Metrics computed. Tests pass. |
| M2 | Layout + first image (CLI) | `treeviz render` produces radial PNG and PDF of the 10k org. Layout tests (no overlap, angles sum correctly). **Earliest useful output.** |
| M3 | Desktop viewer | Pan/zoom, hover/inspector, search, focus subtree with breadcrumbs, depth limit, color by attribute, outline panel. |
| M4 | Export at any level | Export dialog and CLI flags for root, depth, format, size and labels. SVG writer. High-res PNG guardrails. Golden-image tests. |
| M5 | Diff | Matcher, classifier, rollup, union-tree rendering, side-by-side view, change report, `treeviz diff`. Verified against SynthOrg mutations with known counts. |
| M6 | Polish | Sunburst mode, annotation rings, leaf sub-ring packing, dotted-line chords, installer. |

## 9. Testing strategy

- **Unit tests:**
  - Import validation, metrics.
  - Layout invariants: children within parent wedge, no sibling overlap, determinism.
  - Diff classification on hand-built cases.
- **Property tests:**
  - Generate a random org, apply N random mutations, and check the diff reports exactly those mutations.
- **Golden images:**
  - Render fixed inputs to PNG and compare against stored references within a tolerance.
- **Performance benchmarks:**
  - Layout plus scene build for 10k nodes in under 50ms.
  - Frame time while panning in under 16ms.

## 10. Open questions

1. ~~**Source format**~~ — answered: GraphML with key/value pairs.
2. ~~**Stable IDs**~~ — answered: yes.
3. ~~**Labels**~~ — answered: the user picks the keys; no privacy constraints.
4. ~~**Extra relationships**~~ — answered: not in v1, but planned for.
5. ~~**Stack**~~ — answered: .NET.
