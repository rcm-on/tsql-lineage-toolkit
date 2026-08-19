using System.Text;
using System.Xml;

namespace Parser.Graph;

/// <summary>
/// Serializes the Neo4j-shaped GraphPayload to GraphML (graph XML), the lingua
/// franca that Gephi, yEd, Cytoscape, igraph and NetworkX all read - same graph
/// as the graphify JSON, just in XML so it opens in desktop graph tools for
/// force-directed/hierarchical layouts, centrality/community metrics and PDF/SVG
/// export. Every distinct node/edge property becomes a typed &lt;key&gt;; nodes
/// also get synthetic "labels" (all labels, ';'-joined) and "ntype" (most
/// specific label) attributes, and edges an "etype" (relationship type), so a
/// reader can color/group by type without parsing ids.
/// </summary>
public static class GraphMlExporter
{
    public static string ToGraphMl(GraphPayload graph)
    {
        // Union of property keys across all nodes / edges, with a GraphML type
        // inferred from the first value seen (a given attribute is consistently typed).
        var nodeKeys = new Dictionary<string, string>();
        foreach (var n in graph.Nodes)
            foreach (var (k, v) in n.Properties)
                nodeKeys.TryAdd(k, GmlType(v));

        var edgeKeys = new Dictionary<string, string>();
        foreach (var e in graph.Relationships)
            foreach (var (k, v) in e.Properties)
                edgeKeys.TryAdd(k, GmlType(v));

        var sw = new StringWriter();
        var settings = new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 };
        using (var w = XmlWriter.Create(sw, settings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("graphml", "http://graphml.graphdrawing.org/xmlns");

            // Synthetic descriptors first, then one per real property.
            WriteKey(w, "labels", "node", "labels", "string");
            WriteKey(w, "ntype", "node", "ntype", "string");
            WriteKey(w, "etype", "edge", "etype", "string");
            foreach (var (name, type) in nodeKeys)
                WriteKey(w, NodeKeyId(name), "node", name, type);
            foreach (var (name, type) in edgeKeys)
                WriteKey(w, EdgeKeyId(name), "edge", name, type);

            w.WriteStartElement("graph");
            w.WriteAttributeString("edgedefault", "directed");

            foreach (var n in graph.Nodes)
            {
                w.WriteStartElement("node");
                w.WriteAttributeString("id", n.Id);
                WriteData(w, "labels", string.Join(";", n.Labels));
                WriteData(w, "ntype", n.Labels.Count > 0 ? n.Labels[^1] : "");
                foreach (var (k, v) in n.Properties)
                    WriteData(w, NodeKeyId(k), Render(v));
                w.WriteEndElement();
            }

            int edgeId = 0;
            foreach (var e in graph.Relationships)
            {
                w.WriteStartElement("edge");
                w.WriteAttributeString("id", $"e{edgeId++}");
                w.WriteAttributeString("source", e.StartNodeId);
                w.WriteAttributeString("target", e.EndNodeId);
                WriteData(w, "etype", e.Type);
                foreach (var (k, v) in e.Properties)
                    WriteData(w, EdgeKeyId(k), Render(v));
                w.WriteEndElement();
            }

            w.WriteEndElement(); // graph
            w.WriteEndElement(); // graphml
            w.WriteEndDocument();
        }
        return sw.ToString();
    }

    private static void WriteKey(XmlWriter w, string id, string domain, string attrName, string attrType)
    {
        w.WriteStartElement("key");
        w.WriteAttributeString("id", id);
        w.WriteAttributeString("for", domain);
        w.WriteAttributeString("attr.name", attrName);
        w.WriteAttributeString("attr.type", attrType);
        w.WriteEndElement();
    }

    private static void WriteData(XmlWriter w, string keyId, string value)
    {
        w.WriteStartElement("data");
        w.WriteAttributeString("key", keyId);
        w.WriteString(value);
        w.WriteEndElement();
    }

    private static string NodeKeyId(string name) => "nd_" + name;
    private static string EdgeKeyId(string name) => "ed_" + name;

    private static string GmlType(object? v) => v switch
    {
        bool => "boolean",
        int or long => "long",
        float or double => "double",
        _ => "string",
    };

    private static string Render(object? v) => v switch
    {
        null => "",
        bool b => b ? "true" : "false",
        _ => v.ToString() ?? "",
    };
}
