using System.Text;
using System.Xml;

namespace DevToolsMcp.Server.Modules.Archi;

/// <summary>
/// Turns an <see cref="ArchiModelSpec"/> into an ArchiMate 3.1 Open Exchange Format document (model + diagrams).
/// Nodes without coordinates are laid out automatically: one row band per ArchiMate layer (strategy and motivation
/// at the top, physical and implementation at the bottom), nested nodes in a grid inside their parent.
/// </summary>
public static class ExchangeModelBuilder
{
    public const string Namespace = "http://www.opengroup.org/xsd/archimate/3.0/";
    private const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private const string SchemaLocation = Namespace + " http://www.opengroup.org/xsd/archimate/3.1/archimate3_Diagram.xsd";

    internal const int NodeWidth = 120, NodeHeight = 55, Gap = 40, Margin = 20, Padding = 10, LabelSpace = 25, PerRow = 6, ChildrenPerRow = 3;

    // Layer index: rows are ordered by it in auto layout.
    private static readonly Dictionary<string, (string Name, int Layer)> ElementTypes = Index(
        (0, "Stakeholder Driver Assessment Goal Outcome Principle Requirement Constraint Meaning Value"),
        (1, "Resource Capability ValueStream CourseOfAction"),
        (2, "BusinessActor BusinessRole BusinessCollaboration BusinessInterface BusinessProcess BusinessFunction BusinessInteraction BusinessEvent BusinessService BusinessObject Contract Representation Product"),
        (3, "ApplicationComponent ApplicationCollaboration ApplicationInterface ApplicationFunction ApplicationInteraction ApplicationProcess ApplicationEvent ApplicationService DataObject"),
        (4, "Node Device SystemSoftware TechnologyCollaboration TechnologyInterface Path CommunicationNetwork TechnologyFunction TechnologyProcess TechnologyInteraction TechnologyEvent TechnologyService Artifact"),
        (5, "Equipment Facility DistributionNetwork Material"),
        (6, "WorkPackage Deliverable ImplementationEvent Plateau Gap"),
        (7, "Grouping Location AndJunction OrJunction"));

    private static readonly Dictionary<string, string> RelationshipTypes =
        "Composition Aggregation Assignment Realization Serving Access Influence Triggering Flow Specialization Association"
            .Split(' ').ToDictionary(t => t, t => t, StringComparer.OrdinalIgnoreCase);

    // Relationships that nesting already expresses, so no line is drawn from a parent to its nested child.
    private static readonly HashSet<string> NestingTypes = new(StringComparer.Ordinal)
        { "Composition", "Aggregation", "Assignment", "Realization", "Specialization", "Association" };

    private static readonly string[] AccessTypes = ["Access", "Read", "Write", "ReadWrite"];

    public sealed record Result(string Xml, int Elements, int Relationships, IReadOnlyList<string> Views);

    public static Result Build(ArchiModelSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var errors = new List<string>();

        // Elements
        var elements = new Dictionary<string, (string Xid, string Type, int Layer, ElementSpec Spec)>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in spec.Elements ?? [])
        {
            if (string.IsNullOrWhiteSpace(e.Id))
            {
                errors.Add($"element '{e.Name}': id missing");
                continue;
            }

            if (!ElementTypes.TryGetValue(Normalize(e.Type), out var type))
            {
                errors.Add($"element '{e.Id}': unknown type '{e.Type}'");
                continue;
            }

            var xid = Xid(e.Id);
            if (!ids.Add(xid) || !elements.TryAdd(e.Id, (xid, type.Name, type.Layer, e)))
            {
                errors.Add($"element '{e.Id}': duplicate id");
            }
        }

        if (elements.Count == 0 && errors.Count == 0)
        {
            errors.Add("elements: at least one element is required");
        }

