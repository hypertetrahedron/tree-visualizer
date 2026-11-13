# Roadmap

Status legend: Not Started · In Progress · Complete · Deferred
Last updated: 2026-11-13

| Feature | Status | Notes |
|---|---|---|
| Project plan | Complete | See PLAN.md |
| Input format: GraphML tree profile | Complete | docs/FORMAT.md. Reader and writer; validation for duplicates, unknown endpoints, multiple parents, cycles, self-loops; synthetic root for multiple roots. Tested. |
| M0 Scaffold + synthetic org generator | Complete | .NET 10 / Avalonia 12.1 / SkiaSharp 3.119. `treeviz generate` / `mutate`: 10k nodes, average leaf depth ≈ 6.2–6.4. |
| M1 Core model + import + validation + stats | Complete | Key/value attributes with declared types. `treeviz stats`. |
| M2 Polar layout + CLI render | Complete | Radial and sunburst layouts; alpha weighting; sort by id, headcount, attribute or input order; open angle; O(log n) polar hit-testing. About 35 ms layout + scene for 10k nodes. |
| Cascade layout (parent in line with first child, peers clockwise) | Complete | Added 2026-11-12: `LayoutMode.Cascade`, CLI `--layout cascade`, app Layout option "Cascade tree". Verified by tests, CLI renders (including diff), and selecting it in the running app on 2026-11-12. |
| M3 Desktop viewer | Complete | Pan/zoom at cursor, hover inspector, double-click to re-center, breadcrumbs, search with on-chart rings, diff mode, legend. All View controls (layout, color, sort, depth, balance, label mode, stagger, label keys, annotation rings) exercised in the running app through UI Automation on 2026-11-12. |
| Virtualized outline panel in the app | Complete | Flattened, virtualized ListBox (Avalonia's TreeDataGrid is a commercial add-on). Chart order, headcounts, diff markers, expand/collapse. Choosing a row selects it on the chart; selecting on the chart expands and selects the row. Exercised 2026-11-12. |
| M4 Export at any level | Complete | CLI: root/depth/layout/color/labels/rings/size; PNG/PDF/SVG; title and legend outside the tree area; PNG size guard. App export through the save dialog exercised 2026-11-12. |
| M5 Tree diff | Complete | Engine, union tree with ghosts, rollup, diff shading, CSV report, `treeviz diff`, verified against generated mutations. In the app, "Load synthetic sample", "Diff against older snapshot…" (real file dialog) and "Clear diff" exercised 2026-11-12. Side-by-side view (before / after, same ordering and weights, linked pan, zoom and hover) exercised 2026-11-12. |
| Side-by-side export (both panels in one image) | Complete | `PairExporter`: before and after panels at the same scale, one title, merged legend (e.g. Removed counted from before, Added from after); PNG, PDF and SVG. CLI `treeviz diff … --side-by-side`; the app's Export uses it when side-by-side is on. Click-selection is now mirrored between panels. Tested, and exercised in the app on 2026-11-12. |
| M6 Annotation rings | Complete | One ring per chosen attribute outside the tree, colored by each visible leaf (or the most common value under a depth-limited node), with a legend section per ring. CLI `--rings`, app "Annotation rings" list. Tested and exercised 2026-11-12. |
| M6 Leaf sub-ring packing | Complete | Crowded leaf groups (6+ leaf siblings) spread over 2–3 staggered sub-rings, giving each dot up to 3× the room. Radial and cascade layouts. CLI `--stagger`, app "Stagger". Tested and exercised 2026-11-12. |
| M6 Packaging: portable single-file executables | Complete | `publish.ps1 [-Runtime win-x64\|win-arm64] [-Version] [-Zip]` builds self-contained `TreeVisualizer.exe` (~99 MB) and `treeviz.exe` (~82 MB); the release zip is ~80 MB. |
| One-line installer and release workflow | In Progress | Started 2026-11-13. `install.ps1` (per-user install, SHA-256 check, PATH, Start menu, Settings > Apps uninstall) tested end to end against a local server on 2026-11-13: install, upgrade, tampered-checksum refusal, uninstall restoring PATH exactly. GitHub Actions: CI on push, release on `v*` tags (x64 + ARM64 zips, SHA256SUMS.txt). First release pending. |
| Secondary relationships (dotted-line / matrix) | Not Started | Planned after v1. GraphML `relation` edges are already parsed and kept (`Tree.SecondaryEdges`). Rendering plan: bundled chords inside the circle. |
| Smaller PDFs with label mode "all" | Complete | Cause confirmed: Skia writes stroked text (label halos) to PDF as outlines. PDFs now skip halos on labels under 4 pt (`ExportSettings.PdfHaloMinFontPt`); larger labels, which sit on connector lines, keep them. 10k-node poster: 36 MB → 7.4 MB. Tested 2026-11-12. |

## Deferred
- **Tree-edit-distance matching (APTED/Zhang–Shasha)**: not needed because the input guarantees stable IDs (confirmed 2026-11-07).
- **Dotted-line / matrix relationships in v1**: out of scope for v1 per the user (2026-11-07). The format and model already carry them (see the row above).
- **Code signing and a Windows installer (MSIX or Velopack)**: decided 2026-11-12 to ship the portable executables unsigned for now. Revisit if the app gets a wider distribution. The options are written up in docs/SIGNING.md: the company's internal CA plus Intune for company-wide rollout, otherwise Microsoft Artifact Signing or an OV certificate. MSIX needs a signature, so an installer waits on the same decision.
