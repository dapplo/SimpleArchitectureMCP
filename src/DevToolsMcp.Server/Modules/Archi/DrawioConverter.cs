using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DevToolsMcp.Server.Modules.Archi;

/// <summary>
/// Converts the diagrams of an ArchiMate 3.x Open Exchange Format document into a draw.io (diagrams.net) file, one
/// page per view, using draw.io's built-in ArchiMate 3 shapes (<c>mxgraph.archimate3.*</c>) and Archi's default
/// colours. Positions, sizes, nesting and bendpoints are kept; the result opens in draw.io desktop, the web app and
/// the draw.io apps for Confluence and Jira.
/// </summary>
public static class DrawioConverter
{
    private static readonly XNamespace A = ExchangeModelBuilder.Namespace;
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private const string Business = "#ffffb5", Application = "#b5ffff", Technology = "#c9e7b7", Motivation = "#ccccff",
        Strategy = "#f5deaa", Implementation = "#ffe0e0", Migration = "#e0ffe0", LocationColor = "#fbb875";

    // ArchiMate type -> (draw.io appType, archiType or null, fill colour)
    private static readonly Dictionary<string, (string AppType, string? ArchiType, string Fill)> Shapes = new(StringComparer.Ordinal)
    {
        ["BusinessActor"] = ("actor", "square", Business),
        ["BusinessRole"] = ("role", "square", Business),
        ["BusinessCollaboration"] = ("collab", "square", Business),
        ["BusinessInterface"] = ("interface", "square", Business),
        ["BusinessProcess"] = ("proc", "rounded", Business),
        ["BusinessFunction"] = ("func", "rounded", Business),
        ["BusinessInteraction"] = ("interaction", "rounded", Business),
        ["BusinessEvent"] = ("event", "rounded", Business),
        ["BusinessService"] = ("serv", "rounded", Business),
        ["BusinessObject"] = ("passive", "square", Business),
        ["Contract"] = ("contract", "square", Business),
        ["Representation"] = ("representation", "square", Business),
        ["Product"] = ("product", "square", Business),
        ["ApplicationComponent"] = ("comp", "square", Application),
        ["ApplicationCollaboration"] = ("collab", "square", Application),
        ["ApplicationInterface"] = ("interface", "square", Application),
        ["ApplicationFunction"] = ("func", "rounded", Application),
        ["ApplicationInteraction"] = ("interaction", "rounded", Application),
        ["ApplicationProcess"] = ("proc", "rounded", Application),
        ["ApplicationEvent"] = ("event", "rounded", Application),
        ["ApplicationService"] = ("serv", "rounded", Application),
        ["DataObject"] = ("passive", "square", Application),
        ["Node"] = ("node", "square", Technology),
        ["Device"] = ("device", null, Technology),
        ["SystemSoftware"] = ("sysSw", "square", Technology),
        ["TechnologyCollaboration"] = ("collab", "square", Technology),
        ["TechnologyInterface"] = ("interface", "square", Technology),
        ["Path"] = ("path", "square", Technology),
        ["CommunicationNetwork"] = ("netw", "square", Technology),
        ["TechnologyFunction"] = ("func", "rounded", Technology),
        ["TechnologyProcess"] = ("proc", "rounded", Technology),
        ["TechnologyInteraction"] = ("interaction", "rounded", Technology),
        ["TechnologyEvent"] = ("event", "rounded", Technology),
        ["TechnologyService"] = ("serv", "rounded", Technology),
        ["Artifact"] = ("artifact", "square", Technology),
        ["Equipment"] = ("equipment", "square", Technology),
        ["Facility"] = ("facility", "square", Technology),
        ["DistributionNetwork"] = ("distribution", "square", Technology),
        ["Material"] = ("material", "square", Technology),
        ["Stakeholder"] = ("role", "oct", Motivation),
        ["Driver"] = ("driver", "oct", Motivation),
        ["Assessment"] = ("assess", "oct", Motivation),
        ["Goal"] = ("goal", "oct", Motivation),
        ["Outcome"] = ("outcome", "oct", Motivation),
        ["Principle"] = ("principle", "oct", Motivation),
        ["Requirement"] = ("requirement", "oct", Motivation),
        ["Constraint"] = ("constraint", "oct", Motivation),
        ["Meaning"] = ("meaning", "oct", Motivation),
        ["Value"] = ("amValue", "oct", Motivation),
        ["Resource"] = ("resource", "square", Strategy),
        ["Capability"] = ("capability", "rounded", Strategy),
        ["ValueStream"] = ("valueStream", "rounded", Strategy),
        ["CourseOfAction"] = ("course", "rounded", Strategy),
        ["WorkPackage"] = ("workPackage", "rounded", Implementation),
        ["Deliverable"] = ("deliverable", null, Implementation),
        ["ImplementationEvent"] = ("event", "rounded", Implementation),
        ["Plateau"] = ("plateau", null, Migration),
        ["Gap"] = ("gap", null, Migration),
        ["Location"] = ("location", "square", LocationColor),
        ["Grouping"] = ("grouping", "square", "none"),
    };