        // Relationships
        var relationships = new List<(string Xid, string Type, string Source, string Target, RelationshipSpec Spec)>();
        foreach (var r in spec.Relationships ?? [])
        {
            if (!RelationshipTypes.TryGetValue(Normalize(r.Type), out var type))
            {
                errors.Add($"relationship {r.Source}->{r.Target}: unknown type '{r.Type}'");
                continue;
            }

            if (!elements.ContainsKey(r.Source) || !elements.ContainsKey(r.Target))
            {
                errors.Add($"relationship {r.Source}->{r.Target}: unknown {(elements.ContainsKey(r.Source) ? "target" : "source")}");
                continue;
            }

            if (r.Access is { } access && !AccessTypes.Contains(access, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"relationship {r.Source}->{r.Target}: access must be Read, Write, ReadWrite or Access");
                continue;
            }

            var xid = UniqueId(ids, $"id-rel-{relationships.Count + 1}");
            relationships.Add((xid, type, r.Source, r.Target, r));
        }

        // Views (default: one view showing every element)
        var views = spec.Views is { Count: > 0 }
            ? spec.Views
            : [new ViewSpec { Name = string.IsNullOrWhiteSpace(spec.Name) ? "Default View" : spec.Name, Elements = [.. elements.Keys] }];
        var laidOut = new List<(string Xid, ViewSpec Spec, List<LayoutNode> Nodes)>();
        foreach (var v in views)
        {
            var nodes = new List<LayoutNode>();
            var keys = v.Elements is null && v.Nodes is null ? [.. elements.Keys] : v.Elements ?? [];
            foreach (var key in keys)
            {
                nodes.Add(new LayoutNode(new NodeSpec { Ref = key }));
            }

            foreach (var n in v.Nodes ?? [])
            {
                nodes.Add(LayoutNode.From(n));
            }

            var unknown = Flatten(nodes).Where(n => !elements.ContainsKey(n.Spec.Ref)).Select(n => n.Spec.Ref).Distinct().ToList();
            if (unknown.Count > 0)
            {
                errors.Add($"view '{v.Name}': unknown element id(s) {string.Join(", ", unknown)}");
                continue;
            }

            Layout(nodes, elements.ToDictionary(e => e.Key, e => e.Value.Layer));
            laidOut.Add((UniqueId(ids, $"id-view-{laidOut.Count + 1}"), v, nodes));
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid model: " + string.Join("; ", errors.Take(10)) + (errors.Count > 10 ? $"; and {errors.Count - 10} more" : ""));
        }

