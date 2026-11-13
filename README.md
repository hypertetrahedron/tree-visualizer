# Tree Visualizer

Radial tree visualizer for large hierarchies (built and tested at 10k nodes), with image export at any level and a diff between two snapshots. The first target is corporate org charts.

- **Input:** GraphML. See [docs/FORMAT.md](docs/FORMAT.md) for the tree profile.
- **Layouts:** radial node-link tree, cascade tree (each manager in line with their first report, later reports clockwise) and sunburst.
- **Export:** PNG, PDF (vector) or SVG. Any node can be the center, with any depth limit, colors and label keys.
- **Annotation rings:** one colored ring per chosen attribute (e.g. location) outside the tree.
- **Diff:** nodes are matched by id and shaded as added, removed (ghost), moved, moved-with-manager or modified. Managers whose teams changed get a rollup tint. A CSV change report is available. The app can also show before and after side by side.

Plan: [PLAN.md](PLAN.md) · Status: [ROADMAP.md](ROADMAP.md)

## Install (Windows 10/11, x64 or ARM64)

```
powershell -NoProfile -ExecutionPolicy Bypass -c "irm https://raw.githubusercontent.com/hypertetrahedron/tree-visualizer/main/install.ps1 | iex"
```

This installs the latest release for the current user without admin rights. The files go in `%LOCALAPPDATA%\Programs\TreeVisualizer` and are checked against the release's SHA-256 checksums. The installer adds a Start menu shortcut and puts `treeviz` on your PATH (open a new terminal to use it). Run the same command again to upgrade. To uninstall, use Settings → Apps → Tree Visualizer.

[install.ps1](install.ps1) is short; read it before running it. Prefer manual? Download a zip from [Releases](https://github.com/hypertetrahedron/tree-visualizer/releases) and run `TreeVisualizer.exe` from anywhere.

## Build

Requires the .NET 10 SDK on Windows.

```
git clone https://github.com/hypertetrahedron/tree-visualizer.git
cd tree-visualizer
dotnet build TreeVisualizer.slnx
dotnet test tests/TreeVisualizer.Tests
.\publish.ps1        # portable single-file builds in dist\win-x64 (add -Zip for the release zip)
```

## Unsigned executables

Releases and the builds from `publish.ps1` are not code-signed (decided 2026-11-12; see [docs/SIGNING.md](docs/SIGNING.md)). If you download a release zip with a browser, Windows SmartScreen shows "Windows protected your PC" on first run: choose **More info → Run anyway**. The install command above does not trigger this prompt. Machines with application-control policies (AppLocker or WDAC) may block unsigned executables entirely; ask IT to allow them.

## Desktop app

```
dotnet run --project src/TreeVisualizer.App -- [tree.graphml] [older-snapshot.graphml]
```

- **Navigate:** drag to pan, scroll to zoom at the cursor.
- **Inspect:** hover or click a node to see it in the inspector.
- **Re-center:** double-click a manager to make it the center, and use the breadcrumbs to go back up.
- **Outline:** the right panel lists the org as an expandable outline, linked to the chart selection.
- **Compare:** after "Diff against older snapshot…", tick "Side by side" for before and after panels with linked pan, zoom, hover and selection. Export then saves both panels in one image.
- **No data handy?** "Load synthetic sample" generates a 10k-person org plus a changed copy.

## Command line

```
treeviz generate --out org-a.graphml --nodes 10000 --seed 1
treeviz mutate   org-a.graphml --out org-b.graphml --seed 2
treeviz stats    org-a.graphml
treeviz render   org-a.graphml --out org.png --size 4000
treeviz render   org-a.graphml --out vp.pdf --root E000003 --depth 4 --labels name,title --color location
treeviz render   org-a.graphml --out poster.pdf --layout sunburst --color department --label-mode all
treeviz render   org-a.graphml --out cascade.png --layout cascade --stagger 3
treeviz render   org-a.graphml --out rings.png --rings location,department
treeviz diff     org-a.graphml org-b.graphml --out changes.png --report changes.csv
treeviz diff     org-a.graphml org-b.graphml --out compare.pdf --side-by-side
```

Run `treeviz --help` for all options. The CLI binary is `src/TreeVisualizer.Cli/bin/<config>/net10.0/treeviz.exe`.

## License

[MIT](LICENSE)