    public sealed record Result(string Xml, IReadOnlyList<string> Pages);

    /// <summary>Converts the selected views (names or ids; all when empty) of an exchange document.</summary>
    public static Result Convert(string exchangeXml, IReadOnlyCollection<string>? views = null)
    {
        XDocument doc;
        try
        {
            // No DTDs: an entity-expansion bomb in a model file must not eat memory, and nothing external is resolved.
            using var reader = XmlReader.Create(new StringReader(exchangeXml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true,
            });
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new ArgumentException($"Not an XML document: {ex.Message}", ex);
        }

        if (doc.Root?.Name != A + "model")
        {
            throw new ArgumentException("Not an ArchiMate 3.x Open Exchange Format file (expected <model> in " + A.NamespaceName + ").");
        }

        var elements = doc.Root.Element(A + "elements")?.Elements(A + "element")
            .ToDictionary(e => (string?)e.Attribute("identifier") ?? "", e => e, StringComparer.Ordinal) ?? [];
        var relationships = doc.Root.Element(A + "relationships")?.Elements(A + "relationship")
            .ToDictionary(e => (string?)e.Attribute("identifier") ?? "", e => e, StringComparer.Ordinal) ?? [];

        var all = doc.Root.Element(A + "views")?.Element(A + "diagrams")?.Elements(A + "view").ToList() ?? [];
        if (all.Count == 0)
        {
            throw new ArgumentException("The model has no views.");
        }

        var wanted = views?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList() ?? [];
        var selected = wanted.Count == 0
            ? all
            : all.Where(v => wanted.Contains((string?)v.Attribute("identifier") ?? "", StringComparer.Ordinal)
                             || wanted.Contains(Name(v) ?? "", StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0)
        {
            throw new ArgumentException($"No view '{string.Join("', '", wanted)}'. Views: {string.Join(", ", all.Select(v => Name(v)).Take(30))}");
        }

        var sb = new StringBuilder();
        var pages = new List<string>();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true }))
        {
            w.WriteStartElement("mxfile");
            w.WriteAttributeString("host", "devtools-mcp");
            foreach (var view in selected)
            {
                var name = Name(view) ?? "View";
                pages.Add(name);
                w.WriteStartElement("diagram");
                w.WriteAttributeString("id", (string?)view.Attribute("identifier") ?? "view-" + pages.Count);
                w.WriteAttributeString("name", name);
                w.WriteStartElement("mxGraphModel");
                foreach (var (k, v) in new[] { ("grid", "1"), ("gridSize", "10"), ("guides", "1"), ("connect", "1"), ("arrows", "1"), ("page", "0") })
                {
                    w.WriteAttributeString(k, v);
                }

                w.WriteStartElement("root");
                Cell(w, "0", null);
                w.WriteEndElement();
                Cell(w, "1", "0");
                w.WriteEndElement();
                foreach (var node in view.Elements(A + "node"))
                {
                    WriteNode(w, node, "1", 0, 0, elements);
                }

                foreach (var connection in view.Descendants(A + "connection"))
                {
                    WriteConnection(w, connection, relationships);
                }

                w.WriteEndElement(); // root
                w.WriteEndElement(); // mxGraphModel
                w.WriteEndElement(); // diagram
            }

            w.WriteEndElement();
        }

        return new Result(sb.ToString(), pages);
    }

    private static void WriteNode(XmlWriter w, XElement node, string parent, int parentX, int parentY, Dictionary<string, XElement> elements)
    {
        var id = (string?)node.Attribute("identifier") ?? Guid.NewGuid().ToString("N");
        int x = Int(node, "x"), y = Int(node, "y"), width = Int(node, "w", 120), height = Int(node, "h", 55);
        var children = node.Elements(A + "node").ToList();
        var nodeType = (string?)node.Attribute(Xsi + "type");

        string label;
        string style;
        if (nodeType == "Label")
        {
            label = Text(node.Element(A + "label"));
            style = "text;html=1;whiteSpace=wrap;align=left;verticalAlign=top;spacing=5;strokeColor=#000000;fillColor=#ffffff;";
        }
        else if (nodeType == "Container" || node.Attribute("elementRef") is null)
        {
            label = Text(node.Element(A + "label"));
            style = "rounded=0;html=1;whiteSpace=wrap;verticalAlign=top;align=left;spacingLeft=5;fillColor=#ffffff;";
        }
        else
        {
            var element = elements.GetValueOrDefault((string)node.Attribute("elementRef")!);
            var type = (string?)element?.Attribute(Xsi + "type") ?? "";
            label = Name(element) ?? "";
            style = ElementStyle(type, children.Count > 0);
        }

        if (Fill(node) is { } fill)
        {
            style += "fillColor=" + fill + ";";
        }

        w.WriteStartElement("mxCell");
        w.WriteAttributeString("id", id);
        w.WriteAttributeString("value", WebUtility.HtmlEncode(label).Replace("\n", "<br>", StringComparison.Ordinal));
        w.WriteAttributeString("style", style);
        w.WriteAttributeString("vertex", "1");
        w.WriteAttributeString("parent", parent);
        Geometry(w, x - parentX, y - parentY, width, height);
        w.WriteEndElement();

        // draw.io child geometry is relative to the parent cell; exchange coordinates are absolute.
        foreach (var child in children)
        {
            WriteNode(w, child, id, x, y, elements);
        }
    }

    internal static string ElementStyle(string type, bool hasChildren)
    {
        if (type is "AndJunction" or "OrJunction")
        {
            return $"ellipse;html=1;aspect=fixed;strokeColor=#000000;fillColor={(type == "AndJunction" ? "#000000" : "#ffffff")};";
        }

        var (appType, archiType, fill) = Shapes.GetValueOrDefault(type, ("generic", "square", "#ebebeb"));
        var style = new StringBuilder("html=1;outlineConnect=0;whiteSpace=wrap;shape=mxgraph.archimate3.application;appType=").Append(appType).Append(';');
        if (archiType is not null)
        {
            style.Append("archiType=").Append(archiType).Append(';');
        }

        if (type == "Grouping")
        {
            style.Append("dashed=1;");
        }

        if (hasChildren || type == "Grouping")
        {
            style.Append("verticalAlign=top;align=left;spacingLeft=5;");
        }

        return style.Append("fillColor=").Append(fill).Append(';').ToString();
    }

    private static void WriteConnection(XmlWriter w, XElement connection, Dictionary<string, XElement> relationships)
    {
        var relationship = connection.Attribute("relationshipRef") is { } r ? relationships.GetValueOrDefault(r.Value) : null;
        w.WriteStartElement("mxCell");
        w.WriteAttributeString("id", (string?)connection.Attribute("identifier") ?? Guid.NewGuid().ToString("N"));
        w.WriteAttributeString("value", WebUtility.HtmlEncode(relationship is null ? Text(connection.Element(A + "label")) : Name(relationship) ?? ""));
        w.WriteAttributeString("style", relationship is null ? "html=1;endArrow=none;" : RelationshipStyle(relationship));
        w.WriteAttributeString("edge", "1");
        w.WriteAttributeString("parent", "1");
        w.WriteAttributeString("source", (string?)connection.Attribute("source") ?? "");
        w.WriteAttributeString("target", (string?)connection.Attribute("target") ?? "");

        w.WriteStartElement("mxGeometry");
        w.WriteAttributeString("relative", "1");
        w.WriteAttributeString("as", "geometry");
        var bendpoints = connection.Elements(A + "bendpoint").ToList();
        if (bendpoints.Count > 0)
        {
            w.WriteStartElement("Array");
            w.WriteAttributeString("as", "points");
            foreach (var b in bendpoints)
            {
                w.WriteStartElement("mxPoint");
                w.WriteAttributeString("x", Int(b, "x").ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("y", Int(b, "y").ToString(CultureInfo.InvariantCulture));
                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        w.WriteEndElement();
        w.WriteEndElement();
    }

    /// <summary>ArchiMate notation as draw.io edge styles (straight lines like Archi; draw.io's palette uses the same arrows).</summary>
    internal static string RelationshipStyle(XElement relationship)
    {
        const string Line = "html=1;rounded=0;";
        return (string?)relationship.Attribute(Xsi + "type") switch
        {
            "Composition" => Line + "startArrow=diamondThin;startFill=1;startSize=10;endArrow=none;",
            "Aggregation" => Line + "startArrow=diamondThin;startFill=0;startSize=10;endArrow=none;",
            "Assignment" => Line + "startArrow=oval;startFill=1;endArrow=block;endFill=1;",
            "Realization" => Line + "dashed=1;endArrow=block;endFill=0;",
            "Serving" => Line + "endArrow=open;endFill=1;",
            "Access" => Line + "dashed=1;dashPattern=1 4;" + ((string?)relationship.Attribute("accessType") switch
            {
                "Read" => "startArrow=open;startFill=0;endArrow=none;",
                "ReadWrite" => "startArrow=open;startFill=0;endArrow=open;endFill=0;",
                "Access" => "endArrow=none;",
                _ => "endArrow=open;endFill=0;", // Write is the exchange format default
            }),
            "Influence" => Line + "dashed=1;dashPattern=6 4;endArrow=open;endFill=0;",
            "Triggering" => Line + "endArrow=block;endFill=1;",
            "Flow" => Line + "dashed=1;dashPattern=6 4;endArrow=block;endFill=1;",
            "Specialization" => Line + "endArrow=block;endFill=0;",
            "Association" => Line + ((string?)relationship.Attribute("isDirected") == "true" ? "endArrow=openAsync;endFill=0;" : "endArrow=none;"),
            _ => Line + "endArrow=none;",
        };
    }

    private static void Cell(XmlWriter w, string id, string? parent)
    {
        w.WriteStartElement("mxCell");
        w.WriteAttributeString("id", id);
        if (parent is not null)
        {
            w.WriteAttributeString("parent", parent);
        }
    }

    private static void Geometry(XmlWriter w, int x, int y, int width, int height)
    {
        w.WriteStartElement("mxGeometry");
        w.WriteAttributeString("x", x.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("y", y.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("width", width.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("height", height.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("as", "geometry");
        w.WriteEndElement();
    }

    /// <summary>A node's own fill colour from its exchange-format style, as #rrggbb.</summary>
    private static string? Fill(XElement node)
    {
        var fill = node.Element(A + "style")?.Element(A + "fillColor");
        return fill is null ? null : $"#{Int(fill, "r"):x2}{Int(fill, "g"):x2}{Int(fill, "b"):x2}";
    }

    private static string? Name(XElement? e) => e?.Elements(A + "name").FirstOrDefault()?.Value;

    private static string Text(XElement? e) => e?.Value ?? "";

    private static int Int(XElement e, string attribute, int fallback = 0) =>
        int.TryParse((string?)e.Attribute(attribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