        // Write
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, IndentChars = "  ", Encoding = Encoding.UTF8 }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("model", Namespace);
            w.WriteAttributeString("xmlns", "xsi", null, Xsi);
            w.WriteAttributeString("schemaLocation", Xsi, SchemaLocation);
            w.WriteAttributeString("identifier", UniqueId(ids, "id-model"));
            WriteText(w, "name", string.IsNullOrWhiteSpace(spec.Name) ? "Model" : spec.Name);
            WriteText(w, "documentation", spec.Doc);

            w.WriteStartElement("elements", Namespace);
            foreach (var (_, (xid, type, _, e)) in elements)
            {
                w.WriteStartElement("element", Namespace);
                w.WriteAttributeString("identifier", xid);
                w.WriteAttributeString("type", Xsi, type);
                WriteText(w, "name", e.Name);
                WriteText(w, "documentation", e.Doc);
                w.WriteEndElement();
            }

            w.WriteEndElement();

            if (relationships.Count > 0)
            {
                w.WriteStartElement("relationships", Namespace);
                foreach (var (xid, type, source, target, r) in relationships)
                {
                    w.WriteStartElement("relationship", Namespace);
                    w.WriteAttributeString("identifier", xid);
                    w.WriteAttributeString("source", elements[source].Xid);
                    w.WriteAttributeString("target", elements[target].Xid);
                    w.WriteAttributeString("type", Xsi, type);
                    if (type == "Access")
                    {
                        w.WriteAttributeString("accessType", AccessTypes.First(a => a.Equals(r.Access ?? "Write", StringComparison.OrdinalIgnoreCase)));
                    }
                    else if (type == "Association" && r.Directed == true)
                    {
                        w.WriteAttributeString("isDirected", "true");
                    }

                    WriteText(w, "name", r.Name);
                    w.WriteEndElement();
                }

                w.WriteEndElement();
            }

            w.WriteStartElement("views", Namespace);
            w.WriteStartElement("diagrams", Namespace);
            foreach (var (viewXid, v, nodes) in laidOut)
            {
                w.WriteStartElement("view", Namespace);
                w.WriteAttributeString("identifier", viewXid);
                w.WriteAttributeString("type", Xsi, "Diagram");
                WriteText(w, "name", string.IsNullOrWhiteSpace(v.Name) ? "View" : v.Name);

                // Node ids per element; an element shown twice gets connections on its first node only.
                var nodeIds = new Dictionary<string, string>(StringComparer.Ordinal);
                var ancestors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (var n in nodes)
                {
                    WriteNode(w, n, 0, 0, ids, viewXid, elements, nodeIds, ancestors, []);
                }

                if (v.Connections != false)
                {
                    foreach (var (relXid, type, source, target, _) in relationships)
                    {
                        if (!nodeIds.TryGetValue(source, out var s) || !nodeIds.TryGetValue(target, out var t) || source == target)
                        {
                            continue;
                        }

                        var nested = ancestors[target].Contains(source) || ancestors[source].Contains(target);
                        if (nested && NestingTypes.Contains(type))
                        {
                            continue;
                        }

                        w.WriteStartElement("connection", Namespace);
                        w.WriteAttributeString("identifier", UniqueId(ids, $"{viewXid}-c"));
                        w.WriteAttributeString("relationshipRef", relXid);
                        w.WriteAttributeString("type", Xsi, "Relationship");
                        w.WriteAttributeString("source", s);
                        w.WriteAttributeString("target", t);
                        w.WriteEndElement();
                    }
                }

                w.WriteEndElement();
            }

            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndDocument();
        }

        // StringBuilder output declares utf-16; the file is written as UTF-8.
        var xml = sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"UTF-8\"", StringComparison.Ordinal);
        return new Result(xml, elements.Count, relationships.Count, laidOut.Select(v => string.IsNullOrWhiteSpace(v.Spec.Name) ? "View" : v.Spec.Name).ToList());
    }

    private static void WriteNode(XmlWriter w, LayoutNode n, int offsetX, int offsetY, HashSet<string> ids, string viewXid,
        Dictionary<string, (string Xid, string Type, int Layer, ElementSpec Spec)> elements, Dictionary<string, string> nodeIds,
        Dictionary<string, HashSet<string>> ancestors, HashSet<string> parents)
    {
        // Exchange format coordinates are absolute (relative to the view), the spec's are relative to the parent.
        var x = offsetX + n.X;
        var y = offsetY + n.Y;
        var xid = UniqueId(ids, $"{viewXid}-n");
        nodeIds.TryAdd(n.Spec.Ref, xid);
        ancestors.TryAdd(n.Spec.Ref, parents);

        w.WriteStartElement("node", Namespace);
        w.WriteAttributeString("identifier", xid);
        w.WriteAttributeString("elementRef", elements[n.Spec.Ref].Xid);
        w.WriteAttributeString("type", Xsi, "Element");
        w.WriteAttributeString("x", x.ToString(System.Globalization.CultureInfo.InvariantCulture));
        w.WriteAttributeString("y", y.ToString(System.Globalization.CultureInfo.InvariantCulture));
        w.WriteAttributeString("w", n.W.ToString(System.Globalization.CultureInfo.InvariantCulture));
        w.WriteAttributeString("h", n.H.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var childParents = new HashSet<string>(parents, StringComparer.Ordinal) { n.Spec.Ref };
        foreach (var c in n.Children)
        {
            WriteNode(w, c, x, y, ids, viewXid, elements, nodeIds, ancestors, childParents);
        }

        w.WriteEndElement();
    }

    /// <summary>Sizes nested nodes bottom-up, then places unpositioned top-level nodes in one row band per layer.</summary>
    private static void Layout(List<LayoutNode> nodes, Dictionary<string, int> layers)
    {
        foreach (var n in nodes)
        {
            Size(n);
        }

        var y = Margin;
        foreach (var band in nodes.Where(n => n.Spec.X is null || n.Spec.Y is null).GroupBy(n => layers[n.Spec.Ref]).OrderBy(g => g.Key))
        {
            foreach (var row in band.Chunk(PerRow))
            {
                var x = Margin;
                foreach (var n in row)
                {
                    n.X = x;
                    n.Y = y;
                    x += n.W + Gap;
                }

                y += row.Max(n => n.H) + Gap;
            }
        }

        foreach (var n in nodes.Where(n => n.Spec.X is not null && n.Spec.Y is not null))
        {
            n.X = n.Spec.X!.Value;
            n.Y = n.Spec.Y!.Value;
        }
    }

    private static void Size(LayoutNode n)
    {
        foreach (var c in n.Children)
        {
            Size(c);
        }

        // Children without coordinates go in a grid below the parent's label.
        var y = LabelSpace;
        foreach (var row in n.Children.Where(c => c.Spec.X is null || c.Spec.Y is null).Chunk(ChildrenPerRow))
        {
            var x = Padding;
            foreach (var c in row)
            {
                c.X = x;
                c.Y = y;
                x += c.W + Padding;
            }

            y += row.Max(c => c.H) + Padding;
        }

        foreach (var c in n.Children.Where(c => c.Spec.X is not null && c.Spec.Y is not null))
        {
            c.X = c.Spec.X!.Value;
            c.Y = c.Spec.Y!.Value;
        }

        var contentW = n.Children.Count == 0 ? 0 : n.Children.Max(c => c.X + c.W) + Padding;
        var contentH = n.Children.Count == 0 ? 0 : n.Children.Max(c => c.Y + c.H) + Padding;
        n.W = n.Spec.W ?? Math.Max(NodeWidth, contentW);
        n.H = n.Spec.H ?? Math.Max(NodeHeight, contentH);
    }

    private static IEnumerable<LayoutNode> Flatten(IEnumerable<LayoutNode> nodes) =>
        nodes.SelectMany(n => Flatten(n.Children).Prepend(n));

    private static void WriteText(XmlWriter w, string name, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        w.WriteStartElement(name, Namespace);
        w.WriteAttributeString("xml", "lang", null, "en");
        w.WriteString(text);
        w.WriteEndElement();
    }

    /// <summary>Makes a caller key a valid, readable xs:ID: id-&lt;key&gt; with non-NCName characters replaced.</summary>
    internal static string Xid(string key)
    {
        var sb = new StringBuilder("id-", key.Length + 3);
        foreach (var ch in key.Trim())
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
        }

        return sb.ToString();
    }

    private static string UniqueId(HashSet<string> ids, string stem)
    {
        for (var i = 1; ; i++)
        {
            var id = stem.EndsWith("-n", StringComparison.Ordinal) || stem.EndsWith("-c", StringComparison.Ordinal) ? stem + i : i == 1 ? stem : $"{stem}-{i}";
            if (ids.Add(id))
            {
                return id;
            }
        }
    }

    private static string Normalize(string? type) => (type ?? string.Empty).Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);

    private static Dictionary<string, (string, int)> Index(params (int Layer, string Types)[] layers)
    {
        var map = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (layer, types) in layers)
        {
            foreach (var t in types.Split(' '))
            {
                map[t] = (t, layer);
            }
        }

        map["Junction"] = ("AndJunction", 7);
        return map;
    }

    private sealed class LayoutNode(NodeSpec spec)
    {
        public NodeSpec Spec { get; } = spec;
        public List<LayoutNode> Children { get; } = [];
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }

        public static LayoutNode From(NodeSpec spec)
        {
            var node = new LayoutNode(spec);
            foreach (var c in spec.Nodes ?? [])
            {
                node.Children.Add(From(c));
            }

            return node;
        }
    }
}
