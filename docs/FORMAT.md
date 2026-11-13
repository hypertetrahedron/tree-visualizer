# Tree Visualizer input format: the GraphML tree profile

Tree Visualizer reads trees as **[GraphML](http://graphml.graphdrawing.org/)**, the XML graph format standardized by the graph-drawing community. We did not invent a new format, because GraphML already covers every requirement:

| Requirement | GraphML feature |
|---|---|
| Stable node identity across snapshots (needed for diff) | `node/@id` |
| Arbitrary key/value attributes, chosen per dataset | `<key>` declarations + `<data>` values |
| Typed values (sort numbers as numbers) | `key/@attr.type` = `string`, `int`, `long`, `float`, `double`, `boolean` |
| Directed parent → child edges | `graph/@edgedefault="directed"`, `edge/@source`, `edge/@target` |
| Future non-hierarchy links (dotted-line, matrix) | an edge attribute (`relation`), see below |
| Snapshot metadata | `<data>` on the `<graph>` element |
| Tooling | written and read by NetworkX, igraph, yEd, Gephi, Cytoscape, Neo4j (APOC), and others |

Alternatives considered:

- **JSON Graph Format:** less tooling, no typed attribute declarations.
- **GEXF:** Gephi-centric.
- **DOT:** untyped attributes, layout-oriented.
- **Newick:** trees only, no key/value attributes.

## Profile rules

Tree Visualizer accepts standard GraphML and adds these conventions:

1. **One graph per file.** The first `<graph>` is used. Nested graphs, hyperedges and ports are rejected.
2. **Node `id` is the stable key.** It must be unique, and it must be the same across snapshots that will be compared. The diff relies on it.
3. **Edges point parent → child.** `source` is the parent (for example the manager) and `target` is the child (the direct report).
4. **Edge relation.** Edges may carry a `relation` attribute (a key with `attr.name="relation"`, `for="edge"`):
   - Missing, empty, or `hierarchy`: a tree edge.
   - Any other value (for example `dotted-line`): a **secondary edge**. These are parsed and kept, but **not rendered in v1**. They are reserved for planned dotted-line / matrix relationship support.
5. **Tree constraints on hierarchy edges.** Each node has at most one parent, there are no cycles, and every edge endpoint must exist.
   - If more than one node has no parent, Tree Visualizer adds a synthetic root (`__root__`) and reports a warning.
6. **Attributes.**
   - Every node `<key>` is a user-selectable key/value pair.
   - `attr.name` is the display key. If it is missing, the key `id` is used instead.
   - `<default>` values apply to nodes that omit the key.
   - Values are kept as text. `attr.type` controls sorting and comparison.
7. **Graph attributes** (keys with `for="graph"`) describe the snapshot, for example `snapshot` = `2026-11-01`. They are optional.
8. **Undirected graphs** (`edgedefault="undirected"`) are accepted with a warning, and `source` is still treated as the parent.

## Example

```xml
<?xml version="1.0" encoding="UTF-8"?>
<graphml xmlns="http://graphml.graphdrawing.org/xmlns">
  <key id="snapshot" for="graph" attr.name="snapshot" attr.type="string"/>
  <key id="name"  for="node" attr.name="name"  attr.type="string"/>
  <key id="title" for="node" attr.name="title" attr.type="string"/>
  <key id="level" for="node" attr.name="level" attr.type="int"/>
  <key id="dept"  for="node" attr.name="department" attr.type="string">
    <default>Unassigned</default>
  </key>
  <key id="rel"   for="edge" attr.name="relation" attr.type="string"/>
  <graph id="org" edgedefault="directed">
    <data key="snapshot">2026-11-01</data>
    <node id="E1"><data key="name">Ada</data><data key="title">CEO</data><data key="level">0</data></node>
    <node id="E2"><data key="name">Grace</data><data key="title">CTO</data><data key="dept">Engineering</data></node>
    <node id="E3"><data key="name">Linus</data><data key="title">Engineer</data><data key="dept">Engineering</data></node>
    <edge source="E1" target="E2"/>
    <edge source="E2" target="E3"/>
    <edge source="E1" target="E3"><data key="rel">dotted-line</data></edge> <!-- kept, not drawn in v1 -->
  </graph>
</graphml>
```

## Producing it

From Python with NetworkX:

```python
import networkx as nx
G = nx.DiGraph()
G.add_node("E1", name="Ada", title="CEO", level=0)
G.add_edge("E1", "E2")            # manager -> report
nx.write_graphml(G, "org.graphml")
```

The `treeviz generate` command writes synthetic files in this profile, which are useful for testing.
