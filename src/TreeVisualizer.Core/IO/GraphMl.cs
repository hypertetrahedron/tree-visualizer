using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TreeVisualizer.Core.Model;

namespace TreeVisualizer.Core.IO;

/// <summary>Reads and writes trees in the Tree Visualizer GraphML profile (see docs/FORMAT.md).</summary>
public static class GraphMl
{
    public const string Namespace = "http://graphml.graphdrawing.org/xmlns";
    public const string RelationKey = "relation";
    public const string HierarchyRelation = "hierarchy";

    public sealed record ReadResult(Tree Tree, IReadOnlyList<ValidationIssue> Issues);

    private sealed record KeyDef(string Id, string For, string Name, AttributeType Type, string? Default);

    public static ReadResult Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static ReadResult Read(Stream stream)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(stream, settings);
        var doc = XDocument.Load(reader);
        return Read(doc);
    }

    public static ReadResult Read(XDocument doc)
    {
        var builder = new TreeBuilder();
        var rootEl = doc.Root ?? throw new FormatException("Empty XML document.");
        if (rootEl.Name.LocalName != "graphml")
            throw new FormatException($"Not a GraphML file (root element is <{rootEl.Name.LocalName}>).");

        var keys = new Dictionary<string, KeyDef>(StringComparer.Ordinal);
        foreach (var k in Children(rootEl, "key"))
        {
            var id = (string?)k.Attribute("id") ?? throw new FormatException("<key> without id.");
            var forAttr = ((string?)k.Attribute("for") ?? "all").ToLowerInvariant();
            var name = (string?)k.Attribute("attr.name") ?? id;
            var type = ParseType((string?)k.Attribute("attr.type"));
            var def = Children(k, "default").FirstOrDefault()?.Value;
            keys[id] = new KeyDef(id, forAttr, name, type, def);
        }

        var nodeKeys = keys.Values.Where(k => k.For is "node" or "all").ToList();
        foreach (var k in nodeKeys)
        {
            // An edge-only 'relation' key declared for="all" should not appear as a node attribute.
            if (k.For == "all" && k.Name == RelationKey) continue;
            builder.DeclareAttribute(k.Name, k.Type);
        }

        var graphs = Children(rootEl, "graph").ToList();
        if (graphs.Count == 0) throw new FormatException("GraphML contains no <graph>.");
        if (graphs.Count > 1) builder.AddWarning($"File contains {graphs.Count} graphs; only the first is used.");
        var graph = graphs[0];

        var edgeDefault = ((string?)graph.Attribute("edgedefault") ?? "directed").ToLowerInvariant();
        if (edgeDefault != "directed")
            builder.AddWarning("Graph is undirected; treating edge 'source' as the parent.");

        foreach (var d in Children(graph, "data"))
        {
            var keyId = (string?)d.Attribute("key");
            if (keyId == null) continue;
            var name = keys.TryGetValue(keyId, out var kd) ? kd.Name : keyId;
            builder.SetGraphAttribute(name, d.Value);
        }
        foreach (var k in keys.Values.Where(k => k.For == "graph" && k.Default != null))
            builder.SetGraphAttribute(k.Name, k.Default!);

        if (Children(graph, "hyperedge").Any())
            throw new FormatException("Hyperedges are not supported.");

        foreach (var n in Children(graph, "node"))
        {
            var id = (string?)n.Attribute("id") ?? throw new FormatException("<node> without id.");
            if (Children(n, "graph").Any())
                throw new FormatException($"Nested graphs are not supported (node '{id}').");
            if (Children(n, "port").Any())
                throw new FormatException($"Ports are not supported (node '{id}').");

            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var d in Children(n, "data"))
            {
                var keyId = (string?)d.Attribute("key");
                if (keyId == null) continue;
                if (keys.TryGetValue(keyId, out var kd))
                {
                    if (kd.For is not ("node" or "all")) continue;
                    attrs[kd.Name] = d.Value;
                }
                else
                {
                    attrs[keyId] = d.Value; // undeclared key: keep it as a string attribute
                }
            }
            foreach (var k in nodeKeys)
                if (k.Default != null && !attrs.ContainsKey(k.Name) && !(k.For == "all" && k.Name == RelationKey))
                    attrs[k.Name] = k.Default;
            builder.AddNode(id, attrs);
        }

        var relationKey = keys.Values.FirstOrDefault(k => k.Name == RelationKey && k.For is "edge" or "all");
        foreach (var e in Children(graph, "edge"))
        {
            var src = (string?)e.Attribute("source");
            var dst = (string?)e.Attribute("target");
            if (src == null || dst == null) throw new FormatException("<edge> without source or target.");
            if (e.Attribute("sourceport") != null || e.Attribute("targetport") != null)
                throw new FormatException("Ports are not supported.");

            string? relation = relationKey?.Default;
            if (relationKey != null)
            {
                var d = Children(e, "data").FirstOrDefault(x => (string?)x.Attribute("key") == relationKey.Id);
                if (d != null) relation = d.Value;
            }
            relation = relation?.Trim();
            if (string.IsNullOrEmpty(relation) || relation.Equals(HierarchyRelation, StringComparison.OrdinalIgnoreCase))
                builder.AddEdge(src, dst);
            else
                builder.AddSecondaryEdge(src, dst, relation);
        }

        var tree = builder.Build();
        return new ReadResult(tree, builder.Issues.ToList());
    }

    public static void Save(Tree tree, string path)
    {
        using var stream = File.Create(path);
        Write(tree, stream);
    }

    public static void Write(Tree tree, Stream stream)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var w = XmlWriter.Create(stream, settings);
        w.WriteStartDocument();
        w.WriteStartElement("graphml", Namespace);

        var graphKeyIds = new Dictionary<string, string>(StringComparer.Ordinal);
        int gi = 0;
        foreach (var name in tree.GraphAttributes.Keys)
        {
            var id = "g" + gi++;
            graphKeyIds[name] = id;
            WriteKey(w, id, "graph", name, AttributeType.String);
        }

        var nodeKeyIds = new Dictionary<string, string>(StringComparer.Ordinal);
        int ni = 0;
        foreach (var (name, type) in tree.AttributeTypes)
        {
            var id = "n" + ni++;
            nodeKeyIds[name] = id;
            WriteKey(w, id, "node", name, type);
        }
        bool hasSecondary = tree.SecondaryEdges.Count > 0;
        if (hasSecondary) WriteKey(w, "rel", "edge", RelationKey, AttributeType.String);

        w.WriteStartElement("graph", Namespace);
        w.WriteAttributeString("id", "G");
        w.WriteAttributeString("edgedefault", "directed");
        foreach (var (name, value) in tree.GraphAttributes)
        {
            w.WriteStartElement("data", Namespace);
            w.WriteAttributeString("key", graphKeyIds[name]);
            w.WriteString(value);
            w.WriteEndElement();
        }

        foreach (var n in tree.Nodes)
        {
            if (n.Id == TreeBuilder.SyntheticRootId) continue;
            w.WriteStartElement("node", Namespace);
            w.WriteAttributeString("id", n.Id);
            foreach (var (k, v) in n.Attributes)
            {
                if (!nodeKeyIds.TryGetValue(k, out var keyId)) continue;
                w.WriteStartElement("data", Namespace);
                w.WriteAttributeString("key", keyId);
                w.WriteString(v);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        foreach (var n in tree.Nodes)
        {
            if (n.Parent == null || n.Parent.Id == TreeBuilder.SyntheticRootId) continue;
            w.WriteStartElement("edge", Namespace);
            w.WriteAttributeString("source", n.Parent.Id);
            w.WriteAttributeString("target", n.Id);
            w.WriteEndElement();
        }
        foreach (var e in tree.SecondaryEdges)
        {
            w.WriteStartElement("edge", Namespace);
            w.WriteAttributeString("source", e.SourceId);
            w.WriteAttributeString("target", e.TargetId);
            w.WriteStartElement("data", Namespace);
            w.WriteAttributeString("key", "rel");
            w.WriteString(e.Relation);
            w.WriteEndElement();
            w.WriteEndElement();
        }

        w.WriteEndElement(); // graph
        w.WriteEndElement(); // graphml
        w.WriteEndDocument();
    }

    private static void WriteKey(XmlWriter w, string id, string forWhat, string name, AttributeType type)
    {
        w.WriteStartElement("key", Namespace);
        w.WriteAttributeString("id", id);
        w.WriteAttributeString("for", forWhat);
        w.WriteAttributeString("attr.name", name);
        w.WriteAttributeString("attr.type", type.ToString().ToLowerInvariant());
        w.WriteEndElement();
    }

    private static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(e => e.Name.LocalName == localName);

    private static AttributeType ParseType(string? s) => (s ?? "string").ToLowerInvariant() switch
    {
        "int" => AttributeType.Int,
        "long" => AttributeType.Long,
        "float" => AttributeType.Float,
        "double" => AttributeType.Double,
        "boolean" => AttributeType.Boolean,
        _ => AttributeType.String,
    };

    /// <summary>Parses a value of a numeric attribute type (invariant culture).</summary>
    public static bool TryParseNumber(string? value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
}
